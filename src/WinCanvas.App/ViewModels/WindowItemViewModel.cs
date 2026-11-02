using CommunityToolkit.Mvvm.ComponentModel;
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

    public WindowItemViewModel(
        WindowInfo info,
        WindowState state,
        IWindowControlService windowControl,
        Action<WindowState> persist,
        Action<string> reportError)
    {
        Info = info;
        State = state;
        _windowControl = windowControl;
        _persist = persist;
        _reportError = reportError;

        DisplayName = string.IsNullOrWhiteSpace(info.Title) ? "（无标题窗口）" : info.Title;
        Subtitle = $"{info.ProcessName} ({info.ProcessId}) · {info.ClassName}";
        HwndText = $"0x{info.Hwnd.ToInt64():X}";

        // 把持久化的值恢复到界面属性（真正应用到窗口在选中时由 RestoreAsync 执行）
        IsTopmost = state.IsTopmost;
        Alpha = state.Alpha;
        ClickThrough = state.ClickThrough;
        Corner = state.Corner;
    }

    /// <summary>
    /// 选中该窗口时恢复其已保存的属性档案：
    /// 把持久化的置顶 / 透明 / 穿透 / 圆角值实际应用回窗口，
    /// 保证界面开关、档案值与窗口真实状态三者一致。
    /// 置顶采用「先读真实状态、仅在不一致时按目标值设置」的幂等语义 ——
    /// 不能用翻转语义，否则窗口实际已置顶（本次会话设置过 / 刷新后重新选中）时，
    /// 恢复动作反而会把置顶翻掉并把错误状态写盘。
    /// </summary>
    public async Task RestoreAsync()
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
        }
        catch (Exception ex)
        {
            // UIPI 专项（WP5）：错误 5 时给出「以管理员身份重启」引导，其余保持原语义
            _reportError(ProcessElevation.DescribeFailure(
                ex, $"恢复窗口「{DisplayName}」的已保存属性"));
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

    /// <summary>设置透明度（0 全透明 ~ 255 不透明）并实时保存。</summary>
    public async Task SetAlphaAsync(byte alpha)
    {
        if (alpha == _appliedAlpha)
        {
            return;
        }

        try
        {
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
}
