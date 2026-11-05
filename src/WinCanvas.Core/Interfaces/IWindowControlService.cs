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
    /// 读取窗口当前的实际置顶状态（WS_EX_TOPMOST 样式位）。
    /// 用于恢复档案前先比对真实状态，避免把已置顶的窗口「翻转」掉。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>true = 窗口当前已置顶。</returns>
    Task<bool> GetIsTopmostAsync(IntPtr hwnd);

    /// <summary>
    /// 将窗口置顶 / 取消置顶（幂等语义：重复设置同一状态无副作用）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="topmost">true = 置顶（HWND_TOPMOST），false = 取消置顶（HWND_NOTOPMOST）。</param>
    Task SetTopmostAsync(IntPtr hwnd, bool topmost);

    /// <summary>
    /// 设置窗口透明度（通过 WS_EX_LAYERED + SetLayeredWindowAttributes）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="alpha">透明度 0（全透明）~ 255（不透明）。</param>
    Task SetTransparencyAsync(IntPtr hwnd, byte alpha);

    /// <summary>
    /// 启用 / 禁用鼠标穿透（WS_EX_TRANSPARENT）。
    /// 启用时一并置位 WS_EX_LAYERED（穿透样式只有在分层窗口上才生效）；
    /// 关闭时只清 WS_EX_TRANSPARENT、保留 WS_EX_LAYERED —— 透明度功能仍依赖分层样式，
    /// 若一并清除会把用户已设置的窗口透明效果一起抹掉。
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

    // ===== 窗口几何（窗口位置记忆工作包新增） =====

    /// <summary>
    /// 读取窗口当前的外接矩形（物理像素、虚拟桌面坐标，与 SetWindowPos 同一口径）。
    /// 句柄无效或 GetWindowRect 失败时抛 <see cref="Win32Exception"/>（与既有失败语义一致）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>窗口当前矩形。注意：最小化窗口返回的是 (-32000,-32000) 挂起矩形，
    /// 调用方须先用 <see cref="GetMinMaxStateAsync"/> 预检。</returns>
    Task<WindowBounds> GetWindowBoundsAsync(IntPtr hwnd);

    /// <summary>
    /// 应用窗口矩形（位置 + 尺寸，SetWindowPos，不改 Z 序、不抢焦点）。
    /// 幂等语义：对已处于目标几何的窗口重复调用无副作用（位置差为 0 时窗口不动）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="bounds">目标矩形（物理像素）。</param>
    Task SetWindowBoundsAsync(IntPtr hwnd, WindowBounds bounds);

    /// <summary>
    /// 读取窗口的最小化 / 最大化状态（IsIconic / IsZoomed）。
    /// 几何恢复与几何捕获前都必须先查：最小化窗口的矩形是 -32000 挂起值、
    /// 最大化窗口应保持最大化而不是被改写成普通矩形。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>状态快照；句柄无效时两个标志均为 false（与 Win32 语义一致，调用方另行预检存活）。</returns>
    Task<WindowMinMaxState> GetMinMaxStateAsync(IntPtr hwnd);

    /// <summary>
    /// 枚举当前会话全部显示器的矩形（物理像素，虚拟桌面坐标）。
    /// 供「档案矩形是否落在任何显示器上」的屏幕外判定使用。
    /// </summary>
    /// <returns>每台显示器一条矩形；枚举失败抛 <see cref="Win32Exception"/>。</returns>
    Task<IReadOnlyList<WindowBounds>> GetMonitorRectsAsync();

    /// <summary>
    /// 同步读取窗口当前矩形；失败或句柄无效返回 null（不抛）。
    /// 仅供【应用退出】的一次性捕获路径使用：那里处在窗口关闭回调中，
    /// 消息循环即将结束，异步派发可能没有机会完成，必须同步拿结果；
    /// 常规业务路径请用 <see cref="GetWindowBoundsAsync"/>。
    /// </summary>
    WindowBounds? TryGetWindowBoundsSync(IntPtr hwnd);

    /// <summary>
    /// 同步读取窗口最小化 / 最大化状态。使用场景限制同 <see cref="TryGetWindowBoundsSync"/>。
    /// </summary>
    WindowMinMaxState GetMinMaxStateSync(IntPtr hwnd);
}
