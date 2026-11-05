using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.Core.Models;
using WinCanvas.Wallpaper;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的壁纸库部分（v0.3.0 新增；v0.4.0 扩展为多来源 + 缓存秒开）。
///
/// 能力：
/// ① <b>壁纸库自动检索</b>：后台扫描 Wallpaper Engine 工坊、系统主题壁纸、用户图片库
///    与自定义目录，把「上百个文件夹里翻文件」变成「面板里点一下」。检索本身在
///    <c>WallpaperEngineLibrary</c>，本文件只做界面状态与挂接动作。
/// ② <b>缓存秒开 + 增量刷新</b>：上次扫描结果整包缓存在 config（<see cref="AppConfig.WallpaperLibraryCache"/>），
///    启动先回填面板（不阻塞、不报错），后台再按根做签名增量刷新；
///    「检索壁纸库」按钮走强制全量，绕过签名复用。
/// ③ <b>来源标签</b>：每条条目带 工坊 / 系统 / 图片库 / 自定义 标签，随扫描一路贯通。
/// ④ <b>声音开关</b>：默认关闭（v0.3.0 语义不变）。
///
/// 单开 partial 文件的原因与 Shell 部分相同：MainViewModel 已经承担过多职责，
/// 新能力在文件层面先隔离。
/// </summary>
public partial class MainViewModel
{
    /// <summary>检索到的可挂接条目（完整数据源；过滤视图与恢复管线都基于它）。</summary>
    public ObservableCollection<WallpaperLibraryItemViewModel> LibraryItems { get; } = new();

    /// <summary>库面板实际绑定的过滤视图（<see cref="LibraryFilter"/> 生效），整体重建。</summary>
    public ObservableCollection<WallpaperLibraryItemViewModel> FilteredLibraryItems { get; } = new();

    /// <summary>设置区「壁纸库目录」的行集合（仅用户自定义项；自动探测项只读展示在 RootsText）。</summary>
    public ObservableCollection<WallpaperLibraryRootRow> LibraryRootRows { get; } = new();

    /// <summary>正在检索（按钮禁用 + 进度环 + 空态隐藏）。</summary>
    [ObservableProperty]
    public partial bool IsLibraryScanning { get; set; }

    /// <summary>检索按钮是否可点（= 不在检索中）。用正向语义避免在 XAML 里做布尔取反。</summary>
    public bool CanScanLibrary => !IsLibraryScanning;

    partial void OnIsLibraryScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScanLibrary));
        NotifyLibraryEmptyState();
    }

    /// <summary>检索结果说明（条目数、耗时、根目录数 / 缓存载入提示）。</summary>
    [ObservableProperty]
    public partial string LibraryStatusText { get; set; } = "尚未检索。可点「检索壁纸库」，或先添加扫描目录。";

    /// <summary>
    /// 检索进度（「正在扫描第 N / M 个目录…」）。扫描期间非空，完成 / 取消 / 失败后清空——
    /// 之前只有一个转圈 ProgressRing，用户对「扫到哪了、还要多久」毫无概念。
    /// </summary>
    [ObservableProperty]
    public partial string LibraryProgressText { get; set; } = string.Empty;

    /// <summary>当前生效的扫描根（带来源标签，换行拼接，供界面显示）。</summary>
    [ObservableProperty]
    public partial string LibraryRootsText { get; set; } = "（未配置，将使用自动探测结果）";

    /// <summary>库面板的过滤关键字。</summary>
    [ObservableProperty]
    public partial string LibraryFilter { get; set; } = string.Empty;

    partial void OnLibraryFilterChanged(string value)
    {
        RebuildFilteredLibrary();
    }

    /// <summary>
    /// 库面板的空态可见性：没有条目且不在扫描中时显示引导面板。
    /// 与窗口列表的空态同理——「空白」必须说清是【没扫到】还是【正在扫】，
    /// 否则会被当成程序坏了（ask 要求：未装 WE / 目录为空时给引导文案，不报错）。
    /// </summary>
    public Visibility LibraryEmptyVisibility =>
        FilteredLibraryItems.Count == 0 && !IsLibraryScanning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>空态引导文案：区分「过滤无命中」与「库里真的什么都没有」两种情形。</summary>
    public string LibraryEmptyText
    {
        get
        {
            if (LibraryItems.Count > 0)
            {
                return $"没有匹配「{LibraryFilter?.Trim()}」的条目，换个关键字试试。";
            }

            return "没有找到壁纸。这可能是因为：\n" +
                   "· 没装 Wallpaper Engine —— 本程序不依赖它，属正常现象；\n" +
                   "· 系统主题与「图片」库里没有受支持的图片 / 视频；\n" +
                   "· 壁纸放在别处 —— 点上方「添加目录」（或到「设置」页 →「壁纸库目录」）" +
                   "把存有图片 / 视频 / Wallpaper Engine 项目的文件夹加进扫描范围，添加后立即重新检索。";
        }
    }

    /// <summary>空态的两个绑定属性一起刷新（集合与扫描状态变化处调用）。</summary>
    private void NotifyLibraryEmptyState()
    {
        OnPropertyChanged(nameof(LibraryEmptyVisibility));
        OnPropertyChanged(nameof(LibraryEmptyText));
    }

    // —— 扫描生命周期（并发说明：以下三个成员都只在 UI 线程读写；扫描本体在
    //    Task.Run 的线程池线程上执行，只读取 EffectiveRoots() 的快照与 token，
    //    不触碰本类任何 UI 状态，结果经 await 回到 UI 线程后再落到集合上） ——

    /// <summary>当前扫描的取消源。刻意不 Dispose：置回 / 取消都在 UI 线程，
    /// 而后台任务仍可能持有 token——先 Dispose 再 Cancel 会抛
    /// ObjectDisposedException；不 Dispose 只是短命对象晚一点被 GC，无正确性影响。</summary>
    private CancellationTokenSource? _libraryScanCts;

    /// <summary>当前扫描任务（供新扫描接替时 await 旧任务收尾）。</summary>
    private Task _libraryScanTask = Task.CompletedTask;

    /// <summary>扫描世代号：新扫描递增，旧扫描的收尾按世代判断是否还有资格写 UI / 落盘。</summary>
    private int _libraryScanGeneration;

    /// <summary>
    /// 是否允许壁纸发声。<b>默认 false</b>。
    /// 变更时立即作用到已在播的会话（由服务层负责），并持久化。
    /// </summary>
    [ObservableProperty]
    public partial bool WallpaperAudioEnabled { get; set; }

    /// <summary>声音关闭时的说明文案（含音频线索的壁纸需要被解释，否则像 bug）。</summary>
    public string AudioHintText => WallpaperAudioEnabled
        ? "壁纸声音已开启：带音轨的视频壁纸会外放（音量 60%，可在系统音量合成器单独调）。"
        : "壁纸声音已关闭（默认）。带音乐的视频壁纸不会出声 —— 需要听就在下方打开开关。";

    /// <summary>启动回填时抑制 setter 副作用的标志。</summary>
    private bool _suppressAudioSync;

    partial void OnWallpaperAudioEnabledChanged(bool value)
    {
        if (_suppressAudioSync)
        {
            return; // 回填：服务侧的设置由 InitializeWallpaperLibrary 统一做一次
        }

        _services.Wallpaper.AudioEnabled = value;
        _appConfig.WallpaperAudioEnabled = value;
        PersistConfig();
        OnPropertyChanged(nameof(AudioHintText));
    }

    /// <summary>
    /// 把服务的音频设置与配置的扫描根接到界面状态上。
    /// 由 <c>InitializeAsync</c> 在配置加载后调用。
    /// </summary>
    internal void InitializeWallpaperLibrary()
    {
        // 回填走抑制标志：启动阶段是「恢复上次选择」，不该触发
        // 「写回配置 + 重设所有播放器」这条写路径（还会多一条提示文案变更通知）
        _suppressAudioSync = true;
        WallpaperAudioEnabled = _appConfig.WallpaperAudioEnabled;
        _suppressAudioSync = false;
        _services.Wallpaper.AudioEnabled = _appConfig.WallpaperAudioEnabled;
        OnPropertyChanged(nameof(WallpaperAudioEnabled));
        OnPropertyChanged(nameof(AudioHintText));

        RefreshRootRows();
        RefreshRootsText();

        // —— 重启秒开：先把上次扫描的缓存回填进面板（纯内存转换，毫秒级），——
        // 后台再增量刷新兜底。缓存里是上次扫出的【事实】（含不可用条目及其原因），
        // 回填它比空着等扫描快得多，也和「设置先显示上次值」是同一套体验逻辑。
        int cachedEntries = 0;
        var restored = new List<WallpaperLibraryEntry>();
        foreach (WallpaperLibraryRootCache rootCache in _appConfig.WallpaperLibraryCache)
        {
            foreach (WallpaperLibraryCacheEntry cacheEntry in rootCache.Entries)
            {
                WallpaperLibraryEntry? entry = WallpaperEngineLibrary.FromCacheEntry(cacheEntry);
                if (entry is not null)
                {
                    restored.Add(entry);
                }
            }
        }

        if (restored.Count > 0)
        {
            ReplaceLibraryItems(restored);
            cachedEntries = restored.Count;
            LibraryStatusText = $"已从缓存载入 {cachedEntries} 项（上次扫描结果），正在后台增量刷新…";
        }

        // 无论有无缓存都做一次后台增量刷新：目录可能被外部改动过。
        // 不等待（fire-and-forget）：扫描失败 / 取消都由 RunLibraryScanAsync 自己收口。
        _ = RunLibraryScanAsync(forceFullScan: false);
    }

    /// <summary>把「当前生效的扫描根」刷到界面文本（带来源标签）。</summary>
    private void RefreshRootsText()
    {
        List<WallpaperLibraryRoot> roots = EffectiveRoots();
        LibraryRootsText = roots.Count == 0
            ? "（没有找到任何候选目录 —— 请点「添加目录」指定存有壁纸 / 图片 / 视频的文件夹）"
            : string.Join(Environment.NewLine, roots.Select(r => $"[{SourceLabelOf(r.Source)}] {r.Path}"));
    }

    /// <summary>来源的中文短标签（与条目标签同一份口径）。</summary>
    private static string SourceLabelOf(WallpaperLibrarySource source) => source switch
    {
        WallpaperLibrarySource.Workshop => "工坊",
        WallpaperLibrarySource.SystemTheme => "系统",
        WallpaperLibrarySource.Pictures => "图片库",
        WallpaperLibrarySource.Custom => "自定义",
        _ => "未知",
    };

    /// <summary>把 config 里的自定义根刷成设置区的行集合（整体重建，行对象不可变）。</summary>
    private void RefreshRootRows()
    {
        LibraryRootRows.Clear();
        foreach (string path in _appConfig.WallpaperLibraryRoots)
        {
            LibraryRootRows.Add(new WallpaperLibraryRootRow(
                path,
                removeCommand: new RelayCommand(() => _ = RemoveLibraryRootCommand.ExecuteAsync(path))));
        }
    }

    /// <summary>
    /// 实际参与扫描的根：用户配置的在前（来源=自定义），自动探测的并入并去重。
    /// 自动探测覆盖工坊（注册表 / Steam 多库）、系统主题与图片库；两者都要：
    /// 只靠自动探测覆盖不了「壁纸放在自选文件夹」，只靠手工配置又会让 Steam 用户
    /// 每次都要自己指路径。
    /// </summary>
    private List<WallpaperLibraryRoot> EffectiveRoots()
    {
        var result = new List<WallpaperLibraryRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string r in _appConfig.WallpaperLibraryRoots)
        {
            string trimmed = r.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
            {
                result.Add(new WallpaperLibraryRoot(trimmed, WallpaperLibrarySource.Custom));
            }
        }

        foreach (WallpaperLibraryRoot detected in WallpaperEngineLibrary.DetectCandidateRoots())
        {
            if (seen.Add(detected.Path))
            {
                result.Add(detected);
            }
        }

        return result;
    }

    /// <summary>「检索壁纸库」按钮：强制全量重扫（绕过签名缓存，用于怀疑结果过期时）。</summary>
    [RelayCommand]
    private Task ScanLibraryAsync() => RunLibraryScanAsync(forceFullScan: true);

    /// <summary>
    /// 执行一次壁纸库扫描（后台线程池，可取消，不阻塞 UI）。
    ///
    /// 接替语义：上一条扫描还在跑时又触发（用户改目录 / 手动点按钮），直接取消旧的、
    /// 等它收尾再起新的——旧结果不会覆盖新扫描。旧扫描的收尾按世代号让位：
    /// 世代不符的回调一律不写 UI、不落盘。
    /// </summary>
    private async Task RunLibraryScanAsync(bool forceFullScan)
    {
        if (IsLibraryScanning)
        {
            _libraryScanCts?.Cancel();
            try
            {
                await _libraryScanTask; // 旧扫描的异常由它自己的 catch 收口，这里只等它结束
            }
            catch (Exception)
            {
                // 旧扫描的失败已经（或即将）在它自己的 catch 里呈现，这里不重复报告
            }
        }

        int generation = ++_libraryScanGeneration;
        var cts = new CancellationTokenSource();
        _libraryScanCts = cts;

        List<WallpaperLibraryRoot> roots = EffectiveRoots();
        RefreshRootsText();

        // Progress<T> 在 UI 线程构造：回调自动封送回 UI 线程，线程安全
        var progress = new Progress<WallpaperLibraryScanProgress>(p =>
        {
            if (generation != _libraryScanGeneration)
            {
                return; // 已被更新的扫描接替：过期进度不上屏
            }

            LibraryProgressText = p.TotalRoots == 0
                ? string.Empty
                : $"正在扫描第 {p.ScannedRoots} / {p.TotalRoots} 个目录…（已发现 {p.EntryCount} 项）";
        });

        IsLibraryScanning = true;
        LibraryStatusText = "正在检索壁纸库…";
        LibraryProgressText = string.Empty;

        // 有效快照先取好再进线程池：扫描体不触碰 UI 状态（见上方并发说明）
        IReadOnlyList<WallpaperLibraryRootCache> previousCache = _appConfig.WallpaperLibraryCache;

        Task<WallpaperLibraryScanResult> scanTask = Task.Run(() => WallpaperEngineLibrary.ScanWithCache(
            roots, previousCache, forceFullScan, progress, cts.Token));
        _libraryScanTask = scanTask;

        try
        {
            WallpaperLibraryScanResult result = await scanTask;

            if (generation != _libraryScanGeneration)
            {
                return; // 期间又触发了新扫描：本次结果作废（新扫描会用同样的输入重算）
            }

            ReplaceLibraryItems(result.Entries);

            // 扫描结果写回 config：既是「重启秒开」的缓存，也是增量记账（按根签名）
            _appConfig.WallpaperLibraryCache = result.RootCaches.ToList();
            PersistConfig();

            int usable = LibraryItems.Count(i => i.IsUsable);
            LibraryStatusText = roots.Count == 0
                ? "没有可扫描的目录：请点「添加目录」指定存有壁纸 / 图片 / 视频的文件夹。"
                : $"检索到 {LibraryItems.Count} 项（可挂接 {usable}），来自 {roots.Count} 个目录。";
            LibraryProgressText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            if (generation == _libraryScanGeneration)
            {
                LibraryStatusText = "检索已取消。可点「检索壁纸库」重新开始。";
                LibraryProgressText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            if (generation == _libraryScanGeneration)
            {
                LibraryStatusText = $"检索失败：{ex.Message}";
                LibraryProgressText = string.Empty;
                ShowInfo(InfoBarSeverity.Error, $"壁纸库检索失败：{ex.Message}");
            }
        }
        finally
        {
            if (generation == _libraryScanGeneration)
            {
                IsLibraryScanning = false;
            }
        }
    }

    /// <summary>用新扫描结果整体替换库条目与过滤视图，并刷新空态。</summary>
    private void ReplaceLibraryItems(IReadOnlyList<WallpaperLibraryEntry> entries)
    {
        LibraryItems.Clear();
        foreach (WallpaperLibraryEntry entry in entries)
        {
            LibraryItems.Add(new WallpaperLibraryItemViewModel(entry, _dispatcherQueue));
        }

        RebuildFilteredLibrary();
        NotifyLibraryEmptyState();
    }

    /// <summary>重建库面板的过滤视图。</summary>
    private void RebuildFilteredLibrary()
    {
        string keyword = LibraryFilter?.Trim() ?? string.Empty;
        FilteredLibraryItems.Clear();

        foreach (WallpaperLibraryItemViewModel item in LibraryItems)
        {
            if (keyword.Length == 0
                || item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.KindLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.SourceLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                FilteredLibraryItems.Add(item);
            }
        }

        NotifyLibraryEmptyState();
    }

    /// <summary>
    /// 添加一个自定义扫描目录。<paramref name="pickedPath"/> 由 View 的文件夹选择器传入
    /// （ViewModel 不直接碰 WinUI 对话框，保持可测试）。持久化后立即强制全量重扫。
    /// </summary>
    /// <param name="pickedPath">用户选中的目录；null 表示取消。</param>
    [RelayCommand]
    private async Task AddLibraryRootAsync(string? pickedPath)
    {
        if (string.IsNullOrWhiteSpace(pickedPath))
        {
            return;
        }

        string normalized = pickedPath.Trim().Trim('"');

        if (_appConfig.WallpaperLibraryRoots.Any(
                r => string.Equals(r.Trim().Trim('"'), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            LibraryStatusText = $"该目录已在扫描列表里：{normalized}";
            return;
        }

        _appConfig.WallpaperLibraryRoots.Add(normalized);
        PersistConfig();
        RefreshRootRows();
        RefreshRootsText();

        // 新根没有旧缓存必然全扫；这里强制全量是为了让界面立刻拿到确定的新结果
        await RunLibraryScanAsync(forceFullScan: true);
    }

    /// <summary>移除一个自定义扫描目录（按界面显示的列表项），持久化后立即重扫。</summary>
    [RelayCommand]
    private async Task RemoveLibraryRootAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        int removed = _appConfig.WallpaperLibraryRoots.RemoveAll(r =>
            string.Equals(r.Trim().Trim('"'), path.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            PersistConfig();
            RefreshRootRows();
            RefreshRootsText();
            await RunLibraryScanAsync(forceFullScan: true);
        }
    }

    /// <summary>
    /// 把选中的库条目挂到当前选中的窗口上。
    /// </summary>
    /// <param name="item">库面板里的条目。</param>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ApplyLibraryItemAsync(WallpaperLibraryItemViewModel? item)
    {
        var selected = Selected;
        if (selected == null || item == null)
        {
            ShowInfo(InfoBarSeverity.Warning, "请先在左侧选择一个窗口，再从壁纸库里挑一项。");
            return;
        }

        if (!item.IsUsable)
        {
            ShowInfo(InfoBarSeverity.Warning, $"「{item.Title}」无法挂接：{item.UnusableReason}");
            return;
        }

        string path = item.Path;
        WallpaperFilePathText = path;
        WallpaperStatusText = "正在后台加载背景资源…";
        IsWallpaperLoading = true;
        WallpaperProgress = 0;

        try
        {
            selected.State.BackgroundPath = path;
            selected.State.Background = DetectBackgroundType(path);
            PersistState(selected.State);

            await _services.Wallpaper.UpdateWallpaperAsync(selected.Hwnd, path);

            WallpaperProgress = 100;
            WallpaperStatusText = $"背景已加载（{item.SourceLabel} · {item.KindLabel}：{item.Title}）。";
        }
        catch (Exception ex)
        {
            WallpaperStatusText = "背景加载失败。";
            ShowInfo(InfoBarSeverity.Error,
                $"「{item.Title}」加载失败：{ex.Message}");
        }
        finally
        {
            IsWallpaperLoading = false;
        }
    }

    /// <summary>开 / 关壁纸声音（全局热键与托盘菜单用）。</summary>
    private void ToggleWallpaperAudio()
    {
        WallpaperAudioEnabled = !WallpaperAudioEnabled;
        ShowInfo(InfoBarSeverity.Informational, WallpaperAudioEnabled
            ? "壁纸声音已开启。"
            : "壁纸声音已关闭（静音）。");
    }
}
