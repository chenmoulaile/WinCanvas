using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinCanvas.App.Native;

namespace WinCanvas.App.Services;

/// <summary>
/// 一个跑在专用消息线程上的【隐藏顶层窗口】，为托盘图标与全局热键提供
/// 接收 <c>WM_TRAYICON</c> / <c>WM_HOTKEY</c> 的句柄。
///
/// 为什么两者共用一个窗口：
/// RegisterHotKey 与 Shell_NotifyIcon 都要求「有一个窗口来收消息」。
/// 各自开一个窗口就是两条常驻消息线程；合到一个隐藏窗口上，
/// 托盘和热键共用同一条泵线程，进程里只多一条线程、多一个 HWND。
///
/// 为什么是 WS_POPUP 而不是「消息窗口」(HWND_MESSAGE)：
/// 消息-only 窗口与托盘弹菜单的前台/焦点配合不可靠，会出现
/// 「菜单点了不关、鼠标穿透」的经典毛病。这里建的是真窗口，
/// 但不带 WS_VISIBLE、尺寸 0x0，任务栏与 alt-tab 都看不到它。
///
/// 职责边界：本类只管「有窗口、有消息循环、把消息转成事件」。
/// 托盘菜单的具体构建与 TrackPopupMenu 交给 <see cref="TrayIconService"/>
/// （它需要自己的 Hwnd 作参数，放在这里反而要来回传）。
///
/// 线程安全：事件一律在【泵线程】上触发，订阅方需要 UI 线程时自己封送。
/// </summary>
internal sealed class MessagePumpWindow : IDisposable
{
    /// <summary>进程内窗口类名（注册一次，多个实例共用）。</summary>
    private const string ClassName = "WinCanvas.SystemMessageWindow";

    /// <summary>
    /// hwnd → 实例的路由表。
    /// 用托管字典而不是 GWLP_USERDATA 存 GCHandle：后者要保证句柄在窗口销毁前
    /// 一定 Free，漏一个就是 use-after-free；路由表由创建与销毁两路各自增删，
    /// 生命周期天然对齐，最坏情况也只是消息没人处理，不会踩内存。
    /// </summary>
    private static readonly ConcurrentDictionary<IntPtr, MessagePumpWindow> s_routes = new();

    /// <summary>窗口类已注册的 atom（0 = 未注册或注册失败）。</summary>
    private static ushort s_classAtom;

    /// <summary>
    /// 静态持有 WndProc 委托。必须长期引用：它是非托管函数指针指向的目标，
    /// 委托一旦被 GC，回调会打到已回收的桩代码上（表现为随机崩溃）。
    /// </summary>
    private static readonly ShellNativeMethods.WndProc s_wndProc = WndProcStatic;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new(false);
    private IntPtr _hwnd;
    private int _createError;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <summary>本窗口句柄（创建完成后可用；失败为 IntPtr.Zero）。</summary>
    internal IntPtr Hwnd => _hwnd;

    /// <summary>WM_HOTKEY 命中（参数 = 注册时用的热键 Id）。在泵线程触发。</summary>
    internal event Action<int>? HotkeyPressed;

    /// <summary>
    /// 需要弹出托盘上下文菜单（右键 / 中键点托盘图标）。参数为鼠标屏幕坐标。
    /// 在泵线程触发，处理器【必须同步返回】——TrackPopupMenuEx 本身是模态的，
    /// 异步化会让菜单丢掉鼠标捕获。
    /// </summary>
    internal Action<ShellNativeMethods.POINT>? TrayMenuRequested;

    /// <summary>托盘图标上的其它鼠标消息（左键抬起 / 双击等）。在泵线程触发。</summary>
    internal event Action<uint>? TrayMouseMessage;

    /// <summary>
    /// 自定义「到泵线程执行」消息（WM_APP 起手可自由使用，系统不占用）。
    /// </summary>
    private const uint WM_INVOKE = 0x8000 + 1;

    /// <summary>待泵线程执行的工作项队列（任意线程入队，泵线程出队）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _invokeQueue = new();

    private MessagePumpWindow()
    {
        _thread = new Thread(Pump)
        {
            Name = "WinCanvas.SystemMessage",
            // 后台线程：进程退出时不被它卡住。托盘与热键属于「有更好、没有也不能崩」的设施。
            IsBackground = true,
        };
    }

    /// <summary>
    /// 创建并启动消息窗口，阻塞等待窗口真正建好（调用方拿到 Hwnd 就要注册热键）。
    /// </summary>
    /// <param name="error">失败原因；成功为 null。</param>
    /// <returns>成功返回实例；失败返回 null。</returns>
    internal static MessagePumpWindow? TryCreate(out string? error)
    {
        error = null;
        var window = new MessagePumpWindow();

        try
        {
            window._thread.Start(window);
            if (!window._created.Wait(TimeSpan.FromSeconds(5)))
            {
                error = "系统消息窗口创建超时（5 秒）";
                window.Dispose();
                return null;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            window.Dispose();
            return null;
        }

        if (window._hwnd == IntPtr.Zero)
        {
            error = $"隐藏消息窗口创建失败（Win32 错误 {window._createError}）";
            window.Dispose();
            return null;
        }

        return window;
    }

    /// <summary>泵线程主体：建窗口 → GetMessage 循环 → 销毁窗口。</summary>
    private void Pump()
    {
        try
        {
            EnsureClassRegistered();

            // WS_POPUP 不带 WS_VISIBLE：窗口存在但从不显示
            _hwnd = ShellNativeMethods.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: ClassName,
                lpWindowName: "WinCanvas System Message Window",
                dwStyle: 0,
                x: 0, y: 0, nWidth: 0, nHeight: 0,
                hWndParent: IntPtr.Zero,
                hMenu: IntPtr.Zero,
                hInstance: ShellNativeMethods.GetModuleHandleW(null),
                lpParam: IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _createError = Marshal.GetLastWin32Error();
                return;
            }

            s_routes[_hwnd] = this;
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "系统消息窗口线程初始化失败");
        }
        finally
        {
            // 无论成功失败都要让创建方下来，否则它白等 5 秒
            _created.Set();
        }

        // 标准消息循环。GetMessageW 返回 false 表示收到 WM_QUIT。
        while (!_stopRequested && ShellNativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            ShellNativeMethods.TranslateMessage(ref msg);
            ShellNativeMethods.DispatchMessageW(ref msg);
        }

        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            s_routes.TryRemove(hwnd, out _);
            ShellNativeMethods.DestroyWindow(hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    /// <summary>注册窗口类（进程内一次）。失败时 atom 为 0，CreateWindow 会连带失败。</summary>
    private static void EnsureClassRegistered()
    {
        if (s_classAtom != 0)
        {
            return;
        }

        var wc = new ShellNativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<ShellNativeMethods.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = s_wndProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = ShellNativeMethods.GetModuleHandleW(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero,
        };

        s_classAtom = ShellNativeMethods.RegisterClassExW(ref wc);
    }

    /// <summary>
    /// 静态窗口过程：查路由表转给实例。
    /// 【绝不许异常穿出到非托管帧】——托管异常穿过 WndProc 会直接终结进程，
    /// 而且是在消息派发路径上炸，症状是「不知为什么程序就没了」。
    /// </summary>
    private static IntPtr WndProcStatic(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (s_routes.TryGetValue(hWnd, out var window))
            {
                return window.HandleMessage(msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, $"系统消息窗口 WndProc 异常（msg=0x{msg:X}）");
            return IntPtr.Zero;
        }

        return ShellNativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>实例级消息处理。</summary>
    private IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case ShellNativeMethods.WM_HOTKEY:
                HotkeyPressed?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;

            case ShellNativeMethods.WM_TRAYICON:
                return HandleTrayIcon(lParam);

            case WM_INVOKE:
                // 把排队的委托在本线程上跑完。异常必须吞在循环内：
                // 一个注册失败不能把整条消息线程带崩。
                while (_invokeQueue.TryDequeue(out var work))
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        App.LogFatal(ex, "系统消息窗口上的排队动作失败");
                    }
                }

                return IntPtr.Zero;

            case ShellNativeMethods.WM_DESTROY:
                ShellNativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            case ShellNativeMethods.WM_CLOSE:
                _stopRequested = true;
                ShellNativeMethods.DestroyWindow(_hwnd);
                return IntPtr.Zero;

            default:
                return ShellNativeMethods.DefWindowProcW(_hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// 托盘回调：<c>lParam</c> 是鼠标消息号（未调用 NIM_SETVERSION，走经典语义）。
    /// 右键 / 中键交给 TrayMenuRequested，其余原样转给 TrayMouseMessage。
    /// </summary>
    private IntPtr HandleTrayIcon(IntPtr lParam)
    {
        uint mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);

        switch (mouseMessage)
        {
            case ShellNativeMethods.WM_RBUTTONUP:
            case ShellNativeMethods.WM_MBUTTONUP:
                // 取鼠标位置交给服务层弹菜单；菜单本身不在这里碰
                if (ShellNativeMethods.GetCursorPos(out var point))
                {
                    TrayMenuRequested?.Invoke(point);
                }

                return IntPtr.Zero;

            default:
                TrayMouseMessage?.Invoke(mouseMessage);
                return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 在泵线程上执行 <paramref name="work"/> 并等待其结果。
    ///
    /// 为什么必须有这个通道：<c>RegisterHotKey</c> 的 WM_HOTKEY 是投给
    /// 【调用注册的那个线程】的消息队列的。热键若从 UI 线程注册，消息就全去了
    /// UI 队列、本窗口的 WndProc 一条也收不到 —— 表现为「热键注册成功但按了没反应」。
    /// 所以注册/注销必须在泵线程上做。
    ///
    /// 为什么用 PostMessage + TCS 而不是 SendMessage：SendMessage 是同步的，
    /// 泵线程此刻可能正卡在模态的托盘菜单里（TrackPopupMenuEx 内部跑自己的消息循环，
    /// 但调用栈仍在那条线程上），从 UI 线程发同步消息会把 UI 线程一起挂住。
    /// PostMessage 异步 + 超时兜底，最坏情况是这次注册超时失败并被如实报告。
    /// </summary>
    /// <param name="work">要在泵线程执行的函数。</param>
    /// <param name="timeout">等待结果的最长时间。</param>
    /// <returns>work 的返回值；窗口已失效或超时时返回 default。</returns>
    internal async Task<T?> InvokeAsync<T>(Func<T?> work, TimeSpan timeout) where T : class
    {
        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || _disposed)
        {
            return default;
        }

        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _invokeQueue.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!ShellNativeMethods.PostMessageW(hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero))
        {
            _invokeQueue.TryDequeue(out _);
            return default;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            return default; // 超时：调用方按「未生效」处理
        }

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "泵线程上的委托抛出异常");
            return default;
        }
    }

    /// <summary>释放：请求泵线程退出并等它把窗口销毁。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            // PostMessage 而不是 SendMessage：从调用线程同步等泵线程处理，
            // 而泵线程此刻可能正卡在模态菜单里，会直接把调用方拖死。
            ShellNativeMethods.PostMessageW(hwnd, ShellNativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        try
        {
            _created.Set(); // 万一还没建起来就要求释放，别让它白等
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "系统消息窗口线程回收超时");
        }
    }
}
