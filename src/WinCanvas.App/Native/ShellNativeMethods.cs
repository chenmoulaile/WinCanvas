using System.Runtime.InteropServices;

namespace WinCanvas.App.Native;

/// <summary>
/// 系统托盘与全局热键所需的 Win32 封装（v0.2.0 新增）。
///
/// 为什么手写而不用 H.NotifyIcon 之类的第三方库：本项目的宿主窗口、消息泵、
/// 键盘钩子都已经是一整套自研 Win32 设施（见 WinCanvas.Wallpaper.Hosting），
/// 再加一个 GUI 库等于引入第二套窗口/消息生命周期，net10 + unpackaged WinUI 3
/// 下的兼容性还得单独验证。托盘和热键本身就只用几个 API，自研更可控。
///
/// 约定：全部 P/Invoke 显式写 SetLastError / CharSet，句柄一律 IntPtr（不用 int，
/// 避免 64 位截断）；调用方负责检查返回值并把 Win32 错误码带进日志。
/// </summary>
internal static class ShellNativeMethods
{
    // ===== 消息常量 =====

    /// <summary>托盘回调消息基址（WM_USER + 0）。</summary>
    public const int WM_USER = 0x0400;

    /// <summary>本程序托盘图标的回调消息号（WM_USER + 1）。</summary>
    public const int WM_TRAYICON = WM_USER + 1;

    /// <summary>全局热键触发（wParam = 热键 Id，lParam = 修饰键/主键）。</summary>
    public const int WM_HOTKEY = 0x0312;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_DESTROY = 0x0002;
    public const int WM_CLOSE = 0x0010;
    public const int WM_COMMAND = 0x0111;
    public const int WM_NULL = 0x0000;

    /// <summary>设置窗口图标（wParam = ICON_SMALL / ICON_BIG，lParam = HICON）。</summary>
    public const int WM_SETICON = 0x0080;

    /// <summary>WM_SETICON 的 wParam：ICON_SMALL。</summary>
    public const int ICON_SMALL = 0;

    /// <summary>WM_SETICON 的 wParam：ICON_BIG。</summary>
    public const int ICON_BIG = 1;

    // ===== RegisterHotKey 的 fsModifiers =====
    // 与 WinCanvas.Core.Models.HotkeyModifiers 数值同构，直接强转即可。

    /// <summary>扩展键（如多媒体键）需要带这个标志注册。</summary>
    public const uint MOD_EXTENDED = 0x4000;

    /// <summary>即使目标窗口不处理该键也注册成功（避免被系统吞掉）。</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    // ===== Shell_NotifyIcon =====

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;

    /// <summary>balloon 提示图标：无图标 / 信息 / 警告 / 错误。</summary>
    public const uint NIIF_NONE = 0x00000000;
    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_WARNING = 0x00000002;
    public const uint NIIF_ERROR = 0x00000003;

    // ===== 托盘菜单 =====

    /// <summary>TrackPopupMenuEx：鼠标位置即为菜单锚点。</summary>
    public const uint TPM_LEFTALIGN = 0x0000;

    /// <summary>TrackPopupMenuEx： BOTTOMALIGN —— 托盘在屏幕底部，菜单要向上展开。</summary>
    public const uint TPM_BOTTOMALIGN = 0x0020;

    /// <summary>TrackPopupMenuEx：直接返回被选中的项 Id，不通过 WM_COMMAND 派发。</summary>
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    // ===== 图标加载 =====

    public const int IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    // ===== 窗口类 / 创建 =====

    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// NOTIFYICONDATA（Windows 10/11 当前形态）。
    /// 注意 szTip 是 128 个字符、szInfo 是 256 个字符的定长缓冲区，
    /// 结构大小必须由 <see cref="Marshal.SizeOf"/> 取得后填入 cbSize —— 填错
    /// 会让 Shell_NotifyIcon 静默失败（这是托盘图标不出现的最常见原因）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProc? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>窗口过程委托。必须保持被引用，否则被 GC 回收后回调会变成野指针。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 取当前【前台窗口】（用户正在交互、拥有键盘焦点的顶层窗口）的句柄（v0.3.3 焦点窗口热键专用）。
    /// </summary>
    /// <returns>
    /// 前台窗口句柄；【失败语义】系统当前没有前台窗口（如刚切屏、锁屏瞬间）返回
    /// IntPtr.Zero —— 调用方必须判零并以「没有焦点窗口」提示收场，不得把 0 当合法句柄
    /// 传给后续 Win32 调用（0 号句柄会被部分 API 解释成特殊值，后果不可控）。
    /// </returns>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 查询窗口所属的线程 Id 与进程 Id（v0.3.3 焦点窗口热键专用）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpdwProcessId">输出参数：接收创建该窗口的进程 Id；句柄无效时为 0。</param>
    /// <returns>创建该窗口的线程 Id；【失败语义】句柄无效时返回 0 且 lpdwProcessId 为 0
    /// —— 本程序用它做「前台窗口是否属于本进程」的判定（与 Environment.ProcessId 比较），
    /// 0 不等于任何真实进程 Id，无效句柄自然落入「非本进程窗口」分支，不会误排除。</returns>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// 把窗口标题复制到调用方缓冲区（v0.3.3 焦点窗口热键专用，用于失败提示里指明目标窗口）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpString">接收标题的 Unicode 缓冲区（建议调用方先填 '\0'，失败时内容保持原样）。</param>
    /// <param name="nMaxCount">缓冲区可容纳的最大字符数（含结尾 '\0'），标题会被截断到该长度。</param>
    /// <returns>实际复制的字符数（不含 '\0'）；【失败语义】句柄无效或无标题返回 0
    /// —— 调用方按「取不到标题」处理，用句柄值代替展示，不得据此判定窗口不存在。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // ===== 消息循环结构 =====

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // ===== 全局热键 =====

    /// <summary>
    /// 注册一个全局热键。命中时向 <paramref name="hWnd"/> 发 WM_HOTKEY，wParam = id。
    /// 失败最常见原因是 HOTKEY_ALREADY_REGISTERED（错误码 1409）——被别的程序占了。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ===== 托盘 =====

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    // ===== 托盘菜单 =====

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>
    /// 弹出托盘菜单。<see cref="TPM_RETURNCMD"/> 下同步返回被选项 Id（0 = 取消）。
    /// 调用前必须 SetForegroundWindow(ownHwnd)，否则菜单点不中、不自动收起。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    // ===== 图标 =====

    /// <summary>从 .ico 文件加载图标；返回 HICON，用完必须 <see cref="DestroyIcon"/>。</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, int uType,
        int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 读取虚拟键的实时状态。返回值最高位为 1 表示按下。
    ///
    /// 为什么用 Win32 而不是 WinUI 的 InputKeyboardSource：后者属于 UWP 的
    /// Windows.UI.Input 命名空间，在 unpackaged WinUI 3 桌面的投影程序集里不可用
    /// （实测编译期即报「类型不存在」）。而快捷键录入要读的正是键盘全局状态，
    /// GetKeyState 就是权威来源，也顺带避开了 WinUI 事件参数上修饰位不可靠的问题。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    // ===== 修饰键虚拟键码 =====

    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;   // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_ESCAPE = 0x1B;
}
