using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.Services;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Hosting;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.App.ViewModels;

/// <summary>圆角偏好选项（供 RadioButtons 绑定展示）。</summary>
/// <param name="Value">对应的 Core 圆角枚举值。</param>
/// <param name="DisplayName">界面显示名。</param>
public sealed record CornerOption(CornerPreference Value, string DisplayName);

/// <summary>
/// 主界面 ViewModel（严格 MVVM）：
/// - 窗口列表：调用 Core 的 <see cref="IWindowControlService.EnumerateTopLevelWindowsAsync"/>（异步，不阻塞 UI）；
/// - 窗口属性：置顶 / 透明 / 穿透 / 圆角通过 <see cref="IWindowControlService"/> 应用，
///   并通过 <see cref="IConfigurationService"/> 实时保存；
/// - 壁纸：文件选择 + 后台加载（<see cref="IWallpaperService"/>），UI 显示进度；
/// - 档案恢复管线（WP3）：枚举完成后对配置中已有档案的窗口后台自动恢复
///   （置顶 / 透明 / 穿透 / 圆角 / 背景），受「启动时自动恢复窗口状态」开关控制；
/// - 设置：启动自动恢复开关、WinTips 提示开关（控制 <see cref="WinCanvas.Wallpaper.WinTipsIntegrationService.IsEnabled"/>）、
///   FPS 性能监控悬浮显示。
/// 业务逻辑全部在本类，code-behind 只做服务装配。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<Task<string?>> _pickWallpaperFile;
    private readonly Func<IntPtr> _getOwnHwnd;
    private readonly FpsMonitor _fpsMonitor;

    /// <summary>内存中的配置对象（与 ConfigurationService 共享引用，变更后 Update 触发防抖落盘）。</summary>
    private AppConfig _appConfig = new();

    /// <summary>反向同步标志：从选中项刷新镜像属性时不再回写选中项。</summary>
    private bool _suppressSelectedSync;

    /// <summary>反向同步标志：初始化从配置回填「WinTips 提示」开关时不触发钩子装卸与回写。</summary>
    private bool _suppressWinTipsSync;

    /// <summary>反向同步标志：初始化从配置回填「启动自动恢复」开关值时，不触发回写与提示（WP3）。</summary>
    private bool _suppressAutoRestoreSync;

    /// <summary>「自动恢复已关闭」的提示每次会话只说一次（刷新会反复走同一条分支）。</summary>
    private bool _restoreDisabledNoticeShown;

    /// <summary>上一个选中项（用于换选时退订 PropertyChanged）。</summary>
    private WindowItemViewModel? _previousSelected;

    // —— WP3 档案恢复管线 ——
    //（并发说明：管线经任务链串行（同一时刻最多一条），判重账本用 ConcurrentDictionary 的
    //  原子 TryAdd / TryRemove——两者都只是既有并发语义之上的轻量记账，不新增独立锁体系；
    //  壁纸加载的串行化仍完全复用 WallpaperService 的 _updateGates / _gate / 会话字典。）

    /// <summary>
    /// 恢复管线任务链的尾节点：同一时刻最多一条管线在跑，新触发等上一条跑完再跑
    /// （启动初始化与手动刷新连续触发时不会并发恢复同一窗口）。
    /// 只在 UI 线程读写（InitializeAsync / RefreshAsync 均在 UI 线程触发管线）。
    /// </summary>
    private Task _restorePipelineTask = Task.CompletedTask;

    /// <summary>
    /// 本应用会话内已由恢复管线处理过的窗口句柄（幂等判重账本，WP3）：hwnd -> 0。
    /// 手动刷新会重建列表并再次触发管线，账本保证同一 hwnd 只恢复一次；
    /// 窗口关闭后重开会得到新 hwnd，自然重新恢复；恢复失败撤销标记允许重试。
    /// 线程安全：ConcurrentDictionary 原子操作（判重账本，非锁体系）。
    /// </summary>
    private readonly ConcurrentDictionary<IntPtr, byte> _pipelineProcessedHwnds = new();

    /// <summary>恢复管线逐个排队的间隔（毫秒）：防启动风暴（规格 WP3：200ms 间隔）。</summary>
    private const int RestoreDispatchIntervalMs = 200;

    /// <summary>恢复管线的单窗口并发上限（规格 WP3：并发上限 2）。</summary>
    private const int RestoreMaxConcurrency = 2;

    /// <summary>ViewModel 构造时刻（Tick 毫秒）：启动耗时打点的基准（「列表可用」「恢复完成」均相对它）。</summary>
    private readonly long _createdTickCount = Environment.TickCount64;

    public MainViewModel(
        AppServices services,
        DispatcherQueue dispatcherQueue,
        Func<Task<string?>> pickWallpaperFile,
        Func<IntPtr> getOwnHwnd)
    {
        _services = services;
        _dispatcherQueue = dispatcherQueue;
        _pickWallpaperFile = pickWallpaperFile;
        _getOwnHwnd = getOwnHwnd;
        _fpsMonitor = new FpsMonitor(dispatcherQueue);
        SelectedCorner = CornerOptions[0];

        // WinTips：诊断日志与本地回退提示 → InfoBar
        //（是否真正弹出浮窗 / 抛回退事件受「WinTips 提示」开关控制，见 OnWinTipsEnabledChanged）
        _services.WinTips.LogMessage += message =>
            ShowInfo(InfoBarSeverity.Informational, $"WinTips：{message}");
        _services.WinTips.NumLockTipRequested += isLocked =>
            ReportNumLockFallback(isLocked);

        // 壁纸：服务上报（pkg 复杂场景降级提示、加载错误、A2 自动透明提示等）→ InfoBar
        //（工作线程触发，ShowInfo 内部封送）
        _services.Wallpaper.StatusReported += message =>
            ShowInfo(InfoBarSeverity.Informational, message);

        // 壁纸：宿主挂接模式确定 / 变化（WP1-A）→ 壁纸状态文本可见「嵌入模式 / 跟随模式」
        //（宿主 pump 线程或线程池触发，封送回 UI 线程再写可绑定属性）
        _services.Wallpaper.SessionHostModeChanged += (_, mode) =>
            _dispatcherQueue.TryEnqueue(() =>
                WallpaperStatusText = mode == HostAttachMode.Embed
                    ? "背景挂接：嵌入模式（宿主已嵌入目标窗口客户区）。"
                    : "背景挂接：跟随模式（宿主位于目标窗口身后，目标需轻度透明）。");

        // FPS：每秒取样结果 → 悬浮指示文本
        _fpsMonitor.FpsChanged += (_, fps) =>
            _dispatcherQueue.TryEnqueue(() => FpsText = $"{fps} FPS");
    }

    /// <summary>窗口列表。</summary>
    public ObservableCollection<WindowItemViewModel> Windows { get; } = new();

    /// <summary>
    /// 左侧列表实际绑定的是这一份【过滤视图】（v0.2.0 新增）。
    ///
    /// 为什么需要：迁移前的界面把全部可见顶层窗口直接铺在 ListView 里，没有任何检索手段。
    /// 实测一次枚举就有 20+ 项（还混着 ApplicationFrameHost、TextInputHost 这类
    /// 用户根本不会去操作的框架窗口），窗口一多就只能靠滚动找 ——
    /// 对一个「随时调一下某个窗口」的工具，这是最直接的体验瓶颈。
    ///
    /// <see cref="Windows"/> 仍是完整数据源（恢复管线、选中项匹配都基于它），
    /// 本集合只决定「显示哪些」，重建时整体替换，避免增量同步引入的不一致。
    /// </summary>
    public ObservableCollection<WindowItemViewModel> FilteredWindows { get; } = new();

    /// <summary>
    /// 窗口搜索关键字：匹配标题、进程名或类名（不区分大小写）。
    /// 空串 = 显示全部。
    /// </summary>
    [ObservableProperty]
    public partial string WindowFilter { get; set; } = string.Empty;

    /// <summary>过滤后的条目数（界面显示「N / M」用）。</summary>
    [ObservableProperty]
    public partial int FilteredCount { get; set; }

    /// <summary>按当前关键字重建过滤视图。</summary>
    private void RebuildFiltered()
    {
        string keyword = WindowFilter?.Trim() ?? string.Empty;

        FilteredWindows.Clear();

        if (keyword.Length == 0)
        {
            foreach (var item in Windows)
            {
                FilteredWindows.Add(item);
            }
        }
        else
        {
            foreach (var item in Windows)
            {
                if (item.MatchesFilter(keyword))
                {
                    FilteredWindows.Add(item);
                }
            }
        }

        FilteredCount = FilteredWindows.Count;
    }

    partial void OnWindowFilterChanged(string value)
    {
        // 搜索框每敲一个字都会进来一次。重建本身是 O(n) 的集合替换，
        // 当前规模（几十到几百窗口）下无需防抖；若将来窗口数上千再考虑节流。
        RebuildFiltered();
    }

    /// <summary>
    /// 刷新按钮是否可点（= 不在刷新中）。
    /// 迁移前按钮从不置灰，连点会把 N 条枚举管线排进队列；
    /// 用一个正向语义的属性表达「可以点」，比在 XAML 里做布尔取反更清楚。
    /// </summary>
    public bool CanRefresh => !IsRefreshing;

    partial void OnIsRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefresh));
    }

    /// <summary>
    /// 「没有匹配的窗口」提示的可见性：仅在【有窗口但全被过滤掉】时显示。
    /// 区分「枚举结果为空」与「搜索没命中」两种空白，避免用户以为程序坏了。
    /// </summary>
    public Visibility EmptyFilterVisibility =>
        Windows.Count > 0 && FilteredWindows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>过滤视图变化后刷新提示可见性（由 RebuildFiltered 调用）。</summary>
    partial void OnFilteredCountChanged(int value)
    {
        OnPropertyChanged(nameof(EmptyFilterVisibility));
    }

    /// <summary>圆角偏好候选（无 / 大圆角 / 小圆角）。</summary>
    public IReadOnlyList<CornerOption> CornerOptions { get; } = new[]
    {
        new CornerOption(CornerPreference.DoNotRound, "无"),
        new CornerOption(CornerPreference.Round, "大圆角"),
        new CornerOption(CornerPreference.RoundSmall, "小圆角"),
    };

    // ---------- 列表 / 选中 ----------

    [ObservableProperty]
    public partial WindowItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearWallpaperCommand))]
    public partial bool HasSelection { get; set; }

    // ---------- 选中项的镜像属性（避免把控件 TwoWay 直接绑到可空的 Selected 上，
    // 防止换选时把旧控件值误写进新选中项） ----------

    [ObservableProperty]
    public partial string SelectedDisplayName { get; set; } = "（未选择窗口）";

    [ObservableProperty]
    public partial string SelectedSubtitle { get; set; } = "在左侧列表选择一个窗口后，可在此调整其属性。";

    [ObservableProperty]
    public partial string SelectedHwndText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SelectedIsTopmost { get; set; }

    [ObservableProperty]
    public partial double SelectedAlpha { get; set; } = 255;

    [ObservableProperty]
    public partial bool SelectedClickThrough { get; set; }

    [ObservableProperty]
    public partial CornerOption SelectedCorner { get; set; }

    /// <summary>透明度滑块的数值展示（0~255）。</summary>
    public string SelectedAlphaText => $"{SelectedAlpha:0} / 255";

    // ---------- 壁纸 ----------

    [ObservableProperty]
    public partial string WallpaperFilePathText { get; set; } = "（未选择）";

    [ObservableProperty]
    public partial bool IsWallpaperLoading { get; set; }

    /// <summary>壁纸加载进度（0~100，ProgressBar 用）。</summary>
    [ObservableProperty]
    public partial double WallpaperProgress { get; set; }

    [ObservableProperty]
    public partial string WallpaperStatusText { get; set; } = "选择图片 / 视频 / .pkg 文件作为所选窗口的背景。";

    // ---------- 设置 ----------

    /// <summary>
    /// 设置区「当前权限」状态文本（WP5 UIPI 预检）：进程启动时经 <see cref="ProcessElevation"/>
    /// 检测一次并缓存，运行期不变（提权只发生在进程启动时，无需响应式刷新）。
    /// 用途：用户据此判断「目标窗口操作被 UIPI 拒绝」时是否需要以管理员身份重启本应用。
    /// </summary>
    public string ElevationStatusText { get; } = ProcessElevation.IsElevated
        ? "当前权限：管理员（提升）。可对以管理员运行的窗口执行置顶 / 透明 / 穿透与背景挂接。"
        : "当前权限：标准（未提升）。对以管理员运行的窗口操作会被系统拒绝并提示引导。";

    /// <summary>「启动自动恢复」开关的存储字段。</summary>
    private bool _autoRestoreOnStartup;

    /// <summary>
    /// 设置区开关「启动时自动恢复窗口状态」（WP3）：镜像 <see cref="AppConfig.AutoRestoreOnStartup"/>。
    /// 手写属性而非 [ObservableProperty]：后者在 WinUI 3 场景会生成 MVVMTK0045 警告
    /// （AOT 不兼容、建议改用分部属性），新增一个字段就会突破 WP0 的 26 条 warning 基线门禁；
    /// 这里复用 ObservableObject.SetProperty 完成等价的变更通知，零新增警告。
    /// </summary>
    public bool AutoRestoreOnStartup
    {
        get => _autoRestoreOnStartup;
        set
        {
            if (SetProperty(ref _autoRestoreOnStartup, value))
            {
                OnAutoRestoreOnStartupChanged(value);
            }
        }
    }

    /// <summary>开关变化：写回内存配置并走既有防抖落盘通道；初始化回填（_suppressAutoRestoreSync）时静默。</summary>
    private void OnAutoRestoreOnStartupChanged(bool value)
    {
        if (_suppressAutoRestoreSync)
        {
            return; // 初始化从配置回填：值本就来自配置，不回写、不提示
        }

        _appConfig.AutoRestoreOnStartup = value;
        _services.Configuration.Update(_appConfig);
        ShowInfo(InfoBarSeverity.Informational, value
            ? "启动自动恢复已开启：下次启动应用时，已保存档案的窗口属性与背景将自动恢复。"
            : "启动自动恢复已关闭：启动与手动刷新都不再自动恢复窗口状态；重新开启后重启应用即可恢复。");
    }

    [ObservableProperty]
    public partial bool WinTipsEnabled { get; set; }

    [ObservableProperty]
    public partial bool FpsOverlayEnabled { get; set; }

    [ObservableProperty]
    public partial string FpsText { get; set; } = "-- FPS";

    // ---------- InfoBar 提示 ----------

    [ObservableProperty]
    public partial bool IsInfoBarOpen { get; set; }

    [ObservableProperty]
    public partial string InfoBarMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity InfoBarSeverity { get; set; } = InfoBarSeverity.Informational;

    // ---------- 可见性（供 x:Bind 使用） ----------

    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FpsOverlayVisibility => FpsOverlayEnabled ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 初始化 / 刷新 ----------

    /// <summary>启动初始化：加载配置 → 回填设置开关 → 首次枚举窗口（枚举完成即触发档案恢复管线，WP3）。</summary>
    public async Task InitializeAsync()
    {
        // 启动打点（WP3 DoD 证据基准）：进程启动时刻独立于 ViewModel 构造时刻（两者差值 =
        // WinUI 宿主初始化耗时），验收按「进程启动 → 恢复完成」口径用本行与管线完成行的时间戳计算。
        // Process.StartTime 极端情况下可能抛 Win32Exception：只丢这条打点，不让初始化失败。
        try
        {
            RestoreDiagnostics.Log($"启动初始化开始：PID={Environment.ProcessId}，" +
                                   $"进程启动于 {Process.GetCurrentProcess().StartTime:HH:mm:ss.fff}" +
                                   $"（自 ViewModel 构造 {Environment.TickCount64 - _createdTickCount} ms）。");
        }
        catch (Exception ex)
        {
            RestoreDiagnostics.Log($"启动初始化开始：PID={Environment.ProcessId}（进程启动时刻不可用：{ex.Message}）。");
        }

        _appConfig = await _services.Configuration.LoadAsync();

        // 把配置里的「启动自动恢复」回填到界面开关（抑制回写：值本就来自配置，静默赋值不出提示）
        _suppressAutoRestoreSync = true;
        AutoRestoreOnStartup = _appConfig.AutoRestoreOnStartup;
        _suppressAutoRestoreSync = false;

        // 回填「WinTips 提示」开关（v0.2.0 才真正有持久化）。
        // 抑制标志必须打开：setter 会装/卸低级键盘钩子并弹 InfoBar，
        // 启动阶段这是「恢复用户上次的选择」，不是「用户刚刚改了这个开关」。
        _suppressWinTipsSync = true;
        WinTipsEnabled = _appConfig.WinTipsIndicatorEnabled;
        _suppressWinTipsSync = false;
        if (_appConfig.WinTipsIndicatorEnabled)
        {
            // 回填时钩子还没装（setter 被抑制了），这里补一次真正的启动动作
            _services.WinTips.IsEnabled = true;
            _services.KeyboardIndicator.Start();
        }

        // 全局热键：配置此刻才真正加载完，界面行要按配置重建、并按配置注册。
        // 修掉的接线遗漏：AttachShell 在构造函数里就跑（那时 _appConfig 还是默认值），
        // 而注册只在用户点「应用」时发生 → 程序刚启动的整段会话里【一个热键都没注册】，
        // 表现为「设置界面上写着 Ctrl+Alt+C，按了却毫无反应」。
        InitializeHotkeyBindings();

        // 壁纸库与声音：接配置里的扫描根与音频开关，配了根就自动检索一次
        InitializeWallpaperLibrary();

        await RefreshAsync();
    }

    /// <summary>刷新窗口列表：后台线程枚举，完成后重建列表（不阻塞 UI 线程）。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var ownHwnd = _getOwnHwnd();
            var previous = Selected;
            var list = await _services.WindowControl.EnumerateTopLevelWindowsAsync();

            Windows.Clear();
            WindowItemViewModel? restored = null;
            foreach (var info in list)
            {
                // 排除应用自身主窗口，避免在界面上误操作自己（如把自己设成穿透）
                if (info.Hwnd == ownHwnd)
                {
                    continue;
                }

                // 只查找已保存的档案，不主动为每个窗口新建档案（落盘推迟到用户实际修改时）
                var state = _appConfig.Windows.FirstOrDefault(w => w.Key == info.Key)
                            ?? new WindowState
                            {
                                Key = info.Key,
                                HwndHash = info.HwndHash,
                                ProcessName = info.ProcessName,
                                Title = info.Title,
                                ProcessId = info.ProcessId,
                            };
                var item = new WindowItemViewModel(
                    info, state, _services.WindowControl,
                    PersistState,
                    message => ShowInfo(InfoBarSeverity.Error, message));
                Windows.Add(item);

                // 刷新后尽量按句柄 / 组合键恢复之前的选中项
                if (previous is not null && (info.Hwnd == previous.Hwnd || info.Key == previous.Info.Key))
                {
                    restored = item;
                }
            }

            Selected = restored;

            // 列表重建后同步一次过滤视图（v0.2.0 新增窗口搜索）
            RebuildFiltered();

            // 列表可用打点（WP3 DoD「启动到列表可用 < 1.5s」的打点证据）：列表此刻已重建可交互；
            // 恢复管线在本行之后才后台触发，不阻塞列表构建与首帧渲染。
            RestoreDiagnostics.Log($"窗口列表重建完成：{Windows.Count} 项（配置档案 {_appConfig.Windows.Count} 份），" +
                                   $"自 ViewModel 构造 {Environment.TickCount64 - _createdTickCount} ms —— 列表可用。");

            // —— 档案恢复管线（WP3）：对配置中已有档案的窗口后台逐个恢复（置顶/透明/穿透/圆角/背景）。
            //    与列表构建解耦：本方法只入队即返回，恢复全程后台进行。
            QueueRestorePipeline(Windows);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"枚举窗口失败：{ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // ---------- WP3 档案恢复管线 ----------

    /// <summary>
    /// 触发档案恢复管线（WP3）：对「配置中已有档案」的窗口，按 200ms 间隔逐个排队
    /// （线程池执行，防启动风暴；并发上限 2）恢复置顶 / 透明 / 穿透 / 圆角 / 背景。
    /// 与 UI 列表构建解耦：本方法只入队即返回（列表先可用，恢复后台进行，不阻塞首帧渲染）。
    /// 管线经任务链串行（_restorePipelineTask）：启动 / 手动刷新连续触发时等上一条跑完再跑；
    /// 单窗口级幂等由 _pipelineProcessedHwnds（本会话判重账本）与壁纸会话字典
    /// （IWallpaperService.IsBackgroundActive）共同保证，不新增独立锁体系。
    /// 须在 UI 线程调用（_restorePipelineTask 与 _appConfig 的访问均以 UI 线程为准）。
    /// </summary>
    private void QueueRestorePipeline(IReadOnlyList<WindowItemViewModel> items)
    {
        if (!_appConfig.AutoRestoreOnStartup)
        {
            // 开关关闭：跳过整个管线（DoD「关闭后零恢复动作」的日志证据即本行）。
            // 档案仍保留在配置中不受影响；重新开启开关后重启应用即可恢复。
            RestoreDiagnostics.Log($"档案恢复管线：AutoRestoreOnStartup=false，跳过全部恢复（本次枚举 {items.Count} 项）。");

            // 但【必须在界面上说出来】：档案里存着背景壁纸与窗口属性，开关却关着，
            // 用户看到的就是「挂好的壁纸重启后全没了，什么功能都像失效」——
            // 而日志在 %LOCALAPPDATA% 里，没人会去翻。沉默不是中性的，它会被读成故障。
            // 每次会话只提一次：本方法在每次刷新都会被调用，反复弹提示同样是噪音。
            if (!_restoreDisabledNoticeShown && _appConfig.Windows.Count > 0)
            {
                _restoreDisabledNoticeShown = true;
                ShowInfo(InfoBarSeverity.Informational,
                    $"「启动时自动恢复」已关闭：配置里存有 {_appConfig.Windows.Count} 份窗口档案"
                    + "（含背景壁纸），启动与刷新时都不会应用。需要恢复请在「设置」页打开该开关。");
            }

            return;
        }

        // 只恢复「配置中已有档案」的窗口：有档案的 State 是配置列表里的同一对象（引用相等），
        // 无档案窗口在枚举时新建的临时 State 不在列表里，天然被过滤（也避免为默认值窗口空跑）。
        var archived = items.Where(i => _appConfig.Windows.Contains(i.State)).ToList();

        // 任务链串行：与上一条管线汇合后再跑本条，杜绝两条管线并发重复恢复
        var previous = _restorePipelineTask;
        _restorePipelineTask = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                await RunRestorePipelineCoreAsync(archived).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 管线是后台兜底路径：任何异常只记录（UI 提示由单窗口失败路径负责），绝不反噬刷新流程
                RestoreDiagnostics.Log($"档案恢复管线：异常终止：{ex}");
            }
        });
    }

    /// <summary>
    /// 恢复管线主体（在线程池后台线程执行）：待恢复档案按 <see cref="RestoreDispatchIntervalMs"/>
    /// 间隔逐个派发，同时在途恢复不超过 <see cref="RestoreMaxConcurrency"/> 个；
    /// 单窗口的属性应用封送 UI 线程（见 <see cref="RestorePropertiesOnUiThreadAsync"/>），
    /// 壁纸加载走 WallpaperService 内部的线程池与 _updateGates 串行闸门。
    /// </summary>
    private async Task RunRestorePipelineCoreAsync(List<WindowItemViewModel> items)
    {
        if (items.Count == 0)
        {
            RestoreDiagnostics.Log("档案恢复管线：无可恢复档案（0 个），空跑结束。");
            return;
        }

        var startedAt = Environment.TickCount64;
        RestoreDiagnostics.Log($"档案恢复管线：开始，待恢复档案 {items.Count} 个" +
                               $"（{RestoreDispatchIntervalMs}ms 间隔排队 / 并发上限 {RestoreMaxConcurrency}）。");

        // 窗口级并发上限（BCL 信号量）：单窗口内部的壁纸更新串行化仍由服务层 _updateGates 负责，
        // 此处限的是「同时恢复几个窗口」，避免启动瞬间对多个目标窗口同时施压。
        using var throttle = new SemaphoreSlim(RestoreMaxConcurrency, RestoreMaxConcurrency);
        var workers = new List<Task>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            // 200ms 间隔逐个排队：第 1 个立即派发，其后每个间隔 200ms（防启动风暴）
            if (i > 0)
            {
                await Task.Delay(RestoreDispatchIntervalMs).ConfigureAwait(false);
            }

            await throttle.WaitAsync().ConfigureAwait(false);
            workers.Add(Task.Run(async () =>
            {
                try
                {
                    await RestoreOneAsync(item).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        RestoreDiagnostics.Log($"档案恢复管线：全部完成，本条管线耗时 {Environment.TickCount64 - startedAt} ms。");
    }

    /// <summary>
    /// 恢复单个窗口（在线程池后台线程执行）：属性（封送 UI 线程）→ 壁纸（服务层线程池）。
    /// 幂等语义（WP3）：本会话已处理过的 hwnd 直接跳过（_pipelineProcessedHwnds 判重）；
    /// 背景会话已存在（会话字典判重）跳过重复加载；背景文件缺失只警告、档案保留
    /// （用户可能只是临时移动文件，规格明确不清除档案）。
    /// </summary>
    private async Task RestoreOneAsync(WindowItemViewModel item)
    {
        var hwnd = item.Hwnd;
        var state = item.State;

        // —— 本会话判重（WP3）：TryAdd 原子占位，同一 hwnd 只恢复一次
        if (!_pipelineProcessedHwnds.TryAdd(hwnd, 0))
        {
            RestoreDiagnostics.Log($"恢复管线：hwnd=0x{hwnd:X}「{item.DisplayName}」本会话已恢复过，幂等跳过。");
            return;
        }

        var startedAt = Environment.TickCount64;
        RestoreDiagnostics.Log(
            $"恢复管线：hwnd=0x{hwnd:X}「{item.DisplayName}」开始恢复" +
            $"（置顶={state.IsTopmost}，透明={state.Alpha}，穿透={state.ClickThrough}，" +
            $"圆角={state.Corner}，背景={(state.BackgroundPath ?? "无")}）。");

        try
        {
            // —— 1) 属性恢复：直接复用现有 RestoreAsync（幂等置顶语义已就绪，规格明确勿另写一份），
            //      经封送在 UI 线程写入可绑定属性（背景见 RestorePropertiesOnUiThreadAsync 注释）
            await RestorePropertiesOnUiThreadAsync(item).ConfigureAwait(false);

            // —— 2) 壁纸恢复：档案背景路径非空且文件存在 → 交给既有 UpdateWallpaperAsync
            //      （内部经 _updateGates 串行 + 线程池加载，解码 / 解包重活不占 UI 线程）
            var backgroundPath = state.BackgroundPath;
            if (string.IsNullOrEmpty(backgroundPath))
            {
                RestoreDiagnostics.Log($"恢复管线：hwnd=0x{hwnd:X} 无背景档案，属性恢复完成" +
                                       $"（单窗口 {Environment.TickCount64 - startedAt} ms）。");
            }
            else if (!WallpaperFormats.IsAvailableBackgroundPath(backgroundPath))
            {
                // 背景资源缺失：警告但【不清除档案】（用户可能临时移动文件后放回，规格 WP3 明确保留）。
                // 判定必须走 IsAvailableBackgroundPath 而不是 File.Exists ——
                // Wallpaper Engine 项目是【目录】，File.Exists 对目录恒为 false，
                // 用目录挂的壁纸会在重启后全被误判成「文件已不存在」。
                ShowInfo(InfoBarSeverity.Warning,
                    $"背景资源已不存在：{backgroundPath}，档案保留；" +
                    "请把文件（或项目目录）放回原位置后重启应用，或重新为该窗口选择背景。");
                RestoreDiagnostics.Log($"恢复管线：hwnd=0x{hwnd:X} 背景资源已不存在（{backgroundPath}），已警告且档案保留。");
            }
            else if (_services.Wallpaper.IsBackgroundActive(hwnd, backgroundPath))
            {
                // 会话字典判重（规格 WP3：用会话字典判重）：该窗口已挂着同一背景文件，跳过重复加载
                RestoreDiagnostics.Log($"恢复管线：hwnd=0x{hwnd:X} 背景会话已存在（{Path.GetFileName(backgroundPath)}），" +
                                       $"壁纸恢复幂等跳过（单窗口 {Environment.TickCount64 - startedAt} ms）。");
            }
            else
            {
                await _services.Wallpaper.UpdateWallpaperAsync(hwnd, backgroundPath).ConfigureAwait(false);
                RestoreDiagnostics.Log($"恢复管线：hwnd=0x{hwnd:X} 恢复完成（属性 + 背景 {Path.GetFileName(backgroundPath)}，" +
                                       $"单窗口 {Environment.TickCount64 - startedAt} ms）。");
            }
        }
        catch (Exception ex)
        {
            // 失败撤销判重：本轮没恢复成功，允许下次刷新重试（不把失败状态当作已恢复）；
            // UIPI 专项（WP5）：错误 5 时经 DescribeFailure 给出「以管理员身份重启」引导
            _pipelineProcessedHwnds.TryRemove(hwnd, out _);
            var message = ProcessElevation.DescribeFailure(ex, $"自动恢复窗口「{item.DisplayName}」");
            RestoreDiagnostics.Log($"恢复管线：hwnd=0x{hwnd:X} 恢复失败，已撤销判重标记允许重试：{ex.Message}");
            ShowInfo(InfoBarSeverity.Error,
                $"{message}可点「刷新」再次触发恢复，或手动选中该窗口调整。");
        }
    }

    /// <summary>
    /// 在 UI 线程执行单窗口的属性恢复（WP3）。
    /// WindowItemViewModel.RestoreAsync 会把恢复结果写入其可绑定属性（ObservableObject 变更通知），
    /// 变更通知必须在 UI 线程发出（x:Bind 的界面更新不做跨线程封送，后台线程发通知可能直接崩溃）；
    /// 其内部的 Win32 调用本就由 WindowControlService 派发线程池执行，UI 线程上只停留毫秒级的
    /// 属性同步，await 之间全部让出调度。管线的排队 / 节流 / 壁纸加载仍在后台线程——
    /// 规格「禁止在 UI 线程同步串行执行全部恢复」指的是不让 UI 线程阻塞式逐个跑完，
    /// 本实现按 200ms 间隔从后台管线逐个封送，UI 线程任意时刻只持有一个待完成的轻量恢复片。
    /// </summary>
    private Task RestorePropertiesOnUiThreadAsync(WindowItemViewModel item)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await item.RestoreAsync();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    // RestoreAsync 内部已吞业务异常（转 _reportError 提示），能到这里的是意外情况
                    //（如属性通知链路异常）：转交管线按失败处理（撤销判重允许重试）
                    completion.TrySetException(ex);
                }
            }))
        {
            return completion.Task;
        }

        // DispatcherQueue 已关闭（应用退出中）：恢复无从执行，按取消收场，不把管线拖死
        completion.TrySetCanceled();
        return completion.Task;
    }

    // ---------- 选中项切换与属性联动 ----------

    partial void OnSelectedChanged(WindowItemViewModel? value)
    {
        if (_previousSelected is not null)
        {
            _previousSelected.PropertyChanged -= OnSelectedItemPropertyChanged;
        }
        _previousSelected = value;

        _suppressSelectedSync = true;
        HasSelection = value is not null;
        SelectedDisplayName = value?.DisplayName ?? "（未选择窗口）";
        SelectedSubtitle = value?.Subtitle ?? "在左侧列表选择一个窗口后，可在此调整其属性。";
        SelectedHwndText = value?.HwndText ?? string.Empty;
        SelectedIsTopmost = value?.IsTopmost ?? false;
        SelectedAlpha = value?.Alpha ?? 255;
        SelectedClickThrough = value?.ClickThrough ?? false;
        SelectedCorner = value is null
            ? CornerOptions[0]
            : CornerOptions.FirstOrDefault(o => o.Value == value.Corner) ?? CornerOptions[0];
        WallpaperFilePathText = value?.State.BackgroundPath ?? "（未选择）";
        _suppressSelectedSync = false;

        if (value is not null)
        {
            value.PropertyChanged += OnSelectedItemPropertyChanged;
            // 选中即恢复该窗口已保存的属性档案（置顶/透明/穿透/圆角）
            _ = value.RestoreAsync();
        }
    }

    /// <summary>选中项属性被服务结果更新后（如置顶实际结果），把真实值同步回镜像属性。</summary>
    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectedSync || Selected is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(WindowItemViewModel.IsTopmost):
                _suppressSelectedSync = true;
                SelectedIsTopmost = Selected.IsTopmost;
                _suppressSelectedSync = false;
                break;
            case nameof(WindowItemViewModel.Alpha):
                _suppressSelectedSync = true;
                SelectedAlpha = Selected.Alpha;
                _suppressSelectedSync = false;
                break;
            case nameof(WindowItemViewModel.ClickThrough):
                _suppressSelectedSync = true;
                SelectedClickThrough = Selected.ClickThrough;
                _suppressSelectedSync = false;
                break;
            case nameof(WindowItemViewModel.Corner):
                _suppressSelectedSync = true;
                SelectedCorner = CornerOptions.FirstOrDefault(o => o.Value == Selected.Corner) ?? CornerOptions[0];
                _suppressSelectedSync = false;
                break;
        }
    }

    // 控件 → 选中项（转发给 Core 服务应用 + 实时保存）
    partial void OnSelectedIsTopmostChanged(bool value)
    {
        if (!_suppressSelectedSync)
        {
            _ = Selected?.SetTopmostAsync(value);
        }
    }

    partial void OnSelectedAlphaChanged(double value)
    {
        OnPropertyChanged(nameof(SelectedAlphaText));
        if (!_suppressSelectedSync)
        {
            _ = Selected?.SetAlphaAsync((byte)Math.Round(value));
        }
    }

    partial void OnSelectedClickThroughChanged(bool value)
    {
        if (!_suppressSelectedSync)
        {
            _ = Selected?.SetClickThroughAsync(value);
        }
    }

    partial void OnSelectedCornerChanged(CornerOption value)
    {
        if (!_suppressSelectedSync && value is not null)
        {
            _ = Selected?.SetCornerAsync(value.Value);
        }
    }

    partial void OnHasSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    // ---------- 壁纸 ----------

    /// <summary>选择背景文件并后台加载（图片 / 视频 / .pkg）。</summary>
    [RelayCommand]
    private async Task PickWallpaperAsync()
    {
        var item = Selected;
        if (item is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "请先在左侧选择一个窗口。");
            return;
        }

        string? path;
        try
        {
            path = await _pickWallpaperFile();
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"打开文件选择器失败：{ex.Message}。可重试一次；" +
                                            "仍失败请重启应用后再次选择。");
            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            return; // 用户取消
        }

        WallpaperFilePathText = path;
        WallpaperStatusText = "正在后台加载背景资源…";
        IsWallpaperLoading = true;
        WallpaperProgress = 0;
        try
        {
            // 先把选择结果写入窗口档案并实时保存
            item.State.BackgroundPath = path;
            item.State.Background = DetectBackgroundType(path);
            PersistState(item.State);

            // 后台加载（真实 WallpaperService：图片解码缩放 / 视频 MF 解码 / repkg 解包均在线程池执行）
            await _services.Wallpaper.UpdateWallpaperAsync(item.Hwnd, path);
            WallpaperProgress = 100;
            WallpaperStatusText = $"背景已加载（类型：{DetectBackgroundType(path)}）。";
        }
        catch (Exception ex)
        {
            WallpaperStatusText = "背景加载失败。";
            ShowInfo(InfoBarSeverity.Error, $"背景加载失败：{ex.Message}。" +
                                            "可重试一次；仍失败请更换文件（支持图片 / 视频 / .pkg）或检查文件是否被占用。");
        }
        finally
        {
            IsWallpaperLoading = false;
        }
    }

    /// <summary>
    /// 按扩展名推断背景类型。口径统一取 <see cref="WallpaperFormats"/>（WP5 起全仓库单一事实来源，
    /// 本方法只做 WallpaperKind → Core BackgroundType 的枚举映射，不再持有任何字面扩展名列表）。
    /// </summary>
    private static BackgroundType DetectBackgroundType(string path)
    {
        // Wallpaper Engine 项目目录：按扩展名判会返回 None（目录名常是纯数字工坊 Id，
        // 根本没有扩展名），于是档案里记成「无背景」、恢复管线也不会重新挂它。
        // 这里显式归到 Pkg（同为 WE 壁纸资源）。
        if (Directory.Exists(path)
            && WinCanvas.Wallpaper.Loaders.WallpaperProjectLoader.IsProjectDirectory(path))
        {
            return BackgroundType.Pkg;
        }

        return WallpaperFormats.DetectKind(path) switch
        {
            WallpaperKind.Image => BackgroundType.Image,
            WallpaperKind.Video => BackgroundType.Video,
            WallpaperKind.Pkg => BackgroundType.Pkg,
            _ => BackgroundType.None,
        };
    }

    // ---------- 设置 ----------

    partial void OnWinTipsEnabledChanged(bool value)
    {
        // 开关同时控制两件事（WP2）：
        // 1) WinTipsIntegrationService.IsEnabled —— 浮窗 / 本地回退事件是否对外呈现；
        // 2) NumLock LL 键盘钩子启停 —— 开→Start() 装钩，关→Stop() 卸钩。
        // Start/Stop 内部把建线程、装/卸钩、投递全部派发线程池，UI 线程零 P/Invoke、零阻塞；
        // 钩子侧只上报「状态变化」（去抖），重复状态不会产生提示风暴。

        // 启动回填走抑制分支：只恢复状态，不重复装钩、不弹「已开启」提示
        if (_suppressWinTipsSync)
        {
            return;
        }

        _services.WinTips.IsEnabled = value;
        if (value)
            _services.KeyboardIndicator.Start();
        else
            _services.KeyboardIndicator.Stop();

        // v0.2.0：这个偏好现在会持久化（此前配置里没有字段，重启即丢）
        _appConfig.WinTipsIndicatorEnabled = value;
        PersistConfig();

        ShowInfo(InfoBarSeverity.Informational, value
            ? "WinTips 提示已开启：NumLock 钩子安装中，按下 NumLock 后将弹出浮窗（未部署 WinTips.exe 时在此回退提示）。该选择已保存，下次启动自动生效。"
            : "WinTips 提示已关闭：NumLock 钩子已卸载，按键不再产生任何提示。该选择已保存。");
    }

    partial void OnFpsOverlayEnabledChanged(bool value)
    {
        _fpsMonitor.Enabled = value;
        OnPropertyChanged(nameof(FpsOverlayVisibility));
    }

    // ---------- 配置持久化 ----------

    /// <summary>
    /// 档案集合的变更锁。
    ///
    /// 为什么需要：<c>AppConfig.Windows</c> 是 <see cref="List{T}"/>，不是线程安全集合，
    /// 而它有两个写入方——UI 线程（开关、滑块、选文件）与 WP3 恢复管线的线程池线程
    /// （并发上限 2，RestoreAsync 回读真实状态后写回档案）。两个线程同时 Add 会
    /// 破坏 List 内部数组（元素丢失、索引越界，甚至 size 与数组不一致后在
    /// ConfigurationService 序列化时抛异常）。
    /// ConfigurationService 侧已改为「锁内序列化成不可变快照」，本锁负责的是
    /// 快照生成【之前】的这段共享可变状态。
    /// </summary>
    private readonly object _configGate = new();

    /// <summary>把窗口档案并入配置并实时保存（ConfigurationService 内部防抖批量落盘）。</summary>
    private void PersistState(WindowState state)
    {
        lock (_configGate)
        {
            if (!_appConfig.Windows.Contains(state))
            {
                _appConfig.Windows.Add(state);
            }

            _services.Configuration.Update(_appConfig);
        }
    }

    /// <summary>
    /// 非档案路径的配置写入（全局开关等）同样走配置锁：
    /// 它与 PersistState 共享 _appConfig 实例，混用两套互斥等于没有互斥。
    /// </summary>
    private void PersistConfig()
    {
        lock (_configGate)
        {
            _services.Configuration.Update(_appConfig);
        }
    }

    /// <summary>
    /// 启动初始化失败的对外报告（由 MainWindow 的兜底 catch 调用）。
    /// 旧实现里这条路径完全静默：InitializeAsync 抛异常 → 即发即忘的 Task 无人观察 →
    /// 用户面对一个永远空白、也没有任何提示的界面。
    /// </summary>
    /// <param name="exception">失败原因。</param>
    public void ReportStartupFailure(Exception exception)
    {
        ShowInfo(InfoBarSeverity.Error,
            $"启动初始化失败：{exception.Message}。窗口列表与已保存档案可能未恢复，" +
            "可点「刷新」重试；详情见 %LOCALAPPDATA%\\WinCanvas\\logs\\crash.log。");
    }

    // ---------- 提示 ----------

    /// <summary>在 InfoBar 显示一条提示（切回 UI 线程）。</summary>
    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            InfoBarSeverity = severity;
            InfoBarMessage = message;
            IsInfoBarOpen = true;
        });
    }

    /// <summary>
    /// 释放：先收外壳（注销全局热键、摘托盘图标、停消息线程），再停 FPS 监控。
    ///
    /// 顺序说明：全局热键是【系统级】资源，不显式注销就会一直占着那组按键，
    /// 即便本进程即将退出——期间用户在别的程序里按那组键是没反应的。
    /// FpsMonitor 订阅的是静态 CompositionTarget.Rendering，不摘就每帧回调
    /// 终身存在，并经静态事件根钉住本 ViewModel 与整棵 XAML 树。
    /// </summary>
    public void Dispose()
    {
        DisposeShell();
        _fpsMonitor.Dispose();
    }
}
