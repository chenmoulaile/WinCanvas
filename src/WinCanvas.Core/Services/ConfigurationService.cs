using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;

namespace WinCanvas.Core.Services;

/// <summary>
/// <see cref="IConfigurationService"/> 的默认实现：
/// 持久化到 %APPDATA%/WinCanvas/config.json，内存缓存 + 延迟防抖批量写盘。
///
/// v0.2.0 并发与数据安全收口（四条实测缺陷）：
/// ① 撕裂写盘：旧实现把调用方传进来的【活对象引用】存进 _cached，随后在
///    线程池上 JsonSerializer.Serialize 同一实例，而 UI 线程此刻正在
///    config.Windows.Add(...) / 改字段。<see cref="_syncLock"/> 只保护引用交换、
///    不保护内容，于是偶发 InvalidOperationException（Collection was modified）；
///    而该 Task 被 `_ = Task.Run(...)` 丢弃，异常无人观察 → 配置静默丢失。
///    现在改为【在锁内序列化成不可变字符串快照】，写盘线程只碰字符串，
///    与调用方对象彻底解耦。
/// ② CTS 世代竞态：旧实现防抖任务收尾时无条件 Dispose/null 共享的
///    _pendingSaveCts，可能干掉【下一次】Update 新建的 CTS，使新任务的
///    Task.Delay 抛 ObjectDisposedException（旧代码只 catch 了
///    OperationCanceledException）→ 最新改动永不落盘。
///    现在 CTS 由它所属的那一次任务【独占并负责释放】，Update 只 Cancel 不
///    Dispose；任务再按世代号认领，非最新世代直接让位，不碰任何共享状态。
/// ③ 数据不可恢复：旧实现 JSON 解析失败即静默回退默认值，且下一次写盘直接
///    覆盖原文件——一次手滑编辑就永久丢档。现在覆盖前留 .bak，
///    主文件损坏先试 .bak，都失败才把坏文件隔离成 .corrupt-时间戳 后回退默认，
///    全程留诊断日志。
/// ④ 退出冻结 / 丢最后一段改动：旧 Flush 无超时地 _ioGate.Wait()，
///    旧 Dispose 又直接丢弃待写快照。现在 Flush 带超时兜底、Dispose 尽力落盘。
/// </summary>
public sealed class ConfigurationService : IConfigurationService, IDisposable
{
    /// <summary>
    /// 本实现支持的最高配置文件格式版本。
    /// 读到的版本高于此值时不丢弃数据（未知字段本就被忽略），只记诊断日志；
    /// 写盘时统一钉成此值。修掉的缺陷：旧实现有 Version 字段但【从未读取】，
    /// 等于没有演进契约——配置文件格式一变就会静默错读。
    ///
    /// 版本 2：新增 AppConfig.Hotkeys（全局热键绑定）。旧 v1 文件缺字段反序列化成
    /// 空列表、运行期补默认绑定，属向后兼容的增量字段，故不需要显式迁移代码。
    /// </summary>
    public const int SupportedConfigVersion = 2;

    /// <summary>延迟写盘的默认防抖间隔：多次 Update 在该窗口内合并为一次落盘。</summary>
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Flush / Dispose 等待磁盘闸门的最长时间。修掉的缺陷：旧实现在窗口关闭回调里
    /// 无超时地 _ioGate.Wait()，一旦防抖写盘正卡在慢盘或杀软扫描上，退出会冻结。
    /// 超时后放弃本次同步写并记日志——宁可丢最后一次改动，也不要让程序退不出去。
    /// </summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    /// <summary>序列化选项：中文原样输出（不转 \uXXXX）、缩进便于人工查看。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>写盘编码：UTF-8 且【不带 BOM】（JSON 按 RFC 8259 不得带 BOM）。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _configFilePath;

    /// <summary>
    /// 本实例生效的防抖间隔：默认取 <see cref="DefaultDebounceDelay"/>（500ms），
    /// 可经构造参数注入更短值——WP4 单元测试据此把「10 次快速 Update 合并为 1 次落盘」的
    /// 验证从 500ms 级等待缩短到毫秒级（行为语义与生产值完全一致，只改时长）。
    /// </summary>
    private readonly TimeSpan _debounceDelay;

    /// <summary>保护内存缓存、待写快照与防抖任务状态的对象锁。</summary>
    private readonly object _syncLock = new();

    /// <summary>串行化磁盘写入，防止防抖写盘与 FlushAsync 并发写同一文件。</summary>
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private AppConfig? _cached;

    /// <summary>
    /// 待落盘的【不可变 JSON 快照】：在锁内由活对象序列化而来，
    /// 因此写盘线程读它时不可能再被 UI 线程撕裂。为 null 表示无待写内容。
    /// </summary>
    private string? _pendingJson;

    /// <summary>
    /// 写盘世代号：每次 Update / Flush / Dispose 递增。防抖任务只在自己仍是最新世代时
    /// 才清理共享状态并落盘，否则直接让位给更新的那一次。
    /// </summary>
    private long _saveGeneration;

    /// <summary>当前挂起的防抖写盘的取消源（生命周期归该次任务独占）。</summary>
    private CancellationTokenSource? _pendingSaveCts;

    /// <summary>最近一次挂起的防抖写盘任务，供 Dispose 收尾等待。</summary>
    private Task? _pendingSaveTask;

    private Exception? _lastSaveError;
    private bool _disposed;

    /// <summary>用默认的 %APPDATA% 目录构造。</summary>
    public ConfigurationService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // %APPDATA%
            "WinCanvas",
            "config.json"),
            DefaultDebounceDelay)
    {
    }

    /// <summary>测试用构造：允许注入配置文件路径（防抖间隔取默认 500ms）。</summary>
    /// <param name="configFilePath">config.json 的绝对路径。</param>
    public ConfigurationService(string configFilePath)
        : this(configFilePath, DefaultDebounceDelay)
    {
    }

    /// <summary>
    /// 测试用构造：允许同时注入配置文件路径与防抖间隔。
    /// </summary>
    /// <param name="configFilePath">config.json 的绝对路径。</param>
    /// <param name="debounceDelay">防抖间隔；必须为正值（TimeSpan.Zero 或负值属于装配错误，尽早暴露）。</param>
    /// <exception cref="ArgumentOutOfRangeException">防抖间隔非正值。</exception>
    public ConfigurationService(string configFilePath, TimeSpan debounceDelay)
    {
        if (debounceDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceDelay), debounceDelay, "防抖间隔必须为正值。");
        }

        _configFilePath = configFilePath;
        _debounceDelay = debounceDelay;
    }

    /// <summary>配置文件完整路径（供诊断使用）。</summary>
    public string ConfigFilePath => _configFilePath;

    /// <summary>
    /// 上一次落盘失败的原因；成功写入后清 null。
    /// 供退出路径判断「是否已丢改动」——旧实现把写盘异常整批吞掉，无从得知。
    /// </summary>
    public Exception? LastSaveError
    {
        get
        {
            lock (_syncLock)
            {
                return _lastSaveError;
            }
        }
    }

    /// <inheritdoc/>
    public Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_cached is not null)
        {
            return Task.FromResult(_cached); // 命中内存缓存
        }

        return LoadFromDiskAsync(cancellationToken);
    }

    /// <summary>
    /// 从磁盘加载配置并填充内存缓存。
    /// 损坏处理顺序：主文件 → .bak 备份 → 隔离主文件后回退默认（绝不静默覆盖）。
    /// </summary>
    private async Task<AppConfig> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        AppConfig loaded = await ReadWithBackupRecoveryAsync(cancellationToken).ConfigureAwait(false);

        lock (_syncLock)
        {
            _cached ??= loaded; // 并发下先到者为准
            loaded = _cached;
        }

        return loaded;
    }

    /// <summary>
    /// 读配置并做损坏恢复。任何情况下都不抛异常（与既有契约一致：
    /// 「损坏 JSON 回退默认配置且不抛异常」）。
    /// </summary>
    private async Task<AppConfig> ReadWithBackupRecoveryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configFilePath))
        {
            return new AppConfig(); // 首次运行：默认配置
        }

        (AppConfig? config, Exception? failure) = await TryReadFileAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            return EnsureVersion(config!, _configFilePath);
        }

        // 主文件读不动：先试上一次写盘留下的 .bak
        string backupPath = BackupPath;
        if (File.Exists(backupPath))
        {
            (AppConfig? bakConfig, Exception? bakFailure) = await TryReadFileAsync(backupPath, cancellationToken).ConfigureAwait(false);
            if (bakFailure is null)
            {
                ConfigDiagnostics.Log(
                    $"主配置损坏（{failure.GetType().Name}: {failure.Message}），" +
                    $"已从 {Path.GetFileName(backupPath)} 恢复。");
                return EnsureVersion(bakConfig!, backupPath);
            }
        }

        // 主文件与备份都不可用：把坏文件【隔离】而不是留着被下次写盘覆盖，
        // 这样用户仍有机会手工找回内容；随后回退默认配置。
        string quarantined = QuarantineCorruptFile(_configFilePath);
        ConfigDiagnostics.Log(
            $"配置文件无法解析且无可用备份，已隔离为 {Path.GetFileName(quarantined)} 并使用默认配置。" +
            $"原因：{failure.GetType().Name}: {failure.Message}");

        return new AppConfig();
    }

    /// <summary>读并反序列化单个文件，返回 (配置, 失败异常)；不抛</summary>
    private static async Task<(AppConfig? Config, Exception? Failure)> TryReadFileAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            // File.ReadAllTextAsync 用 UTF-8 解码；JSON 按规范不带 BOM。
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            AppConfig? parsed = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            return (parsed ?? new AppConfig(), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// 版本契约收口：高于本实现支持的版本只记日志不丢数据（未知字段本就被忽略，
    /// 降级读写仍可工作），并统一钉成当前版本，避免旧版本字段缺失被误判。
    /// </summary>
    private static AppConfig EnsureVersion(AppConfig config, string sourcePath)
    {
        if (config.Version > SupportedConfigVersion)
        {
            ConfigDiagnostics.Log(
                $"配置文件版本 {config.Version} 高于本程序支持的 {SupportedConfigVersion}" +
                $"（来自 {Path.GetFileName(sourcePath)}），将按已知字段读取，未知字段忽略。");
        }

        config.Version = SupportedConfigVersion;
        config.Windows ??= new List<WindowState>();
        return config;
    }

    /// <summary>把损坏文件改名隔离，返回新路径；改名失败时退回原路径（只记日志）。</summary>
    private static string QuarantineCorruptFile(string path)
    {
        string target = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, target);
            return target;
        }
        catch (Exception ex)
        {
            ConfigDiagnostics.Log(
                $"隔离损坏配置文件失败（{ex.GetType().Name}: {ex.Message}），原文件保留在 {Path.GetFileName(path)}。");
            return path;
        }
    }

    /// <summary>上一次成功写盘留下的备份路径。</summary>
    private string BackupPath => _configFilePath + ".bak";

    /// <inheritdoc/>
    public void Update(AppConfig config)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(config);

        CancellationTokenSource cts;
        long generation;

        lock (_syncLock)
        {
            _cached = config;
            config.Version = SupportedConfigVersion;

            // 关键：在锁内把活对象序列化成不可变快照。此后写盘线程只读字符串，
            // UI 线程再怎么改 config.Windows 也不可能撕裂落盘内容。
            // 代价是序列化移到 UI 线程并持锁——配置体量小（每窗口一条档案），
            // 且换来的是「不可能丢档」，比在锁外序列化活对象更划算。
            string json = JsonSerializer.Serialize(config, SerializerOptions);
            _pendingJson = json;

            generation = ++_saveGeneration;
            cts = new CancellationTokenSource();

            // 只 Cancel，不 Dispose：CTS 生命周期归它所属的那一次任务独占，
            // 从根上消灭「旧任务释放新任务 CTS」的世代竞态。
            _pendingSaveCts?.Cancel();
            _pendingSaveCts = cts;

            _pendingSaveTask = Task.Run(() => DelayedSaveAsync(generation, cts, json));
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string? json = TakeSnapshotForFlush();
        if (json is null)
        {
            return; // 无待写快照也无内存缓存：与旧实现一致，不凭空造文件
        }

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WriteJsonAtomically(json);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ThrowIfDisposed();

        string? json = TakeSnapshotForFlush();
        if (json is null)
        {
            return;
        }

        // 同步等待 IO 闸门并同步写盘：调用方（窗口关闭回调）所在的消息循环可能
        // 即将退出，本方法返回前必须保证数据已完整写到磁盘。带超时见 FlushTimeout。
        if (!_ioGate.Wait(FlushTimeout))
        {
            ConfigDiagnostics.Log($"Flush 等待磁盘闸门超时（{FlushTimeout.TotalSeconds:0} 秒），本次同步落盘跳过。");
            return;
        }

        try
        {
            WriteJsonAtomically(json);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// 取走当前待写快照并取消挂起的防抖任务（退出路径）。
    /// 返回 null 表示既无待写快照也无内存缓存，调用方应不产生任何文件。
    /// </summary>
    private string? TakeSnapshotForFlush()
    {
        lock (_syncLock)
        {
            _pendingSaveCts?.Cancel();
            _pendingSaveCts = null;
            _saveGeneration++; // 让在途的防抖任务世代校验失败，自动让位

            string? json = _pendingJson
                ?? (_cached is null ? null : JsonSerializer.Serialize(_cached, SerializerOptions));

            _pendingJson = null;
            return json;
        }
    }

    /// <summary>
    /// 防抖延迟写盘主体。cts 与 json 由创建方（<see cref="Update"/>）随任务一并传入，
    /// 任务全程不重新读共享的活对象，因此不存在撕裂；cts 由本任务在 finally 独占释放。
    /// </summary>
    private async Task DelayedSaveAsync(long generation, CancellationTokenSource cts, string json)
    {
        try
        {
            try
            {
                await Task.Delay(_debounceDelay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // 被新的 Update / Flush 取代，属正常防抖语义
            }

            bool claimed;
            lock (_syncLock)
            {
                // 世代校验：期间若有新的 Update，_pendingJson 已被换成更新的内容，
                // 本任务必须让位（由那一代负责落盘），且绝不触碰共享的 CTS 引用。
                claimed = generation == _saveGeneration;
                if (claimed && ReferenceEquals(_pendingSaveCts, cts))
                {
                    _pendingJson = null;
                    _pendingSaveCts = null;
                }
            }

            if (!claimed)
            {
                return;
            }

            await _ioGate.WaitAsync().ConfigureAwait(false);
            try
            {
                WriteJsonAtomically(json);
            }
            finally
            {
                _ioGate.Release();
            }
        }
        catch (Exception ex)
        {
            // 落盘失败不再静默：留痕 + 记日志 + 把快照还回 _pendingJson（若无人接续），
            // 让 Flush 或下一次 Update 仍能写出这份内容。
            lock (_syncLock)
            {
                _lastSaveError = ex;
                if (generation == _saveGeneration && _pendingJson is null)
                {
                    _pendingJson = json;
                }
            }

            ConfigDiagnostics.Log($"防抖落盘失败（{ex.GetType().Name}: {ex.Message}），待写快照已保留。");
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// 原子写盘核心：随机名临时文件 → 刷到磁盘 → File.Replace 原子替换并留 .bak。
    ///
    /// 修掉的三处旧隐患：
    /// ① 固定 ".tmp" 文件名：本程序未做单实例互斥时，两个实例会互相踩同一个临时文件；
    /// ② 写完直接 File.Move 覆盖：系统缓存未落盘时断电可能留下 0 字节目标文件，
    ///    「原子写」名不副实——现用 FileStream.Flush(true) 强制刷盘；
    /// ③ 覆盖即丢上一版：现由 File.Replace 顺带产出 .bak，损坏时可回退。
    /// </summary>
    private void WriteJsonAtomically(string json)
    {
        string? directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory); // 幂等：目录已存在时不抛异常
        }

        string tempPath = $"{_configFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, Utf8NoBom))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true); // 真正落到磁盘，而不是停在系统缓存
            }

            if (File.Exists(_configFilePath))
            {
                // 原子替换，同时把被替换掉的旧内容留成 .bak
                File.Replace(tempPath, _configFilePath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _configFilePath);
            }

            lock (_syncLock)
            {
                _lastSaveError = null;
            }
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            ConfigDiagnostics.Log($"清理临时文件失败（{Path.GetFileName(path)}：{ex.GetType().Name}）。");
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_syncLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// 释放：取消挂起的防抖任务，并【尽力同步落盘】剩余快照后释放同步对象。
    /// 修掉的缺陷：旧 Dispose 直接丢弃待写内容，默认会吞掉最后 500ms 防抖窗口内的改动。
    /// 调用方仍应在退出前显式 Flush（那才是保证落盘的主路径），这里只是兜底。
    /// </summary>
    public void Dispose()
    {
        string? leftover;
        Task? inFlight;

        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingSaveCts?.Cancel();
            _pendingSaveCts = null;
            _saveGeneration++; // 在途任务世代校验失败，不会与下面的兜底写抢文件
            leftover = _pendingJson;
            _pendingJson = null;
            inFlight = _pendingSaveTask;
        }

        // 等在途任务收尾，避免它与兜底写并发操作同一文件（它已让位，只是等它退出）
        try
        {
            inFlight?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 在途任务的失败已由其内部 catch 记入日志，这里不再抛
        }

        if (leftover is not null)
        {
            try
            {
                if (_ioGate.Wait(FlushTimeout))
                {
                    try
                    {
                        WriteJsonAtomically(leftover);
                    }
                    finally
                    {
                        _ioGate.Release();
                    }
                }
                else
                {
                    ConfigDiagnostics.Log("Dispose 兜底落盘等待磁盘闸门超时，剩余快照未写出。");
                }
            }
            catch (Exception ex)
            {
                ConfigDiagnostics.Log($"Dispose 兜底落盘失败（{ex.GetType().Name}: {ex.Message}）。");
            }
        }

        _ioGate.Dispose();
    }
}
