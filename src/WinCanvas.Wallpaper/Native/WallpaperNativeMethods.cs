using System;
using System.Runtime.InteropServices;

namespace WinCanvas.Wallpaper.Native;

/// <summary>
/// 壁纸模块专用的 Win32 API 封装。
/// 仅用于全屏前台窗口检测与显示器分辨率获取，全部 P/Invoke 均带详细中文注释。
/// </summary>
internal static class WallpaperNativeMethods
{
    #region 前台窗口 / 全屏检测

    /// <summary>
    /// 获取当前前台（获得焦点、位于 Z 序顶端）的窗口句柄。
    /// </summary>
    /// <returns>前台窗口句柄；无前台窗口（如锁屏）时返回 IntPtr.Zero。</returns>
    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 判断指定显示器上的窗口是否构成"全屏前台窗口"：
    /// 前台窗口无边框、置顶、且矩形完整覆盖所在显示器的整个矩形。
    /// </summary>
    /// <param name="borderlessOnly">
    /// true = 仅把无边框置顶窗口视为全屏（排除资源管理器最大化窗口的误判，
    /// 因最大化窗口带标题栏且非 WS_EX_TOPMOST）；false = 只要覆盖显示器即算。
    /// </param>
    /// <returns>true = 检测到全屏前台窗口（含 D3D 独占/无边框全屏游戏）。</returns>
    public static bool IsForegroundWindowFullscreen(bool borderlessOnly = true)
    {
        // 1. 拿到前台窗口句柄；没有前台窗口时不可能全屏
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        // 2. 查询前台窗口的扩展样式：全屏程序（游戏/播放器）通常带 WS_EX_TOPMOST 置顶位
        var exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);

        if (borderlessOnly && (exStyle & WS_EX_TOPMOST) == 0)
            return false;

        // 3. 窗口自身矩形
        if (!GetWindowRect(hwnd, out var windowRect))
            return false;

        // 4. 该窗口所在显示器的完整矩形（MONITOR_DEFAULTTONEAREST：不在任何显示器上时取最近的）
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO
        {
            // GetMonitorInfoW 要求调用方先填 cbSize，否则返回失败
            cbSize = (uint) Marshal.SizeOf<MONITORINFO>(),
        };

        if (!GetMonitorInfoW(monitor, ref info))
            return false;

        // 5. 窗口矩形完整覆盖显示器矩形 => 认定为全屏前台窗口
        return windowRect.Left <= info.rcMonitor.Left
               && windowRect.Top <= info.rcMonitor.Top
               && windowRect.Right >= info.rcMonitor.Right
               && windowRect.Bottom >= info.rcMonitor.Bottom;
    }

    /// <summary>
    /// 获取指定窗口所在显示器（壁纸承载窗口所在的屏）的分辨率。
    /// 多显示器场景下，导入图片缩放的目标分辨率就是这张屏的分辨率。
    /// </summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    /// <param name="width">输出：显示器宽度（像素）。</param>
    /// <param name="height">输出：显示器高度（像素）。</param>
    /// <returns>true = 成功获取；false = 句柄无效或查询失败（此时调用方应回退主屏分辨率）。</returns>
    public static bool TryGetMonitorResolutionForWindow(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO
        {
            // GetMonitorInfoW 要求调用方先填 cbSize，否则返回失败
            cbSize = (uint) Marshal.SizeOf<MONITORINFO>(),
        };

        if (!GetMonitorInfoW(monitor, ref info))
            return false;

        width = info.rcMonitor.Right - info.rcMonitor.Left;
        height = info.rcMonitor.Bottom - info.rcMonitor.Top;
        return width > 0 && height > 0;
    }

    #endregion

    #region 原生声明

    /// <summary>
    /// GetWindowLongW 的索引：取 GWL_EXSTYLE（扩展窗口样式位）。
    /// </summary>
    private const int GWL_EXSTYLE = -20;

    /// <summary>
    /// 扩展样式位：顶层窗口（不 Taskbar 排序、不被其他窗口盖住）。
    /// 全屏游戏/播放器通常带此位，普通最大化窗口不带，用作全屏判定条件之一。
    /// </summary>
    private const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>
    /// MonitorFromWindow 的标志：窗口与任何显示器都不相交时，返回最近的显示器句柄。
    /// </summary>
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// RECT：Windows 原生矩形（左上角在屏幕左上，y 向下）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;   // 左边界 x
        public int Top;    // 上边界 y
        public int Right;  // 右边界 x（不含）
        public int Bottom; // 下边界 y（不含）
    }

    /// <summary>
    /// MONITORINFO：显示器信息（GetMonitorInfoW 的输出结构）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;       // 结构体大小（调用方必须先填，API 用它做版本校验）
        public RECT rcMonitor;    // 显示器完整矩形（虚拟屏幕坐标）
        public RECT rcWork;       // 去掉任务栏后的工作区矩形
        public uint dwFlags;      // 标志位（如 MONITORINFOF_PRIMARY 表示主显示器）
    }

    /// <summary>
    /// 读取窗口属性（此处用于取 GWL_EXSTYLE 扩展样式）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要取的属性索引（GWL_EXSTYLE = -20）。</param>
    /// <returns>对应属性值；失败返回 0（GetLastError 可查原因）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    /// <summary>
    /// 获取窗口（含非客户区）的外接矩形。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpRect">输出：窗口矩形。</param>
    /// <returns>true = 成功；false = 窗口句柄无效。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// 取窗口所在（或最近）显示器的句柄。
    /// </summary>
    /// <param name="hwnd">参考窗口句柄。</param>
    /// <param name="dwFlags">MONITOR_DEFAULTTONEAREST = 2：不相交时取最近显示器。</param>
    /// <returns>显示器句柄（HMONITOR）；失败返回 IntPtr.Zero。</returns>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>
    /// 查询显示器信息（矩形、工作区等）。
    /// </summary>
    /// <param name="hMonitor">MonitorFromWindow 返回的显示器句柄。</param>
    /// <param name="lpmi">输出结构；调用方必须先填 cbSize。</param>
    /// <returns>true = 成功；false = 句柄无效。</returns>
    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    #endregion
}
