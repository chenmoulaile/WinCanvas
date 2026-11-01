using System.Diagnostics;
using SixLabors.ImageSharp; // SaveAsPngAsync 扩展方法所在命名空间
using System.Runtime.InteropServices;
using System.Text;
using WinCanvas.Wallpaper.Hosting;

namespace Wp1AHostDiag;

/// <summary>
/// WP1 阶段 A 挂接验收的自动化诊断命令（规格 DoD：「验证代码可写入临时诊断命令或测试」）。
/// 流程：spawn 记事本 → A1 嵌入挂接并校验（父子关系 / 矩形一致 / 尺寸跟随 ≤100ms / 关闭后 1s 内清理）
/// → 再 spawn 一个记事本强制 A2 跟随挂接并校验（顶层 / 矩形 = 外接框 / 正下方钉位 / 跟随 / 最小化显隐 /
/// 关闭清理）→ 结束时 EnumWindows 数 WinCanvasWallpaperHost 残留应为 0。
/// 「宿主存在期间目标交互正常」的点击 / 键盘 / 菜单为人工验证项（清单见 WORKLOG），
/// 本命令只做程序化等价探针：SendMessageTimeout(WM_NULL) 证明目标消息循环存活。
/// 退出码：0 = 全部断言通过；1 = 存在失败项（失败明细打印在末尾汇总表）。
/// </summary>
internal static class Program
{
    // ===== 诊断工具自用的最小 P/Invoke（不进产品代码；注释从简，语义见 win32 文档）=====

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT rect, int size);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    private const uint GA_PARENT = 1;
    private const uint GA_ROOT = 2;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_NULL = 0x0000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4：与 WinUI 3 主程序相同的 DPI 感知上下文。
    /// 必须在首个窗口 / P/Invoke 前设置：Windows 拒绝跨进程 SetParent / 跨进程 CreateWindow(WS_CHILD)
    /// 当父子两侧 DPI 感知上下文不一致（实测：unaware 工具对 PMv2 记事本 CreateWindowExW 返回 NULL 且
    /// GetLastError=0，见 WORKLOG 实测结论）；同时 DPI-unaware 进程的 GetWindowRect 会被虚拟化
    /// （本机 200% 缩放下 ÷2），与 DWM 外接框（恒物理像素）不可直接比对。</summary>
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>断言记录（名称 / 是否通过 / 明细），末尾汇总输出。</summary>
    private sealed record Check(string Name, bool Pass, string Detail);

    private static readonly List<Check> _checks = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 先于一切窗口操作设置 PMv2（失败则打印警告继续：结果会失真，汇总表会暴露）
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            Console.WriteLine("警告：SetProcessDpiAwarenessContext(PMv2) 失败，跨进程挂接与坐标比对结果可能失真。");

        // probe 模式：最小化分离「直接跨进程 CreateWindowExW(WS_CHILD)」与「两步 SetParent」两种 A1 路径
        if (args.Contains("probe"))
            return RunCrossProcessProbe();

        // service 模式：走 WallpaperService 产品接线（UpdateWallpaperAsync → 宿主创建 → ShutdownAll）的集成冒烟
        if (args.Contains("service"))
            return await RunServiceSmokeAsync();

        Console.WriteLine("== WP1 阶段 A 挂接诊断 ==");

        // 目标定位一律按「spawn 前快照 → 新出现的指定类名窗口」差集，不依赖 pid
        //（商店版 notepad.exe 是 stub、Edge 复用既有浏览器进程，pid 都不可靠）
        var before = SnapshotWindows("Notepad");
        before.UnionWith(SnapshotWindows("Chrome_WidgetWin_1"));

        // DoD 要求的两类目标：记事本（自绘背景类）与浏览器（控件密集类，Edge）
        var p1 = RunScenario("记事本-A1嵌入", () => Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }),
            "Notepad", before, preferredEmbed: true, settleMs: 800);
        var p2 = RunScenario("记事本-A2跟随", () => Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }),
            "Notepad", before, preferredEmbed: false, settleMs: 800);
        var p3 = RunScenario("浏览器(Edge)-A1嵌入",
            () => Process.Start(new ProcessStartInfo("msedge.exe", "--new-window about:blank") { UseShellExecute = true }),
            "Chrome_WidgetWin_1", before, preferredEmbed: true, settleMs: 2500);

        foreach (var p in new[] { p1, p2, p3 })
        {
            if (p != null)
                CloseTarget(p.Value.hwnd);
        }

        Thread.Sleep(300);

        var leftovers = WallpaperHostWindow.CountAliveHosts();
        AddCheck("残留计数（EnumWindows+EnumChildWindows）", leftovers == 0, $"WinCanvasWallpaperHost 存活 = {leftovers}（应 0）");

        Console.WriteLine();
        Console.WriteLine("== 断言汇总 ==");
        var failed = 0;
        foreach (var c in _checks)
        {
            if (!c.Pass)
                failed++;

            Console.WriteLine($"[{(c.Pass ? "PASS" : "FAIL")}] {c.Name}：{c.Detail}");
        }

        Console.WriteLine(failed == 0 ? "\n全部通过。" : $"\n失败 {failed} 项。");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 单个目标场景：spawn → 挂接（A1 或强制 A2）→ 矩形 / 跟随 / 显隐断言 → 关闭清理断言。
    /// 返回（目标 hwnd, 宿主）；spawn 失败返回 null。
    /// </summary>
    private static (IntPtr hwnd, WallpaperHostWindow host)? RunScenario(
        string label, Func<Process?> spawn, string windowClass, HashSet<IntPtr> before,
        bool preferredEmbed, int settleMs)
    {
        Console.WriteLine($"\n-- 场景：{label} --");
        Process? process = null;
        try
        {
            process = spawn();
        }
        catch (Exception ex)
        {
            AddCheck($"{label}：spawn 目标", false, $"启动失败：{ex.Message}");
            return null;
        }

        var hwnd = WaitForNewWindow(windowClass, before, TimeSpan.FromSeconds(15));
        if (hwnd == IntPtr.Zero)
        {
            AddCheck($"{label}：spawn 目标", false, $"15s 内未检测到新的 {windowClass} 窗口（应用可能未安装或无交互桌面）");
            process?.Dispose(); // UseShellExecute 复用既有进程时 Process.Start 可能返回 null
            return null;
        }

        AddCheck($"{label}：spawn 目标", true, $"目标 = 0x{hwnd:X}（{DescribeWindow(hwnd)}）");
        Thread.Sleep(settleMs); // 等目标界面完全稳定（初次布局 / 浏览器渲染启动），避免挂接后立刻有布局事件干扰

        var host = WallpaperHostWindow.TryCreate(
            hwnd, preferredEmbed ? HostAttachMode.Embed : HostAttachMode.Follow, out var error);
        if (host == null)
        {
            AddCheck($"{label}：宿主创建", false, error ?? "未知原因");
            CloseTarget(hwnd);
            return (hwnd, null!);
        }

        AddCheck($"{label}：宿主创建", true, $"mode = {host.Mode}，host = 0x{host.Hwnd:X}");

        // 镜像 WallpaperService 的清理语义：TargetLost → Dispose 宿主（卸钩子 + 销毁窗口）。
        // 处理器在宿主 pump 线程上执行，Dispose 内部对同线程调用走内联快路径，无死锁；
        // 先 Dispose 后 Set：保证主线程被唤醒时销毁已完成（否则 IsWindow 断言存在竞态）
        var targetLost = new ManualResetEventSlim(false);
        host.TargetLost += _ =>
        {
            host.Dispose();
            targetLost.Set();
        };

        VerifyAttach(label, host, hwnd);

        // 消息循环存活探针：目标消息处理正常（交互不受干扰的程序化等价证据，人工清单另记 WORKLOG）
        var alive = SendMessageTimeoutW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;
        AddCheck($"{label}：目标消息循环存活", alive, "SendMessageTimeout(WM_NULL) 应答正常");

        // 跟随断言：程序化改目标矩形 → 轮询宿主矩形匹配 → 记录耗时（DoD：≤100ms）
        MeasureFollow(label, host, hwnd);

        if (!preferredEmbed)
            VerifyMinimizeShowHide(label, host, hwnd);

        // 清理断言：关闭目标 → 1s 内 TargetLost 上报且宿主销毁（DoD：目标关闭 1s 内宿主销毁且会话 Dispose）
        var t0 = Environment.TickCount64;
        CloseTarget(hwnd);
        var destroyed = targetLost.Wait(TimeSpan.FromSeconds(2));
        var delta = Environment.TickCount64 - t0;
        AddCheck($"{label}：目标关闭后清理", destroyed && delta <= 1000,
            $"TargetLost 触发 = {destroyed}，耗时 = {delta}ms（应 ≤1000ms）");
        AddCheck($"{label}：宿主句柄失效", !IsWindow(host.Hwnd), $"IsWindow(host) = {IsWindow(host.Hwnd)}（应 false）");

        return (hwnd, host);
    }

    /// <summary>挂接形态断言：父子关系 / 矩形一致 /（A2）顶层 + 正下方钉位。
    /// 全部「同时刻」成对读取目标与宿主矩形再比对（目标自身可能仍在打开动画中，勿用创建时的旧值）。</summary>
    private static void VerifyAttach(string label, WallpaperHostWindow host, IntPtr target)
    {
        if (host.Mode == HostAttachMode.Embed)
        {
            // A1：宿主是目标的 WS_CHILD 子窗口
            var parent = GetAncestor(host.Hwnd, GA_PARENT);
            AddCheck($"{label}：父子关系", parent == target, $"GetAncestor(host, GA_PARENT) = 0x{parent:X}，target = 0x{target:X}（应相等）");

            GetClientRect(target, out var client);
            GetWindowRect(host.Hwnd, out var hostRect);
            // 实测（Win11 26300 / DWM）：子窗口 GetWindowRect 返回屏幕物理坐标（非文档所述父客户区坐标），
            // 位置断言须经 ScreenToClient(目标) 映射回目标客户区：宿主左上应恰为目标客户区原点 (0,0)
            var origin = new POINT { X = hostRect.L, Y = hostRect.T };
            ScreenToClient(target, ref origin);
            var clientW = client.R - client.L;
            var clientH = client.B - client.T;
            var hostW = hostRect.R - hostRect.L;
            var hostH = hostRect.B - hostRect.T;
            AddCheck($"{label}：矩形 = 目标客户区", clientW == hostW && clientH == hostH && origin.X == 0 && origin.Y == 0,
                $"客户区 {clientW}x{clientH} vs 宿主 {hostW}x{hostH}，宿主左上映射客户区 ({origin.X},{origin.Y})（应相等且为 0,0）");
        }
        else
        {
            // A2：宿主是顶层 WS_POPUP。判顶层的正确口径是 GA_ROOT == 自身
            //（GA_PARENT 对顶层窗口返回桌面窗口 #32769，实测见 WORKLOG）
            var root = GetAncestor(host.Hwnd, GA_ROOT);
            AddCheck($"{label}：宿主为顶层窗口", root == host.Hwnd, $"GA_ROOT = 0x{root:X}，host = 0x{host.Hwnd:X}（应相等）");

            DwmGetWindowAttribute(target, DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, Marshal.SizeOf<RECT>());
            GetWindowRect(host.Hwnd, out var hostRect);
            var same = bounds.L == hostRect.L && bounds.T == hostRect.T && bounds.R == hostRect.R && bounds.B == hostRect.B;
            AddCheck($"{label}：矩形 = 目标外接框", same,
                $"DWM 外接 ({bounds.L},{bounds.T},{bounds.R},{bounds.B}) vs 宿主 ({hostRect.L},{hostRect.T},{hostRect.R},{hostRect.B})");

            // Z 序：宿主须位于目标「下方」（壁纸在目标身后）。系统 IME 等辅助窗口可能临时插在两者之间，
            // 「严格紧邻」断言会误报——口径取「从目标向下遍历可遇到宿主」，并打印第一格窗口辅助诊断
            var below = GetWindow(target, GW_HWNDNEXT);
            var found = false;
            for (var i = 0; i < 500 && below != IntPtr.Zero; i++)
            {
                if (below == host.Hwnd)
                {
                    found = true;
                    break;
                }

                below = GetWindow(below, GW_HWNDNEXT);
            }

            var firstBelow = GetWindow(target, GW_HWNDNEXT);
            AddCheck($"{label}：Z 序钉位（宿主位于目标下方）", found,
                $"目标下方第一格 = 0x{firstBelow:X}（{DescribeWindow(firstBelow)}），向下遍历遇到宿主 = {found}");
        }
    }

    /// <summary>
    /// 服务级集成冒烟：记事本 → WallpaperService.UpdateWallpaperAsync(png)（真实三层加载第一层：图片解码）
    /// → 断言 SessionHostModeChanged 上报嵌入模式且宿主存活 1 个 → ShutdownAll → 断言宿主清零。
    /// 验证 WP1-A 接线（会话替换 / 宿主创建 / 模式上报 / 退出清理）在产品调用路径上真实工作。
    /// </summary>
    private static async Task<int> RunServiceSmokeAsync()
    {
        Console.WriteLine("== WallpaperService 接线冒烟 ==");

        // 生成一张 100x100 的临时 PNG（ImageSharp 在 Wallpaper 依赖中，产品加载器真实解码它）
        var pngPath = Path.Combine(Path.GetTempPath(), $"wp1a-smoke-{Guid.NewGuid():N}.png");
        using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(100, 100,
                   new SixLabors.ImageSharp.PixelFormats.Rgba32(30, 80, 180)))
        {
            await image.SaveAsPngAsync(pngPath);
        }

        var before = SnapshotWindows("Notepad");
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        var hwnd = WaitForNewWindow("Notepad", before, TimeSpan.FromSeconds(10));
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("冒烟失败：10s 内未出现新记事本窗口。");
            File.Delete(pngPath);
            return 1;
        }

        Thread.Sleep(800);
        Console.WriteLine($"目标 = 0x{hwnd:X}");

        // 未注入 IWindowControlService（null 构造）：A1 嵌入模式不触发自动透明，冒烟只验挂接与清理
        var service = new WinCanvas.Wallpaper.WallpaperService();
        HostAttachMode? reportedMode = null;
        service.SessionHostModeChanged += (_, mode) => reportedMode = mode;
        service.StatusReported += message => Console.WriteLine($"[StatusReported] {message}");

        await service.UpdateWallpaperAsync(hwnd, pngPath);
        await Task.Delay(500); // 会话替换为 fire-and-forget 线程池任务，留出完成窗口

        var aliveAfterUpdate = WallpaperHostWindow.CountAliveHosts();
        AddCheck("服务冒烟：挂接模式上报", reportedMode == HostAttachMode.Embed,
            $"SessionHostModeChanged = {reportedMode}（应为 Embed：PMv2 进程对 PMv2 记事本）");
        AddCheck("服务冒烟：宿主已创建", aliveAfterUpdate == 1, $"宿主存活 = {aliveAfterUpdate}（应 1）");

        service.ShutdownAll();
        var aliveAfterShutdown = WallpaperHostWindow.CountAliveHosts();
        AddCheck("服务冒烟：ShutdownAll 清零", aliveAfterShutdown == 0,
            $"ShutdownAll 后宿主存活 = {aliveAfterShutdown}（应 0）");

        CloseTarget(hwnd);
        File.Delete(pngPath);

        var failed = _checks.Count(c => !c.Pass);
        foreach (var c in _checks)
            Console.WriteLine($"[{(c.Pass ? "PASS" : "FAIL")}] {c.Name}：{c.Detail}");

        Console.WriteLine(failed == 0 ? "\n冒烟全部通过。" : $"\n冒烟失败 {failed} 项。");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 跨进程父子探针：spawn 记事本后依次尝试
    /// ① CreateWindowExW(WS_CHILD, parent=记事本)（产品当前实现）；
    /// ② CreateWindowExW(WS_POPUP) → 改样式为 WS_CHILD → SetParent(记事本)；
    /// ③ CreateWindowExW(WS_POPUP) → 直接 SetParent(记事本)（不改样式）。
    /// 各自打印成败 / GetLastError / GA_PARENT 校验，为 A1 实现方式提供事实依据。
    /// </summary>
    private static int RunCrossProcessProbe()
    {
        var before = SnapshotWindows("Notepad");
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        var target = WaitForNewWindow("Notepad", before, TimeSpan.FromSeconds(10));
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("探针失败：10s 内未出现新记事本窗口。");
            return 1;
        }

        Console.WriteLine($"探针目标 = 0x{target:X}");
        Thread.Sleep(800);

        // 注册独立探针窗口类（不依赖产品代码）
        var wc = new WndClassW { lpfnWndProc = DefWindowProcW, hInstance = GetModuleHandleW(null), lpszClassName = "Wp1AProbeClass" };
        RegisterClassW(ref wc);

        // ① 直接以跨进程窗口为父创建 WS_CHILD
        Marshal.SetLastSystemError(0);
        var direct = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT, "Wp1AProbeClass", null,
            WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 400, 300, target, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        var directErr = Marshal.GetLastSystemError();
        Console.WriteLine($"① 直接 CreateWindowExW(WS_CHILD, parent=target)：{(direct != IntPtr.Zero ? "成功" : "失败")}，GetLastError={directErr}" +
                          (direct != IntPtr.Zero ? $"，GA_PARENT=0x{GetAncestor(direct, GA_PARENT):X}" : ""));

        // ② 两步：先 WS_POPUP 自建 → 改样式 WS_CHILD → SetParent
        Marshal.SetLastSystemError(0);
        var popup = CreateWindowExW(0, "Wp1AProbeClass", null, WS_POPUP, 10, 10, 400, 300, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (popup != IntPtr.Zero)
        {
            var style = GetWindowLongPtrW(popup, GWL_STYLE).ToInt64();
            SetWindowLongPtrW(popup, GWL_STYLE, new IntPtr((style & ~WS_POPUP_L) | WS_CHILD_L));
            Marshal.SetLastSystemError(0);
            var ok2 = SetParent(popup, target);
            var err2 = Marshal.GetLastSystemError();
            Console.WriteLine($"② POPUP→改WS_CHILD→SetParent：{(ok2 != IntPtr.Zero ? "成功" : "失败")}，GetLastError={err2}" +
                              (ok2 != IntPtr.Zero ? $"，GA_PARENT=0x{GetAncestor(popup, GA_PARENT):X}" : ""));
        }
        else
        {
            Console.WriteLine("② POPUP 创建即失败。");
        }

        // ③ 两步：先 WS_POPUP 自建 → 直接 SetParent（不改样式）
        var popup2 = CreateWindowExW(0, "Wp1AProbeClass", null, WS_POPUP, 20, 20, 400, 300, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (popup2 != IntPtr.Zero)
        {
            Marshal.SetLastSystemError(0);
            var ok3 = SetParent(popup2, target);
            var err3 = Marshal.GetLastSystemError();
            Console.WriteLine($"③ POPUP→直接SetParent：{(ok3 != IntPtr.Zero ? "成功" : "失败")}，GetLastError={err3}" +
                              (ok3 != IntPtr.Zero ? $"，GA_PARENT=0x{GetAncestor(popup2, GA_PARENT):X}" : ""));
        }
        else
        {
            Console.WriteLine("③ POPUP 创建即失败。");
        }

        // 清理
        if (direct != IntPtr.Zero) DestroyWindow(direct);
        if (popup != IntPtr.Zero) DestroyWindow(popup);
        if (popup2 != IntPtr.Zero) DestroyWindow(popup2);
        PostMessageW(target, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return 0;
    }

    // ===== 探针自用的补充 P/Invoke =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClassW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr h);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr val);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    private const int GWL_STYLE = -16;
    private const long WS_POPUP_L = 0x80000000L;
    private const long WS_CHILD_L = 0x40000000L;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>窗口画像（类名 + 进程 Id），诊断输出用。</summary>
    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return "（无效句柄）";

        var sb = new StringBuilder(64);
        GetClassNameW(hwnd, sb, 64);
        GetWindowThreadProcessId(hwnd, out var pid);
        return $"类名={sb} pid={pid}";
    }

    /// <summary>
    /// 跟随耗时测量：程序化缩放 / 移动目标 → 2ms 步进轮询「宿主矩形 == 目标矩形」直到匹配或超时 500ms。
    /// 比对口径按模式区分（<see cref="HostMatchesTarget"/>）；计时含 WinEvent 排队延迟，
    /// 即「用户视角」的跟随延迟（DoD：≤100ms）。连续测 3 轮取最大值。
    /// </summary>
    private static void MeasureFollow(string label, WallpaperHostWindow host, IntPtr target)
    {
        long worst = 0;
        var lastOk = false;
        var detail = "";

        // 轮次 1/2：缩放（客户区尺寸变化）；轮次 3：整窗移动 + 缩放
        (int x, int y, int cx, int cy)[] moves =
        {
            (100, 100, 700, 480),
            (140, 120, 820, 560),
            (60, 80, 640, 420),
        };

        foreach (var (x, y, cx, cy) in moves)
        {
            var t0 = Environment.TickCount64;
            SetWindowPos(target, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);

            var matched = false;
            while (Environment.TickCount64 - t0 < 500)
            {
                if (HostMatchesTarget(host, target))
                {
                    matched = true;
                    break;
                }

                Thread.Sleep(2);
            }

            var delta = Environment.TickCount64 - t0;
            worst = Math.Max(worst, delta);
            lastOk = matched;
            detail = $"目标→({x},{y} {cx}x{cy}) 宿主匹配 = {matched}，耗时 = {delta}ms";
            HostDiagnostics.LogFollow($"诊断跟随打点 {detail} host=0x{host.Hwnd:X}");
        }

        AddCheck($"{label}：跟随 ≤100ms", lastOk && worst <= 100, $"三轮最大 {worst}ms（{detail}）");
    }

    /// <summary>
    /// 「宿主矩形 == 目标矩形」的即时判定（模式相关口径）：
    /// A1 = 宿主左上经 ScreenToClient(目标) 映射为 (0,0) 且宽高 == 目标 GetClientRect 宽高
    ///   （实测子窗口 GetWindowRect 返回屏幕坐标，须映射后比对）；
    /// A2 = 宿主 GetWindowRect == 目标 DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS)。
    /// （PMv2 进程下 GetWindowRect 为物理像素，与 DWM 外接框同一坐标系，可直接相等比对。）
    /// </summary>
    private static bool HostMatchesTarget(WallpaperHostWindow host, IntPtr target)
    {
        if (host.Mode == HostAttachMode.Embed)
        {
            GetClientRect(target, out var client);
            GetWindowRect(host.Hwnd, out var hostRect);
            var origin = new POINT { X = hostRect.L, Y = hostRect.T };
            ScreenToClient(target, ref origin);
            return origin.X == 0 && origin.Y == 0
                   && hostRect.R - hostRect.L == client.R - client.L
                   && hostRect.B - hostRect.T == client.B - client.T;
        }

        DwmGetWindowAttribute(target, DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, Marshal.SizeOf<RECT>());
        GetWindowRect(host.Hwnd, out var hr);
        return hr.L == bounds.L && hr.T == bounds.T && hr.R == bounds.R && hr.B == bounds.B;
    }

    /// <summary>A2 显隐断言：目标最小化 → 宿主隐藏；恢复 → 宿主重新可见且矩形同步。</summary>
    private static void VerifyMinimizeShowHide(string label, WallpaperHostWindow host, IntPtr target)
    {
        ShowWindow(target, SW_MINIMIZE);
        Thread.Sleep(400); // 等 MINIMIZESTART 事件送达 pump 并处理
        var hidden = !IsWindowVisible(host.Hwnd);
        AddCheck($"{label}：目标最小化 → 宿主隐藏", hidden, $"IsWindowVisible(host) = {IsWindowVisible(host.Hwnd)}（应 false）");

        ShowWindow(target, SW_RESTORE);
        Thread.Sleep(400); // 等 MINIMIZEEND / LOCATIONCHANGE 事件
        var visible = IsWindowVisible(host.Hwnd);
        DwmGetWindowAttribute(target, DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, Marshal.SizeOf<RECT>());
        GetWindowRect(host.Hwnd, out var hostRect);
        var synced = bounds.L == hostRect.L && bounds.T == hostRect.T && bounds.R == hostRect.R && bounds.B == hostRect.B;
        AddCheck($"{label}：目标恢复 → 宿主重现并同步", visible && synced,
            $"可见 = {visible}，矩形同步 = {synced}（DWM ({bounds.L},{bounds.T},{bounds.R},{bounds.B}) vs 宿主 ({hostRect.L},{hostRect.T},{hostRect.R},{hostRect.B})）");
    }

    // ===== 目标窗口定位辅助（按类名 + spawn 前后差集，不依赖 pid）=====

    /// <summary>枚举当前全部可见的指定类名顶层窗口（spawn 前后差集 = 本次 spawn 的目标）。</summary>
    private static HashSet<IntPtr> SnapshotWindows(string className)
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && IsWindowOfClass(h, className))
                set.Add(h);

            return true;
        }, IntPtr.Zero);
        return set;
    }

    private static bool IsWindowOfClass(IntPtr h, string className)
    {
        var sb = new StringBuilder(64);
        GetClassNameW(h, sb, 64);
        return sb.ToString() == className;
    }

    /// <summary>轮询等待一个「快照中不存在」的指定类名新窗口。</summary>
    private static IntPtr WaitForNewWindow(string className, HashSet<IntPtr> before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (found == IntPtr.Zero && !before.Contains(h) && IsWindowVisible(h) && IsWindowOfClass(h, className))
                    found = h;

                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
                return found;

            Thread.Sleep(120);
        }

        return IntPtr.Zero;
    }

    private static void CloseTarget(IntPtr hwnd)
    {
        PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static void AddCheck(string name, bool pass, string detail) => _checks.Add(new Check(name, pass, detail));
}
