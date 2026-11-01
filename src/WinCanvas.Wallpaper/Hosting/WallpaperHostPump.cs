using System;
using System.Runtime.InteropServices;
using System.Threading;
using WinCanvas.Wallpaper.Native;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 宿主窗口专用消息线程（全部宿主窗口共享一条线程，避免每会话一线程）。
/// 职责：
/// - 跑标准 GetMessageW 循环——这是两个硬需求的前提：
///   ① 跨线程 DestroyWindow 非法，窗口必须在创建线程销毁，宿主窗口全部在本线程创建/销毁；
///   ② SetWinEventHook(WINEVENT_OUTOFCONTEXT) 的回调只在「安装钩子且在跑消息循环的线程」上派发，
///      钩子也统一在本线程安装。
/// - 提供跨线程同步执行入口 <see cref="Execute"/>：把委托打包成 GCHandle 经
///   PostThreadMessageW 投递，在本线程执行并回执完成信号（创建宿主 / 销毁宿主 / 卸钩子都走它）。
/// 线程模型：IsBackground 后台线程（进程退出自动回收），永不自旋、永不 Sleep 等待，
/// 空闲时阻塞在 GetMessageW 上，零 CPU 占用。
/// </summary>
internal sealed class WallpaperHostPump
{
    /// <summary>进程级共享实例：全部宿主窗口与 WinEvent 钩子共用一条消息线程。</summary>
    public static readonly WallpaperHostPump Shared = new();

    /// <summary>保护启动竞态的锁（EnsureStarted 可能被多个会话并发调用）。</summary>
    private readonly object _startGate = new();

    /// <summary>pump 线程完成初始化（已注册原生线程 Id）的信号。</summary>
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>pump 线程的原生线程 Id（PostThreadMessageW 的投递目标）；由本线程自身写入。</summary>
    private volatile uint _nativeThreadId;

    /// <summary>pump 线程的托管线程 Id（Execute 的「已在 pump 线程上」快路径判断）。</summary>
    private volatile int _managedThreadId;

    /// <summary>pump 线程是否仍在运行（IsAlive 快照，仅诊断用）。</summary>
    public bool IsRunning => _pumpThread?.IsAlive ?? false;

    /// <summary>pump 线程对象（保留引用防 GC；线程本体从不主动退出，除非 RequestStop）。</summary>
    private Thread? _pumpThread;

    /// <summary>确保 pump 线程已启动并完成初始化。线程安全（锁内双检）。</summary>
    /// <returns>true = 线程在运行；false = 启动失败（极罕见，调用方放弃挂接并上报）。</returns>
    public bool EnsureStarted()
    {
        if (IsRunning && _nativeThreadId != 0)
            return true;

        lock (_startGate)
        {
            if (IsRunning && _nativeThreadId != 0)
                return true;

            _ready.Reset();
            _pumpThread = new Thread(PumpLoop)
            {
                Name = "WinCanvas.Wallpaper.HostPump",
                // 后台线程：应用退出时不阻塞进程终止；宿主窗口的常规销毁走 ShutdownAll 显式完成
                IsBackground = true,
                Priority = ThreadPriority.Normal,
            };
            _pumpThread.Start();

            // 等线程注册好原生线程 Id（GetMessage 循环开始前完成），失败视为启动异常
            return _ready.Wait(TimeSpan.FromSeconds(5)) && _nativeThreadId != 0;
        }
    }

    /// <summary>
    /// 在 pump 线程上同步执行一个委托（宿主创建 / 销毁 / 卸钩子的统一跨线程通道）。
    /// 已在 pump 线程上时直接内联执行（WinEvent 回调里需要再动宿主时的自调用场景）。
    /// </summary>
    /// <param name="action">待执行委托（在 pump 线程上运行，内部不得再同步等待 pump）。</param>
    /// <param name="timeoutMs">等待回执的超时（pump 假死时让调用方及时脱身，默认 10s）。</param>
    /// <returns>true = 已执行完成；false = 投递失败或等待超时（调用方按挂接失败处理）。</returns>
    public bool Execute(Action action, int timeoutMs = 10000)
    {
        if (!EnsureStarted())
            return false;

        // 快路径：已在 pump 线程（如 WinEvent 回调内部操作宿主），直接执行避免自等待死锁
        if (Environment.CurrentManagedThreadId == _managedThreadId)
        {
            action();
            return true;
        }

        // 慢路径：GCHandle 打包委托 → PostThreadMessageW 投递 → 等待回执。
        // 句柄所有权：投递成功后由 pump 端 Free；投递失败由本端 Free（避免句柄泄漏）。
        var done = new ManualResetEventSlim(false);
        Action wrapped = () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // pump 任务异常不允许杀死消息循环；记录诊断后继续服务后续任务
                HostDiagnostics.Log($"pump 任务异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                done.Set();
            }
        };

        var handle = GCHandle.Alloc(wrapped);
        try
        {
            // PostThreadMessageW 在目标线程尚未创建消息队列时报 ERROR_INVALID_THREAD_ID——
            // 线程的消息队列要到第一次消息函数调用才建立；虽然 pump 已在 ready 前自建队列，
            // 仍保留有限重试兜底（WP1-C 实测存在启动窗口期投递失败）。
            for (var attempt = 0; ; attempt++)
            {
                if (HostNativeMethods.PostThreadMessageW(
                        _nativeThreadId, HostNativeMethods.WM_APP_PUMP_TASK, GCHandle.ToIntPtr(handle), IntPtr.Zero))
                    break;

                if (attempt >= 4)
                {
                    handle.Free();
                    return false;
                }

                Thread.Sleep(20);
            }

            handle = default; // 所有权已移交 pump 端
            return done.Wait(timeoutMs);
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();

            done.Dispose();
        }
    }

    /// <summary>请求 pump 线程退出（仅诊断命令结束时调用；应用退出靠 IsBackground 自动回收）。</summary>
    public void RequestStop()
    {
        if (_nativeThreadId != 0)
            HostNativeMethods.PostThreadMessageW(_nativeThreadId, HostNativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>pump 线程主体：初始化 COM → 注册线程 Id → 强制建消息队列 → 就绪信号 → GetMessageW 循环。</summary>
    private void PumpLoop()
    {
        // 关键：COM 初始化必须先行——WinRT 对象激活（如视频组合的 new Compositor()）要求线程
        // 处于 COM apartment；裸线程直接激活会抛 UnauthorizedAccessException（WP1-C 实测）。
        // STA(0x2)：WinRT 组合对象要求 STA 语义；S_FALSE(已初始化) / RPC_E_CHANGED_MODE 视为可继续。
        var coInitHr = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
        if (coInitHr != 0 && coInitHr != 1 /* S_FALSE */)
            HostDiagnostics.Log($"pump 线程 CoInitializeEx 失败 hr=0x{coInitHr:X8}（WinRT 激活可能不可用）");

        _managedThreadId = Environment.CurrentManagedThreadId;
        // PostThreadMessageW 的投递目标必须是 Win32 原生线程 Id（非托管 Thread.ManagedThreadId）
        _nativeThreadId = GetCurrentThreadId();

        // 关键：先强制创建本线程的消息队列再发就绪信号——线程队列在首次消息函数调用时才建立，
        // 若先 Set 就绪、调用方立即 PostThreadMessageW，会撞上队列未建的竞态窗口
        //（WP1-C 实测 ERROR_INVALID_THREAD_ID 间歇性失败，见 WORKLOG）。
        _ = PeekMessageW(out _, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */);
        _ready.Set();

        while (HostNativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == HostNativeMethods.WM_APP_PUMP_TASK)
            {
                // 任务消息：wParam = GCHandle<Action>，执行后释放句柄（所有权见 Execute）
                try
                {
                    var handle = GCHandle.FromIntPtr(msg.wParam);
                    if (handle.Target is Action action)
                        action();
                }
                finally
                {
                    GCHandle.FromIntPtr(msg.wParam).Free();
                }

                continue;
            }

            HostNativeMethods.TranslateMessage(ref msg);
            HostNativeMethods.DispatchMessageW(ref msg);
        }
    }

    /// <summary>
    /// 取当前线程的原生线程 Id（PostThreadMessageW 投递目标）。
    /// </summary>
    /// <returns>当前线程的 Win32 线程 Id。</returns>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// 非移除式消息探测（pump 启动时调用一次，强制创建线程消息队列）。
    /// </summary>
    /// <param name="msg">输出：取到的消息（PM_NOREMOVE 下不取走）。</param>
    /// <param name="hWnd">过滤窗口（NULL）。</param>
    /// <param name="min">最低消息 Id。</param>
    /// <param name="max">最高消息 Id。</param>
    /// <param name="remove">PM_NOREMOVE(0) / PM_REMOVE(1)。</param>
    /// <returns>true = 有消息；false = 无消息。</returns>
    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out PeekMsg msg, IntPtr hWnd, uint min, uint max, uint remove);

    /// <summary>
    /// 当前线程 COM 初始化（pump 启动时调用：WinRT 激活要求线程处于 COM apartment）。
    /// </summary>
    /// <param name="reserved">必须为 NULL。</param>
    /// <param name="coInit"> apartment 模型（COINIT_APARTMENTTHREADED = 0x2）。</param>
    /// <returns>S_OK(0) 新初始化；S_FALSE(1) 已按此模型初始化；RPC_E_CHANGED_MODE 已按其他模型初始化。</returns>
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    /// <summary>MSG 结构（PeekMessageW 出参；与 user32 MSG 同布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PeekMsg
    {
        public IntPtr Hwnd;     // 目标窗口（线程消息为 NULL）
        public uint Message;    // 消息 Id
        public IntPtr WParam;   // 字参数
        public IntPtr LParam;   // 长参数
        public uint Time;       // 投递时间
        public int PtX;         // 光标 x
        public int PtY;         // 光标 y
    }
}
