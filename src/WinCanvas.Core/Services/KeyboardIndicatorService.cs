using System.Runtime.InteropServices;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Native;

namespace WinCanvas.Core.Services;

/// <summary>
/// NumLock 键盘指示器服务（WP2）：监听物理 / 注入的 NumLock 键事件，把「锁定状态变化」上报给
/// <see cref="IStatusNotifier"/>（即 WinCanvas.Wallpaper 的 WinTipsIntegrationService，
/// 由它决定弹 WinTips.exe 浮窗还是走本地回退通道）。
///
/// 线程模型（为什么这样设计）：
/// - 低级键盘钩子（WH_KEYBOARD_LL）的回调在「安装钩子的线程」上同步执行，且该线程必须持续泵消息，
///   否则全系统键盘输入被挂起——因此安装 / 卸载 / 回调全部在一条专用后台线程
///   （"WinCanvas.KeyboardHook"）上完成，绝不在 UI 线程装钩子（总提示词 §WP2 明令禁止）。
/// - 线程主循环是 GetMessageW：空闲时阻塞在队列上，CPU 占用为零；停止时由外部向该线程
///   PostThreadMessageW(WM_QUIT) 使 GetMessage 返回 0 退出循环，循环退出后由钩子线程自身
///   UnhookWindowsHookEx（时序最干净：卸载一定发生在最后一次回调之后）。
/// - 钩子回调内只允许做：读一次 GetKeyState + 去抖比较 + 必要时线程池派发；
///   弹浮窗 / 启动进程等耗时动作全部在 ThreadPool 上异步执行，
///   保证回调在微秒级返回，全系统键盘输入无可感知延迟。
///
/// 生命周期模型（Start/Stop 的并发正确性）：
/// - 每次 Start 创建一个独立的 <see cref="HookCycle"/>（线程 + 就绪信号 + Win32 线程 Id + 钩子句柄），
///   停止流程只触碰自己捕获的那个 cycle 对象——快速「关→开」时，旧周期的收尾绝不会误伤新周期。
/// - Start 只做托管线程创建（Thread.Start 是托管 API），钩子安装发生在新线程内部——
///   调用方（UI 线程）零 P/Invoke、零阻塞，满足「UI 线程 16ms 预算」约束；
///   Stop 的 P/Invoke 部分（PostThreadMessageW）与 Join 派发线程池执行，调用方立即返回。
///   Start→Stop 的顺序由同一线程发起的 Task.Run 全局队列 FIFO 保证。
///
/// 并发与共享状态（每个字段声明保护方式）：
/// - <see cref="_cycle"/>：由 <see cref="_stateGate"/> 保护（锁内只做引用交换，绝不等待）。
/// - <see cref="HookCycle.Hook"/> / <see cref="HookCycle.ThreadId"/>：该 cycle 的钩子线程写
///   （Volatile.Write 释放），StopCore / IsHookInstalled 以 Volatile.Read 读取。
/// - <see cref="_lastReportedState"/>：各 cycle 的回调与去抖共享（跨周期也不重复上报同一状态），
///   用 Interlocked 原子交换，无锁。
/// - <see cref="_disposed"/>：volatile，Dispose 置位后 Start 不再起线程（退出后迟到的 Start 不复活钩子）。
/// </summary>
public sealed class KeyboardIndicatorService : IDisposable
{
    /// <summary>上报目标（构造注入）：通常为 WinTipsIntegrationService，内部已有 IsEnabled 开关兜底。</summary>
    private readonly IStatusNotifier _notifier;

    /// <summary>保护 _cycle / _disposed 相关置换的锁（锁内只做标志与引用读写，绝不等待）。</summary>
    private readonly object _stateGate = new();

    /// <summary>当前运行中的钩子周期；null = 未运行（或上一轮已完整退出）。由 <see cref="_stateGate"/> 保护。</summary>
    private HookCycle? _cycle;

    /// <summary>
    /// 已释放标志：置位后 Start 不再起线程（应用退出后的迟到 Start 请求不复活钩子线程）。
    /// volatile：Dispose 在调用方线程写，Start 在（可能不同的）调用方线程读。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 状态去抖器（WP4 自本类抽离为纯逻辑类型 <see cref="NumLockStateDebouncer"/>）：
    /// 与上次上报值相同的事件不上报（抑制按住自动重复的 KEYDOWN 风暴）。
    /// 各周期的钩子线程共享同一实例（Interlocked 原子交换，无锁），跨周期也不重复上报。
    /// </summary>
    private readonly NumLockStateDebouncer _debouncer = new();

    /// <summary>诊断打点（低频事件：启停 / 状态变化 / 上报失败），供 WORKLOG 验收与问题定位。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>
    /// 当前已成功安装的钩子数（0 = 无钩子）。由各钩子线程在装钩成功 / 卸钩完成时以 Interlocked 增减，
    /// IsHookInstalled 只读窥视——注意它反映「真实钩子状态」而非「服务是否处于运行意图」：
    /// Stop() 立即把 _cycle 置空并返回，但真实卸钩要等钩子线程收尾完成（有界毫秒级），期间该值仍为 1。
    /// </summary>
    private int _installedCount;

    /// <summary>
    /// 钩子当前是否已安装（供诊断 / 验收轮询，以 <see cref="_installedCount"/> 为准的跨周期真实口径）。
    /// </summary>
    public bool IsHookInstalled => Volatile.Read(ref _installedCount) > 0;

    /// <param name="notifier">上报目标；不得为 null（构造即校验，装配错误尽早暴露）。</param>
    public KeyboardIndicatorService(IStatusNotifier notifier)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    /// <summary>一次 Start 到 Stop 的完整钩子周期：每周期独立持有线程与句柄，互不串扰。</summary>
    private sealed class HookCycle
    {
        /// <summary>钩子线程本体。</summary>
        public Thread Thread = null!;

        /// <summary>就绪信号：建队 + 装钩（或放弃装钩）完成后置位，停止方等它再投递 WM_QUIT。</summary>
        public ManualResetEventSlim Ready = null!;

        /// <summary>本 cycle 专属的回调委托：闭包捕获本 cycle，保证 CallNextHookEx 用对句柄；
        /// 作为字段持有，SetWindowsHookExW 存的函数指针在钩子存续期内始终有根引用（防 GC 回收后野指针）。</summary>
        public NativeMethods.LowLevelKeyboardProc Proc = null!;

        /// <summary>
        /// 停止请求标志：Stop / Dispose 与「线程还在装钩子」竞态时，线程见此标志直接跳过安装退出。
        /// 不声明 volatile 关键字（避免与 Volatile.Read/Write 混用触发 CS0420 噪声），
        /// 全部经 Volatile.Write 写、Volatile.Read 读。
        /// </summary>
        public bool StopRequested;

        /// <summary>本线程的 Win32 线程 Id（0 = 未就绪）。钩子线程 Volatile.Write，停止方 Volatile.Read。</summary>
        public uint ThreadId;

        /// <summary>钩子句柄（IntPtr.Zero = 未安装）。仅本 cycle 的钩子线程读写；外部只 Volatile.Read 窥视。</summary>
        public IntPtr Hook;
    }

    /// <summary>
    /// 启动监听（安装 LL 键盘钩子）。幂等：已在运行或已释放时是空操作。
    /// 本方法只做托管线程创建，钩子安装发生在新线程内部——调用方（UI 线程）零 P/Invoke、零阻塞。
    /// </summary>
    public void Start()
    {
        lock (_stateGate)
        {
            if (_disposed || _cycle != null)
                return; // 幂等：重复 Start / 释放后再 Start 都不动作
        }

        // 先建对象再挂闭包（回调 / 线程体都要引用 cycle 本体，不能在初始化器内自引用）
        var cycle = new HookCycle();
        // 委托在此创建并长期持有：避免「每次调用新建委托 → 旧委托被 GC 回收」的经典钩子崩溃模式
        cycle.Proc = (nCode, wParam, lParam) => KeyboardHookProc(cycle, nCode, wParam, lParam);
        cycle.Ready = new ManualResetEventSlim(false);
        cycle.Thread = new Thread(() => HookThreadProc(cycle))
        {
            IsBackground = true,              // 进程退出自动回收（「退出自动卸载」兜底之一）
            Name = "WinCanvas.KeyboardHook",  // 诊断时可在转储中一眼认出
            Priority = ThreadPriority.Normal, // 不提优先级：回调只做微秒级判定，Normal 足够
        };

        lock (_stateGate)
        {
            // 二次检查（两个并发 Start 之间）：后到者不覆盖先到者的周期
            if (_disposed || _cycle != null)
                return;

            _cycle = cycle;
            cycle.Thread.Start();
        }
    }

    /// <summary>
    /// 停止监听（卸载 LL 键盘钩子）。幂等：未运行时是空操作。
    /// 投递 WM_QUIT 与 Join 都是 P/Invoke / 阻塞动作，故整体派发线程池执行，调用方立即返回。
    /// </summary>
    public void Stop()
    {
        // 在调用方线程（可能 UI 线程）只做锁内引用交换，真正的投递 / 等待交给线程池
        HookCycle? cycle;
        lock (_stateGate)
        {
            if (_disposed || _cycle == null)
                return; // 幂等：未运行 / 已释放都不动作

            cycle = _cycle;
            _cycle = null;
        }

        Volatile.Write(ref cycle.StopRequested, true); // 与「装钩尚未完成」竞态时，线程自行跳过安装
        _ = Task.Run(() =>
        {
            try
            {
                StopCore(cycle);
            }
            catch (Exception ex)
            {
                Log($"NumLock 钩子停止流程异常：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 释放：显式同步卸钩（应用退出时由 MainWindow.Closed 调用——「进程退出自动卸载 + 显式 Unhook」双保险的后者）。
    /// 关闭窗口是进程收尾路径，允许一次有界等待（就绪 ≤5s + 退出 ≤3s），换取「方法返回时钩子必然已卸载」的确定性。
    /// </summary>
    public void Dispose()
    {
        HookCycle? cycle;
        lock (_stateGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            cycle = _cycle;
            _cycle = null;
        }

        if (cycle != null)
        {
            Volatile.Write(ref cycle.StopRequested, true);
            StopCore(cycle);
        }
    }

    /// <summary>
    /// 停止核心（线程池或 Dispose 路径执行）：等待该周期就绪 → 向其线程投递 WM_QUIT → 等线程退出。
    /// 卸钩动作本身由钩子线程在消息循环退出后自行执行（见 <see cref="HookThreadProc"/> 尾段）。
    /// </summary>
    /// <param name="cycle">要停止的钩子周期（Start 与 Stop 捕获的是同一对象，快速开关也不串扰）。</param>
    private void StopCore(HookCycle cycle)
    {
        // 就绪等待：线程刚启动的瞬间窗口内（建队 / 装钩前），WM_QUIT 还不能投——等它把队列建好
        if (!cycle.Ready.Wait(5000))
        {
            Log("NumLock 钩子线程 5 秒未就绪，放弃投递 WM_QUIT（钩子未必已装，残留由进程退出兜底）");
            return;
        }

        var tid = Volatile.Read(ref cycle.ThreadId);
        if (tid == 0 || !NativeMethods.PostThreadMessageW(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero))
        {
            Log($"NumLock 钩子线程投递 WM_QUIT 失败（tid={tid}，err={Marshal.GetLastWin32Error()}），改由进程退出兜底卸载");
            return;
        }

        if (!cycle.Thread.Join(3000))
            Log("NumLock 钩子线程 3 秒未退出（极端情况；进程退出时 IsBackground 线程会被强制回收）");
    }

    /// <summary>
    /// 钩子线程主体：建队 → 报线程 Id → 装钩 → 泵消息 → 卸钩。安装与卸载都在本线程完成，时序自洽。
    /// </summary>
    /// <param name="cycle">本线程所属的钩子周期（装钩 / 卸钩都写回该对象）。</param>
    private void HookThreadProc(HookCycle cycle)
    {
        try
        {
            // 1. 先建 Win32 消息队列：线程队列是惰性创建的，先 Peek 一次（PM_NOREMOVE）再让外部投递
            //    WM_QUIT，避免「队列尚不存在 → PostThreadMessageW 失败」的竞态。
            NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);

            // 2. 记录本线程 Win32 线程 Id（先写 Id 再报就绪，两者都完成后外部投递才可靠）
            Volatile.Write(ref cycle.ThreadId, NativeMethods.GetCurrentThreadId());

            // 3. 装钩（除非停止请求先到）：先播种状态再装钩，装钩后回调才可能到达——播种与回调无竞态
            if (Volatile.Read(ref cycle.StopRequested))
            {
                Log("停止请求先于安装到达：本次 Start 放弃装钩，线程直接退出");
                return;
            }

            // 播种去抖基线：读当前全局 NumLock 开关位作为「上次上报值」，
            // 使开关开启后的第一次真实翻转必然上报（且不会把「开启前就处于的旧状态」误报为变化）。
            var lockedNow = (NativeMethods.GetKeyState(NativeMethods.VK_NUMLOCK) & 0x0001) != 0;
            _debouncer.Seed(lockedNow);

            cycle.Hook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL, cycle.Proc, NativeMethods.GetModuleHandleW(null), 0);
            if (cycle.Hook == IntPtr.Zero)
            {
                Log($"SetWindowsHookExW 失败（err={Marshal.GetLastWin32Error()}），NumLock 提示不可用");
                return;
            }

            Interlocked.Increment(ref _installedCount); // IsHookInstalled 的跨周期真实口径
            Log($"NumLock LL 钩子已安装（tid={cycle.ThreadId}，初始锁定={lockedNow}）");

            // 报就绪（必须在这里、进泵之前——而不是等线程退出的 finally）：
            // 此刻队列已建、线程 Id 已记录、装钩已定（成或败），停止方可以安全投递 WM_QUIT。
            // 若停止方赶在进泵前投递，WM_QUIT 会在队列里等首次 GetMessage 立即出循环，同样正确。
            cycle.Ready.Set();

            // 4. 泵消息循环：空闲阻塞在 GetMessageW（零 CPU）；收到 WM_QUIT 时返回 0 退出。
            //    LL 钩子回调在本线程泵消息期间由系统同步调用（见 KeyboardHookProc）。
            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                // 本线程没有窗口、也无需分发任何消息：唯一入队的消息是 WM_QUIT（GetMessage 返回 0 出循环），
                // 其他消息（理论上不会有）直接丢弃即可。
            }

            // 5. 卸钩（在钩子线程自身收尾：一定发生在最后一次回调之后，时序最干净）
            var hook = Interlocked.Exchange(ref cycle.Hook, IntPtr.Zero);
            if (hook != IntPtr.Zero)
            {
                Interlocked.Decrement(ref _installedCount);
                var ok = NativeMethods.UnhookWindowsHookEx(hook);
                Log($"UnhookWindowsHookEx 调用完成：{(ok ? "成功" : $"失败（err={Marshal.GetLastWin32Error()}）")}，NumLock 钩子已卸载");
            }
        }
        catch (Exception ex)
        {
            // 线程主体绝不让异常逃逸（未观察线程异常会击穿进程）；钩子残留由进程退出兜底
            Log($"NumLock 钩子线程异常终止：{ex.Message}");
        }
        finally
        {
            cycle.Ready.Set(); // 无论成败都报就绪，保证停止方的等待不会永久挂起
        }
    }

    /// <summary>
    /// LL 钩子回调（钩子线程上由系统同步调用）：过滤 WM_KEYDOWN/WM_KEYUP 且 vkCode==VK_NUMLOCK 的事件，
    /// 读全局开关位并去抖，状态变化时派发线程池上报；其余事件（包括全部非 NumLock 按键）立即放行。
    /// 性能红线：本回调每次全系统按键都会进入，必须以微秒级返回——
    /// 只做「结构体读取 + 一次 GetKeyState + 整数比较」，弹提示等重活全部在线程池。
    /// </summary>
    /// <param name="cycle">发起本次装钩的周期（取它的钩子句柄做链式放行）。</param>
    /// <param name="nCode">HC_ACTION(0) = 事件有效；负值必须原样转交，不得处理。</param>
    /// <param name="wParam">键盘消息 Id（WM_KEYDOWN=0x0100 / WM_KEYUP=0x0101 等）。</param>
    /// <param name="lParam">指向 KBDLLHOOKSTRUCT 的只读指针。</param>
    /// <returns>永远返回 CallNextHookEx 的结果（本服务是旁路观察者，绝不吞键）。</returns>
    private IntPtr KeyboardHookProc(HookCycle cycle, int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == NativeMethods.HC_ACTION)
            {
                var msgId = (uint)wParam; // wParam 携带消息 Id（KEYDOWN/KEYUP 为小值，安全窄化）
                if (msgId is NativeMethods.WM_KEYDOWN or NativeMethods.WM_KEYUP)
                {
                    var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    if (kb.vkCode == NativeMethods.VK_NUMLOCK)
                        OnNumLockKeyEvent(kb);
                }
            }
        }
        catch
        {
            // 回调内绝不让异常打穿系统边界（打穿会导致钩子被系统摘除甚至进程崩溃）
        }

        return NativeMethods.CallNextHookEx(Volatile.Read(ref cycle.Hook), nCode, wParam, lParam);
    }

    /// <summary>
    /// 处理一个 NumLock 键事件：读开关位 → 去抖（<see cref="_debouncer"/>）→ 状态变化才派发线程池上报。
    ///
    /// 自纠错设计（为什么 KEYDOWN / KEYUP 都读一次）：
    /// 不必依赖系统对 NumLock 开关位的翻转时机（按下时翻 or 抬起时翻）——
    /// KEYDOWN 与 KEYUP 两个时机各读一次 GetKeyState，先看到新状态的时机触发上报，
    /// 另一时机读到相同值被去抖吞掉；无论系统在哪个时机翻转，都恰好上报一次正确的新状态。
    /// 按住不放的自动重复 KEYDOWN 读到的开关位不变，同样被去抖抑制（无提示风暴）。
    /// </summary>
    /// <param name="kb">钩子事件数据（只读）。</param>
    private void OnNumLockKeyEvent(NativeMethods.KBDLLHOOKSTRUCT kb)
    {
        // GetKeyState 的切换位（bit0）是全局会话状态（即 LED 灯态），从钩子线程读即全局现值
        var locked = (NativeMethods.GetKeyState(NativeMethods.VK_NUMLOCK) & 0x0001) != 0;

        if (!_debouncer.OnSample(locked))
        {
            // 重复状态事件（按住自动重复 / 同一翻转的另一时机读到同值）：仅节流打点，不上报
            LogThrottled($"NumLock 重复状态事件已抑制（locked={locked}，vk=0x{kb.vkCode:X2}）");
            return;
        }

        // 事件设备时间戳 → 回调到达的耗时（同为 GetTickCount 毫秒口径）；
        // 值域护栏：kb.time 是 uint（约 49.7 天回绕），负值或离谱大值说明跨了回绕点，跳过该打点
        var latencyMs = Environment.TickCount64 - kb.time;
        var latencyText = latencyMs is >= 0 and < 60000 ? $"事件延迟 {latencyMs}ms" : "事件延迟（跨回绕点，不计）";

        Log($"NumLock 状态变化 → {(locked ? "已锁定" : "已解锁")}（{latencyText}），派发线程池上报");

        var captured = locked; // 闭包捕获局部副本，避免共享状态跨线程可见性问题
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _notifier.ShowNumLockStatus(captured);
            }
            catch (Exception ex)
            {
                // 上报目标（WinTips 进程调用 / InfoBar 事件）的异常绝不能反噬钩子线程
                Log($"NumLock 提示上报失败：{ex.Message}");
            }
        });
    }

    /// <summary>写一条诊断打点（同时进日志文件与 Diagnostics 事件）。</summary>
    /// <param name="message">已组好的人读文本。</param>
    private void Log(string message)
    {
        KeyboardDiagnostics.Log(message);
        try { Diagnostics?.Invoke(message); }
        catch { // 订阅方异常不反噬服务
        }
    }

    /// <summary>1 秒节流版打点：用于自动重复这类高频且重复的日志（热路径只做一次时间比较）。</summary>
    /// <param name="message">已组好的人读文本。</param>
    private void LogThrottled(string message)
    {
        if (KeyboardDiagnostics.LogThrottled(message))
        {
            try { Diagnostics?.Invoke(message); }
            catch { // 订阅方异常不反噬服务
            }
        }
    }
}
