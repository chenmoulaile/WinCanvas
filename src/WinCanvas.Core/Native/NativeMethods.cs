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

    // ===== WinEvent 钩子（前台窗口监听，隐私遮蔽专用） =====
    /// <summary>
    /// EVENT_SYSTEM_FOREGROUND(0x0003)：前台窗口已切换。
    /// 数值含义：0x0003 是 winuser.h 定义的系统事件 Id，任何窗口取得前台
    /// （拥有键盘焦点的顶层窗口发生变化）时系统对全局广播一次该事件；
    /// <see cref="WineventProc"/> 的 hwnd 参数即【新的前台窗口】句柄。
    /// 失败语义：事件 Id 非法时 SetWinEventHook 返回 NULL，本常量恒为合法值。
    /// </summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>
    /// WINEVENT_OUTOFCONTEXT(0x0)：WinEvent 钩子回调以「脱离上下文」方式投递。
    /// 数值含义：位标志 0x0 表示回调不在事件源进程内执行，而是投递到【安装钩子的线程】
    /// 的消息队列（该线程必须持续泵消息，否则回调永远不触发且不阻塞事件源）。
    /// 失败语义：与其它标志位组合不当时 SetWinEventHook 返回 NULL。
    /// </summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>OBJID_WINDOW(0)：WinEvent / 可访问性对象 Id——事件属于窗口本体而非其子控件。</summary>
    public const int OBJID_WINDOW = 0;

    /// <summary>
    /// WinEvent 钩子回调委托，对应 Win32 的 WINEVENTPROC。
    /// 必须作为字段长期持有（防 GC 回收后原生回调悬空，与低级键盘钩子的约定相同）。
    /// </summary>
    /// <param name="hWinEventHook">本回调所属的钩子句柄（同一委托可挂多个钩子，用它可以区分来源）。</param>
    /// <param name="event">事件 Id（如 EVENT_SYSTEM_FOREGROUND=0x0003）。</param>
    /// <param name="hWnd">事件关联的窗口句柄（前台切换事件 = 新前台窗口；窗口已销毁时为 0）。</param>
    /// <param name="idObject">对象 Id：OBJID_WINDOW(0) = 窗口本体；其余为子控件 / 可访问性对象。</param>
    /// <param name="idChild">子对象 Id（窗口本体事件为 0）。</param>
    /// <param name="dwEventThread">产生事件的线程 Id。</param>
    /// <param name="dwmsEventTime">事件时间戳（GetTickCount 毫秒口径）。</param>
    public delegate void WineventProc(
        IntPtr hWinEventHook, uint @event, IntPtr hWnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    /// <summary>
    /// 安装 WinEvent 事件钩子，系统随后把范围内的事件投递到调用线程的消息队列。
    /// </summary>
    /// <param name="eventMin">要监听的事件 Id 下限（含）。</param>
    /// <param name="eventMax">要监听的事件 Id 上限（含）；与下限相同即只监听单个事件。</param>
    /// <param name="hmodWinEventProc">回调所在模块句柄；回调在本进程程序集内传 GetModuleHandleW(null)，传 NULL 亦可。</param>
    /// <param name="pfnWinEventProc">回调委托（调用方必须长期持有引用防 GC 回收）。</param>
    /// <param name="idProcess">事件源进程过滤：0 = 全系统进程（监听其它进程的窗口必须传 0）。</param>
    /// <param name="idThread">事件源线程过滤：0 = 该进程全部线程。</param>
    /// <param name="dwFlags">WINEVENT_* 位标志组合（本程序传 WINEVENT_OUTOFCONTEXT=0x0）。</param>
    /// <returns>钩子句柄；【失败语义】返回 IntPtr.Zero（用 GetLastError 取错误码）。
    /// 常见失败：回调为 null、flags 非法；安装线程随后必须泵消息，否则回调不会触发。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WineventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    /// <summary>
    /// 卸载 SetWinEventHook 安装的钩子。必须在钩子线程收尾时调用（与 KeyboardIndicatorService
    /// 的 LL 键盘钩子同一时序约定：Unhook 一定发生在最后一次回调之后）。
    /// </summary>
    /// <param name="hWinEventHook">SetWinEventHook 返回的钩子句柄。</param>
    /// <returns>true = 成功；【失败语义】false = 句柄无效或已被卸载（幂等场景可安全忽略）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

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
    /// 校验句柄是否【仍然是一个窗口】。
    ///
    /// 为什么必须用它：本服务的 hwnd 来自上一次枚举的快照，而恢复管线是「200ms 间隔 + 并发 2」
    /// 排队派发的 —— 从枚举到真正调用 SetWindowPos 之间，窗口可能已被销毁、
    /// 其句柄值也可能被系统【复用给另一个窗口】。少了这一步预检，
    /// 最坏情况是把置顶/穿透/透明打到了一个毫不相干的窗口上（句柄复用是经典事故源）。
    /// 注意它挡不住「句柄已销毁且恰好被复用」这一极端情形，但能挡住绝大多数
    /// 「目标已关闭仍在调用」的常规竞态。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <returns>true = 句柄当前有效。</returns>
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// DWM 是否正在「 cloaking 」该窗口（UWP/WinUI 应用被挂起、虚拟桌面切走、
    /// MinWin 组合器隐藏等）。被 cloaked 的窗口 <see cref="IsWindowVisible"/> 仍返回 true，
    /// 因此只按可见性过滤会把一批「看不见但存在」的框架窗口放进列表。
    /// </summary>
    public const int DWMWA_CLOAKED = 14;

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

    // ===== 键盘低级钩子（WH_KEYBOARD_LL，WP2 NumLock 指示器专用） =====
    // 使用约定（为什么这样调）：
    // 1. LL 钩子的回调在「安装钩子的线程」上同步执行，且要求该线程持续跑消息循环（GetMessage）——
    //    系统把键盘事件投递到该线程队列、由泵消息触发回调；线程不泵消息 = 全系统键盘输入被挂起。
    //    因此 KeyboardIndicatorService 把安装/卸载/回调全部放在专用后台线程上，绝不占用 UI 线程。
    // 2. 回调内只允许做极短的工作（读状态 + 判定），任何耗时操作（弹提示、启动进程）必须派发线程池，
    //    否则低级钩子超时（系统默认 LowLevelHooksTimeout）会被系统直接摘除钩子。
    // 3. 回调必须无条件 CallNextHookEx 放行事件——本服务只是「旁路观察者」，绝不吞键。

    /// <summary>WH_KEYBOARD_LL(13)：低级键盘钩子，监视所有线程的键盘事件（注入与物理按键都可见）。</summary>
    public const int WH_KEYBOARD_LL = 13;

    /// <summary>VK_NUMLOCK(0x90)：NumLock 虚拟键码。</summary>
    public const int VK_NUMLOCK = 0x90;

    /// <summary>WM_KEYDOWN(0x0100)：非系统键按下消息。</summary>
    public const uint WM_KEYDOWN = 0x0100;

    /// <summary>WM_KEYUP(0x0101)：非系统键抬起消息。</summary>
    public const uint WM_KEYUP = 0x0101;

    /// <summary>WM_QUIT(0x0012)：终止线程消息循环的专用消息——GetMessage 收到它返回 0 使循环退出。</summary>
    public const uint WM_QUIT = 0x0012;

    /// <summary>HC_ACTION(0)：钩子回调 nCode 的正常处理值，表示 wParam/lParam 携带有效键盘事件。</summary>
    public const int HC_ACTION = 0;

    /// <summary>LLKHF_INJECTED(0x10)：KBDLLHOOKSTRUCT.flags 位——事件由 SendInput 等注入产生而非物理按键。</summary>
    public const uint LLKHF_INJECTED = 0x10;

    /// <summary>
    /// LL 键盘钩子回调委托。对应 Win32 的 HOOKPROC；必须作为字段长期持有（防 GC 回收后系统回调野指针）。
    /// </summary>
    /// <param name="nCode">HC_ACTION(0) = 事件有效需处理；负值 = 必须原样 CallNextHookEx 转交，不得处理。</param>
    /// <param name="wParam">键盘消息类型（WM_KEYDOWN / WM_KEYUP / WM_SYSKEYDOWN / WM_SYSKEYUP）。</param>
    /// <param name="lParam">指向 KBDLLHOOKSTRUCT 的指针（只读，不得修改）。</param>
    /// <returns>若本钩子处理了事件，返回非零可阻止后续钩子与目标线程收到（本服务永远放行，返回 CallNextHookEx 的结果）。</returns>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 低级键盘钩子事件结构（winuser.h 的 KBDLLHOOKSTRUCT，lParam 所指）。
    /// 布局与 Win32 严格一致（Sequential + Pack 默认即可，全为 4/指针大小字段）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        /// <summary>虚拟键码（本服务只关心 VK_NUMLOCK=0x90）。</summary>
        public uint vkCode;

        /// <summary>硬件扫描码（含厂商扩展位，本服务不使用）。</summary>
        public uint scanCode;

        /// <summary>标志位：LLKHF_INJECTED(0x10)=注入事件、LLKHF_EXTENDED(0x01)=扩展键等。</summary>
        public uint flags;

        /// <summary>事件时间戳（GetTickCount 毫秒口径，与 GetMessageTime 可比；用于打点延迟）。</summary>
        public uint time;

        /// <summary>注入来源的附加信息（SendInput 的 dwExtraInfo 原样回传，本服务不使用）。</summary>
        public UIntPtr dwExtraInfo;
    }

    /// <summary>
    /// 安装钩子。LL 键盘钩子必须由带消息循环的线程安装并泵消息，否则回调永远不触发。
    /// </summary>
    /// <param name="idHook">钩子类型，本服务传 WH_KEYBOARD_LL(13)。</param>
    /// <param name="lpfn">回调委托（须由调用方长期持有引用，防 GC 回收）。</param>
    /// <param name="hMod">含回调代码的模块句柄；回调在本进程程序集内时传 GetModuleHandleW(null)（或 NULL 亦可，统一用前者与主流示例一致）。</param>
    /// <param name="dwThreadId">关联线程 Id；0 = 全局钩子（LL 钩子只能是全局，传 0）。</param>
    /// <returns>钩子句柄；失败返回 IntPtr.Zero（用 GetLastError 取错误码，如超时/权限不足）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>
    /// 卸载钩子。必须由安装钩子的线程或任意线程调用均可生效；本服务在钩子线程自身退出前调用（时序最干净）。
    /// </summary>
    /// <param name="hhk">SetWindowsHookExW 返回的钩子句柄。</param>
    /// <returns>true = 成功；false = 失败（句柄无效或已被系统摘除）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>
    /// 把钩子事件转交给钩子链中的下一个钩子（或最终目标）。本服务的回调无论是否处理都必须调用它放行，
    /// 否则全系统键盘事件链被截断（其他键盘钩子与目标应用收不到事件）。
    /// </summary>
    /// <param name="hhk">本钩子的句柄（系统用它定位链中下一个钩子；可传 IntPtr.Zero，系统仍按链推进，规范起见传真实句柄）。</param>
    /// <param name="nCode">原样透传回调收到的 nCode。</param>
    /// <param name="wParam">原样透传回调收到的 wParam。</param>
    /// <param name="lParam">原样透传回调收到的 lParam。</param>
    /// <returns>钩子链中下一个钩子的返回值（本服务原样返回给系统）。</returns>
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 读取指定虚拟键的同步键盘状态（低位 bit0 = 切换键开关系数；高位 bit15 = 当前按下）。
    /// </summary>
    /// <remarks>
    /// 为什么用 GetKeyState 而不是 GetAsyncKeyState：NumLock 的「开关位」是全局会话状态（即 LED 灯态），
    /// GetKeyState 返回的切换位在任何线程上都读到该全局值；而按下位才是随线程消息处理的同步状态。
    /// 在 LL 钩子回调里于 WM_KEYDOWN / WM_KEYUP 两个时机都读一次，配合「与上次上报不同才上报」的去抖，
    /// 无论系统在按下还是抬起时刻才翻转开关位，都能恰好上报一次正确的新状态（自纠错设计，见服务注释）。
    /// </remarks>
    /// <param name="nVirtKey">虚拟键码（本服务传 VK_NUMLOCK=0x90）。</param>
    /// <returns>SHORT 状态值：bit0=1 表示 NumLock 处于锁定（开）状态；bit15=1 表示键当前按下。</returns>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    /// <summary>
    /// 向指定线程的消息队列投递一条消息（本服务用它向钩子线程投递 WM_QUIT 以退出 GetMessage 循环）。
    /// </summary>
    /// <param name="idThread">目标线程 Id（本服务传钩子线程的 GetCurrentThreadId 返回值）。</param>
    /// <param name="msg">消息 Id（WM_QUIT=0x0012 使 GetMessage 返回 0 退出循环）。</param>
    /// <param name="wParam">消息 wParam（WM_QUIT 场景传 0）。</param>
    /// <param name="lParam">消息 lParam（WM_QUIT 场景传 0）。</param>
    /// <returns>true = 已入队；false = 失败（线程 Id 无效或队列满，用 GetLastError 取错误码）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 取当前进程主模块（exe）句柄。LL 钩子回调在本进程内，用它作为 SetWindowsHookExW 的 hMod 参数。
    /// </summary>
    /// <param name="lpModuleName">模块名；null = 返回当前进程的 exe 模块句柄（本服务的用法）。</param>
    /// <returns>模块句柄；失败返回 IntPtr.Zero。</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>
    /// 取调用线程的 Win32 线程 Id。钩子线程装好钩子后记录它，
    /// Stop 方向以 PostThreadMessageW(WM_QUIT) 定向投递到该 Id 使消息循环退出。
    /// </summary>
    /// <returns>当前线程的 Win32 线程 Id（注意与 .NET 的 ManagedThreadId 不是同一个数）。</returns>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>Win32 POINT 结构（MSG.pt 字段用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>X 坐标。</summary>
        public int X;

        /// <summary>Y 坐标。</summary>
        public int Y;
    }

    /// <summary>
    /// Win32 MSG 结构（winuser.h 的 tagMSG）。必须包含末尾的 lPrivate 字段补齐 64 位下的真实布局——
    /// GetMessageW 会按原生结构全尺寸写入，托管侧结构若声明偏小会造成缓冲区越界。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>消息目标窗口句柄（线程消息为 IntPtr.Zero）。</summary>
        public IntPtr hwnd;

        /// <summary>消息 Id（如 WM_QUIT）。</summary>
        public uint message;

        /// <summary>消息 wParam。</summary>
        public IntPtr wParam;

        /// <summary>消息 lParam。</summary>
        public IntPtr lParam;

        /// <summary>消息投递时间（GetTickCount 毫秒口径）。</summary>
        public uint time;

        /// <summary>消息投递时的光标位置。</summary>
        public POINT pt;

        /// <summary>Windows 私有字段（必须声明以补齐结构尺寸，勿读写）。</summary>
        public uint lPrivate;
    }

    /// <summary>
    /// 从调用线程的消息队列取一条消息；没有消息时阻塞等待（这正是钩子线程空闲时零 CPU 占用的机制）。
    /// </summary>
    /// <param name="lpMsg">输出：接收取到的消息。</param>
    /// <param name="hWnd">过滤窗口句柄；IntPtr.Zero = 不过滤（取本线程全部消息）。</param>
    /// <param name="wMsgFilterMin">消息 Id 过滤下限；0 = 不过滤。</param>
    /// <param name="wMsgFilterMax">消息 Id 过滤上限；0 = 不过滤。</param>
    /// <returns>大于 0 = 取到消息（需 DispatchMessage）；0 = 取到 WM_QUIT（循环应退出）；-1 = 出错（用 GetLastError 取错误码）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>
    /// 非阻塞地窥视消息队列。本服务在钩子线程启动时以 PM_NOREMOVE 调用一次——
    /// Win32 线程的消息队列是惰性创建的，首次调用队列类 API 才会建队；
    /// 先建队再让外部 PostThreadMessageW(WM_QUIT)，避免「队列尚不存在导致投递失败」的竞态。
    /// </summary>
    /// <param name="lpMsg">输出：接收窥视到的消息（PM_NOREMOVE 下不取走）。</param>
    /// <param name="hWnd">过滤窗口句柄；IntPtr.Zero = 不过滤。</param>
    /// <param name="wMsgFilterMin">消息 Id 过滤下限；0 = 不过滤。</param>
    /// <param name="wMsgFilterMax">消息 Id 过滤上限；0 = 不过滤。</param>
    /// <param name="wRemoveMsg">PM_NOREMOVE(0x0000) = 只看不取走（本服务的用法）。</param>
    /// <returns>true = 队列中有可用消息；false = 队列为空或出错（用 GetLastError 区分，ERROR_NO_MORE_MESSAGES(1158) 为队列为空）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
}
