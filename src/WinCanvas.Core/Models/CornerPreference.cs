namespace WinCanvas.Core.Models;

/// <summary>
/// 窗口圆角偏好，取值与 Win32 DWMWCP_* 常量一一对应。
/// </summary>
public enum CornerPreference
{
    /// <summary>由系统按 Windows 版本默认行为决定（DWMWCP_DEFAULT）。</summary>
    Default = 0,

    /// <summary>不使用圆角（DWMWCP_DONOTROUND = 1）。</summary>
    DoNotRound = 1,

    /// <summary>标准大圆角（DWMWCP_ROUND = 2）。</summary>
    Round = 2,

    /// <summary>小圆角（DWMWCP_ROUNDSMALL = 3）。</summary>
    RoundSmall = 3,
}
