using WinCanvas.Core.Services;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.Native;
using WinCanvas.App.Services;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的应用外壳部分：系统托盘 + 全局热键（v0.2.0 新增）。
///
/// 为什么单开一个 partial 文件：MainViewModel 迁移前已经 782 行、承担约 11 项职责
/// （列表重建 / 选中项镜像 / 四类窗口属性转发 / 配置持久化 / 恢复管线 / 壁纸编排 /
/// WinTips 开关 / FPS / InfoBar 总线 / 启动打点）。托盘与热键是【第 12 项】，
/// 塞进主文件等于把「上帝类」继续喂胖 —— 而它本身就在我的待修清单上。
/// partial 让职责在文件层面先分开，后续要抽成独立的 ShellController 也是平移。
///
/// 线程纪律：托盘与热键的事件都在 <see cref="MessagePumpWindow"/> 的泵线程上触发，
/// 本文件里所有触碰可绑定属性或调用窗口操作的分支，一律先
/// <c>_dispatcherQueue.TryEnqueue</c> 回 UI 线程。
/// </summary>
public partial class MainViewModel
{
    private MessagePumpWindow? _pump;
    private TrayIconService? _tray;
    private GlobalHotkeyService? _hotkeys;

    /// <summary>「显示主窗口」由 View 提供（VM 不该直接持有 Window）。</summary>
    private Action? _showMainWindow;

    /// <summary>「隐藏主窗口到托盘」由 View 提供。</summary>
    private Action? _hideMainWindow;

    /// <summary>查询主窗口当前是否可见，供托盘菜单文案使用。</summary>
    private Func<bool>? _isMainWindowVisible;

    /// <summary>
    /// 正在等待按键的行。同一时刻只允许一行处于捕获态——
    /// 两行同时等着收键会把同一次按键写进两条绑定。
    /// </summary>
    private HotkeyRowViewModel? _capturingRow;

    /// <summary>快捷键设置表的每一行。</summary>
    public ObservableCollection<HotkeyRowViewModel> HotkeyRows { get; } = new();

    /// <summary>外壳（托盘 + 热键线程）是否已成功建立。</summary>
    public bool IsShellActive => _pump is not null;

    /// <summary>托盘图标是否在系统托盘里可见。</summary>
    public bool IsTrayActive => _tray?.IsActive == true;

    /// <summary>是否有热键正在等待用户按键（界面据此显示提示条）。</summary>
    [ObservableProperty]
    public partial bool IsCapturingHotkey { get; set; }

    /// <summary>快捷键区底部的状态文本（注册结果、冲突数量等）。</summary>
    [ObservableProperty]
    public partial string HotkeyStatusText { get; set; } = "尚未应用。";

    /// <summary>壁纸渲染是否被用户暂停（托盘菜单与热键共用此状态）。</summary>
    [ObservableProperty]
    public partial bool IsWallpaperPaused { get; set; }

    /// <summary>
    /// 建立应用外壳：隐藏消息窗口 → 托盘图标 → 热键注册。
    /// 由 MainWindow 在窗口建好后调用（它才能提供显示/隐藏窗口的动作）。
    ///
    /// 失败策略：整条链路任何一步失败都【只降级不抛】。
    /// 托盘起不来时主窗口照常可用，热键注册不上时界面里的按钮照常可用 ——
    /// 外壳是锦上添花，不能因为它把主功能拖死。
    /// </summary>
    /// <param name="showMainWindow">把主窗口带到前台。</param>
    /// <param name="hideMainWindow">隐藏主窗口（收进托盘）。</param>
    /// <param name="isMainWindowVisible">查询主窗口当前可见性。</param>
    /// <param name="error">失败原因；成功为 null。</param>
    /// <returns>true = 外壳已可用。</returns>
    public bool AttachShell(Action showMainWindow, Action hideMainWindow,
        Func<bool> isMainWindowVisible, out string? error)
    {
        error = null;
        _showMainWindow = showMainWindow;
        _hideMainWindow = hideMainWindow;
        _isMainWindowVisible = isMainWindowVisible;

        _pump = MessagePumpWindow.TryCreate(out error);
        if (_pump == null)
        {
            return false;
        }

        // 托盘图标：优先用随程序发布的 WinCanvas.ico，取不到就退回系统应用图标
        string iconPath = Path.Combine(AppContext.BaseDirectory, "WinCanvas.ico");
        _tray = new TrayIconService(_pump, iconPath, "WinCanvas — 窗口管理与壁纸背景", out string? trayError);

        if (_tray.IsActive)
        {
            _tray.ShowHideRequested += ToggleMainWindow;
            _tray.RefreshRequested += () => _dispatcherQueue.TryEnqueue(async () => await RefreshAsync());
            _tray.TogglePauseRequested += () => _dispatcherQueue.TryEnqueue(ToggleWallpaperPause);
            _tray.ToggleWinTipsRequested += () => _dispatcherQueue.TryEnqueue(ToggleWinTips);
            _tray.ExitRequested += () => _dispatcherQueue.TryEnqueue(RequestExit);

            // 菜单文案要反映实时状态，这几个委托由服务层在泵线程上同步调用，
            // 只读布尔字段（写入都在 UI 线程 + 属性变更通知），不做跨线程界面访问
            _tray.IsMainWindowVisible = () => _isMainWindowVisible?.Invoke() ?? true;
            _tray.IsWallpaperPaused = () => IsWallpaperPaused;
            _tray.IsWinTipsEnabled = () => _services.WinTips.IsEnabled;
        }
        else
        {
            // 托盘失败不阻断热键：两者唯一的共同依赖是那条消息线程
            ConfigLog($"托盘图标未能建立：{trayError}");
        }

        _hotkeys = new GlobalHotkeyService(_pump);
        _hotkeys.Triggered += action => _dispatcherQueue.TryEnqueue(() => _ = ExecuteHotkeyAsync(action));

        BuildHotkeyRows();
        return true;
    }

    /// <summary>
    /// 配置加载完成后初始化热键：按配置重建界面行，并【立即注册】。
    /// 由 <c>InitializeAsync</c> 在 LoadAsync 之后调用 —— AttachShell 跑得太早，
    /// 那时 _appConfig 还是默认值，注册必须等配置到位。
    /// </summary>
    internal void InitializeHotkeyBindings()
    {
        if (_hotkeys is null)
        {
            // 这条分支是「快捷键整体失效」最隐蔽的原因：界面照样显示默认组合，
            // 但一个都没注册，看上去像「功能没做」。不静默。
            ConfigLog("热键初始化跳过：应用外壳（系统消息窗口）未建立，全局快捷键不可用。");
            return;
        }

        BuildHotkeyRows();
        _ = ApplyHotkeysAsync();
    }

    /// <summary>
    /// 用配置里的绑定表重建界面行（缺项补默认）。
    /// 每次从配置加载后调用，保证「配置里没写全」与「界面显示全」两件事不打架。
    /// </summary>
    private void BuildHotkeyRows()
    {
        HotkeyRows.Clear();

        var bindings = HotkeyTable.Normalize(_appConfig.Hotkeys);
        _appConfig.Hotkeys = bindings;

        foreach (var (action, label, description) in HotkeyTable.Catalog)
        {
            var binding = bindings.First(b => b.Action == action);
            HotkeyRows.Add(new HotkeyRowViewModel(action, label, description, binding, OnRowChanged, BeginCapture));
        }

        HotkeyStatusText = $"共 {bindings.Count(b => b.Enabled && b.VirtualKey != 0)} 条热键待注册。";
    }

    /// <summary>某一行的绑定/开关变了：写回配置并重新注册热键。</summary>
    private void OnRowChanged(HotkeyRowViewModel row)
    {
        // 行里的 Binding 实例要同步回配置列表，否则持久化的是旧值
        int index = _appConfig.Hotkeys.FindIndex(b => b.Action == row.Action);
        if (index >= 0)
        {
            _appConfig.Hotkeys[index] = row.Binding;
        }
        else
        {
            _appConfig.Hotkeys.Add(row.Binding);
        }

        PersistConfig();
        _ = ApplyHotkeysAsync();
    }

    /// <summary>
    /// 让某一行进入按键捕获态。由界面按钮调用。
    /// </summary>
    /// <param name="row">要捕获的行。</param>
    public void BeginCapture(HotkeyRowViewModel row)
    {
        // 先结束上一行的捕获：避免旧行的按键被写进新行
        _capturingRow?.EndCapture();

        _capturingRow = row;
        row.IsCapturing = true;
        row.ComboText = "请按下组合键…（Esc 取消）";
        IsCapturingHotkey = true;
    }

    /// <summary>取消捕获并恢复文案。</summary>
    public void CancelCapture()
    {
        _capturingRow?.EndCapture();
        _capturingRow = null;
        IsCapturingHotkey = false;
    }

    /// <summary>
    /// 接收一次按键捕获结果（由 MainWindow 的 KeyDown 处理器调用，已在 UI 线程）。
    /// </summary>
    /// <param name="virtualKey">主键虚拟键码。</param>
    /// <param name="modifiers">按下时生效的修饰键。</param>
    /// <returns>true = 已接受并应用；false = 当前不在捕获态或校验失败。</returns>
    public bool HandleCapturedKey(int virtualKey, HotkeyModifiers modifiers)
    {
        var row = _capturingRow;
        if (row == null)
        {
            return false;
        }

        // Esc（VK_ESCAPE=0x1B）不带修饰键时视为取消，符合「按 Esc 退出录入」的直觉
        if (virtualKey == 0x1B && modifiers == HotkeyModifiers.None)
        {
            CancelCapture();
            return true;
        }

        var all = _appConfig.Hotkeys;
        string? problem = row.CommitCapture(modifiers, virtualKey, all);

        CancelCapture();

        if (problem != null)
        {
            HotkeyStatusText = $"未接受：{problem}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按当前绑定表重新注册全部全局热键，并把结果如实汇报到界面。
    /// </summary>
    [RelayCommand]
    private async Task ApplyHotkeysAsync()
    {
        var hotkeys = _hotkeys;
        if (hotkeys == null)
        {
            HotkeyStatusText = "热键线程未建立，快捷键不可用。";
            return;
        }

        var result = await hotkeys.ApplyAsync(_appConfig.Hotkeys);

        // 把注册结果落到诊断日志：热键「按了没反应」是这类工具最难归因的故障，
        // 可能的原因至少有三层（外壳没起来 / 泵线程超时 / 组合被别的程序占用），
        // 界面上虽有红字提示，但用户收进托盘后根本看不到 —— 日志是唯一的远程排障入口。
        ConfigLog($"全局热键注册结果：成功 {hotkeys.ActiveCount} 条，失败 {result.Failures.Count} 条" +
                  (result.Failures.Count == 0
                      ? string.Empty
                      : $"（{string.Join("；", result.Failures.Select(f => f.Reason))}）"));

        foreach (var row in HotkeyRows)
        {
            row.Error = null;
        }

        if (result.AllOk)
        {
            HotkeyStatusText = $"已注册 {hotkeys.ActiveCount} 个全局快捷键。";
            return;
        }

        foreach (var (action, reason) in result.Failures)
        {
            var row = HotkeyRows.FirstOrDefault(r => r.Action == action);
            if (row != null)
            {
                row.Error = reason;
            }
        }

        HotkeyStatusText = $"已注册 {hotkeys.ActiveCount} 个，{result.Failures.Count} 个未生效（见下方红色提示）。";
        ShowInfo(InfoBarSeverity.Warning,
            $"{result.Failures.Count} 个全局快捷键注册失败：{result.Failures[0].Reason}");
    }

    /// <summary>把全部快捷键恢复成默认组合。</summary>
    [RelayCommand]
    private void ResetAllHotkeys()
    {
        CancelCapture();

        foreach (var row in HotkeyRows)
        {
            row.ResetToDefault();
        }

        // ResetToDefault 换掉了行内的 Binding 实例，配置列表里还是旧对象 ——
        // 整表重建一次，保证「界面看到的」与「写进 config.json 的」是同一批数据。
        _appConfig.Hotkeys = HotkeyRows.Select(r => r.Binding).ToList();

        PersistConfig();
        _ = ApplyHotkeysAsync();
    }

    /// <summary>
    /// 执行一个热键动作。全局热键与托盘菜单都汇聚到这里，保证「同一个动作
    /// 无论从哪触发，行为与限制完全一致」。
    /// </summary>
    /// <param name="action">要执行的动作。</param>
    private async Task ExecuteHotkeyAsync(HotkeyAction action)
    {
        var selected = Selected;

        switch (action)
        {
            case HotkeyAction.ShowHideMainWindow:
                ToggleMainWindow();
                return;

            case HotkeyAction.RefreshWindowList:
                await RefreshAsync();
                return;

            case HotkeyAction.ToggleWallpaperPause:
                ToggleWallpaperPause();
                return;

            case HotkeyAction.ToggleWinTipsIndicator:
                ToggleWinTips();
                return;

            case HotkeyAction.ToggleWallpaperAudio:
                ToggleWallpaperAudio();
                return;

            case HotkeyAction.NextWallpaper:
                // 壁纸轮换（壁纸库轮换工作包）：热键立即切换下一张；announceEmptyTargets=true
                // 让「没有任何已挂背景窗口」时得到明确提示（验收点：提示而不做事）。
                // RotateWallpaperTickAsync 自兜全部异常，不会反噬热键派发链。
                await RotateWallpaperTickAsync(announceEmptyTargets: true);
                return;

            case HotkeyAction.ToggleTopmost:
                if (selected == null)
                {
                    NotifyNeedsSelection();
                    return;
                }

                selected.IsTopmost = !selected.IsTopmost;
                return;

            case HotkeyAction.ToggleClickThrough:
                if (selected == null)
                {
                    NotifyNeedsSelection();
                    return;
                }

                selected.ClickThrough = !selected.ClickThrough;
                return;

            case HotkeyAction.IncreaseOpacity:
                if (selected == null)
                {
                    NotifyNeedsSelection();
                    return;
                }

                selected.Alpha = (byte)Math.Min(255, selected.Alpha + 32);
                return;

            case HotkeyAction.DecreaseOpacity:
                if (selected == null)
                {
                    NotifyNeedsSelection();
                    return;
                }

                selected.Alpha = (byte)Math.Max(0, selected.Alpha - 32);
                return;

            case HotkeyAction.CycleCornerPreference:
                if (selected == null)
                {
                    NotifyNeedsSelection();
                    return;
                }

                CycleSelectedCorner();
                return;

            case HotkeyAction.ClearWallpaper:
                if (selected == null)
                {
                    NotifyNeedsSelection();
                    return;
                }

                await ClearWallpaperAsync();
                return;

            // 焦点窗口系列（v0.3.3）：作用于 GetForegroundWindow()，与列表选中无关 ——
            // 这正是「焦点窗口按下快捷键没反应」报障的修复口径：任何窗口获得焦点（包括
            // 非本应用窗口）时按键立即生效，无需切回主窗口选中它。
            case HotkeyAction.FocusToggleTopmost:
            case HotkeyAction.FocusToggleClickThrough:
            case HotkeyAction.FocusIncreaseOpacity:
            case HotkeyAction.FocusDecreaseOpacity:
            case HotkeyAction.FocusRestoreOpaque:
            case HotkeyAction.FocusCycleCorner:
                await ExecuteFocusWindowHotkeyAsync(action);
                return;

            case HotkeyAction.FocusTogglePrivacy:
                ExecuteFocusPrivacyToggle();
                return;

            default:
                return;
        }
    }

    // ---------- 焦点窗口热键（v0.3.3） ----------

    /// <summary>
    /// 不在主界面列表中的焦点窗口的会话内状态基准（v0.3.3）。
    ///
    /// 为什么需要：列表只包含上次刷新时可见、非 cloaked、有标题的窗口，用户运行中
    /// 新开的窗口按热键时列表里还没有它。置顶可以每次读真实状态（<c>ToggleTopmostAsync</c>
    /// 自带读-翻转），但穿透/透明度/圆角没有「读」的接口，只能以「上次应用过的值」为基准 ——
    /// 对无档案窗口，这个基准就记在这里，让连续按键的 ±10 / 开关语义在会话内成立。
    /// </summary>
    private sealed class FocusWindowRuntime
    {
        /// <summary>上次应用的置顶状态。</summary>
        public bool IsTopmost;

        /// <summary>上次应用的穿透状态。</summary>
        public bool ClickThrough;

        /// <summary>上次应用的不透明度（0~255）。</summary>
        public byte Alpha = 255;

        /// <summary>上次应用的圆角偏好。</summary>
        public CornerPreference Corner = CornerPreference.Default;
    }

    /// <summary>
    /// hwnd → 焦点窗口会话基准。
    /// 并发纪律：【只在 UI 线程读写】—— 热键事件经 GlobalHotkeyService.Triggered →
    /// DispatcherQueue.TryEnqueue 全部封送到 UI 线程执行（见 AttachShell 的订阅），
    /// 不存在并发访问者，故不加锁；不要从后台线程触碰本字典。
    /// 窗口销毁后的残留条目以句柄值作键，句柄被系统复用时会以新窗口的状态覆盖，
    /// 最坏影响是一次状态基准偏差，不涉及内存或句柄安全。
    /// </summary>
    private readonly Dictionary<IntPtr, FocusWindowRuntime> _focusWindowStates = new();

    /// <summary>
    /// 执行一个【焦点窗口】系列热键：目标 = 当前前台窗口（GetForegroundWindow），
    /// 复用 <see cref="IWindowControlService"/> 既有 Win32 方法；能关联到列表条目或
    /// 配置档案时同步其状态并经 <see cref="PersistState"/> 走配置防抖落盘。
    /// 在 UI 线程执行（由 <see cref="ExecuteHotkeyAsync"/> 封送）。
    /// </summary>
    /// <param name="action">焦点窗口系列动作。</param>
    private async Task ExecuteFocusWindowHotkeyAsync(HotkeyAction action)
    {
        IntPtr hwnd = ShellNativeMethods.GetForegroundWindow();

        if (hwnd == IntPtr.Zero)
        {
            // 失败 + 原因 + 怎么办：切屏 / 锁屏瞬间系统确实可能没有前台窗口，重按一次即可
            ShowInfo(InfoBarSeverity.Informational,
                "快捷键未执行：系统当前没有返回前台窗口（可能正处在切屏或锁屏瞬间）。请再按一次；仍无效请先点击目标窗口取得焦点。");
            return;
        }

        // 排除本应用自身窗口（主窗口与壁纸宿主同在本进程，进程 Id 即可整组排除）：
        // 1) 把置顶/穿透打到自己身上会造成「主界面突然点不动 / 突然置顶」这类自伤；
        // 2) 主窗口聚焦时按热键，与应用内的快捷键录入等按键处理互相干扰。
        // 命中时静默返回：这是设计行为（对自身窗口不生效），不是故障，不打扰用户。
        ShellNativeMethods.GetWindowThreadProcessId(hwnd, out uint foregroundProcessId);
        if (foregroundProcessId == (uint)Environment.ProcessId)
        {
            return;
        }

        // —— 状态基准与写回目标 ——
        // 列表条目（item）：界面属性、档案（item.State）齐全，写回后界面同步显示；
        // 会话缓存（runtime）：仅列表外窗口需要；档案（archive）：HwndHash 匹配的配置档案，
        // 存在则随动作更新并防抖落盘，不存在则不凭空建档（避免空 Key 的孤儿档案进 config.json）。
        var item = Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        WindowState? archive;
        FocusWindowRuntime? runtime = null;

        bool topmost;
        bool clickThrough;
        byte alpha;
        CornerPreference corner;

        if (item is not null)
        {
            archive = item.State;
            topmost = item.IsTopmost;
            clickThrough = item.ClickThrough;
            alpha = item.Alpha;
            corner = item.Corner;
        }
        else
        {
            archive = _appConfig.Windows.FirstOrDefault(w => w.HwndHash == hwnd.ToInt64());

            // 并发纪律：_focusWindowStates 仅 UI 线程读写（见字段注释），本方法在 UI 线程
            if (!_focusWindowStates.TryGetValue(hwnd, out runtime))
            {
                runtime = new FocusWindowRuntime();

                // 首次操作该窗口：档案存在时以档案为初始基准，否则档案里已存的
                // 置顶/透明度会被会话默认值当作「上次应用的值」而覆盖
                if (archive is not null)
                {
                    runtime.IsTopmost = archive.IsTopmost;
                    runtime.ClickThrough = archive.ClickThrough;
                    runtime.Alpha = archive.Alpha;
                    runtime.Corner = archive.Corner;
                }

                _focusWindowStates[hwnd] = runtime;
            }

            topmost = runtime.IsTopmost;
            clickThrough = runtime.ClickThrough;
            alpha = runtime.Alpha;
            corner = runtime.Corner;
        }

        try
        {
            switch (action)
            {
                case HotkeyAction.FocusToggleTopmost:
                {
                    // ToggleTopmostAsync 自带「读 WS_EX_TOPMOST 实际状态 → 翻转」语义并返回
                    // 切换后的真实状态：置顶以【真实状态】为准，档案过时也不会把置顶翻错
                    topmost = await _services.WindowControl.ToggleTopmostAsync(hwnd);
                    break;
                }

                case HotkeyAction.FocusToggleClickThrough:
                {
                    // IWindowControlService 没有「读穿透状态」的接口，切换基准取档案/缓存值 ——
                    // 与主界面 ToggleSwitch 的翻转语义同构（SetClickThroughAsync 同样以条目状态为基准）
                    clickThrough = !clickThrough;
                    await _services.WindowControl.EnableClickThroughAsync(hwnd, clickThrough);
                    break;
                }

                case HotkeyAction.FocusIncreaseOpacity:
                case HotkeyAction.FocusDecreaseOpacity:
                {
                    alpha = HotkeyTable.NextFocusAlpha(alpha, action == HotkeyAction.FocusIncreaseOpacity);
                    await _services.WindowControl.SetTransparencyAsync(hwnd, alpha);
                    break;
                }

                case HotkeyAction.FocusRestoreOpaque:
                {
                    alpha = 255;
                    await _services.WindowControl.SetTransparencyAsync(hwnd, alpha);
                    break;
                }

                case HotkeyAction.FocusCycleCorner:
                {
                    corner = NextCornerInCycle(corner);
                    await _services.WindowControl.SetCornerPreferenceAsync(hwnd, corner);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // 失败 + 原因 + 怎么办：ProcessElevation.DescribeFailure 对 Win32 错误 5（UIPI）
            // 会自动附上「以管理员身份重启」的引导，其余错误带原始消息
            ShowInfo(InfoBarSeverity.Error, ProcessElevation.DescribeFailure(
                ex, $"对前台窗口{DescribeFocusTarget(hwnd, item?.DisplayName)}执行「{HotkeyTable.LabelOf(action)}」"));
            return;
        }

        // —— 成功：把新状态写回三处（界面条目 / 会话缓存 / 配置档案）——
        if (item is not null)
        {
            // SyncApplied 同时对齐可绑定属性（详情面板 / 选中项镜像自动跟着刷新）、
            // 档案（item.State）与条目内部的「已应用基准」（_applied*）——
            // 少最后一份，之后界面上把滑块拖回旧值会被 Set*Async 的短路防抖吞掉
            item.SyncApplied(topmost, clickThrough, alpha, corner);
        }

        if (runtime is not null)
        {
            runtime.IsTopmost = topmost;
            runtime.ClickThrough = clickThrough;
            runtime.Alpha = alpha;
            runtime.Corner = corner;
        }

        if (archive is not null)
        {
            archive.IsTopmost = topmost;
            archive.ClickThrough = clickThrough;
            archive.Alpha = alpha;
            archive.Corner = corner;
            PersistState(archive); // 配置锁内并入 _appConfig 并经 ConfigurationService 防抖批量落盘
        }
    }

    /// <summary>
    /// 「隐私模式切换（焦点窗口）」热键：对当前前台窗口翻转其隐私开关（UI 线程执行）。
    /// 与其它焦点热键一致作用在 GetForegroundWindow()；窗口必须已在列表中
    /// （隐私状态挂在列表条目与配置档案上，列表外窗口无处落盘）。
    /// 开关本身的翻转经 <see cref="WindowItemViewModel.IsPrivacyEnabled"/> 触发
    /// 单一应用路径（失焦遮蔽 / 聚焦恢复 / 持久化 / 监听启停全部复用）。
    /// </summary>
    private void ExecuteFocusPrivacyToggle()
    {
        IntPtr hwnd = ShellNativeMethods.GetForegroundWindow();

        if (hwnd == IntPtr.Zero)
        {
            // 失败 + 原因 + 怎么办：切屏 / 锁屏瞬间系统确实可能没有前台窗口，重按一次即可
            ShowInfo(InfoBarSeverity.Informational,
                "快捷键未执行：系统当前没有返回前台窗口（可能正处在切屏或锁屏瞬间）。请再按一次；仍无效请先点击目标窗口取得焦点。");
            return;
        }

        ShellNativeMethods.GetWindowThreadProcessId(hwnd, out uint foregroundProcessId);
        if (foregroundProcessId == (uint)Environment.ProcessId)
        {
            return; // 对自身窗口不生效（主窗口遮罩由设置开关控制），设计行为不打扰用户
        }

        var item = Windows.FirstOrDefault(w => w.Hwnd == hwnd);
        if (item is null)
        {
            ShowInfo(InfoBarSeverity.Warning,
                "隐私模式未切换：当前焦点窗口不在窗口列表中（可能是刚打开、尚未刷新）。" +
                "请先点主界面「刷新」再按热键，或在列表中选中该窗口打开隐私开关。");
            return;
        }

        // 属性 setter 内部走 ApplyPrivacyAsync 单一路径（档案落盘 + 遮蔽迁移 + 监听启停）
        item.IsPrivacyEnabled = !item.IsPrivacyEnabled;
    }

    /// <summary>
    /// 圆角档位循环的下一档：无 → 大 → 小 → 无。
    /// 与主界面 <see cref="CycleSelectedCorner"/>（CornerOptions 顺序）完全同序，保证
    /// 「同一个窗口无论从热键还是界面循环，档位走向一致」——包括对档案默认值
    /// Default（= 跟随系统，不在循环表内）的处理：界面路径把它当作第 0 档
    /// （currentIndex 保持初值 0），下一档取「大圆角」，这里保持同一行为。
    /// </summary>
    private static CornerPreference NextCornerInCycle(CornerPreference current)
    {
        CornerPreference[] cycle =
        {
            CornerPreference.DoNotRound,
            CornerPreference.Round,
            CornerPreference.RoundSmall,
        };

        int index = Array.IndexOf(cycle, current);
        if (index < 0)
        {
            index = 0; // Default / 未知值：与 CycleSelectedCorner 的初值口径一致，下一档取「大」
        }

        return cycle[(index + 1) % cycle.Length];
    }

    /// <summary>
    /// 失败提示里对目标窗口的人读描述：优先真实窗口标题，其次列表条目名，
    /// 都取不到就退回句柄值 —— 让用户能对上是哪个窗口，而不是面对一串裸错误。
    /// </summary>
    private static string DescribeFocusTarget(IntPtr hwnd, string? fallbackTitle)
    {
        try
        {
            var title = new System.Text.StringBuilder(64);
            ShellNativeMethods.GetWindowTextW(hwnd, title, title.Capacity);

            if (!string.IsNullOrWhiteSpace(title.ToString()))
            {
                return $"「{title}」";
            }
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "读取前台窗口标题失败（不影响热键执行）");
        }

        return string.IsNullOrWhiteSpace(fallbackTitle) ? $"0x{hwnd.ToInt64():X}" : $"「{fallbackTitle}」";
    }

    /// <summary>热键动作依赖「已选中窗口」而当前没选中时的提示。</summary>
    private void NotifyNeedsSelection()
    {
        ShowInfo(InfoBarSeverity.Warning, "该快捷键需要一个已选中的窗口：请先在左侧列表选择一个窗口。");
    }

    /// <summary>圆角档位循环：无 → 大 → 小 → 无。</summary>
    private void CycleSelectedCorner()
    {
        var selected = Selected;
        if (selected == null)
        {
            return;
        }

        int currentIndex = 0;
        for (int i = 0; i < CornerOptions.Count; i++)
        {
            if (CornerOptions[i].Value == selected.Corner)
            {
                currentIndex = i;
                break;
            }
        }

        SelectedCorner = CornerOptions[(currentIndex + 1) % CornerOptions.Count];
    }

    /// <summary>
    /// 清除选中窗口的背景：让服务销毁宿主，并把档案里的背景路径抹掉。
    /// 之前界面里没有「清除」入口，用户挂上背景后只能关掉目标窗口才能摆脱。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ClearWallpaperAsync()
    {
        var selected = Selected;
        if (selected == null)
        {
            return;
        }

        string? path = selected.State.BackgroundPath;
        if (string.IsNullOrEmpty(path))
        {
            WallpaperStatusText = "该窗口当前没有背景可清除。";
            return;
        }

        try
        {
            WallpaperStatusText = "正在清除背景…";
            await _services.Wallpaper.RemoveWallpaperAsync(selected.Hwnd);

            selected.State.BackgroundPath = null;
            PersistState(selected.State);

            WallpaperFilePathText = "（未选择）";
            WallpaperStatusText = "背景已清除，目标窗口恢复自身绘制内容。";
        }
        catch (Exception ex)
        {
            WallpaperStatusText = $"清除背景失败：{ex.Message}";
        }
    }

    /// <summary>暂停 / 恢复全部壁纸渲染（托盘菜单与全局热键共用）。</summary>
    private void ToggleWallpaperPause()
    {
        if (IsWallpaperPaused)
        {
            _services.Wallpaper.ResumeRendering();
            IsWallpaperPaused = false;
        }
        else
        {
            _services.Wallpaper.PauseRendering();
            IsWallpaperPaused = true;
        }

        OnPropertyChanged(nameof(IsWallpaperPaused));
        WallpaperStatusText = IsWallpaperPaused
            ? "壁纸渲染已暂停（省电 / 让出 GPU）。"
            : "壁纸渲染已恢复。";
    }

    /// <summary>开 / 关 WinTips NumLock 提示（即低级键盘钩子的启停）。</summary>
    private void ToggleWinTips()
    {
        // 复用既有属性 setter 的完整语义（钩子装卸、诊断、持久化），不另写一套
        WinTipsEnabled = !WinTipsEnabled;
    }

    /// <summary>显示 / 隐藏主窗口（托盘左键、菜单项、ShowHide 热键三条路径共用）。</summary>
    public void ToggleMainWindow()
    {
        bool visible = _isMainWindowVisible?.Invoke() ?? true;

        if (visible)
        {
            _hideMainWindow?.Invoke();
        }
        else
        {
            _showMainWindow?.Invoke();
        }
    }

    /// <summary>托盘菜单「退出」：走与关闭主窗口相同的收口路径。</summary>
    private void RequestExit()
    {
        // 由 View 决定怎么真正退出（Close 主窗口会触发 Closed 里的 Flush + ShutdownAll）
        _exitRequested?.Invoke();
    }

    /// <summary>「请求退出」由 View 订阅（VM 不直接调 Application.Exit，避免退出收口被绕过）。</summary>
    private Action? _exitRequested;

    /// <summary>View 注册退出动作。</summary>
    public void SetExitHandler(Action handler)
    {
        _exitRequested = handler;
    }

    /// <summary>外壳诊断信息落到配置诊断日志（托盘/热键建立失败这类低频但重要的事件）。</summary>
    private static void ConfigLog(string message)
    {
        App.LogFatal(null, message);
    }

    /// <summary>
    /// WinTips 浮窗不可用时的 NumLock 提示回退（v0.3.0）。
    ///
    /// 优先【托盘气泡】：本工具的典型用法是主窗口收在托盘里，
    /// 只发 InfoBar 等于「提示写在一个看不见的窗口上」。
    /// 没有托盘（外壳建立失败）时才退回 InfoBar，保证总有一条通道能提示到。
    /// </summary>
    /// <param name="isLocked">NumLock 是否已锁定。</param>
    private void ReportNumLockFallback(bool isLocked)
    {
        string text = isLocked ? "NumLock 已锁定" : "NumLock 已解锁";
        TrayIconService? tray = _tray;

        if (tray?.IsActive == true)
        {
            // 气泡由托盘线程发，正文里带上程序名：Windows 的气泡标题栏显示的是
            // 通知来源，用户需要知道这是 WinCanvas 而不是系统本身在说话
            _dispatcherQueue.TryEnqueue(() => tray.ShowBalloon("WinCanvas", text));
            return;
        }

        ShowInfo(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, text);
    }

    /// <summary>
    /// 释放外壳资源：注销全部热键、摘掉托盘图标、停掉消息线程。
    /// 顺序有讲究 —— 先摘托盘再停线程，反过来会让 NIM_DELETE 发到已销毁的窗口上，
    /// 图标残留在通知区域直到下次 explorer 刷新（俗称「僵尸图标」）。
    /// </summary>
    private void DisposeShell()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;

        _tray?.Dispose();
        _tray = null;

        _pump?.Dispose();
        _pump = null;
    }
}
