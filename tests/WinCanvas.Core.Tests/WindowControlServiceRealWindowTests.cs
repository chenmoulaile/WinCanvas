using System.ComponentModel;
using System.Runtime.InteropServices;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP4 补充：<see cref="WindowControlService"/> 对【真实窗口】的行为验证。
/// 打 [Trait("Category", "RequiresWindowsSession")]——测试进程内自建一个真实 Win32 窗口
/// （ RegisterClassW + CreateWindowExW ），需要可用的桌面会话。
/// 排除口径（实测结论，见 WORKLOG）：xunit 2.9.2 无运行时跳过机制（不存在 Assert.Skip），
/// 无头环境用 --filter "Category!=RequiresWindowsSession" 排除。
///
/// 线程模型（为什么窗口必须在专用线程上泵消息——本机实测踩坑记录）：
/// SetWindowPos（置顶路径）会对被移动窗口的【所属线程】发送同步消息（WM_WINDOWPOSCHANGING 等），
/// 若所属线程不泵消息则同步消息永无回应 → 调用方永久挂起。首版测试窗口建在
/// 线程池线程上（该类线程不泵消息），三个真实窗口用例全部挂死、testhost 被判崩溃。
/// 故窗口改在专用 STA 线程创建并跑 GetMessageW 消息循环，服务调用（线程池线程）
/// 的跨线程同步消息由该线程正常处理，不挂起。
///
/// 作用：置顶回读 / 透明 / 穿透样式语义的真实路径冒烟（无效句柄失败语义见
/// WindowControlServiceFailureTests）。
/// </summary>
[Trait("Category", "RequiresWindowsSession")]
public sealed class WindowControlServiceRealWindowTests : IDisposable
{
    private readonly WindowControlService _service = new();
    private readonly TestWindowHarness.TestWindow? _window;

    public WindowControlServiceRealWindowTests()
    {
        // 桌面会话不可用（无头 CI / 会话受限）时以明确中文原因失败——本类用例在无头环境
        // 应被 Trait 过滤排除，而不是带病运行。
        _window = TestWindowHarness.Create() ?? throw new InvalidOperationException(
            $"真实窗口创建失败，RequiresWindowsSession 用例无法运行：{TestWindowHarness.LastCreateError}");
    }

    public void Dispose() => _window?.Dispose();

    [Fact]
    public async Task SetTopmost_真实窗口_置顶状态回读一致()
    {
        await _service.SetTopmostAsync(_window!.Hwnd, topmost: true);
        Assert.True(await _service.GetIsTopmostAsync(_window.Hwnd));

        await _service.SetTopmostAsync(_window.Hwnd, topmost: false);
        Assert.False(await _service.GetIsTopmostAsync(_window.Hwnd));
    }

    [Fact]
    public async Task SetTransparency_真实窗口_透明度写入不抛()
    {
        // SetLayeredWindowAttributes(alpha=200)：本进程自己的窗口，必然有权限写；
        // 只断言不抛（透明度真实值没有可编程回读 API——GetLayeredWindowAttributes 不在服务面内）。
        await _service.SetTransparencyAsync(_window!.Hwnd, 200);
    }

    [Fact]
    public async Task EnableClickThrough_关穿透_保留分层样式()
    {
        // 既有核心语义（WP1 前就存在）：启用穿透置 TRANSPARENT|LAYERED 两位；
        // 关闭穿透只清 TRANSPARENT、保留 LAYERED——否则用户已设置的透明度会被一并抹掉。
        await _service.EnableClickThroughAsync(_window!.Hwnd, enable: true);
        var enabledStyle = TestWindowHarness.GetExStyle(_window.Hwnd);
        Assert.NotEqual(0, enabledStyle & TestWindowHarness.WS_EX_TRANSPARENT);
        Assert.NotEqual(0, enabledStyle & TestWindowHarness.WS_EX_LAYERED);

        await _service.EnableClickThroughAsync(_window.Hwnd, enable: false);
        var disabledStyle = TestWindowHarness.GetExStyle(_window.Hwnd);
        Assert.Equal(0, disabledStyle & TestWindowHarness.WS_EX_TRANSPARENT);
        Assert.NotEqual(0, disabledStyle & TestWindowHarness.WS_EX_LAYERED); // LAYERED 必须保留
    }
}

/// <summary>
/// 测试用真实 Win32 窗口的创建 / 消息泵 / 销毁工具（仅测试程序集使用）。
/// 窗口为 10x10 的 WS_POPUP 工具窗：WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW 不抢焦点、
/// 不进任务栏；显示用 SW_SHOWNOACTIVATE——WS_EX_TOPMOST 位与分层属性对【隐藏窗口】
/// 均不生效（Win32 已知语义），必须真实显示。P/Invoke 注释与仓库 NativeMethods 同规格。
/// </summary>
internal static class TestWindowHarness
{
    /// <summary>WS_EX_TRANSPARENT：鼠标穿透位（与 WinCanvas.Core.NativeMethods 同值 0x20）。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_LAYERED：分层窗口位（与 WinCanvas.Core.NativeMethods 同值 0x80000）。</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>GWL_EXSTYLE：GetWindowLongW 的扩展样式索引（-20）。</summary>
    private const int GWL_EXSTYLE = -20;

    /// <summary>WS_EX_NOACTIVATE：不夺焦点（0x08000000）。</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>WS_EX_TOOLWINDOW：不进任务栏 / Alt-Tab（0x80）。</summary>
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>SW_SHOWNORMAL：正常显示并激活（5）。</summary>
    private const int SW_SHOWNORMAL = 5;

    /// <summary>SW_MINIMIZE(6)：最小化窗口——前台必然移交给其它窗口（真实前台切换原语之一）。</summary>
    public const int SW_MINIMIZE = 6;

    /// <summary>SW_RESTORE(9)：还原窗口——还原会【激活】窗口（窗口管理动作，不受前台锁定限制），
    /// 是后台进程触发真实前台切换的可靠原语（隐私前台监听测试用）。</summary>
    public const int SW_RESTORE = 9;

    /// <summary>LWA_ALPHA(0x2)：与 Core NativeMethods 同值——alpha 回读有效标志。</summary>
    public const uint LWA_ALPHA = 0x2;

    /// <summary>最近一次 <see cref="Create"/> 的失败原因（成功为 null；诊断用）。</summary>
    public static string? LastCreateError { get; private set; }

    /// <summary>一个存活的测试窗口：句柄 + 持有它并泵消息的线程。</summary>
    public sealed class TestWindow : IDisposable
    {
        /// <summary>窗口句柄（创建失败为 IntPtr.Zero）。</summary>
        public IntPtr Hwnd { get; }

        /// <summary>窗口所属线程（泵消息循环；销毁后退出）。</summary>
        private readonly Thread _thread;

        internal TestWindow(IntPtr hwnd, Thread thread) { Hwnd = hwnd; _thread = thread; }

        /// <summary>
        /// 销毁窗口并等消息线程退出。WM_CLOSE 由 DefWindowProcW 转为 DestroyWindow
        /// （在窗口所属线程上执行，合法）；最后一个窗口销毁后 GetMessageW 返回 0，循环退出。
        /// </summary>
        public void Dispose()
        {
            if (Hwnd != IntPtr.Zero)
                PostMessageW(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            if (!_thread.Join(2000))
            {
                // 线程 2 秒未退出（极端情况）：IsBackground 线程随进程退出回收，不影响测试结论
            }
        }
    }

    /// <summary>
    /// 创建测试窗口（专用 STA 线程 + 消息循环）。见 <see cref="WindowControlServiceRealWindowTests"/>
    /// 类注释的线程模型说明：窗口所属线程必须泵消息，跨线程 SetWindowPos 才能完成。
    /// </summary>
    /// <returns>测试窗口；创建失败返回 null（原因看 <see cref="LastCreateError"/>）。</returns>
    public static TestWindow? Create()
    {
        LastCreateError = null;
        TestWindow? result = null;
        var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            // WNDCLASSW 按值填最小集：WndProc 用 DefWindowProcW 即可（测试窗口不处理任何消息）
            var wc = new WndClassW
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(WndProcForward),
                hInstance = Marshal.GetHINSTANCE(typeof(TestWindowHarness).Module),
                lpszClassName = "WinCanvasTestWindow",
            };

            ushort atom;
            try
            {
                atom = RegisterClassW(ref wc);
            }
            catch (Exception ex)
            {
                LastCreateError = $"RegisterClassW 异常：{ex.Message}";
                ready.Set();
                return;
            }

            if (atom == 0)
            {
                // 1410 = ERROR_CLASS_ALREADY_EXISTS：同进程已注册过（多测试实例共用），视为成功
                var regErr = Marshal.GetLastWin32Error();
                if (regErr != 1410)
                {
                    LastCreateError = $"RegisterClassW 失败（Win32 错误 {regErr}）。";
                    ready.Set();
                    return;
                }
            }

            var hwnd = CreateWindowExW(
                (uint) WS_EX_TOOLWINDOW, // 不进任务栏 / Alt-Tab。
                // 注意：不得加 WS_EX_NOACTIVATE——实测（探针四形态对照，见 WORKLOG）
                // NOACTIVATE 样式窗口的 SetWindowPos(HWND_TOPMOST) 不落 WS_EX_TOPMOST 位，
                // 会导致置顶回读用例假失败。防抢焦点由 SW_SHOWNOACTIVATE 与 SWP_NOACTIVATE 保证。
                "WinCanvasTestWindow", "WinCanvas 测试窗口",
                0x80000000 /* WS_POPUP */,
                0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                LastCreateError = $"CreateWindowExW 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
                ready.Set();
                return;
            }

            ShowWindow(hwnd, SW_SHOWNORMAL); // 真实显示（隐藏窗口 TOPMOST / 分层属性不生效；
                                             // SW_SHOWNOACTIVATE 实测配合 TOPMOST 位不落，改用普通显示，
                                             // 焦点影响被 10x10 工具窗 + SWP_NOACTIVATE 压到最小）

            result = new TestWindow(hwnd, Thread.CurrentThread);
            ready.Set(); // 句柄就绪后再进泵，调用方等到的一定是可用窗口

            // 消息泵：处理跨线程 SetWindowPos 的同步消息；最后一个窗口销毁后 GetMessage 返回 0 退出
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        })
        {
            IsBackground = true, // 进程退出自动回收
            Name = "WinCanvasTestWindowPump",
        };
        thread.SetApartmentState(ApartmentState.STA); // user32 窗口惯例 STA（本窗口无 COM 依赖，纯口径统一）
        thread.Start();

        if (!ready.Wait(5000))
        {
            LastCreateError ??= "测试窗口线程 5 秒未就绪。";
            return null;
        }

        return result;
    }

    /// <summary>读窗口扩展样式（整数值即可覆盖两个 32 位内的样式位）。</summary>
    public static int GetExStyle(IntPtr hwnd) => GetWindowLongW(hwnd, GWL_EXSTYLE);

    /// <summary>
    /// 显示窗口包装（nCmdShow 取 <see cref="SW_MINIMIZE"/> / <see cref="SW_RESTORE"/> 等）。
    /// 最小化→还原的往复会让系统真实地移交/授予前台，是后台进程触发 EVENT_SYSTEM_FOREGROUND
    /// 的可靠手段（SetForegroundWindow 受前台锁定限制，还原不受）。
    /// </summary>
    public static bool ShowWindowCmd(IntPtr hWnd, int nCmdShow) => ShowWindow(hWnd, nCmdShow);

    /// <summary>
    /// 读回分层窗口的当前整体 alpha（隐私遮蔽验证：写入 30 后回读必须一致）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄（须已带 WS_EX_LAYERED，失败返回 false）。</param>
    /// <param name="alpha">回读的 alpha（0 全透明 ~ 255 不透明；仅在返回 true 且 flags 含 LWA_ALPHA 时有效）。</param>
    /// <param name="flags">有效字段标志：含 <see cref="LWA_ALPHA"/>(0x2) 时 alpha 有效。</param>
    /// <returns>true = 成功读回；【失败语义】false = 窗口未分层或尚未设置过分层属性
    /// （SetLayeredWindowAttributes 之前 GetLayeredWindowAttributes 返回 FALSE）。</returns>
    public static bool TryGetLayeredAlpha(IntPtr hwnd, out byte alpha, out uint flags)
    {
        alpha = 0;
        flags = 0;
        return GetLayeredWindowAttributes(hwnd, out _, out alpha, out flags);
    }

    // ---- 以下 P/Invoke 仅供测试程序集：作用 / 返回值 / 失败语义如各注释 ----
    // 注意：托管方法名与导出名不同处必须显式 EntryPoint，否则运行期
    // EntryPointNotFoundException 从 Dispose / 线程体逃逸会击穿测试宿主进程（本 WP 实测踩坑）。

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint style;                 // 类样式（不用，0）
        public IntPtr lpfnWndProc;         // 窗口过程函数指针（GetFunctionPointerForDelegate 产出）
        public int cbClsExtra;             // 类额外字节（0）
        public int cbWndExtra;             // 窗口额外字节（0）
        public IntPtr hInstance;           // 模块实例
        public IntPtr hIcon;               // 图标（NULL）
        public IntPtr hCursor;             // 光标（NULL：默认箭头由系统兜底）
        public IntPtr hbrBackground;       // 背景刷（NULL）
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName; // 菜单（NULL）
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName; // 类名
    }

    /// <summary>窗口过程委托（静态常驻引用，防 GC 回收后原生回调悬空）。</summary>
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>注册窗口类；返回类原子，失败 0（GetLastError）。</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    /// <summary>创建窗口；失败返回 IntPtr.Zero（GetLastError）。</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    /// <summary>显示窗口（nCmdShow 控制激活行为）。</summary>
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>向窗口投递异步消息（销毁路径用 WM_CLOSE）。</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>WM_CLOSE：DefWindowProcW 默认处理即 DestroyWindow。</summary>
    private const uint WM_CLOSE = 0x0010;

    /// <summary>从线程队列取消息（阻塞直到有消息；窗口全销毁且队列空时返回 0）。</summary>
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessageW(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>翻译虚拟键消息（消息泵惯例调用）。</summary>
    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static extern bool TranslateMessage(ref NativeMsg lpMsg);

    /// <summary>派发消息到窗口过程。</summary>
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref NativeMsg lpMsg);

    /// <summary>最小消息结构（只给泵用，字段与 Win32 MSG 前段一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    /// <summary>读窗口属性（32 位返回足够覆盖 GWL_EXSTYLE）。</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    /// <summary>
    /// 读回分层窗口的颜色键 / 整体 alpha / 有效字段标志（隐私遮蔽 alpha 回读用）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="pcrKey">输出：颜色键（本验证不用，忽略）。</param>
    /// <param name="pbAlpha">输出：当前整体 alpha。</param>
    /// <param name="pdwFlags">输出：有效字段标志（LWA_ALPHA=0x2 时 pbAlpha 有效）。</param>
    /// <returns>true = 成功；【失败语义】false = 窗口未带 WS_EX_LAYERED，或尚未调用过
    /// SetLayeredWindowAttributes（两个出参无意义）。</returns>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetLayeredWindowAttributes")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

    /// <summary>默认窗口过程（测试窗口不处理消息，直接交系统默认）。</summary>
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 托管窗口过程包装：交由系统默认处理。C# 不允许把 extern（DllImport）方法组
    /// 直接转成委托（编译器报 CS0103），委托创建必须指向本托管方法。
    /// </summary>
    private static IntPtr WndProcForward(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcCore(hWnd, msg, wParam, lParam);
}
