using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.Services;
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
    private bool _isCapturingHotkey;

    /// <summary>快捷键区底部的状态文本（注册结果、冲突数量等）。</summary>
    [ObservableProperty]
    private string _hotkeyStatusText = "尚未应用。";

    /// <summary>壁纸渲染是否被用户暂停（托盘菜单与热键共用此状态）。</summary>
    [ObservableProperty]
    private bool _isWallpaperPaused;

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
            _tray.IsWallpaperPaused = () => _isWallpaperPaused;
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

        _hotkeyStatusText = $"共 {bindings.Count(b => b.Enabled && b.VirtualKey != 0)} 条热键待注册。";
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
            _hotkeyStatusText = $"未接受：{problem}";
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
            _hotkeyStatusText = "热键线程未建立，快捷键不可用。";
            return;
        }

        var result = await hotkeys.ApplyAsync(_appConfig.Hotkeys);

        foreach (var row in HotkeyRows)
        {
            row.Error = null;
        }

        if (result.AllOk)
        {
            _hotkeyStatusText = $"已注册 {hotkeys.ActiveCount} 个全局快捷键。";
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

        _hotkeyStatusText = $"已注册 {hotkeys.ActiveCount} 个，{result.Failures.Count} 个未生效（见下方红色提示）。";
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

            default:
                return;
        }
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
        if (_isWallpaperPaused)
        {
            _services.Wallpaper.ResumeRendering();
            _isWallpaperPaused = false;
        }
        else
        {
            _services.Wallpaper.PauseRendering();
            _isWallpaperPaused = true;
        }

        OnPropertyChanged(nameof(IsWallpaperPaused));
        WallpaperStatusText = _isWallpaperPaused
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
