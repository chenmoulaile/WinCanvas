using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;

namespace WinCanvas.Core.Services;

/// <summary>
/// <see cref="IConfigurationService"/> 的默认实现：
/// 持久化到 %APPDATA%/WinCanvas/config.json，内存缓存 + 延迟防抖批量写盘。
/// </summary>
public sealed class ConfigurationService : IConfigurationService, IDisposable
{
    /// <summary>延迟写盘的防抖间隔：多次 Update 在该窗口内合并为一次落盘。</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

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

    /// <summary>保护内存缓存与防抖任务状态的对象锁。</summary>
    private readonly object _syncLock = new();

    /// <summary>串行化磁盘写入，防止防抖写盘与 FlushAsync 并发写同一文件。</summary>
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private AppConfig? _cached;
    private CancellationTokenSource? _pendingSaveCts;
    private bool _disposed;

    /// <summary>用默认的 %APPDATA% 目录构造。</summary>
    public ConfigurationService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // %APPDATA%
            "WinCanvas",
            "config.json"))
    {
    }

    /// <summary>测试用构造：允许注入配置文件路径。</summary>
    /// <param name="configFilePath">config.json 的绝对路径。</param>
    public ConfigurationService(string configFilePath)
    {
        _configFilePath = configFilePath;
    }

    /// <summary>配置文件完整路径（供诊断使用）。</summary>
    public string ConfigFilePath => _configFilePath;

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
    /// 从磁盘加载配置并填充内存缓存；文件缺失或损坏时回退默认配置。
    /// </summary>
    private async Task<AppConfig> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        AppConfig loaded;
        if (!File.Exists(_configFilePath))
        {
            loaded = new AppConfig(); // 首次运行：默认配置
        }
        else
        {
            // File.ReadAllTextAsync 用 UTF-8 解码；JSON 按规范不带 BOM。
            string json = await File.ReadAllTextAsync(_configFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            try
            {
                loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
            }
            catch (JsonException)
            {
                // 配置文件损坏时回退默认值，不让应用崩溃。
                loaded = new AppConfig();
            }
        }

        lock (_syncLock)
        {
            _cached ??= loaded; // 并发下先到者为准
        }
        return loaded;
    }

    /// <inheritdoc/>
    public void Update(AppConfig config)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(config);

        lock (_syncLock)
        {
            _cached = config;

            // 已有待执行的防抖写盘则取消重新计时 —— 实现"延迟批量写盘"。
            _pendingSaveCts?.Cancel();
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = new CancellationTokenSource();
            CancellationToken token = _pendingSaveCts.Token;
            _ = Task.Run(() => DelayedSaveAsync(token), CancellationToken.None);
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        AppConfig? snapshot;
        lock (_syncLock)
        {
            // 取消尚未执行的延迟写盘，立即落盘（应用退出路径）。
            _pendingSaveCts?.Cancel();
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = null;
            snapshot = _cached;
        }

        if (snapshot is null)
        {
            return;
        }

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteToDiskAsync(snapshot, cancellationToken).ConfigureAwait(false);
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

        AppConfig? snapshot;
        lock (_syncLock)
        {
            // 取消尚未执行的延迟写盘，立即落盘（应用退出路径的同步版本）。
            _pendingSaveCts?.Cancel();
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = null;
            snapshot = _cached;
        }

        if (snapshot is null)
        {
            return;
        }

        // 同步等待 IO 闸门并同步写盘：调用方（窗口关闭回调）所在的消息循环可能
        // 即将退出，本方法返回前必须保证数据已完整写到磁盘。
        _ioGate.Wait();
        try
        {
            WriteToDisk(snapshot);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// 防抖延迟写盘主体：等待 <see cref="DebounceDelay"/>，期间被取消则直接返回。
    /// </summary>
    private async Task DelayedSaveAsync(CancellationToken token)
    {
        AppConfig? snapshot;
        try
        {
            await Task.Delay(DebounceDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 被 Update/Flush 取消属正常流程
        }

        lock (_syncLock)
        {
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = null;
            snapshot = _cached;
        }

        if (snapshot is null)
        {
            return;
        }

        await _ioGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WriteToDiskAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// 原子写盘（异步包装）：先写临时文件再覆盖目标，避免写一半崩溃留下损坏的配置。
    /// 注意：JSON 文件按规范【不带 BOM】（见 <see cref="Utf8NoBom"/>）。
    /// </summary>
    private async Task WriteToDiskAsync(AppConfig config, CancellationToken cancellationToken)
    {
        // 真正的写盘逻辑只有同步一条路径（<see cref="WriteToDisk"/>），
        // 保证 Flush（同步）与防抖写盘（异步）的落盘行为完全一致。
        await Task.Run(() => WriteToDisk(config), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同步原子写盘核心：写临时文件 → File.Move 覆盖目标（Windows 上覆盖已存在文件是标准用法）。
    /// </summary>
    private void WriteToDisk(AppConfig config)
    {
        string? directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory); // 幂等：目录已存在时不抛异常
        }

        string json = JsonSerializer.Serialize(config, SerializerOptions);

        string tempPath = _configFilePath + ".tmp";
        File.WriteAllText(tempPath, json, Utf8NoBom);

        File.Move(tempPath, _configFilePath, overwrite: true);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// 释放：取消挂起的防抖任务并释放同步对象。
    /// 若希望退出时确保落盘，请在 Dispose 前显式调用 <see cref="FlushAsync"/>。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (_syncLock)
        {
            _pendingSaveCts?.Cancel();
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = null;
        }
        _ioGate.Dispose();
    }
}
