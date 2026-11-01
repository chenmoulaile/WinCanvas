using System;
using System.Runtime.InteropServices;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 壁纸宿主窗口专用的 Win32 API 封装（user32 / gdi32 / dwmapi）。
/// 覆盖四组能力：窗口类注册与跨进程创建（A1/A2）、宿主表面绘制（阶段 A 纯色填充验证）、
/// 位置 / Z 序 / 分层样式控制、目标窗口跟随所需的 WinEvent 钩子与 DWM 矩形查询。
/// 每个 P/Invoke 均注明：作用 / 参数含义 / 返回值含义 / 失败语义，常量带数值与含义，
/// 注释规格与 <c>WinCanvas.Core/Native/NativeMethods.cs</c> 一致。
/// </summary>
internal static class HostNativeMethods
{
    // ===== 窗口样式（CreateWindowExW 的 dwStyle）=====

    /// <summary>WS_CHILD(0x40000000)：子窗口。A1 路径宿主以此样式嵌入目标窗口（必须指定 hWndParent）。</summary>
    public const uint WS_CHILD = 0x40000000;

    /// <summary>WS_POPUP(0x80000000)：弹出式顶层窗口（无边框、无标题）。A2 路径宿主以此样式独立存在。</summary>
    public const uint WS_POPUP = 0x80000000;

    /// <summary>WS_VISIBLE(0x10000000)：创建后立即可见。</summary>
    public const uint WS_VISIBLE = 0x10000000;

    /// <summary>WS_CLIPSIBLINGS(0x04000000)：绘制时裁剪兄弟窗口重叠区，避免兄弟控件重绘互相污染。</summary>
    public const uint WS_CLIPSIBLINGS = 0x04000000;

    // ===== 扩展窗口样式（CreateWindowExW 的 dwExStyle / GWL_EXSTYLE）=====

    /// <summary>WS_EX_LAYERED(0x00080000)：分层窗口。渲染走 DComp target / GDI 不受影响；
    /// 与 WS_EX_TRANSPARENT 组合时鼠标命中测试直接穿透本窗口（HTTRANSPARENT）。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>WS_EX_TRANSPARENT(0x00000020)：鼠标穿透。仅在与 WS_EX_LAYERED 同时置位时对分层窗口完全生效——
    /// 这是宿主「不干扰目标交互」的关键组合。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_NOACTIVATE(0x08000000)：窗口被点击时不会获得焦点（不激活）。A2 路径防抢焦点。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>WS_EX_TOOLWINDOW(0x00000080)：工具窗口——不出现在任务栏 / Alt-Tab 列表。A2 路径防打扰。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    // ===== GetWindowLong / SetWindowLong 的 nIndex =====

    /// <summary>GWL_STYLE(-16)：读取 / 设置窗口样式。A1 两步挂接用它把自建的 WS_POPUP 改为 WS_CHILD。</summary>
    public const int GWL_STYLE = -16;

    /// <summary>GWL_EXSTYLE(-20)：读取 / 设置窗口扩展样式。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>GWLP_USERDATA(-21)：窗口的用户数据槽（指针大小）。WndProc 用它定位托管宿主实例。</summary>
    public const int GWLP_USERDATA = -21;

    // ===== GetWindow 的 uCmd =====

    /// <summary>GW_HWNDNEXT(2)：取 Z 序中指定窗口「下方」的下一个窗口句柄。A2 路径用它把宿主钉在目标正下方。</summary>
    public const uint GW_HWNDNEXT = 2;

    // ===== GetAncestor 的 uFlags =====

    /// <summary>GA_PARENT(1)：取父窗口（不越过所有者链）。A1 挂接校验：GetAncestor(host, GA_PARENT) 必须等于目标。</summary>
    public const uint GA_PARENT = 1;

    /// <summary>GA_ROOT(2)：沿父链走到顶。顶层窗口的 GA_ROOT 等于自身（判「是否顶层」的正确口径）。</summary>
    public const uint GA_ROOT = 2;

    // ===== SetWindowPos 的 hWndInsertAfter 与 uFlags =====

    /// <summary>HWND_BOTTOM(1)：把窗口放到 Z 序最底部（本进程可见的兄弟序列内）。
    /// A1 路径创建宿主后立即沉底：目标的既有子控件（编辑框等兄弟）位于其上，目标自绘客户区背景被宿主覆盖。</summary>
    public static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>SWP_NOSIZE(0x0001)：保持当前尺寸不变。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>SWP_NOMOVE(0x0002)：保持当前位置不变。</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>SWP_NOACTIVATE(0x0010)：不激活窗口（不抢焦点）。宿主所有 SetWindowPos 都带此标志。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>SWP_NOZORDER(0x0004)：不改变 Z 序。仅调整尺寸 / 位置时使用。</summary>
    public const uint SWP_NOZORDER = 0x0004;

    /// <summary>SWP_NOREDRAW(0x0008)：不触发重绘。跟随热路径（拖动中高频调用）用它降低合成开销。</summary>
    public const uint SWP_NOREDRAW = 0x0008;

    // ===== ShowWindow 的 uCmdShow =====

    /// <summary>SW_HIDE(0)：隐藏窗口。A2 路径目标最小化 / 隐藏时同步隐藏宿主。</summary>
    public const int SW_HIDE = 0;

    /// <summary>SW_SHOWNOACTIVATE(4)：显示窗口但不激活（不抢焦点）。A2 路径目标恢复可见时同步显示宿主。</summary>
    public const int SW_SHOWNOACTIVATE = 4;

    // ===== SetLayeredWindowAttributes / GetLayeredWindowAttributes 的 dwFlags =====

    /// <summary>LWA_ALPHA(0x2)：使用 bAlpha 做整体透明度（0 全透明 ~ 255 不透明）。
    /// 宿主创建后立即以 alpha=255 调用一次：WS_EX_LAYERED 窗口在首次 SetLayeredWindowAttributes /
    /// UpdateLayeredWindow 之前不参与合成（纯色填充会不可见），必须先「激活」分层状态。</summary>
    public const uint LWA_ALPHA = 0x2;

    // ===== RegisterClassW 的 style（本实现不需要 CS_*，占位说明不用）=====

    // ===== DwmGetWindowAttribute 的属性 Id =====

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS(9)：DWM 实际可视外接矩形（物理像素，已排除不可见阴影边距）。
    /// A2 路径优先用它取目标矩形，比 GetWindowRect 更贴合用户肉眼所见。</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // ===== WinEvent 钩子：事件常量（winuser.h 原值）=====

    /// <summary>EVENT_OBJECT_DESTROY(0x8001)：窗口对象被销毁。目标销毁时触发会话清理（宿主销毁 + 字典摘除）。</summary>
    public const uint EVENT_OBJECT_DESTROY = 0x8001;

    /// <summary>EVENT_OBJECT_SHOW(0x8002)：窗口对象显示。A2 路径目标重新可见时同步显示宿主。</summary>
    public const uint EVENT_OBJECT_SHOW = 0x8002;

    /// <summary>EVENT_OBJECT_HIDE(0x8003)：窗口对象隐藏。A2 路径目标隐藏时同步隐藏宿主。</summary>
    public const uint EVENT_OBJECT_HIDE = 0x8003;

    /// <summary>EVENT_OBJECT_REORDER(0x8004)：同层 Z 序重排。A1：宿主重新沉底（HWND_BOTTOM）；
    /// A2：宿主重新钉到目标正下方（GetWindow(target, GW_HWNDNEXT)）。</summary>
    public const uint EVENT_OBJECT_REORDER = 0x8004;

    /// <summary>EVENT_OBJECT_FOCUS(0x8005)：对象获得键盘焦点。A1 观察期降级检测：宿主不应获得焦点，
    /// 若宿主成为焦点对象说明跨进程父子关系干扰了输入队列，触发降级 A2。</summary>
    public const uint EVENT_OBJECT_FOCUS = 0x8005;

    /// <summary>EVENT_OBJECT_LOCATIONCHANGE(0x800B)：窗口位置 / 尺寸变化。跟随的主事件源
    /// （目标移动 / 缩放 / 最小化恢复都会触发）。子控件变化的事件 hwnd 不是目标本身，回调内过滤。</summary>
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    /// <summary>EVENT_SYSTEM_MINIMIZESTART(0x0016)：窗口开始最小化。A2 路径同步隐藏宿主。</summary>
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;

    /// <summary>EVENT_SYSTEM_MINIMIZEEND(0x0017)：窗口结束最小化。A2 路径同步恢复显示宿主。</summary>
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;

    // ===== WinEvent 钩子：dwFlags 与对象 Id =====

    /// <summary>WINEVENT_OUTOFCONTEXT(0x0000)：回调在安装钩子的线程上以普通消息派发方式执行，
    /// 不注入目标进程。要求安装线程跑消息循环（宿主 pump 线程即为此设计）。</summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>WINEVENT_SKIPOWNPROCESS(0x0002)：不接收本进程产生的事件。宿主事件全部来自目标进程，
    /// 跳过自身可显著减少无效回调。</summary>
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary>OBJID_WINDOW(0x00000000)：窗口对象本身。回调内过滤 idObject == OBJID_WINDOW，
    /// 排除目标窗口内部子控件（编辑框等，idObject 为 OBJID_CLIENT 等）的事件。</summary>
    public const int OBJID_WINDOW = 0x00000000;

    /// <summary>
    /// 默认窗口过程：本实现不处理的消息交回系统默认处理（窗口行为保持标准语义）。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="msg">消息 Id。</param>
    /// <param name="wParam">字参数。</param>
    /// <param name="lParam">长参数。</param>
    /// <returns>消息处理结果（含义随消息而定）。</returns>
    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ===== 消息常量 =====

    /// <summary>WM_PAINT(0x000F)：绘制请求。阶段 A 在此用 GDI 纯色填充验证宿主上屏（阶段 B 换 DComp）。</summary>
    public const uint WM_PAINT = 0x000F;

    /// <summary>WM_ERASEBKGND(0x0014)：背景擦除请求。同样做纯色填充并返回 1（已擦除），
    /// 避免 WM_PAINT 前出现白色闪烁。</summary>
    public const uint WM_ERASEBKGND = 0x0014;

    /// <summary>WM_DESTROY(0x0002)：窗口销毁中（子窗口随父销毁时也会收到）。
    /// A1 宿主借它发现「目标已销毁连带销毁了宿主」，通知服务清理会话。</summary>
    public const uint WM_DESTROY = 0x0002;

    /// <summary>WM_NCDESTROY(0x0082)：非客户区销毁（窗口销毁的最后一个消息）。
    /// 宿主在此释放 WM_NCCREATE 收养的 GCHandle 并清空 GWLP_USERDATA。</summary>
    public const uint WM_NCDESTROY = 0x0082;

    /// <summary>WM_NCCREATE(0x0081)：非客户区创建中，第一个携带创建参数（lpCreateParams）的消息。
    /// WndProc 在此把托管实例引用（GCHandle）存入 GWLP_USERDATA。</summary>
    public const uint WM_NCCREATE = 0x0081;

    /// <summary>WM_QUIT(0x0012)：消息循环退出请求。PostThreadMessageW 投递给 pump 线程结束 GetMessage 循环。</summary>
    public const uint WM_QUIT = 0x0012;

    // ===== 阶段 A 验证用的填充色（BGR 顺序的 COLORREF）=====
    // 阶段 B 引入 DComp 后此常量与纯色绘制一并移除；当前用于肉眼确认宿主确实盖在目标客户区上。

    /// <summary>COLORREF(0x00BBGGRR)：深蓝 RGB(30, 80, 180)——阶段 A 纯色填充验证色，与常见应用背景差异明显。</summary>
    public const uint HostPhaseAFillColor = 0x00B4501E; // R=0x1E(30) G=0x50(80) B=0xB4(180)

    /// <summary>
    /// WinEvent 回调委托：钩子事件到达时由系统在安装线程（pump）上调用。
    /// 必须保持委托实例被托管引用持有（存入静态字段），防止 GC 回收导致原生回调悬空。
    /// </summary>
    /// <param name="hHook">钩子句柄（UnhookWinEvent 用，本实现不使用）。</param>
    /// <param name="event">事件 Id（EVENT_OBJECT_* / EVENT_SYSTEM_*）。</param>
    /// <param name="hWnd">产生事件的窗口句柄（可能是目标的子控件，回调内需过滤）。</param>
    /// <param name="idObject">对象 Id（OBJID_WINDOW = 0 表示窗口本身）。</param>
    /// <param name="idChild">子对象 Id。</param>
    /// <param name="idEventThread">产生事件的线程 Id。</param>
    /// <param name="dwmsEventTime">事件发生时间（GetTickCount 毫秒时钟）。</param>
    public delegate void WineventProc(
        IntPtr hHook, uint @event, IntPtr hWnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    /// <summary>
    /// 窗口过程委托：RegisterClassW 注册的类级 WndProc。
    /// 必须保持委托实例被托管引用持有（存入静态字段），防止 GC 回收导致原生回调悬空。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="msg">消息 Id。</param>
    /// <param name="wParam">字参数（随消息而定）。</param>
    /// <param name="lParam">长参数（随消息而定）。</param>
    /// <returns>消息处理结果，含义随消息而定（多数消息返回 0 表示已处理）。</returns>
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// RECT：Windows 原生矩形（左上角在屏幕左上，y 向下；Right/Bottom 为不含边界）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;   // 左边界 x
        public int Top;    // 上边界 y
        public int Right;  // 右边界 x（不含）
        public int Bottom; // 下边界 y（不含）
    }

    /// <summary>
    /// MSG：GetMessageW 的输出消息结构（pump 线程消息循环用）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;      // 目标窗口句柄（线程消息为 NULL）
        public uint message;     // 消息 Id
        public IntPtr wParam;    // 字参数
        public IntPtr lParam;    // 长参数
        public uint time;        // 投递时间
        public int ptX;          // 光标 x（投递时）
        public int ptY;          // 光标 y（投递时）
    }

    /// <summary>
    /// WNDCLASSW：RegisterClassW 的输入结构（窗口类属性）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;                // 类样式（CS_*），本实现用 0：绘制完全自管，无需系统自动重绘
        public WndProc lpfnWndProc;       // 类级窗口过程（必须保持委托存活）
        public int cbClsExtra;            // 类额外字节数（不用，0）
        public int cbWndExtra;            // 窗口额外字节数（不用，0）
        public IntPtr hInstance;          // 模块实例句柄（GetModuleHandleW(null) 取主模块）
        public IntPtr hIcon;              // 类图标（不用，NULL）
        public IntPtr hCursor;            // 类光标（不用，NULL：宿主点击穿透，光标由目标决定）
        public IntPtr hbrBackground;      // 类背景刷（NULL：绘制在 WM_PAINT/WM_ERASEBKGND 自管）
        public string? lpszMenuName;      // 菜单资源名（不用，NULL）
        public string lpszClassName;      // 类名（"WinCanvasWallpaperHost"）
    }

    /// <summary>
    /// PAINTSTRUCT：BeginPaintW 的输出结构（绘制上下文与无效区矩形）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;           // 绘制设备上下文
        public bool fErase;          // 背景是否待擦除
        public RECT rcPaint;         // 待重绘矩形
        public bool fRestore;        // 系统保留
        public bool fIncUpdate;      // 系统保留
        // 原生定义此处为 byte[32] 系统保留区；C# 的 fixed 缓冲区要求 unsafe 上下文，
        // 改用 4×ulong（恒 32 字节、无平台对齐差异）等价占位，本实现从不读写
        public ulong Reserved0;
        public ulong Reserved1;
        public ulong Reserved2;
        public ulong Reserved3;
    }

    // ===== 模块与窗口类 =====

    /// <summary>
    /// 取当前进程主模块句柄（RegisterClassW 的 hInstance 参数）。
    /// </summary>
    /// <param name="lpModuleName">NULL 表示当前进程主模块（本实现固定传 NULL）。</param>
    /// <returns>模块句柄；失败返回 NULL。</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>
    /// 注册窗口类（进程级，同名重复注册返回 0 且 GetLastError = ERROR_CLASS_ALREADY_EXISTS，调用方按幂等处理）。
    /// </summary>
    /// <param name="lpWndClass">类属性（含 WndProc 委托、类名等）；委托必须由调用方长期持有。</param>
    /// <returns>类原子（ATOM）；失败返回 0（GetLastError 取原因）。</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    /// <summary>
    /// 创建窗口（宿主窗口的统一入口）。
    /// A1 路径：dwStyle = WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS，hWndParent = 目标窗口（跨进程，等效 SetParent）；
    /// A2 路径：dwStyle = WS_POPUP（后续 ShowWindow 显示），hWndParent = NULL。
    /// </summary>
    /// <param name="dwExStyle">扩展样式（两路径均为 WS_EX_LAYERED|WS_EX_TRANSPARENT，A2 另加 NOACTIVATE|TOOLWINDOW）。</param>
    /// <param name="lpClassName">已注册的类名。</param>
    /// <param name="lpWindowName">窗口名（不用，NULL）。</param>
    /// <param name="dwStyle">窗口样式。</param>
    /// <param name="x">初始 x（物理像素；A1 固定 0 = 目标客户区左上）。</param>
    /// <param name="y">初始 y（物理像素；A1 固定 0）。</param>
    /// <param name="nWidth">初始宽（物理像素）。</param>
    /// <param name="nHeight">初始高（物理像素）。</param>
    /// <param name="hWndParent">父窗口（A1 = 目标跨进程句柄；A2 = NULL）。</param>
    /// <param name="hMenu">菜单 / 子窗口 Id（不用，NULL）。</param>
    /// <param name="hInstance">模块实例（GetModuleHandleW(null)）。</param>
    /// <param name="lpParam">创建参数（WM_NCCREATE 的 lParam 取回，本实现传 GCHandle 指针定位托管实例）。</param>
    /// <returns>窗口句柄；失败返回 IntPtr.Zero（GetLastError 取原因，如 UIPI 的 ERROR_ACCESS_DENIED=5）。</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    /// <summary>
    /// 销毁窗口。只能在创建该窗口的线程调用（pump 线程）——跨线程销毁会静默失败。
    /// </summary>
    /// <param name="hWnd">待销毁窗口句柄。</param>
    /// <returns>true = 成功；false = 句柄无效或跨线程调用（GetLastError）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>
    /// 取窗口父链上的祖先：GA_PARENT 取直接父窗口（A1 挂接校验用）。
    /// 注意语义：对【顶层窗口】GA_PARENT 返回的是桌面窗口（类名 #32769）而非 NULL——
    /// 「是否顶层」应判定 GA_ROOT 是否等于自身，调用方不得误用 GA_PARENT == NULL 判顶层。
    /// </summary>
    /// <param name="hWnd">起始窗口。</param>
    /// <param name="uFlags">GA_PARENT = 1；GA_ROOT = 2（走到父链顶端的顶层窗口）。</param>
    /// <returns>祖先句柄；桌面窗口自身的 GA_PARENT 返回 NULL。</returns>
    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint uFlags);

    /// <summary>
    /// 变更窗口的父窗口（跨进程允许，受 UIPI 约束：目标完整性与本进程相同或更低才可成功）。
    /// A1 两步挂接用：自建 WS_POPUP 改样式为 WS_CHILD 后，以本 API 建立跨进程父子关系
    /// （实测：直接 CreateWindowExW(WS_CHILD, parent=跨进程) 被 OS 拒绝且不设 LastError，
    /// 两步路径可行，见 WORKLOG 实测结论）。
    /// </summary>
    /// <param name="hWndChild">待挂接的窗口（属本进程、由 pump 线程创建）。</param>
    /// <param name="hWndNewParent">新父窗口（目标窗口，可跨进程）。</param>
    /// <returns>旧父窗口句柄；失败返回 NULL（GetLastError 取原因，如 5 = ERROR_ACCESS_DENIED）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    /// <summary>
    /// 判断句柄是否仍是一个有效窗口。回调与清理路径用它区分「目标已销毁」与「正常存在」。
    /// </summary>
    /// <param name="hWnd">待检窗口句柄。</param>
    /// <returns>true = 有效；false = 已销毁或句柄无效。</returns>
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// 判断窗口是否带 WS_VISIBLE 且所有者链可见。
    /// </summary>
    /// <param name="hWnd">待检窗口句柄。</param>
    /// <returns>true = 可见；false = 隐藏或无效。</returns>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// 判断窗口是否处于最小化（A2 路径：目标最小化时宿主同步隐藏）。
    /// </summary>
    /// <param name="hWnd">待检窗口句柄。</param>
    /// <returns>true = 最小化中；false = 否。</returns>
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// 显示 / 隐藏窗口（宿主统一 SW_SHOWNOACTIVATE / SW_HIDE，永不抢焦点）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="nCmdShow">SW_HIDE(0) 隐藏；SW_SHOWNOACTIVATE(4) 显示但不激活。</param>
    /// <returns>返回值含义随此前状态而定，本实现不检查（失败由后续跟随循环自愈）。</returns>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ===== 绘制（阶段 A 纯色填充验证；阶段 B 换 DComp 后 BeginPaint 仍保留用于无内容回退）=====

    /// <summary>
    /// 开始绘制：取客户区 DC 并填充 PAINTSTRUCT（含无效区）。必须在 WM_PAINT 中配对 EndPaintW。
    /// 注意：真实导出名无 W/A 后缀（BeginPaint 是中性名），须用 EntryPoint 显式指回。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="lpPaint">输出：绘制上下文结构。</param>
    /// <returns>设备上下文句柄；失败返回 NULL。</returns>
    [DllImport("user32.dll", EntryPoint = "BeginPaint")]
    public static extern IntPtr BeginPaintW(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    /// <summary>
    /// 结束绘制：释放 DC 并清空无效区（不调用会导致系统持续发 WM_PAINT 空转）。
    /// 真实导出名无 W/A 后缀，同上用 EntryPoint 指回。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="lpPaint">BeginPaintW 输出的同一结构。</param>
    /// <returns>true = 成功。</returns>
    [DllImport("user32.dll", EntryPoint = "EndPaint")]
    public static extern bool EndPaintW(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    /// <summary>
    /// 创建纯色逻辑画刷（GDI 对象，用毕必须 DeleteObject 释放，否则泄漏 GDI 句柄）。
    /// </summary>
    /// <param name="crColor">COLORREF（0x00BBGGRR）。</param>
    /// <returns>画刷句柄；失败返回 NULL。</returns>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    /// <summary>
    /// 用画刷填充矩形（阶段 A 纯色上屏的落点）。
    /// </summary>
    /// <param name="hDC">目标 DC。</param>
    /// <param name="lprc">待填充矩形（客户区坐标）。</param>
    /// <param name="hbr">画刷句柄。</param>
    /// <returns>true = 成功；false = 参数无效。</returns>
    [DllImport("user32.dll")]
    public static extern bool FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    /// <summary>
    /// 删除 GDI 对象（画刷等），释放 GDI 句柄。
    /// </summary>
    /// <param name="ho">GDI 对象句柄。</param>
    /// <returns>true = 成功；false = 句柄无效。</returns>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr ho);

    // ===== 分层窗口 =====

    /// <summary>
    /// 设置分层窗口的整体透明度（宿主创建后以 alpha=255 调用一次「激活」分层合成；
    /// 之后的 DComp / GDI 绘制内容才会参与合成显示）。
    /// </summary>
    /// <param name="hWnd">分层窗口句柄。</param>
    /// <param name="crKey">颜色键（LWA_COLORKEY 用，本实现传 0）。</param>
    /// <param name="bAlpha">整体不透明度（0~255）。</param>
    /// <param name="dwFlags">LWA_ALPHA(0x2)。</param>
    /// <returns>true = 成功；false = 窗口非分层或参数无效。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 读取分层窗口的透明度（A2 自动透明判定：目标当前 alpha == 255 才需要自动降为 220）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄（须带 WS_EX_LAYERED，否则失败——失败即视为不透明）。</param>
    /// <param name="pcrKey">输出：颜色键（不用）。</param>
    /// <param name="pbAlpha">输出：当前整体不透明度。</param>
    /// <param name="pdwFlags">输出：当前生效标志（LWA_ALPHA 位有效时 pbAlpha 才有意义）。</param>
    /// <returns>true = 成功；false = 窗口未分层或从未设置过属性（此时按不透明处理）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

    // ===== 位置 / 尺寸 / Z 序 =====

    /// <summary>
    /// 设置窗口位置、尺寸与 Z 序（宿主跟随与沉底的核心 API）。
    /// </summary>
    /// <param name="hWnd">宿主窗口句柄。</param>
    /// <param name="hWndInsertAfter">Z 序锚点：HWND_BOTTOM 沉底；A2 路径传 GetWindow(target, GW_HWNDNEXT) 钉到目标正下方。</param>
    /// <param name="x">左上 x（A1 = 客户区坐标恒 0；A2 = 屏幕物理坐标）。</param>
    /// <param name="y">左上 y。</param>
    /// <param name="cx">宽。</param>
    /// <param name="cy">高。</param>
    /// <param name="uFlags">SWP_* 组合（恒带 SWP_NOACTIVATE）。</param>
    /// <returns>true = 成功；false = 失败（GetLastError；句柄无效也会失败，跟随循环据此感知清理时机）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 取窗口客户区矩形（左上恒为 0,0；A1 路径宿主矩形 = 该矩形）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpRect">输出：客户区矩形。</param>
    /// <returns>true = 成功；false = 句柄无效（常用于感知目标销毁）。</returns>
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// 取窗口（含非客户区）外接矩形。
    /// 坐标系实测说明（Win11 26300 / DWM / PMv2 进程）：顶层窗口返回屏幕物理像素坐标；
    /// 子窗口实测同样返回【屏幕物理坐标】（与旧文档「父客户区坐标」的说法不符）——
    /// 涉及位置的比对须先 ScreenToClient 映射，宽高比对不受坐标系影响。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpRect">输出：窗口矩形。</param>
    /// <returns>true = 成功；false = 句柄无效。</returns>
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// 取 Z 序中相邻窗口：GW_HWNDNEXT 取目标「下方」窗口（A2 钉位锚点；目标已是最底则返回 NULL）。
    /// </summary>
    /// <param name="hWnd">参考窗口。</param>
    /// <param name="uCmd">GW_HWNDNEXT = 2。</param>
    /// <returns>相邻窗口句柄；无相邻或失败返回 NULL。</returns>
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // ===== 指针尺寸样式读写的 64 位兼容 =====

    /// <summary>
    /// 读窗口属性（指针尺寸，64/32 位进程兼容封装；GWLP_USERDATA 槽在 64 位下必须走 Ptr 版本）。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="nIndex">属性索引（GWLP_USERDATA = -21）。</param>
    /// <returns>属性值；失败返回 0（与「值恰为 0」同形，调用方自行区分）。</returns>
    public static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex)
    {
        if (Environment.Is64BitProcess)
        {
            return GetWindowLongPtrW(hWnd, nIndex);
        }

        return new IntPtr(GetWindowLongW(hWnd, nIndex));
    }

    /// <summary>
    /// 写窗口属性（指针尺寸，64/32 位进程兼容封装）。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="nIndex">属性索引（GWLP_USERDATA = -21）。</param>
    /// <param name="dwNewLong">新值（本实现传 GCHandle 指针）。</param>
    /// <returns>旧值；失败返回 0 且 GetLastError 非 0 才视为失败。</returns>
    public static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (Environment.Is64BitProcess)
        {
            return SetWindowLongPtrW(hWnd, nIndex, dwNewLong);
        }

        return new IntPtr(SetWindowLongW(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    /// <summary>GetWindowLongPtrW：64 位进程的窗口属性读取（仅 64 位导出存在，勿在 32 位直接调用）。</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    /// <summary>SetWindowLongPtrW：64 位进程的窗口属性写入（仅 64 位导出存在，勿在 32 位直接调用）。</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>GetWindowLongW：32 位回退读取（GWL_EXSTYLE 等 32 位值在两种进程宽度下均可读）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    /// <summary>SetWindowLongW：32 位回退写入。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    // ===== 目标进程 / 线程信息（WinEvent 钩子的过滤参数）=====

    /// <summary>
    /// 取创建窗口的线程 Id（可选输出进程 Id）。钩子按「目标窗口所在线程」过滤，
    /// 显著减少无关进程事件进入回调。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpdwProcessId">输出：进程 Id（不需要时传下划线丢弃）。</param>
    /// <returns>创建该窗口的线程 Id；失败返回 0。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ===== 窗口枚举（宿主残留验证：EnumWindows 数 WinCanvasWallpaperHost）=====

    /// <summary>
    /// EnumWindows 的回调委托：返回 true 继续枚举，返回 false 停止枚举。
    /// 注意：回调期间必须保持委托被引用（本实现用局部函数，调用栈内存活）。
    /// </summary>
    /// <param name="hWnd">当前枚举到的顶层窗口句柄。</param>
    /// <param name="lParam">调用方传入的自定义参数（原样回传）。</param>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 枚举屏幕上所有顶层窗口，对每个窗口调用一次回调（宿主残留计数与诊断的入口）。
    /// </summary>
    /// <param name="lpEnumFunc">回调委托。</param>
    /// <param name="lParam">传给回调的自定义参数。</param>
    /// <returns>true = 枚举完整跑完；false = 被回调提前终止或调用失败。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// 把窗口类名复制到缓冲区（残留验证按类名 "WinCanvasWallpaperHost" 匹配）。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="lpClassName">接收类名的缓冲区（Unicode）。</param>
    /// <param name="nMaxCount">缓冲区可容纳的最大字符数（含结尾 '\0'），超长截断。</param>
    /// <returns>实际复制的字符数（不含 '\0'）；失败返回 0。</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>
    /// 枚举指定窗口的全部后代子窗口（递归遍历），对每个子窗口调用一次回调。
    /// A1 宿主是目标的 WS_CHILD 子窗口，EnumWindows 只枚举顶层窗口，
    /// 残留计数必须叠加本 API 才能覆盖子窗口形态的宿主。
    /// </summary>
    /// <param name="hWndParent">父窗口句柄（NULL = 枚举全部顶层窗口的子窗口）。</param>
    /// <param name="lpEnumFunc">回调委托（语义同 EnumWindowsProc）。</param>
    /// <param name="lParam">传给回调的自定义参数。</param>
    /// <returns>true = 枚举完整跑完；false = 被回调提前终止或参数无效。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // ===== DWM 矩形 =====

    /// <summary>
    /// 读 DWM 窗口属性（此处用于 DWMWA_EXTENDED_FRAME_BOUNDS：DWM 实际可视外接矩形，物理像素）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="dwAttribute">属性 Id（9 = EXTENDED_FRAME_BOUNDS）。</param>
    /// <param name="pvAttribute">输出缓冲区（RECT）。</param>
    /// <param name="cbAttribute">缓冲区字节数（sizeof(RECT) = 16）。</param>
    /// <returns>S_OK(0) 成功；负 HRESULT 失败（如 DWM 未启用、句柄无效）——调用方回退 GetWindowRect。</returns>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    // ===== WinEvent 钩子 =====

    /// <summary>
    /// 安装 WinEvent 钩子（宿主跟随的事件源）。回调在安装线程（pump）上以消息派发方式触发，
    /// 因此 pump 必须跑 GetMessage 循环。
    /// </summary>
    /// <param name="eventMin">监听的最低事件 Id。</param>
    /// <param name="eventMax">监听的最高事件 Id（与 eventMin 相同表示单事件）。</param>
    /// <param name="hmodWinEventProc">WINEVENT_OUTOFCONTEXT 下传 NULL（不加载 DLL）。</param>
    /// <param name="pfnWinEventProc">回调委托（调用方持有引用防 GC）。</param>
    /// <param name="idProcess">目标进程 Id（0 = 全部进程；失败时的全局回退用 0）。</param>
    /// <param name="idThread">目标线程 Id（0 = 该进程全部线程；按目标窗口线程过滤可降噪）。</param>
    /// <param name="dwFlags">WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS。</param>
    /// <returns>钩子句柄；失败返回 NULL（GetLastError）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WineventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    /// <summary>
    /// 卸载 WinEvent 钩子（会话清理 / 应用退出时调用；重复卸载返回 false，幂等安全）。
    /// </summary>
    /// <param name="hWinEventHook">SetWinEventHook 返回的钩子句柄。</param>
    /// <returns>true = 成功；false = 句柄无效或已卸载。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>
    /// 取窗口当前生效的每显示器 DPI（PerMonitorV2 感知进程下随窗口所在显示器变化）。
    /// WP4 引入：A2 宿主为独立顶层窗口，跨屏移动收到 WM_DPICHANGED 时需要「旧 DPI」作换算
    /// 基准——创建完成时读一次存入 <c>WallpaperHostWindow._currentDpi</c>，此后每次收到
    /// WM_DPICHANGED 用旧值换算期望客户区、再用新值刷新该字段（读写均在 pump 线程）。
    /// </summary>
    /// <param name="hWnd">目标窗口（须属本进程；A2 宿主满足）。</param>
    /// <returns>当前 DPI（96 = 100% 缩放；失败返回系统 DPI 兜底值，本路径仅做诊断比对，不另设错误分支）。</returns>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // ===== pump 线程消息循环 =====

    /// <summary>
    /// 从线程队列取消息（阻塞直到有消息）。收到 WM_QUIT 时返回 0，循环应退出。
    /// </summary>
    /// <param name="lpMsg">输出：取到的消息。</param>
    /// <param name="hWnd">过滤窗口（NULL = 不过滤）。</param>
    /// <param name="wMsgFilterMin">最低消息 Id（0 = 不过滤）。</param>
    /// <param name="wMsgFilterMax">最高消息 Id（0 = 不过滤）。</param>
    /// <returns>-1 = 错误；0 = 收到 WM_QUIT；其余 = 消息应继续派发。</returns>
    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>
    /// 翻译虚拟键消息（本线程无键盘交互，按惯例调用保持消息泵完整）。
    /// </summary>
    /// <param name="lpMsg">GetMessageW 取到的消息。</param>
    /// <returns>非零继续派发；WM_QUIT 场景返回 0。</returns>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    /// <summary>
    /// 派发消息到窗口过程（pump 线程消息循环的最后一环）。
    /// </summary>
    /// <param name="lpMsg">待派发消息。</param>
    /// <returns>窗口过程的返回值。</returns>
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    /// <summary>
    /// 向指定线程投递消息（pump 线程间通信通道：宿主创建 / 销毁 / 同步任务全部经它投递）。
    /// 线程消息 hwnd 为 NULL，由 GetMessageW 直接取出。
    /// </summary>
    /// <param name="idThread">目标线程 Id（pump 启动时记录的托管线程原生 Id）。</param>
    /// <param name="msg">自定义消息 Id 或 WM_QUIT。</param>
    /// <param name="wParam">字参数（承载任务对象 GCHandle 等）。</param>
    /// <param name="lParam">长参数。</param>
    /// <returns>true = 已入队；false = 线程不存在或队列满。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>PostThreadMessageW 的自定义任务消息：pump 线程执行托管委托的载体（wParam = GCHandle）。</summary>
    public const uint WM_APP_PUMP_TASK = 0x8000; // WM_APP 起始值，安全避开系统区间
}
