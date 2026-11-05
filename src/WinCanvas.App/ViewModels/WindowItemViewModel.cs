using CommunityToolkit.Mvvm.ComponentModel;
using WinCanvas.App.Native;
using WinCanvas.App.Services;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// 窗口列表项：包装 <see cref="WindowInfo"/>（运行期快照）与持久化 <see cref="WindowState"/>。
/// 属性变更时立即调用 Core 的 <see cref="IWindowControlService"/> 把改动应用到真实窗口，
/// 并通过外层传入的持久化回调（内部走 <see cref="IConfigurationService"/>）实时保存。
/// </summary>
public partial class WindowItemViewModel : ObservableObject
{
    private readonly IWindowControlService _windowControl;
    private readonly Action<WindowState> _persist;
    private readonly Action<string> _reportError;

    /// <summary>
    /// 隐私迁移投递回调（由 MainViewModel 注入）：把「遮蔽 / 恢复」迁移指令送进
    /// ViewModel 的串行任务链。所有隐私相关的 alpha 写入都经同一条链串行执行，
    /// 保证同一窗口上「遮蔽 → 恢复」严格按事件顺序落盘，不会互相覆盖（并发纪律见 ADR）。
    /// </summary>
    private readonly Action<IReadOnlyList<PrivacyMaskRules.PrivacyMaskTransition>> _enqueuePrivacyTransitions;

    /// <summary>隐私开关变化回调（由 MainViewModel 注入）：据此启停前台监听钩子线程。</summary>
    private readonly Action _notifyPrivacyChanged;

    /// <summary>
    /// 屏幕外跳过的对外提示回调（由 MainViewModel 注入）：档案矩形不在任何显示器上时
    /// 提示「失败 + 原因 + 怎么办」，与其它失败路径同一套呈现通道（InfoBar）。
    /// </summary>
    private readonly Action<WindowBounds> _reportBoundsSkippedOffscreen;

    /// <summary>
    /// 几何捕获成功的对外提示回调（由 MainViewModel 注入）：告知用户记住了什么值，
    /// 让「记住当前位置」有可见反馈而不是静默改档案。
    /// </summary>
    private readonly Action<WindowBounds> _reportBoundsCaptured;

    /// <summary>
    /// 构造回填抑制标志：构造函数里从档案回填 <see cref="IsPrivacyEnabled"/> 会触发
    /// <see cref="OnIsPrivacyEnabledChanged"/>，若不加抑制，每次刷新都会对隐私窗口
    /// 立刻发起一次 Win32 遮蔽（与档案恢复管线竞态）。初始遮蔽统一由
    /// MainViewModel.ApplyPrivacyInitialState 在列表重建后按决策规则执行。
    /// </summary>
    private bool _privacyInitialized;

    /// <summary>回滚抑制标志：失败回滚 <see cref="IsPrivacyEnabled"/> 时不再触发一次新的应用。</summary>
    private bool _suppressPrivacyApply;

    /// <summary>已实际应用到 Win32 窗口的值（用于避免重复调用服务）。</summary>
    private bool _appliedTopmost;
    private bool _appliedClickThrough;
    private byte _appliedAlpha = 255;
    private CornerPreference _appliedCorner = CornerPreference.Default;

    /// <summary>枚举到的窗口运行期快照。</summary>
    public WindowInfo Info { get; }

    /// <summary>该窗口的持久化状态（变更后由外层实时写盘）。</summary>
    public WindowState State { get; }

    /// <summary>目标窗口句柄。</summary>
    public IntPtr Hwnd => Info.Hwnd;

    /// <summary>列表主标题（无标题窗口给占位文案）。</summary>
    public string DisplayName { get; }

    /// <summary>列表副标题：进程名 (进程Id) · 类名。</summary>
    public string Subtitle { get; }

    /// <summary>
    /// UIA 里这一行读出来的名字。
    /// WinUI 在 DataTemplate 没有给容器显式 Name 时，回落到 <c>ToString()</c>；
    /// 不覆写的话每一行都念成「WinCanvas.App.ViewModels.WindowItemViewModel」，
    /// 屏幕阅读器与无障碍工具（以及任何按名字定位行的自动化）完全用不了这个列表。
    /// </summary>
    public override string ToString() => $"{DisplayName} — {Subtitle}";

    /// <summary>
    /// 是否匹配搜索关键字（标题 / 进程名 / 类名，不区分大小写，子串匹配）。
    /// 供主界面的窗口搜索框使用——多窗口场景下靠滚动找目标太慢。
    /// </summary>
    /// <param name="keyword">已 trim 且非空的关键字。</param>
    /// <returns>true = 应当显示。</returns>
    public bool MatchesFilter(string keyword)
    {
        if (keyword.Length == 0)
        {
            return true;
        }

        return Info.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || Info.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || Info.ClassName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>句柄的十六进制展示文本。</summary>
    public string HwndText { get; }

    [ObservableProperty]
    public partial bool IsTopmost { get; set; }

    [ObservableProperty]
    public partial byte Alpha { get; set; } = 255;

    [ObservableProperty]
    public partial bool ClickThrough { get; set; }

    [ObservableProperty]
    public partial CornerPreference Corner { get; set; } = CornerPreference.Default;

    /// <summary>
    /// 「窗口隐私模式」开关（持久化进 <see cref="WindowState.PrivacyEnabled"/>）。
    /// 变更后经 <see cref="ApplyPrivacyAsync"/> 单一路径应用：失焦遮蔽 / 聚焦恢复的
    /// Win32 调用全部走 ViewModel 的串行迁移链（见 <see cref="_enqueuePrivacyTransitions"/>）。
    /// </summary>
    [ObservableProperty]
    public partial bool IsPrivacyEnabled { get; set; }

    /// <summary>
    /// 该窗口当前是否处于隐私遮蔽态（纯运行期状态，不持久化）。
    /// 列表项右侧据此显示眼睛图标，让用户看得出「哪些窗口正处于被遮蔽」。
    /// 只在 UI 线程写（迁移链与开关应用路径均在 UI 线程执行）。
    /// </summary>
    [ObservableProperty]
    public partial bool IsPrivacyMasked { get; set; }

    /// <summary>隐私开关变化：走单一路径应用（构造回填与失败回滚走抑制分支）。</summary>
    partial void OnIsPrivacyEnabledChanged(bool value)
    {
        if (!_privacyInitialized || _suppressPrivacyApply)
        {
            return;
        }

        _ = ApplyPrivacyAsync(value);
    }

    /// <summary>遮蔽态变化 → 刷新列表项眼睛图标的可见性。</summary>
    partial void OnIsPrivacyMaskedChanged(bool value)
    {
        OnPropertyChanged(nameof(PrivacyMaskedVisibility));
    }

    /// <summary>列表项隐私开关的无障碍名称（含窗口名，屏幕阅读器能说清开关管的是谁）。</summary>
    public string PrivacyToggleAutomationName => $"窗口隐私模式：{DisplayName}";

    /// <summary>遮蔽态眼睛图标的可见性（列表项模板绑定）。</summary>
    public Microsoft.UI.Xaml.Visibility PrivacyMaskedVisibility =>
        IsPrivacyMasked ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public WindowItemViewModel(
        WindowInfo info,
        WindowState state,
        IWindowControlService windowControl,
        Action<WindowState> persist,
        Action<string> reportError,
        Action<IReadOnlyList<PrivacyMaskRules.PrivacyMaskTransition>> enqueuePrivacyTransitions,
        Action notifyPrivacyChanged,
        Action<WindowBounds> reportBoundsSkippedOffscreen,
        Action<WindowBounds> reportBoundsCaptured)
    {
        Info = info;
        State = state;
        _windowControl = windowControl;
        _persist = persist;
        _reportError = reportError;
        _enqueuePrivacyTransitions = enqueuePrivacyTransitions;
        _notifyPrivacyChanged = notifyPrivacyChanged;
        _reportBoundsSkippedOffscreen = reportBoundsSkippedOffscreen;
        _reportBoundsCaptured = reportBoundsCaptured;

        DisplayName = string.IsNullOrWhiteSpace(info.Title) ? "（无标题窗口）" : info.Title;
        Subtitle = $"{info.ProcessName} ({info.ProcessId}) · {info.ClassName}";
        HwndText = $"0x{info.Hwnd.ToInt64():X}";

        // 把持久化的值恢复到界面属性（真正应用到窗口在选中时由 RestoreAsync 执行）
        IsTopmost = state.IsTopmost;
        Alpha = state.Alpha;
        ClickThrough = state.ClickThrough;
        Corner = state.Corner;

        // 隐私开关回填必须走在 _privacyInitialized 置位之前，否则每次刷新都会立刻遮蔽一次
        IsPrivacyEnabled = state.PrivacyEnabled;
        _privacyInitialized = true;
    }

    /// <summary>
    /// 选中该窗口时恢复其已保存的属性档案：
    /// 把持久化的置顶 / 透明 / 穿透 / 圆角值实际应用回窗口，
    /// 保证界面开关、档案值与窗口真实状态三者一致。
    /// 置顶采用「先读真实状态、仅在不一致时按目标值设置」的幂等语义 ——
    /// 不能用翻转语义，否则窗口实际已置顶（本次会话设置过 / 刷新后重新选中）时，
    /// 恢复动作反而会把置顶翻掉并把错误状态写盘。
    /// </summary>
    /// <param name="restoreBounds">
    /// 是否连窗口几何一起恢复（窗口位置记忆工作包新增）。
    /// 只允许启动恢复管线传 true —— 那是「档案值应生效」的时刻；
    /// 手动选中也传 true 会造成「点一下列表窗口就跳回档案位置」，未被要求也不合直觉。
    /// 几何分支内部自带 IsIconic / IsZoomed 预检与屏幕外判定。
    /// </param>
    public async Task RestoreAsync(bool restoreBounds = false)
    {
        try
        {
            // 置顶：读窗口实际状态，仅在与档案不一致时应用目标值，最后以实际状态为准回写
            var actualTopmost = await _windowControl.GetIsTopmostAsync(Hwnd);
            if (actualTopmost != State.IsTopmost)
            {
                await _windowControl.SetTopmostAsync(Hwnd, State.IsTopmost);
                actualTopmost = await _windowControl.GetIsTopmostAsync(Hwnd);
            }
            _appliedTopmost = actualTopmost;
            State.IsTopmost = actualTopmost;
            IsTopmost = actualTopmost;

            // 透明度：档案不是默认不透明时应用
            if (State.Alpha != 255)
            {
                await _windowControl.SetTransparencyAsync(Hwnd, State.Alpha);
            }
            _appliedAlpha = State.Alpha;

            // 鼠标穿透：档案开启时应用
            if (State.ClickThrough)
            {
                await _windowControl.EnableClickThroughAsync(Hwnd, true);
                _appliedClickThrough = true;
            }
            else
            {
                _appliedClickThrough = false;
            }

            // 圆角：档案非系统默认时应用
            if (State.Corner != CornerPreference.Default)
            {
                await _windowControl.SetCornerPreferenceAsync(Hwnd, State.Corner);
            }
            _appliedCorner = State.Corner;

            ClickThrough = State.ClickThrough;
            Corner = State.Corner;
            Alpha = State.Alpha;

            // —— 几何分支（窗口位置记忆工作包）：恢复档案矩形，自带三道预检 ——
            // ① 最小化（IsIconic）：窗口矩形是 (-32000,-32000) 挂起坐标，应用即错位，跳过；
            // ② 最大化（IsZoomed）：保持最大化状态，不做普通几何应用，避免破坏最大化；
            // ③ 屏幕外：档案矩形与当前全部显示器都不相交（改分辨率/拔屏后），跳过并提示。
            // 三者都只跳过本次应用，档案值一律保留（用户可能只是暂时最小化 / 临时改分辨率）。
            if (restoreBounds && State.HasBounds)
            {
                WindowMinMaxState minMax = await _windowControl.GetMinMaxStateAsync(Hwnd);
                if (minMax.IsIconic)
                {
                    RestoreDiagnostics.Log($"几何恢复：hwnd=0x{Hwnd:X}「{DisplayName}」处于最小化，跳过几何应用（档案值保留）。");
                }
                else if (minMax.IsZoomed)
                {
                    RestoreDiagnostics.Log($"几何恢复：hwnd=0x{Hwnd:X}「{DisplayName}」处于最大化，跳过几何应用（保持最大化状态）。");
                }
                else
                {
                    var archived = State.ToBounds();
                    IReadOnlyList<WindowBounds> monitors = await _windowControl.GetMonitorRectsAsync();
                    if (!WindowBounds.IsOnAnyMonitor(archived, monitors))
                    {
                        RestoreDiagnostics.Log($"几何恢复：hwnd=0x{Hwnd:X}「{DisplayName}」档案矩形 {archived} 不在任何显示器上，跳过。");
                        _reportBoundsSkippedOffscreen(archived);
                    }
                    else
                    {
                        await _windowControl.SetWindowBoundsAsync(Hwnd, archived);
                        RestoreDiagnostics.Log($"几何恢复：hwnd=0x{Hwnd:X}「{DisplayName}」已应用档案矩形 {archived}。");
                    }
                }
            }

            // 隐私遮蔽协同（隐私窗口工作包）：若该窗口当前处于遮蔽态，档案恢复会把
            // alpha 抬回档案值（用户能看见内容）——必须立刻重新套遮蔽，
            // 否则「在列表里选中一下」就把被保护的内容曝光了。
            // 目标 alpha 重新取档案值计算（可能刚被用户改动过）。
            if (IsPrivacyMasked)
            {
                await _windowControl.SetTransparencyAsync(Hwnd, PrivacyMaskRules.ComputeMaskAlpha(State.Alpha));
            }
        }
        catch (Exception ex)
        {
            // UIPI 专项（WP5）：错误 5 时给出「以管理员身份重启」引导，其余保持原语义
            _reportError(ProcessElevation.DescribeFailure(
                ex, $"恢复窗口「{DisplayName}」的已保存属性"));
        }
    }

    /// <summary>
    /// 「记住当前位置」捕获命令（窗口位置记忆工作包）：把窗口当前矩形写入档案并实时保存。
    /// 捕获前置检查：最小化 / 最大化窗口一律不捕获——最小化矩形是 -32000 挂起值、
    /// 最大化矩形恢复成普通窗口语义也是错的；两者都保持档案不变。
    /// </summary>
    public async Task CaptureCurrentBoundsAsync()
    {
        try
        {
            WindowMinMaxState minMax = await _windowControl.GetMinMaxStateAsync(Hwnd);
            if (minMax.IsIconic || minMax.IsZoomed)
            {
                _reportError(
                    $"未能记住「{DisplayName}」的当前位置：" +
                    (minMax.IsIconic ? "窗口处于最小化状态，此时记录的坐标是系统挂起值。" : "窗口处于最大化状态，此时记录的尺寸是最大化尺寸。") +
                    "请先把窗口还原成普通大小，再点「记住当前位置」。");
                return;
            }

            WindowBounds current = await _windowControl.GetWindowBoundsAsync(Hwnd);
            State.SetBounds(current);
            _persist(State);
            _reportBoundsCaptured(current);
        }
        catch (Exception ex)
        {
            _reportError(ProcessElevation.DescribeFailure(ex, $"记住窗口「{DisplayName}」的当前位置"));
        }
    }

    /// <summary>设置置顶状态（幂等语义：按目标值直接设置，以服务读回的实际状态为准）。</summary>
    public async Task SetTopmostAsync(bool desired)
    {
        try
        {
            await _windowControl.SetTopmostAsync(Hwnd, desired);
            var actual = await _windowControl.GetIsTopmostAsync(Hwnd);
            _appliedTopmost = actual;
            IsTopmost = actual;
            State.IsTopmost = actual;
            _persist(State);
            if (actual != desired)
            {
                _reportError($"窗口「{DisplayName}」置顶结果与请求不一致，当前实际为：{(actual ? "已置顶" : "未置顶")}。");
            }
        }
        catch (Exception ex)
        {
            _reportError(ProcessElevation.DescribeFailure(ex, $"切换窗口「{DisplayName}」置顶"));
        }
    }

    /// <summary>
    /// 应用「窗口隐私模式」开关（单一路径：界面 ToggleSwitch / 热键 / 回滚都汇到这里）。
    /// 写档案 → 计算即时迁移 → 投入 ViewModel 串行链执行（Win32 调用 + 遮蔽态更新）。
    /// 已在前台的窗口开启隐私时【不】立即遮蔽——用户正在用的窗口不该被压暗，
    /// 失焦时由前台监听自动遮蔽（见 PrivacyMaskRules.DecideTransitions 的语义）。
    /// </summary>
    private async Task ApplyPrivacyAsync(bool enable)
    {
        try
        {
            State.PrivacyEnabled = enable;
            _persist(State);

            var transitions = new List<PrivacyMaskRules.PrivacyMaskTransition>(1);

            if (enable)
            {
                bool isForeground = ShellNativeMethods.GetForegroundWindow() == Hwnd;
                if (!isForeground)
                {
                    transitions.Add(new PrivacyMaskRules.PrivacyMaskTransition(
                        Hwnd, PrivacyMaskRules.ComputeMaskAlpha(State.Alpha), Masked: true));
                }
            }
            else if (IsPrivacyMasked)
            {
                // 关闭开关且正处于遮蔽态：立即按档案值恢复（档案值 = 用户手动设置的透明度，
                // 不是硬编码 255——这是与既有透明度功能协同的硬规则，见 ADR 决策五）
                transitions.Add(new PrivacyMaskRules.PrivacyMaskTransition(Hwnd, State.Alpha, Masked: false));
            }

            if (transitions.Count > 0)
            {
                _enqueuePrivacyTransitions(transitions);
            }

            _notifyPrivacyChanged();
        }
        catch (Exception ex)
        {
            _reportError(ProcessElevation.DescribeFailure(ex,
                enable ? $"开启窗口「{DisplayName}」的隐私模式" : $"关闭窗口「{DisplayName}」的隐私模式"));

            // 失败回滚：档案与开关显示退回原值，保证「档案 = 界面 = 实际效果」三者一致；
            // _suppressPrivacyApply 防止回滚写再触发一次新的应用。
            State.PrivacyEnabled = !enable;
            _persist(State);
            _suppressPrivacyApply = true;
            IsPrivacyEnabled = !enable;
            _suppressPrivacyApply = false;
        }
    }

    /// <summary>设置透明度（0 全透明 ~ 255 不透明）并实时保存。</summary>
    public async Task SetAlphaAsync(byte alpha)
    {
        if (alpha == _appliedAlpha)
        {
            return;
        }

        try
        {
            if (IsPrivacyMasked)
            {
                // 隐私遮蔽协同（隐私窗口工作包）：遮蔽态下拖滑块【不能】把真实窗口改亮——
                // 否则用户一次拖动就把「失焦遮蔽」抬亮、隐私失效。只更新档案值与界面显示，
                // 新值在该窗口重新聚焦（恢复路径按档案值应用）时生效。
                _appliedAlpha = alpha;
                Alpha = alpha;
                State.Alpha = alpha;
                _persist(State);
                return;
            }

            await _windowControl.SetTransparencyAsync(Hwnd, alpha);
            _appliedAlpha = alpha;
            Alpha = alpha;
            State.Alpha = alpha;
            _persist(State);
        }
        catch (Exception ex)
        {
            _reportError(ProcessElevation.DescribeFailure(ex, $"设置窗口「{DisplayName}」透明度"));
        }
    }

    /// <summary>启用 / 关闭鼠标穿透并实时保存。</summary>
    public async Task SetClickThroughAsync(bool enable)
    {
        if (enable == _appliedClickThrough)
        {
            return;
        }

        try
        {
            await _windowControl.EnableClickThroughAsync(Hwnd, enable);
            _appliedClickThrough = enable;
            ClickThrough = enable;
            State.ClickThrough = enable;
            _persist(State);
        }
        catch (Exception ex)
        {
            _reportError(ProcessElevation.DescribeFailure(ex, $"设置窗口「{DisplayName}」鼠标穿透"));
        }
    }

    /// <summary>设置圆角偏好（无 / 大圆角 / 小圆角）并实时保存。</summary>
    public async Task SetCornerAsync(CornerPreference preference)
    {
        if (preference == _appliedCorner)
        {
            return;
        }

        try
        {
            await _windowControl.SetCornerPreferenceAsync(Hwnd, preference);
            _appliedCorner = preference;
            Corner = preference;
            State.Corner = preference;
            _persist(State);
        }
        catch (Exception ex)
        {
            _reportError(ProcessElevation.DescribeFailure(ex, $"设置窗口「{DisplayName}」圆角"));
        }
    }

    /// <summary>
    /// 把窗口属性的新值由外部路径（v0.3.3 焦点窗口热键）直接同步进本条目。
    ///
    /// 为什么不直接调上面的 Set*Async：那组方法各自有「目标值 == 已应用值就短路」的
    /// 防抖设计，而热键路径已经【实际应用过】Win32 调用，这里只需让条目内部的三份状态
    /// 对齐 —— 可绑定属性（界面详情面板 / 选中项镜像）、持久化档案（State）、
    /// 「已应用基准」（_applied*）。少了最后一份，之后用户在界面上把滑块拖回
    /// 热键设置前的旧值时会被短路吞掉，表现为「滑块动了窗口没变」。
    /// 落盘仍由调用方统一执行（MainViewModel.PersistState），本方法不重复写盘。
    /// </summary>
    internal void SyncApplied(bool topmost, bool clickThrough, byte alpha, CornerPreference corner)
    {
        _appliedTopmost = topmost;
        _appliedClickThrough = clickThrough;
        _appliedAlpha = alpha;
        _appliedCorner = corner;

        IsTopmost = topmost;
        ClickThrough = clickThrough;
        Alpha = alpha;
        Corner = corner;

        State.IsTopmost = topmost;
        State.ClickThrough = clickThrough;
        State.Alpha = alpha;
        State.Corner = corner;
    }
}
