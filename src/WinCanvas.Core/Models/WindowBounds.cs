namespace WinCanvas.Core.Models;

/// <summary>
/// 窗口几何矩形（位置 + 尺寸），坐标与尺寸均为【物理像素】、虚拟桌面坐标系
/// （与 Win32 GetWindowRect / SetWindowPos 同一口径，存什么恢复什么，不做 DPI 换算）。
/// 窗口位置记忆工作包新增：经 <see cref="IWindowControlService"/> 在服务层与
/// <see cref="WindowState"/> 的几何字段之间搬运。
/// </summary>
/// <param name="X">窗口左上角 X（物理像素）。</param>
/// <param name="Y">窗口左上角 Y（物理像素）。</param>
/// <param name="Width">窗口宽度（物理像素，须为正）。</param>
/// <param name="Height">窗口高度（物理像素，须为正）。</param>
public sealed record WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>最小化窗口的 GetWindowRect 特征横坐标（winuser.h 的挂起位置）。</summary>
    public const int MinimizedOffscreenX = -32000;

    /// <summary>
    /// 屏幕外矩形判定（纯函数，供恢复管线与单测共用）：判断一个档案矩形是否与
    /// 传入的【任一显示器矩形】有实际交叠。没有任何交叠 = 该矩形落在当前全部
    /// 显示器可见区域之外（典型场景：改了分辨率 / 拔掉了显示器），恢复时应跳过，
    /// 不把窗口丢到屏幕外。
    /// </summary>
    /// <param name="bounds">待判定的档案矩形。</param>
    /// <param name="monitors">当前全部显示器的矩形（每个显示器一条，调用方经显示器枚举取得）。</param>
    /// <returns>true = 与至少一个显示器有交叠（可安全恢复）；
    /// false = 全部显示器之外（应跳过恢复）。monitorRects 为空恒返回 false。</returns>
    public static bool IsOnAnyMonitor(WindowBounds bounds, IReadOnlyList<WindowBounds> monitors)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || monitors.Count == 0)
        {
            return false;
        }

        foreach (WindowBounds monitor in monitors)
        {
            // 轴对齐矩形相交判定：两个矩形有交叠 ⇔ 每个轴上的区间都重叠
            //（X: bounds.X < m.Right 且 bounds.Right > m.X；Y 同理）。
            if (bounds.X < monitor.X + monitor.Width
                && bounds.X + bounds.Width > monitor.X
                && bounds.Y < monitor.Y + monitor.Height
                && bounds.Y + bounds.Height > monitor.Y)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 窗口的最小化 / 最大化状态快照（IsIconic / IsZoomed 的结果对）。
/// 几何恢复前用它做预检：两种状态下应用普通矩形都是错的（-32000 错位 / 破坏最大化）。
/// </summary>
/// <param name="IsIconic">窗口已最小化。</param>
/// <param name="IsZoomed">窗口已最大化。</param>
public sealed record WindowMinMaxState(bool IsIconic, bool IsZoomed);
