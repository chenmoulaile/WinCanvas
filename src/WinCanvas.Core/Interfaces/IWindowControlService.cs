using WinCanvas.Core.Models;

namespace WinCanvas.Core.Interfaces;

/// <summary>
/// 窗口控制服务：枚举顶层窗口并对其执行置顶 / 透明 / 鼠标穿透 / 圆角等 Win32 操作。
/// </summary>
public interface IWindowControlService
{
    /// <summary>
    /// 在后台线程枚举所有可见的顶层窗口。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可见顶层窗口列表（含 hwnd / 标题 / 类名 / 进程名 / 进程 Id）。</returns>
    Task<IReadOnlyList<WindowInfo>> EnumerateTopLevelWindowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换指定窗口的置顶状态：已置顶则取消置顶，未置顶则置顶。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>切换后窗口是否处于置顶状态。</returns>
    Task<bool> ToggleTopmostAsync(IntPtr hwnd);

    /// <summary>
    /// 设置窗口透明度（通过 WS_EX_LAYERED + SetLayeredWindowAttributes）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="alpha">透明度 0（全透明）~ 255（不透明）。</param>
    Task SetTransparencyAsync(IntPtr hwnd, byte alpha);

    /// <summary>
    /// 启用 / 禁用鼠标穿透（WS_EX_TRANSPARENT，必须与 WS_EX_LAYERED 成对设置/移除）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="enable">true 启用穿透，false 关闭穿透。</param>
    Task EnableClickThroughAsync(IntPtr hwnd, bool enable);

    /// <summary>
    /// 设置窗口圆角（DWMWA_WINDOW_CORNER_PREFERENCE）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="enableSmallRadius">true 使用小圆角（DWMWCP_ROUNDSMALL），false 使用标准大圆角（DWMWCP_ROUND）。
    /// 关闭圆角（DWMWCP_DONOTROUND）请使用 <see cref="SetCornerPreferenceAsync"/>。</param>
    Task SetRoundedCornersAsync(IntPtr hwnd, bool enableSmallRadius = false);

    /// <summary>
    /// 设置任意圆角偏好（含关闭圆角 DWMWCP_DONOTROUND）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="preference">圆角偏好。</param>
    Task SetCornerPreferenceAsync(IntPtr hwnd, CornerPreference preference);
}
