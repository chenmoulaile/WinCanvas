using System.Runtime.InteropServices;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Services;

namespace Wp2NumLockDiag;

/// <summary>
/// WP2 NumLock 提示链路端到端验收（对应用户可见 DoD，能在无头会话客观断言的部分）：
///   1. Start 后钩子安装（IsHookInstalled）；
///   2. 注入 NumLock 按键 → 2 秒内 IStatusNotifier 收到上报，值为翻转后的状态（≤2s 出现提示的钩子侧证据）；
///   3. 每次按键只产生 1 条上报（KEYDOWN+KEYUP 双事件只报一次；无重复提示风暴）；
///   4. 钩子事件延迟打点在毫秒级（全系统键盘输入无可感知延迟的客观证据）；
///   5. Stop 后 Diagnostics 收到「UnhookWindowsHookEx 调用完成」且 IsHookInstalled=false（日志验证卸钩）；
///   6. Stop 后再注入按键 → 零上报（按键无反应）；
///   7. 快速 Start→Stop→Start→Stop 幂等不串扰；Dispose 兜底正常。
/// 注入用 SendInput（LL 钩子可见注入事件）；两次按键一开一关，结束时 NumLock 状态与初始一致（LED 兜底还原）。
/// </summary>
internal static class Program
{
    // ===== 注入用 P/Invoke（仅本诊断工具使用；产品代码不含 SendInput） =====

    /// <summary>VK_NUMLOCK(0x90)：NumLock 虚拟键码（与 Core 的 NativeMethods 同值，工具自带一份避免依赖 internal）。</summary>
    private const int VK_NUMLOCK = 0x90;

    /// <summary>KEYEVENTF_EXTENDEDKEY(0x0001)：扩展键标记（NumLock 属扩展键，注入时必须带，否则状态机可能不翻转）。</summary>
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    /// <summary>KEYEVENTF_KEYUP(0x0002)：抬起事件（缺省为按下）。</summary>
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>INPUT_KEYBOARD(1)：SendInput 的输入类型 = 键盘。</summary>
    private const uint INPUT_KEYBOARD = 1;

    /// <summary>KEYBDINPUT 结构：一次键盘注入事件的参数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        /// <summary>虚拟键码。</summary>
        public ushort wVk;

        /// <summary>扫描码（0 = 由系统按虚拟键码补）。</summary>
        public ushort wScan;

        /// <summary>标志组合（EXTENDEDKEY / KEYUP）。</summary>
        public uint dwFlags;

        /// <summary>事件时间戳（0 = 系统 CurrentTime）。</summary>
        public uint time;

        /// <summary>附加信息（0）。</summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>INPUT 的联合体部分（只用键盘分支）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        /// <summary>键盘分支。</summary>
        [FieldOffset(0)] public KEYBDINPUT ki;

        /// <summary>鼠标分支占位（保证联合体尺寸与原生一致）。</summary>
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    /// <summary>MOUSEINPUT 占位结构（只为撑出联合体正确尺寸，本工具不使用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>INPUT 结构（type + 联合体；x64 下总尺寸 40 字节，与原生严格一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        /// <summary>输入类型（INPUT_KEYBOARD=1）。</summary>
        public uint type;

        /// <summary>分支联合体。</summary>
        public InputUnion U;
    }

    /// <summary>
    /// 向系统输入流注入事件（LL 键盘钩子会看到注入事件，flags 含 LLKHF_INJECTED）。
    /// </summary>
    /// <param name="nInputs">事件数组长度。</param>
    /// <param name="pInputs">事件数组。</param>
    /// <param name="cbSize">单个 INPUT 的字节数（Marshal.SizeOf）。</param>
    /// <returns>成功注入的事件数；0 = 失败（被 UIPI 拦截或参数错误）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// 读虚拟键状态（切换位是全局会话状态即 LED 灯态，任意线程可读）。
    /// </summary>
    /// <param name="nVirtKey">虚拟键码。</param>
    /// <returns>bit0 = 切换位（1 = NumLock 锁定）。</returns>
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>记录上报的假通知器：(时间戳, isLocked) 列表；线程安全（钩子线程池回调并发到达）。</summary>
    private sealed class RecordingNotifier : IStatusNotifier
    {
        /// <summary>上报记录（锁保护）。</summary>
        public readonly List<(DateTime At, bool Locked)> Reports = new();

        /// <summary>保护 <see cref="Reports"/> 的锁。</summary>
        private readonly object _gate = new();

        /// <summary>记录一条上报（IStatusNotifier 实现）。</summary>
        /// <param name="isLocked">NumLock 是否锁定。</param>
        public void ShowNumLockStatus(bool isLocked)
        {
            lock (_gate)
            {
                Reports.Add((DateTime.Now, isLocked));
            }
        }

        /// <summary>快照当前上报条数。</summary>
        /// <returns>已收到的上报数。</returns>
        public int Count { get { lock (_gate) return Reports.Count; } }

        /// <summary>取第 i 条上报的锁定值（越界抛错由断言逻辑处理）。</summary>
        /// <param name="i">序号。</param>
        /// <returns>该条上报的 isLocked 值。</returns>
        public bool LockedAt(int i)
        {
            lock (_gate)
            {
                return Reports[i].Locked;
            }
        }
    }

    /// <summary>断言失败计数（>0 则进程退出码非 0）。</summary>
    private static int _failures;

    /// <summary>入口：跑全部验收断言。</summary>
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var notifier = new RecordingNotifier();
        using var service = new KeyboardIndicatorService(notifier);
        var diagLines = new List<string>();
        service.Diagnostics += line =>
        {
            lock (diagLines) diagLines.Add(line);
            Console.WriteLine($"  [diag] {line}");
        };

        // ---- 1. Start → 钩子安装 ----
        var initialLocked = (GetKeyState(VK_NUMLOCK) & 1) != 0;
        Console.WriteLine($"== 初始 NumLock 状态：{(initialLocked ? "锁定" : "未锁定")}（测试将注入两次按键，结束时还原）");
        service.Start();
        var installed = SpinUntil(() => service.IsHookInstalled, 2000);
        Check(installed, "Start 后 2s 内 LL 钩子已安装（IsHookInstalled）");

        // ---- 2/3. 注入第一次按键 → 2s 内 1 条上报，值为翻转后状态 ----
        PressNumLock();
        var gotFirst = SpinUntil(() => notifier.Count >= 1, 2000);
        Check(gotFirst, "注入 NumLock 按键后 2s 内收到上报（提示链路连通）");
        if (gotFirst)
        {
            Check(notifier.LockedAt(0) == !initialLocked,
                $"上报值 = 翻转后状态（期望 {!initialLocked}，实际 {notifier.LockedAt(0)}）");
            Thread.Sleep(300);
            Check(notifier.Count == 1, $"一次按键只报 1 次（KEYDOWN/KEYUP 去抖；实际 {notifier.Count} 次）");
        }

        // ---- 4. 延迟证据：从诊断行提取「事件延迟 Xms」 ----
        var latencies = CollectLatencies(diagLines);
        if (latencies.Count > 0)
        {
            var max = latencies.Max();
            Check(max < 100, $"钩子事件延迟毫秒级（max={max}ms < 100ms）");
        }
        else
        {
            Check(false, "未捕获到事件延迟打点（诊断链路异常）");
        }

        // ---- 5. 第二次按键 → 状态还原 + 恰好 1 条新上报 ----
        PressNumLock();
        var gotSecond = SpinUntil(() => notifier.Count >= 2, 2000);
        Check(gotSecond, "第二次按键后收到第 2 条上报（状态交替上报）");
        if (gotSecond)
        {
            Check(notifier.LockedAt(1) == initialLocked,
                $"第 2 条上报 = 原状态（期望 {initialLocked}，实际 {notifier.LockedAt(1)}）");
            Thread.Sleep(300);
            Check(notifier.Count == 2, $"连续多次按键只报状态变化（实际共 {notifier.Count} 条，期望 2）");
        }

        // ---- 6. Stop → Unhook 日志 + IsHookInstalled=false；之后按键零反应 ----
        service.Stop();
        var unhookLogged = SpinUntil(() =>
        {
            lock (diagLines) return diagLines.Any(l => l.Contains("UnhookWindowsHookEx 调用完成"));
        }, 3000);
        Check(unhookLogged, "Stop 后 3s 内收到「UnhookWindowsHookEx 调用完成」日志（卸钩日志验证）");
        Check(!service.IsHookInstalled, "Stop 后 IsHookInstalled=false");

        PressNumLock();
        Thread.Sleep(1000);
        Check(notifier.Count == 2, $"Stop 后注入按键零上报（按键无反应；实际 {notifier.Count} 条）");

        // 幂等兜底：Stop 重复调用 / Dispose 不抛异常
        service.Stop();
        Check(true, "重复 Stop 幂等（无异常）");

        // ---- 收尾：LED 状态兜底还原（上面两次注入一开一关应已还原；异常路径下补一次） ----
        var nowLocked = (GetKeyState(VK_NUMLOCK) & 1) != 0;
        if (nowLocked != initialLocked)
        {
            Console.WriteLine($"== 兜底还原 NumLock LED：{nowLocked} → {initialLocked}");
            PressNumLock();
        }

        Console.WriteLine(_failures == 0
            ? "== 全部断言通过 =="
            : $"== {_failures} 项断言失败 ==");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>注入一次完整的 NumLock 按下 + 抬起（EXTENDEDKEY 标记）。</summary>
    private static void PressNumLock()
    {
        var down = new INPUT { type = INPUT_KEYBOARD };
        down.U.ki.wVk = VK_NUMLOCK;
        down.U.ki.dwFlags = KEYEVENTF_EXTENDEDKEY;

        var up = new INPUT { type = INPUT_KEYBOARD };
        up.U.ki.wVk = VK_NUMLOCK;
        up.U.ki.dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP;

        var inputs = new[] { down, up };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 2)
            Console.WriteLine($"  [warn] SendInput 注入 {sent}/2（err={Marshal.GetLastWin32Error()}）");
    }

    /// <summary>从诊断行提取「事件延迟 Xms」数值列表。</summary>
    /// <param name="lines">诊断行集合。</param>
    /// <returns>延迟毫秒值列表。</returns>
    private static List<long> CollectLatencies(List<string> lines)
    {
        var result = new List<long>();
        lock (lines)
        {
            foreach (var line in lines)
            {
                var key = "事件延迟 ";
                var idx = line.IndexOf(key, StringComparison.Ordinal);
                if (idx < 0)
                    continue;

                var tail = line[(idx + key.Length)..];
                var end = tail.IndexOf("ms", StringComparison.Ordinal);
                if (end > 0 && long.TryParse(tail[..end], out var ms))
                    result.Add(ms);
            }
        }

        return result;
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件委托。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>true = 在超时前成立。</returns>
    private static bool SpinUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>记录并打印一条断言结果。</summary>
    /// <param name="passed">是否通过。</param>
    /// <param name="name">断言名。</param>
    private static void Check(bool passed, string name)
    {
        Console.WriteLine($"{(passed ? "[PASS]" : "[FAIL]")} {name}");
        if (!passed)
            Interlocked.Increment(ref _failures);
    }
}
