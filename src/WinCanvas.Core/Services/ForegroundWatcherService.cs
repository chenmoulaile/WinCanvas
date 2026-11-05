using System.Runtime.InteropServices;
using WinCanvas.Core.Native;

namespace WinCanvas.Core.Services;

/// <summary>
/// 前台窗口监听服务（隐私遮蔽专用）：通过全局 <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>
/// 监听「任何窗口取得前台」事件，把【新的前台窗口句柄】上报给订阅方。
///
/// 用途：被管理窗口开启「窗口隐私模式」后，它失去前台（新前台不是它）→ 自动遮蔽内容；
/// 它重新成为前台 → 自动恢复。主窗口自身走 WinUI 的 Activated/Deactivated，不经过本服务。
///
/// 线程模型（与 KeyboardIndicatorService 同一套生命周期纪律）：
/// - WINEVENT_OUTOFCONTEXT 钩子的回调在【安装钩子的线程】上触发，且该线程必须持续泵消息
///   ——安装 / 卸载 / 回调全部在一条专用后台线程（"WinCanvas.ForegroundWatcher"）上完成，
///   绝不在 UI 线程装钩子。
/// - 线程主循环是 GetMessageW：空闲时阻塞在队列上，CPU 占用为零；停止时由外部向该线程
///   PostThreadMessageW(WM_QUIT)，循环退出后由钩子线程自身 UnhookWinEvent
///   （时序最干净：卸载一定发生在最后一次回调之后）。
/// - 回调内只允许做：事件 Id / 对象 Id 过滤 + 转发事件（订阅方自行封送）；
///   任何 Win32 状态变更都必须由订阅方在别的线程做，保证回调微秒级返回。
///
/// 生命周期模型（Start/Stop 的并发正确性，同 KeyboardIndicatorService）：
/// - 每次 Start 创建独立的 <see cref="HookCycle"/>（线程 + 就绪信号 + Win32 线程 Id + 钩子句柄），
///   停止流程只触碰自己捕获的那个 cycle 对象——快速「停→启」时旧周期的收尾绝不会误伤新周期。
/// - Start 只做托管线程创建；钩子安装发生在新线程内部——调用方（UI 线程）零 P/Invoke、零阻塞。
/// - Stop 的 P/Invoke 部分（PostThreadMessageW）派发线程池执行，调用方立即返回。
///
/// 并发与共享状态（每个字段声明保护方式）：
/// - <see cref="_cycle"/>：由 <see cref="_stateGate"/> 保护（锁内只做引用交换，绝不等待）。
/// - <see cref="HookCycle.Hook"/> / <see cref="HookCycle.ThreadId"/>：钩子线程 Volatile.Write，
///   停止方 Volatile.Read。
/// - <see cref="_disposed"/>：volatile，Dispose 置位后 Start 不再起线程。
/// - <see cref="ForegroundChanged"/>：pump 线程触发；订阅方必须自行封送到目标线程
///   （MainViewModel 经 DispatcherQueue.TryEnqueue 回 UI 线程）。
/// </summary>
public sealed class ForegroundWatcherService : IDisposable
{
    /// <summary>保护 <see cref="_cycle"/> / <see cref="_disposed"/> 置换的锁（锁内只做标志与引用读写，绝不等待）。</summary>
    private readonly object _stateGate = new();

    /// <summary>当前运行中的监听周期；null = 未运行。由 <see cref="_stateGate"/> 保护。</summary>
    private HookCycle? _cycle;

    /// <summary>已释放标志：置位后 Start 不再起线程。volatile：Dispose 写、任意线程 Start 读。</summary>
    private volatile bool _disposed;

    /// <summary>
    /// 前台窗口已变化（pump 线程触发）：参数为【新的前台窗口】句柄。
    /// 系统当前没有前台窗口（锁屏瞬间等）不会触发本事件（事件本身就没发生）；
    /// hwnd 为零值理论上仍可能出现（窗口恰在销毁中被上报），订阅方必须判零防御。
    /// </summary>
    public event Action<IntPtr>? ForegroundChanged;

    /// <summary>
    /// 钩子当前是否已安装（真实口径：以 <see cref="_installedCount"/> 为准）。
    /// Stop() 立即把 _cycle 置空并返回，真实卸钩要等钩子线程收尾完成，期间该值仍为 true。
    /// </summary>
    public bool IsHookInstalled => Volatile.Read(ref _installedCount) > 0;

    /// <summary>已安装钩子计数（Interlocked 维护；与 KeyboardIndicatorService 同口径）。</summary>
    private int _installedCount;

    /// <summary>诊断打点（启停 / 安装失败 / 回调异常），供 WORKLOG 验收与问题定位。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>
    /// 钩子安装失败（pump 线程触发，每次 Start 尝试至多一次）。
    /// 参数为可直接展示的原因（中文，含 Win32 错误码）。订阅方（MainViewModel）据此在界面上
    /// 按「失败 + 原因 + 怎么办」提示；服务自身继续存活（Stop/Start 仍可用，重试语义归订阅方）。
    /// </summary>
    public event Action<string>? HookInstallFailed;

    /// <summary>一次 Start 到 Stop 的完整监听周期：每周期独立持有线程与句柄，互不串扰。</summary>
    private sealed class HookCycle
    {
        /// <summary>钩子线程本体。</summary>
        public Thread Thread = null!;

        /// <summary>就绪信号：建队 + 装钩（或放弃装钩）完成后置位，停止方等它再投递 WM_QUIT。
        /// 字段初始化器直接建好实例：HookThreadMain 的异常路径也会 Set 它，绝不允许为 null。</summary>
        public ManualResetEventSlim Ready = new();

        /// <summary>本 cycle 专属的回调委托：作为字段持有，SetWinEventHook 存的函数指针在钩子存续期内始终有根引用。</summary>
        public NativeMethods.WineventProc Proc = null!;

        /// <summary>本线程的 Win32 线程 Id（0 = 未就绪）。钩子线程 Volatile.Write，停止方 Volatile.Read。</summary>
        public uint ThreadId;

        /// <summary>钩子句柄（IntPtr.Zero = 未安装）。仅本 cycle 的钩子线程读写；外部只 Volatile.Read 窥视。</summary>
        public IntPtr Hook;
    }

    /// <summary>
    /// 启动监听（安装全局前台钩子）。幂等：已在运行或已释放时是空操作。
    /// 本方法只做托管线程创建，钩子安装发生在新线程内部——调用方（UI 线程）零 P/Invoke、零阻塞。
    /// 返回 true 表示「监听意图已建立」；钩子真实安装结果经 Diagnostics 上报
    /// （安装失败不打断调用方：隐私开关照常可用，只是失焦不自动遮蔽——失败语义见主界面文案）。
    /// </summary>
    public bool Start()
    {
        lock (_stateGate)
        {
            if (_disposed || _cycle != null)
            {
                return false; // 幂等：重复 Start / 释放后再 Start 都不动作
            }

            var cycle = new HookCycle();
            _cycle = cycle;

            cycle.Thread = new Thread(() => HookThreadMain(cycle))
            {
                IsBackground = true, // 进程退出自动回收，钩子随进程消亡由系统统一摘除
                Name = "WinCanvas.ForegroundWatcher",
            };
            cycle.Thread.Start();
            return true;
        }
    }

    /// <summary>
    /// 停止监听并卸载钩子。幂等：未运行时是空操作。
    /// 本方法立即返回（P/Invoke 派发线程池），真实卸钩由钩子线程在消息循环退出后完成。
    /// </summary>
    public void Stop()
    {
        HookCycle? cycle;
        lock (_stateGate)
        {
            cycle = _cycle;
            _cycle = null;
        }

        if (cycle is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!cycle.Ready.Wait(3000))
                {
                    Diagnostics?.Invoke("前台监听停止：钩子线程 3 秒未就绪，放弃投递 WM_QUIT（线程为后台线程，随进程退出回收）。");
                    return;
                }

                // 投递 WM_QUIT 到钩子线程：GetMessage 返回 0 → 循环退出 → 线程内 UnhookWinEvent
                if (cycle.ThreadId != 0)
                {
                    NativeMethods.PostThreadMessageW(cycle.ThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                Diagnostics?.Invoke($"前台监听停止异常：{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>钩子线程主体：建队 → 装钩 → 泵消息 → 卸钩收尾（一切 Win32 都在本线程）。</summary>
    private void HookThreadMain(HookCycle cycle)
    {
        try
        {
            // 先建消息队列（惰性创建）：PeekMessage(PM_NOREMOVE) 仅建队不取消息，
            // 避免外部稍后 PostThreadMessageW(WM_QUIT) 时队列尚不存在导致投递失败。
            _ = NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);

            cycle.Proc = OnWinEvent;
            cycle.ThreadId = NativeMethods.GetCurrentThreadId();

            // 全局钩子：idProcess=0/idThread=0 = 全系统所有进程线程。
            // 不加 WINEVENT_SKIPOWNPROCESS：本应用主窗口夺取前台时被管理窗口同样失焦，必须照常收到事件。
            cycle.Hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.GetModuleHandleW(null), cycle.Proc,
                0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

            if (cycle.Hook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Diagnostics?.Invoke($"前台监听钩子安装失败：SetWinEventHook 返回 NULL（Win32 错误 {error}），失焦遮蔽不可用。");
                HookInstallFailed?.Invoke($"SetWinEventHook 安装失败（Win32 错误 {error}）。");
            }
            else
            {
                Interlocked.Increment(ref _installedCount);
                Diagnostics?.Invoke("前台监听钩子已安装（EVENT_SYSTEM_FOREGROUND，全局）。");
            }

            cycle.Ready.Set(); // 就绪（无论装钩成败）：停止方此后投递 WM_QUIT 才有意义

            // 消息泵：空闲阻塞零 CPU；收到 WM_QUIT（GetMessage 返回 0）退出
            while (NativeMethods.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // 前台事件走钩子回调（不经消息派发），队列里不会出现需要派发的窗口消息；
                // 兜底派发以防将来有人向该线程 Post 消息。
                // （DispatchMessage 需要 MSG 结构与 Translate/Dispatch 配对，这里不做冗余调用。）
            }
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke($"前台监听线程异常退出：{ex.GetType().Name}: {ex.Message}");
            cycle.Ready.Set(); // 异常也要放行等待方，避免 Stop 永远等就绪
        }
        finally
        {
            // 卸钩一定在最后一次回调之后（本线程内收尾，时序最干净）
            if (cycle.Hook != IntPtr.Zero)
            {
                _ = NativeMethods.UnhookWinEvent(cycle.Hook);
                Interlocked.Decrement(ref _installedCount);
                cycle.Hook = IntPtr.Zero;
                Diagnostics?.Invoke("前台监听钩子已卸载。");
            }
        }
    }

    /// <summary>
    /// WinEvent 回调（pump 线程）。绝不抛异常——异常会沿 DispatchMessageW 涌入 pump 循环
    /// 杀死监听线程（与 WallpaperHostWindow 的回调纪律一致）。
    /// 只做两级过滤（事件 Id / 窗口本体对象 Id）+ 判零，其余全部交给订阅方。
    /// </summary>
    private void OnWinEvent(IntPtr hHook, uint @event, IntPtr hwnd, int idObject, int idChild, uint threadId, uint timeMs)
    {
        try
        {
            if (@event != NativeMethods.EVENT_SYSTEM_FOREGROUND || idObject != NativeMethods.OBJID_WINDOW)
            {
                return;
            }

            if (hwnd == IntPtr.Zero)
            {
                return; // 窗口销毁竞态下系统可能上报零句柄：订阅方无法处理，直接丢弃
            }

            ForegroundChanged?.Invoke(hwnd);
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke($"前台监听回调异常（已吞掉不外抛）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>释放：停止监听（Stop 幂等）。</summary>
    public void Dispose()
    {
        lock (_stateGate)
        {
            _disposed = true;
        }

        Stop();
    }
}
