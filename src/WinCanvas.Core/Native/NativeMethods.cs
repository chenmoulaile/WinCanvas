using System.Runtime.InteropServices;

namespace WinCanvas.Core.Native;

/// <summary>
/// Win32 原生 API 封装（user32 / dwmapi / kernel32）。
/// 每个 P/Invoke 均注明：作用、参数含义、返回值含义。
/// </summary>
internal static partial class NativeMethods
{
    // ===== SetWindowPos 的 hWndInsertAfter 取值 =====
    /// <summary>HWND_TOPMOST(-1)：把窗口放到所有非置顶窗口之上（进入置顶组）。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>HWND_NOTOPMOST(-2)：把窗口放到所有非置顶窗口之上、但移出置顶组（取消置顶）。</summary>
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ===== SetWindowPos 的 uFlags =====
    /// <summary>SWP_NOSIZE(0x0001)：保持当前尺寸不变。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>SWP_NOMOVE(0x0002)：保持当前位置不变。</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>SWP_NOACTIVATE(0x0010)：不激活窗口（不抢焦点）。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    // ===== GetWindowLong / SetWindowLong 的 nIndex =====
    /// <summary>GWL_EXSTYLE(-20)：读取/设置窗口扩展样式。</summary>
    public const int GWL_EXSTYLE = -20;

    // ===== 窗口扩展样式位 =====
    /// <summary>WS_EX_LAYERED(0x00080000)：分层窗口，允许按 alpha 设置整体透明度。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>WS_EX_TRANSPARENT(0x00000020)：鼠标穿透（必须配合 WS_EX_LAYERED 才生效）。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_TOPMOST(0x00000008)：窗口处于置顶组。</summary>
    public const int WS_EX_TOPMOST = 0x00000008;

    // ===== SetLayeredWindowAttributes =====
    /// <summary>LWA_ALPHA(0x2)：使用 bAlpha 参数做整体透明度。</summary>
    public const uint LWA_ALPHA = 0x2;

    // ===== DwmSetWindowAttribute 的属性 Id 与圆角取值 =====
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE(33)：Win11 的窗口圆角偏好属性。</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWMWCP_DONOTROUND(1)：不使用圆角。</summary>
    public const int DWMWCP_DONOTROUND = 1;

    /// <summary>DWMWCP_ROUND(2)：标准大圆角。</summary>
    public const int DWMWCP_ROUND = 2;

    /// <summary>DWMWCP_ROUNDSMALL(3)：小圆角。</summary>
    public const int DWMWCP_ROUNDSMALL = 3;

    /// <summary>
    /// EnumWindows 的回调委托：返回 true 继续枚举，返回 false 停止枚举。
    /// </summary>
    /// <param name="hWnd">当前枚举到的顶层窗口句柄。</param>
    /// <param name="lParam">调用方传入的自定义参数（原样回传）。</param>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 枚举屏幕上所有顶层窗口，对每个窗口调用一次回调。
    /// </summary>
    /// <param name="lpEnumFunc">回调委托（注意：回调期间必须保持委托被引用，防止被 GC 回收）。</param>
    /// <param name="lParam">传给回调的自定义参数，本程序传 IntPtr.Zero。</param>
    /// <returns>true = 枚举完整跑完；false = 被回调提前终止或调用失败（用 GetLastError 取错误码）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// 判断窗口是否可见（含 WS_VISIBLE 样式，且所有者链可见）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <returns>true = 可见；false = 不可见或句柄无效。</returns>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// 返回窗口标题的字符长度（不含结尾 '\0'）；无标题返回 0。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <returns>标题字符数；失败返回 0（无标题与失败均返回 0，需用 GetLastError 区分，本程序无需区分）。</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>
    /// 把窗口标题复制到缓冲区。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpString">接收标题的缓冲区（Unicode）。</param>
    /// <param name="nMaxCount">缓冲区可容纳的最大字符数（含结尾 '\0'），标题会被截断到该长度。</param>
    /// <returns>实际复制的字符数（不含 '\0'）；无标题返回 0。</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>
    /// 把窗口类名复制到缓冲区。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpClassName">接收类名的缓冲区（Unicode）。</param>
    /// <param name="nMaxCount">缓冲区可容纳的最大字符数（含结尾 '\0'）。</param>
    /// <returns>实际复制的字符数（不含 '\0'）；失败返回 0。</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>
    /// 获取创建窗口的线程 Id 与进程 Id。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpdwProcessId">输出参数：接收进程 Id。</param>
    /// <returns>创建该窗口的线程 Id。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// 改变窗口的 Z 序、位置或尺寸（本程序仅用它做置顶切换）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="hWndInsertAfter">Z 序参照：HWND_TOPMOST(-1) 置顶 / HWND_NOTOPMOST(-2) 取消置顶。</param>
    /// <param name="X">新的左上角 X（本程序配合 SWP_NOMOVE 忽略，传 0）。</param>
    /// <param name="Y">新的左上角 Y（同上）。</param>
    /// <param name="cx">新的宽度（配合 SWP_NOSIZE 忽略，传 0）。</param>
    /// <param name="cy">新的高度（同上）。</param>
    /// <param name="uFlags">SWP_* 组合：NOMOVE|NOSIZE|NOACTIVATE 表示只改 Z 序且不抢焦点。</param>
    /// <returns>true = 成功；false = 失败（用 GetLastError 取错误码）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 设置分层窗口（WS_EX_LAYERED）的整体透明度或颜色键。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄（必须已带 WS_EX_LAYERED 样式）。</param>
    /// <param name="crKey">颜色键（本程序做 alpha 透明，传 0 不使用）。</param>
    /// <param name="bAlpha">整体透明度 0（全透明）~ 255（不透明）。</param>
    /// <param name="dwFlags">LWA_ALPHA(0x2)：启用 bAlpha 透明度方式。</param>
    /// <returns>true = 成功；false = 失败（用 GetLastError 取错误码）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // ===== 32/64 位 GetWindowLong / SetWindowLong 原生入口 =====
    // 说明：user32.dll 在 64 位进程导出 GetWindowLongPtrW / SetWindowLongPtrW（处理指针大小的
    // GWL_EXSTYLE 等值），在 32 位进程只有 GetWindowLongW / SetWindowLongW。
    // 为了让代码在 x86 / x64 都能正确读写 64 位宽的值，必须按进程位数选择入口，
    // 由下方 GetWindowLongPtrCompat / SetWindowLongPtrCompat 封装。

    /// <summary>32 位进程使用的 GetWindowLongW 入口。</summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要读取的偏移（如 GWL_EXSTYLE=-20）。</param>
    /// <returns>原值（32 位宽）；失败返回 0（用 GetLastError 区分）。</returns>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    /// <summary>64 位进程使用的 GetWindowLongPtrW 入口。</summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要读取的偏移（如 GWL_EXSTYLE=-20）。</param>
    /// <returns>原值（64 位宽，指针大小）；失败返回 0（用 GetLastError 区分）。</returns>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    /// <summary>32 位进程使用的 SetWindowLongW 入口。</summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要设置的偏移（如 GWL_EXSTYLE=-20）。</param>
    /// <param name="dwNewLong">新值（32 位宽）。</param>
    /// <returns>设置前的旧值；失败返回 0（用 GetLastError 区分）。</returns>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>64 位进程使用的 SetWindowLongPtrW 入口。</summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要设置的偏移（如 GWL_EXSTYLE=-20）。</param>
    /// <param name="dwNewLong">新值（64 位宽，指针大小）。</param>
    /// <returns>设置前的旧值；失败返回 0（用 GetLastError 区分）。</returns>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>
    /// GetWindowLong 的 32/64 位兼容封装：按进程位数自动选择 GetWindowLongPtrW / GetWindowLongW。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要读取的偏移（如 GWL_EXSTYLE=-20）。</param>
    /// <returns>当前值；失败返回 IntPtr.Zero（用 GetLastError 区分）。</returns>
    public static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)                       // 64 位进程
            : new IntPtr(GetWindowLong32(hWnd, nIndex));             // 32 位进程

    /// <summary>
    /// SetWindowLong 的 32/64 位兼容封装：按进程位数自动选择 SetWindowLongPtrW / SetWindowLongW。
    /// 调用方需自行检查返回值（旧值为 0 时需结合 GetLastError 判断是否失败）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nIndex">要设置的偏移（如 GWL_EXSTYLE=-20）。</param>
    /// <param name="dwNewLong">新值。</param>
    /// <returns>设置前的旧值；失败返回 IntPtr.Zero（用 GetLastError 区分）。</returns>
    public static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)            // 64 位进程
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32())); // 32 位进程

    /// <summary>
    /// 读取窗口的 DWM 属性（整数型）。本程序用于 DWMWA_WINDOW_CORNER_PREFERENCE(33)。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="dwAttribute">要读取的 DWM 属性 Id（如 33）。</param>
    /// <param name="pvAttribute">接收属性值的缓冲区（整数属性传 int 指针）。</param>
    /// <param name="cbAttribute">缓冲区字节数（int 属性为 4）。</param>
    /// <returns>HRESULT：S_OK(0) 成功；负值为错误码（用 Marshal.ThrowExceptionForHR 抛出）。</returns>
    [DllImport("dwmapi.dll", SetLastError = false)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// 设置窗口的 DWM 属性（整数型）。本程序用于 DWMWA_WINDOW_CORNER_PREFERENCE(33) 控制圆角。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="dwAttribute">要设置的 DWM 属性 Id（如 33）。</param>
    /// <param name="pvAttribute">属性值指针（整数属性传 int 指针）。</param>
    /// <param name="cbAttribute">值字节数（int 属性为 4）。</param>
    /// <returns>HRESULT：S_OK(0) 成功；负值为错误码。Win10 1809 之前不认识该属性，返回 E_INVALIDARG。</returns>
    [DllImport("dwmapi.dll", SetLastError = false)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
