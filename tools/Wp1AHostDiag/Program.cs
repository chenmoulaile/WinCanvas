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

        // render 模式：WP1-B 图片上屏验收（裸窗口四象限像素采样 / 切图即时性 / CPU≈0% / 点击穿透 / pkg 降级封面）
        if (args.Contains("render"))
            return await RunRenderSmokeAsync();

        // dcomp 模式：DComp 管线分步探针（每步打印 HRESULT，定位互操作问题）
        if (args.Contains("dcomp"))
            return RunDcompProbe();

        // winrt 模式：CsWinRT 互操作面探测（MarshalInterface/CastExtensions）+ 系统 winmd IID 提取
        if (args.Contains("winrt"))
            return RunWinrtProbe();

        // video 模式：WP1-C 视频上屏验收（组合呈现产品路径：流动/比例/全屏暂停/资源释放）
        if (args.Contains("video"))
            return await RunVideoProbeAsync(args);

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
    /// WP1-B 图片上屏验收（render 模式）。流程：
    /// 裸 Win32 目标窗口（无子控件，纯白自绘，可记录收到的鼠标点击）→ WallpaperService 应用
    /// 四象限异色的 2x2 PNG → 屏幕 DC 按象限中心采样像素，验证「画面可见、无错位、无镜像/翻转」
    /// → 换一张异色 PNG 验证「切换即时生效」（记录匹配耗时）→ 空闲 3s 测进程 CPU%（静态图无逐帧工作）
    /// → SendInput 点击客户区中心验证宿主点击穿透（目标窗口收到 WM_LBUTTONDOWN）
    /// → 用 repkg PackageWriter 构造含 scene.json("script")+preview.png 的最小 pkg 验证降级封面上屏
    /// → ShutdownAll 清零。退出码 0 = 全部通过。
    /// </summary>
    private static async Task<int> RunRenderSmokeAsync()
    {
        Console.WriteLine("== WP1-B 图片上屏验收 ==");

        // 象限基色：四角互异（水平镜像会交换红/绿，垂直翻转/上下镜像会交换红/蓝，能同时抓出两类错误）
        (byte r, byte g, byte b) red = (255, 0, 0), green = (0, 255, 0), blue = (0, 0, 255), white = (255, 255, 255);
        var pngA = WriteQuadPng(red, green, blue, white);        // A：上左红 / 上右绿 / 下左蓝 / 下右白
        var pngB = WriteQuadPng(white, blue, green, red);        // B：A 的全交换（旋转 180°+），可验证内容确实变了

        // 裸目标窗口建到宿主 pump 线程：与 DComp 渲染器同线程（跨线程绑定时 DWM 不合成内容，
        // WP1-B 实测见 WORKLOG）；窗口消息由 pump 的 GetMessage 循环自然处理
        IntPtr target = IntPtr.Zero;
        var created = WallpaperHostPump.Shared.Execute(
            () => target = BareTargetWindow.CreateOnCurrentThread(), timeoutMs: 5000);
        if (!created || target == IntPtr.Zero)
        {
            AddCheck("render：裸目标窗口创建", false,
                $"pump 创建失败（Execute 回执 = {created}，hwnd = 0x{target:X}，pump 存活 = {WallpaperHostPump.Shared.IsRunning}）");
            return FinishChecks();
        }

        AddCheck("render：裸目标窗口创建", true, $"目标 = 0x{target:X}");
        // 置顶且可见：像素采样读的是屏幕合成结果，避免被其他窗口遮挡
        SetWindowPos(target, new IntPtr(-1) /* HWND_TOPMOST */, 100, 100, 800, 600, 0x0010 /* SWP_NOACTIVATE */);
        Thread.Sleep(400);

        var service = new WinCanvas.Wallpaper.WallpaperService();
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.StatusReported += reports.Enqueue;

        // ① 应用图片 A：记录端到端耗时（含图片解码 / 宿主创建 / DComp 渲染）
        var t0 = Environment.TickCount64;
        await service.UpdateWallpaperAsync(target, pngA);
        var applyMs = Environment.TickCount64 - t0;
        AddCheck("render：应用图片 A 完成", true, $"UpdateWallpaperAsync 耗时 = {applyMs}ms");

        // ② 象限采样：验证画面可见、无错位、无镜像/翻转
        var (matchA, msA) = ScanQuadrants(target, red, green, blue, white, timeoutMs: 2500);
        AddCheck("render：图片 A 四象限像素匹配（可见/无错位/无翻转）", matchA, $"匹配耗时 = {msA}ms（屏上采样 vs 源图象限色）");

        // ③ 切换图片 B：再次采样应呈现 B 的颜色（切换即时生效）
        var t1 = Environment.TickCount64;
        await service.UpdateWallpaperAsync(target, pngB);
        var switchMs = Environment.TickCount64 - t1;
        var (matchB, msB) = ScanQuadrants(target, white, blue, green, red, timeoutMs: 2500);
        AddCheck("render：切换图片 B 即时生效", matchB, $"调用耗时 = {switchMs}ms，屏上颜色匹配耗时 = {msB}ms");

        // ④ 静态图 CPU≈0%：空闲 3s 测本进程处理器时间占比（渲染器无逐帧工作则应接近 0）
        var proc = Process.GetCurrentProcess();
        var cpuBefore = proc.TotalProcessorTime;
        Thread.Sleep(3000);
        var cpuMs = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var cpuPct = cpuMs / 3000.0 * 100.0;
        AddCheck("render：静态图空闲 CPU≈0%", cpuPct < 2.0, $"3s 内处理器时间占比 = {cpuPct:F2}%（阈值 <2%）");

        // ⑤ 点击穿透：SendInput 点击客户区中心 → 目标窗口应收到 WM_LBUTTONDOWN（宿主不拦截）。
        // SendClick 按屏幕坐标注入，客户区坐标须先经 ClientToScreen 换算
        GetClientRect(target, out var client);
        var cx = client.L + (client.R - client.L) / 2;
        var cy = client.T + (client.B - client.T) / 2;
        var screenPt = new POINT { X = cx, Y = cy };
        ClientToScreen(target, ref screenPt);
        BareTargetWindow.ResetClick();
        SendClick(screenPt.X, screenPt.Y);
        Thread.Sleep(400); // 注入→分派到 pump 线程的窗口过程
        var clicked = BareTargetWindow.TryGetClick(out var gotX, out var gotY);
        AddCheck("render：宿主点击穿透（目标收到点击）", clicked && Math.Abs(gotX - cx) <= 2 && Math.Abs(gotY - cy) <= 2,
            clicked ? $"目标收到 ({gotX},{gotY})，期望 ({cx},{cy})" : "目标未收到 WM_LBUTTONDOWN");

        // ⑥ pkg 降级封面：PackageWriter 构造最小 pkg（scene.json 引用 script → 判定复杂场景 → 封面图上屏）
        var pkgPath = Path.Combine(Path.GetTempPath(), $"wp1b-smoke-{Guid.NewGuid():N}.pkg");
        BuildPkg(pkgPath, sceneJson: "{\"description\":\"diag\",\"script\":\"main.lua\"}", previewPng: pngA);
        reports.Clear(); // ConcurrentQueue 无锁清空
        await service.UpdateWallpaperAsync(target, pkgPath);
        var reportsSnapshot = reports.ToArray();
        var degradedReported = reportsSnapshot.Any(m => m.Contains("降级"));
        var (matchPkg, msPkg) = ScanQuadrants(target, red, green, blue, white, timeoutMs: 2500);
        AddCheck("render：pkg 降级提示上报", degradedReported,
            degradedReported ? "StatusReported 含「降级」提示" : "未见降级提示：" + string.Join(" | ", reportsSnapshot));
        AddCheck("render：pkg 降级封面上屏", matchPkg, $"封面（=图片 A）象限匹配耗时 = {msPkg}ms");

        // ⑦ 收尾清理
        service.ShutdownAll();
        var leftovers = WallpaperHostWindow.CountAliveHosts();
        AddCheck("render：ShutdownAll 清零", leftovers == 0, $"宿主残留 = {leftovers}（应 0）");

        File.Delete(pngA);
        File.Delete(pngB);
        File.Delete(pkgPath);
        return FinishChecks();
    }

    /// <summary>
    /// CsWinRT 互操作面探测（winrt 模式，阶段 C 主路径可行性评估）：
    /// ① 反射 WinRT.Runtime 的 MarshalInterface&lt;T&gt; / CastExtensions 公开方法（原生 IInspectable
    ///    ABI 指针 → 托管 IDirect3DSurface 的官方通道是否存在）；
    /// ② 用 System.Reflection.Metadata 解析系统 winmd（System32\WinMetadata），提取
    ///    IDirect3DSurface 与 ICompositorDesktopInterop 的接口 GUID（ComImport 声明/ABI 直调需要）。
    /// </summary>
    private static int RunWinrtProbe()
    {
        Console.WriteLine("== CsWinRT 互操作面探测 ==");

        // ① WinRT.Runtime 公开互操作通道
        var marshalType = typeof(WinRT.IObjectReference).Assembly.GetType("WinRT.MarshalInterface`1");
        if (marshalType != null)
        {
            Console.WriteLine($"MarshalInterface<T> 存在（{marshalType.AssemblyQualifiedName}）");
            foreach (var m in marshalType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                Console.WriteLine($"   method: {m}");
        }
        else
        {
            Console.WriteLine("MarshalInterface<T> 不存在");
        }

        var castType = typeof(WinRT.IObjectReference).Assembly.GetType("WinRT.CastExtensions");
        if (castType != null)
        {
            Console.WriteLine("CastExtensions 存在：");
            foreach (var m in castType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                Console.WriteLine($"   method: {m}");
        }
        else
        {
            Console.WriteLine("CastExtensions 不存在");
        }

        var inspectableType = typeof(WinRT.IObjectReference).Assembly.GetType("WinRT.MarshalInspectable`1");
        if (inspectableType != null)
        {
            Console.WriteLine("MarshalInspectable<T> 存在：");
            foreach (var m in inspectableType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                Console.WriteLine($"   method: {m}");
        }
        else
        {
            Console.WriteLine("MarshalInspectable<T> 不存在");
        }

        // ② 系统 winmd 的接口 IID 提取
        var metadataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WinMetadata");
        foreach (var file in Directory.EnumerateFiles(metadataDir, "*.winmd"))
        {
            try
            {
                using var fs = File.OpenRead(file);
                unsafe
                {
                    using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                    if (!pe.HasMetadata)
                        continue;

                    var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                    foreach (var handle in reader.TypeDefinitions)
                    {
                        var def = reader.GetTypeDefinition(handle);
                        var ns = def.Namespace.IsNil ? "" : reader.GetString(def.Namespace);
                        var name = reader.GetString(def.Name);
                        var full = ns.Length > 0 ? $"{ns}.{name}" : name;

                        if (full is not ("Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface"
                            or "Windows.UI.Composition.Interop.ICompositorDesktopInterop"
                            or "Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice"))
                        {
                            continue;
                        }

                        foreach (var attr in def.GetCustomAttributes())
                        {
                            var ctor = reader.GetCustomAttribute(attr);
                            string attrName;
                            if (ctor.Constructor.Kind == System.Reflection.Metadata.HandleKind.MethodDefinition)
                            {
                                attrName = reader.GetString(reader.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle) ctor.Constructor).Name);
                            }
                            else if (ctor.Constructor.Kind == System.Reflection.Metadata.HandleKind.MemberReference)
                            {
                                var memberRef = reader.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle) ctor.Constructor);
                                attrName = memberRef.Parent.Kind == System.Reflection.Metadata.HandleKind.TypeReference
                                    ? reader.GetString(reader.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle) memberRef.Parent).Name)
                                    : "";
                            }
                            else
                            {
                                continue;
                            }

                            if (!attrName.Contains("GuidAttribute"))
                                continue;

                            // GuidAttribute ctor blob：prolog(0x00) + uint + ushort + ushort + 8×byte
                            var blobReader = reader.GetBlobReader(ctor.Value);
                            blobReader.ReadByte(); // prolog
                            var a = blobReader.ReadUInt32();
                            var b = blobReader.ReadUInt16();
                            var c = blobReader.ReadUInt16();
                            var g = new byte[8];
                            for (var i = 0; i < 8; i++)
                                g[i] = blobReader.ReadByte();

                            var guid = new Guid((int) a, (short) b, (short) c, g);
                            Console.WriteLine($"IID {full} = {guid:D}（{Path.GetFileName(file)}）");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析 {file} 失败：{ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// 视频上屏产品路径验收（video 模式，阶段 C）：
    /// ① MediaComposition 生成测试 mp4：两段异向双色 320x240 clip（各 1.5s）——
    ///    左半区颜色随片段切换（红 ↔ 蓝）证明帧流动；半区构图（左右分割）用于实测 MF 的比例语义。
    /// ② 裸目标窗口（pump 线程）+ WallpaperService.UpdateWallpaperAsync(mp4)：
    ///    产品接线全链路（帧服务器 MediaPlayer → VideoFrameAvailable → 组合交换链 Present → Commit）。
    /// ③ 验收点：帧计数推进（流动）、加边 vs 拉伸实测（比例语义，ADR 依据）、
    ///    全屏前台窗口 → 视频暂停（帧计数冻结）→ 退出全屏恢复（DynamicFrameRateController 联动）、
    ///    切图片 → 帧计数停止（旧播放器随会话释放）、ShutdownAll → 宿主清零。
    /// 退出码 0 = 全部断言通过。
    /// </summary>
    private static async Task<int> RunVideoProbeAsync(string[] args)
    {
        Console.WriteLine("== WP1-C 视频上屏验收（产品路径）==");

        // ① 造测试视频：两段 320x240 半区双色 clip（各 1.5s）→ 3s mp4
        //    clip1 = 左红右白；clip2 = 左蓝右白——左半区颜色随片段切换（帧流动信号），几何恒定（比例信号）
        var videoPath = Path.Combine(Path.GetTempPath(), $"wp1c-test-{Guid.NewGuid():N}.mp4");
        var clipPaths = new List<string>();
        var comp = new Windows.Media.Editing.MediaComposition();
        foreach (var (lr, lg, lb, rr, rg, rb) in new[]
                 {
                     ((byte) 255, (byte) 0, (byte) 0, (byte) 255, (byte) 255, (byte) 255),
                     ((byte) 0, (byte) 0, (byte) 255, (byte) 255, (byte) 255, (byte) 255),
                 })
        {
            var clipPath = Path.Combine(Path.GetTempPath(), $"wp1c-clip-{Guid.NewGuid():N}.png");
            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(320, 240))
            {
                for (var y = 0; y < 240; y++)
                    for (var x = 0; x < 320; x++)
                        image[x, y] = x < 160
                            ? new SixLabors.ImageSharp.PixelFormats.Rgba32(lr, lg, lb, 255)
                            : new SixLabors.ImageSharp.PixelFormats.Rgba32(rr, rg, rb, 255);

                await image.SaveAsPngAsync(clipPath);
            }

            clipPaths.Add(clipPath);
            var clipFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(clipPath);
            comp.Clips.Add(await Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(clipFile, TimeSpan.FromSeconds(1.5)));
        }

        var tempFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        var outFile = await tempFolder.CreateFileAsync(
            Path.GetFileName(videoPath), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        var renderInfo = await comp.RenderToFileAsync(outFile);
        Console.WriteLine($"① 测试视频生成：{renderInfo}（None = 成功）-> {videoPath}");
        if (renderInfo != Windows.Media.Transcoding.TranscodeFailureReason.None)
            return 1;

        // ② 裸目标窗口（pump 线程，与宿主/渲染器同线程）
        IntPtr target = IntPtr.Zero;
        var created = WallpaperHostPump.Shared.Execute(
            () => target = BareTargetWindow.CreateOnCurrentThread(), timeoutMs: 5000);
        if (!created || target == IntPtr.Zero)
        {
            Console.WriteLine($"裸目标窗口创建失败（Execute 回执 = {created}，hwnd = 0x{target:X}，" +
                              $"pump 存活 = {WallpaperHostPump.Shared.IsRunning}）");
            foreach (var p in clipPaths)
                File.Delete(p);

            File.Delete(videoPath);
            return 1;
        }

        SetWindowPos(target, new IntPtr(-1), 100, 100, 800, 600, 0x0010);
        Thread.Sleep(400);

        // ③ 产品接线：视频组合呈现（GetSurface + DesktopWindowTarget + SpriteVisual 加边）
        var service = new WinCanvas.Wallpaper.WallpaperService();
        Windows.Media.Playback.MediaPlayer? playerRef = null;
        service.VideoPlayerReady += (_, player) =>
        {
            playerRef = player;
            player.MediaFailed += (_, e) => Console.WriteLine($"   [MediaFailed] {e.Error} {e.ErrorMessage}");
            player.MediaOpened += (_, _) => Console.WriteLine("   [MediaOpened]");
            player.PlaybackSession.PlaybackStateChanged += (_, _) =>
                Console.WriteLine($"   [PlaybackState] {player.PlaybackSession.PlaybackState} @ {player.PlaybackSession.Position}");
        };
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.StatusReported += reports.Enqueue;

        var applyT0 = Environment.TickCount64;
        await service.UpdateWallpaperAsync(target, videoPath);
        Console.WriteLine($"② UpdateWallpaperAsync(视频) 完成，耗时 = {Environment.TickCount64 - applyT0}ms");

        // 等待 MediaOpened + 首帧
        Thread.Sleep(1200);

        // ④ 流动验证：采样客户区左 1/4 处颜色（clip1 红 / clip2 蓝 随时间切换）
        var seenColors = new List<string>();
        var seenValues = new HashSet<string>();
        for (var i = 0; i < 30; i++)
        {
            var c = SamplePoint(target, 200, 300);
            var key = $"{c.r},{c.g},{c.b}";
            if (seenValues.Add(key))
                seenColors.Add(key);

            Thread.Sleep(150);
        }

        var flowPass = seenColors.Count >= 2; // 至少观察到两个不同帧内容（帧推进）
        Console.WriteLine($"④ 左 1/4 处颜色序列 = [{string.Join(" -> ", seenColors)}]（应随片段切换变化）");
        AddCheck("video：帧流动（颜色随时间切换）", flowPass, $"颜色序列 {seenColors.Count} 种（应 ≥2：红/蓝随片段切换）");

        // ⑤ 比例语义实测：窗口改 800x800（1:1 目标 + 4:3 视频）
        //    组合呈现按视频比例加边 → x=5% 处为恒定底色（宿主纯色蓝 30,80,180）；拉伸则跟随内容色变化
        SetWindowPos(target, IntPtr.Zero, 100, 100, 800, 800, 0x0010 | 0x0004);
        Thread.Sleep(800);
        var bandSamples = new List<string>();
        var contentSamples = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            bandSamples.Add(ColorKey(SamplePoint(target, 40, 400)));
            contentSamples.Add(ColorKey(SamplePoint(target, 200, 400)));
            var sess = playerRef?.PlaybackSession;
            Console.WriteLine($"   采样{i}: 播放态={sess?.PlaybackState} 位置={sess?.Position}");
            Thread.Sleep(400);
        }

        var bandStable = bandSamples.Distinct().Count() == 1;
        var contentChanged = contentSamples.Distinct().Count() >= 2;
        var aspectMode = bandStable && contentChanged ? "加边（保持比例）" : (bandStable ? "恒定（内容未变？）" : "拉伸");
        Console.WriteLine($"⑤ 800x800 采样：边带 x=5% = [{string.Join(" / ", bandSamples.Distinct())}]，" +
                          $"内容 x=25% = [{string.Join(" / ", contentSamples.Distinct())}] → 判定：{aspectMode}");
        AddCheck("video：比例正确（等比加边，禁止拉伸）", bandStable && contentChanged,
            $"边带恒定 = {bandStable}，内容变化 = {contentChanged}（判定：{aspectMode}）");

        // ⑥ 全屏联动：前台无边框置顶窗口盖满主屏 → 全屏检测（会话渲染循环）→ player.Pause → 画面定格
        //    断言口径（颜色稳定性）：2s 采样窗必然跨越 1.5s 的片段切换边界——播放中必观察到颜色变化，
        //    暂停中必恒定。窗口尺寸保持 800x800（左 1/4 = 200,400；边带不动不影响该点）。
        IntPtr cover = IntPtr.Zero;
        var coverCreated = WallpaperHostPump.Shared.Execute(() =>
        {
            cover = BareTargetWindow.CreateOnCurrentThread();
            _ = SetWindowPos(cover, new IntPtr(-1), 0, 0, GetSystemMetrics(0), GetSystemMetrics(1), 0x0010);
        }, timeoutMs: 5000);
        _ = SetForegroundWindow(cover);
        Thread.Sleep(1500); // 全屏检测节流 250ms + 暂停生效

        var fullscreenColors = new HashSet<string>();
        for (var i = 0; i < 13; i++)
        {
            fullscreenColors.Add(ColorKey(SamplePoint(target, 200, 400)));
            Thread.Sleep(150);
        }

        var pausedFrozen = fullscreenColors.Count == 1;
        Console.WriteLine($"⑥ 全屏盖窗 2s 采样：{fullscreenColors.Count} 种颜色（应 1 种 = 定格）");
        AddCheck("video：全屏前台 → 视频暂停（画面定格）", coverCreated && pausedFrozen,
            $"2s 采样窗颜色种类 = {fullscreenColors.Count}（应 1：2s 必跨越 1.5s 的片段边界）");

        // 退出全屏：撤盖窗 → 恢复播放 → 颜色恢复切换
        _ = WallpaperHostPump.Shared.Execute(() => DestroyWindow(cover), timeoutMs: 5000);
        Thread.Sleep(1500);
        var resumeColors = new HashSet<string>();
        for (var i = 0; i < 13; i++)
        {
            resumeColors.Add(ColorKey(SamplePoint(target, 200, 400)));
            if (i % 4 == 0)
            {
                var sess = playerRef?.PlaybackSession;
                Console.WriteLine($"   恢复采样{i}: 播放态={sess?.PlaybackState} 位置={sess?.Position}");
            }

            Thread.Sleep(150);
        }

        Console.WriteLine($"   退出全屏 2s 采样：{resumeColors.Count} 种颜色（应 ≥2 = 恢复）");
        AddCheck("video：退出全屏 → 恢复播放", resumeColors.Count >= 2,
            $"2s 采样窗颜色种类 = {resumeColors.Count}（应 ≥2）");

        // ⑦ 资源释放：切图片 → 旧视频播放器随会话 Dispose，组合视觉消失（左 1/4 处恒为图片色）
        var pngA = Path.Combine(Path.GetTempPath(), $"wp1c-img-{Guid.NewGuid():N}.png");
        using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64,
                   new SixLabors.ImageSharp.PixelFormats.Rgba32(30, 80, 180, 255)))
        {
            await image.SaveAsPngAsync(pngA);
        }

        await service.UpdateWallpaperAsync(target, pngA);
        Thread.Sleep(600);
        var imageColors = new HashSet<string>();
        for (var i = 0; i < 8; i++)
        {
            imageColors.Add(ColorKey(SamplePoint(target, 200, 400)));
            Thread.Sleep(300);
        }

        var imageStable = imageColors.Count == 1 && imageColors.First() == "30,80,180";
        Console.WriteLine($"⑦ 切图片后左 1/4 采样：[{string.Join(" / ", imageColors)}]（应恒为图片色 30,80,180）");
        AddCheck("video：切图片 → 视频消失（播放器/组合树释放）", imageStable,
            imageStable ? "颜色恒定 = 视频视觉已移除" : "视频内容仍存在或颜色异常");

        // ⑧ 收尾清理
        service.ShutdownAll();
        var leftovers = WallpaperHostWindow.CountAliveHosts();
        AddCheck("video：ShutdownAll 清零", leftovers == 0, $"宿主残留 = {leftovers}（应 0）");

        foreach (var p in clipPaths)
            File.Delete(p);

        File.Delete(pngA);
        var externalVideo = args.SkipWhile(a => a != "video").Skip(1).FirstOrDefault(File.Exists);
        if (externalVideo == null)
            File.Delete(videoPath);

        var failed = _checks.Count(c => !c.Pass);
        Console.WriteLine();
        foreach (var c in _checks)
            Console.WriteLine($"[{(c.Pass ? "PASS" : "FAIL")}] {c.Name}：{c.Detail}");

        Console.WriteLine(failed == 0 ? "\n全部通过。" : $"\n失败 {failed} 项。");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// DComp 管线分步探针（dcomp 模式）：从 D3D11CreateDevice 到 Commit 每步打印 HRESULT，
    /// 并在裸窗口上做象限采样。用于定位 COM 互操作问题（槽位 / 参数封送 / 枚举值）。
    /// </summary>
    private static int RunDcompProbe()
    {
        // 窗口与 DComp 设备同在主线程（跨线程绑定时 DWM 不合成内容，WP1-B 实测对照）
        var target = BareTargetWindow.CreateOnCurrentThread();
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("探针失败：裸窗口创建失败");
            return 1;
        }

        SetWindowPos(target, new IntPtr(-1), 100, 100, 800, 600, 0x0010);
        BareTargetWindow.PumpMessages(300);

        // 1) D3D11 设备
        var levels = new uint[] { 0xb100, 0xb000, 0xa100, 0xa000 };
        var hr = DcompNativeMethods.D3D11CreateDevice(IntPtr.Zero, DcompNativeMethods.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero, DcompNativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, (uint) levels.Length, DcompNativeMethods.D3D11_SDK_VERSION,
            out var d3d, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine($"1 D3D11CreateDevice: 0x{hr:X8}");
        if (hr != 0) return 1;

        // 2) IDXGIDevice
        hr = Marshal.QueryInterface(d3d, in DcompNativeMethods.IID_IDXGIDevice, out var dxgi);
        Console.WriteLine($"2 QI(IDXGIDevice): 0x{hr:X8}");
        if (hr != 0) return 1;

        // 3) DComp 设备
        hr = DcompNativeMethods.DCompositionCreateDevice(dxgi, in DcompNativeMethods.IID_IDCompositionDevice, out var devicePtr);
        Console.WriteLine($"3 DCompositionCreateDevice: 0x{hr:X8}");
        if (hr != 0) return 1;
        var device = (DcompNativeMethods.IDCompositionDevice) Marshal.GetObjectForIUnknown(devicePtr);
        Marshal.Release(devicePtr);

        // 4) 目标
        hr = device.CreateTargetForHwnd(target, 1, out var compTarget);
        Console.WriteLine($"4 CreateTargetForHwnd: 0x{hr:X8}");
        if (hr != 0) return 1;

        // 5) 视觉（缩放走 GDI，不调 SetTransform / SetBitmapInterpolationMode——ABI 歧义规避，见 WORKLOG）
        hr = device.CreateVisual(out var visual);
        Console.WriteLine($"5 CreateVisual: 0x{hr:X8}");
        if (hr != 0) return 1;

        // 6) 表面（尺寸 = 客户区，规格 B.4 口径）
        GetClientRect(target, out var clientRect);
        var clientW = clientRect.R - clientRect.L;
        var clientH = clientRect.B - clientRect.T;
        hr = device.CreateSurface((uint) clientW, (uint) clientH, DcompNativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM,
            DcompNativeMethods.DXGI_ALPHA_MODE_IGNORE, out var surface); // 探针变体：IGNORE 验证 GDI alpha 清零假说
        Console.WriteLine($"6 CreateSurface({clientW}x{clientH}): 0x{hr:X8}");
        if (hr != 0) return 1;

        // 7) BeginDraw（显式整面更新区）+ GetDC + StretchDIBits 贴 2x2 象限图 + EndDraw
        var updateRect = Marshal.AllocHGlobal(Marshal.SizeOf<DcompNativeMethods.RECT>());
        Marshal.StructureToPtr(new DcompNativeMethods.RECT { Left = 0, Top = 0, Right = clientW, Bottom = clientH }, updateRect, false);
        try
        {
            hr = surface.BeginDraw(updateRect, in DcompNativeMethods.IID_IDXGISurface, out var obj, out var offset);
            Console.WriteLine($"7 BeginDraw: 0x{hr:X8} offset=({offset.X},{offset.Y})");
            if (hr != 0) return 1;

            // DComp 表面不支持 IDXGISurface::Map（实测 E_INVALIDARG）；官方 CPU 路径是
            // IDXGISurface1::GetDC 后用 GDI 贴 DIB（32bpp BI_RGB 逐字节拷贝，alpha 字节随行复制）
            if (Marshal.GetObjectForIUnknown(obj) is not DxgiSurface1Tool surface1)
            {
                Console.WriteLine("   BeginDraw 未返回 IDXGISurface1");
                Marshal.Release(obj);
                return 1;
            }

            Marshal.Release(obj);
            hr = surface1.GetDC(0, out var hdc);
            Console.WriteLine($"   GetDC: 0x{hr:X8} hdc=0x{hdc:X}");
            if (hr != 0) return 1;

            // 2x2 像素（B,G,R,A 紧密排列，top-down）：上左红 / 上右绿 / 下左蓝 / 下右白
            var pixelBits = new byte[]
            {
                0, 0, 255, 255,   // TL 红
                0, 255, 0, 255,   // TR 绿
                255, 0, 0, 255,   // BL 蓝
                255, 255, 255, 255, // BR 白
            };

            var bmi = new BitmapInfo();
            bmi.Header.BiSize = (uint) Marshal.SizeOf<BitmapInfoHeader>();
            bmi.Header.BiWidth = 2;
            bmi.Header.BiHeight = -2; // 负高 = top-down
            bmi.Header.BiPlanes = 1;
            bmi.Header.BiBitCount = 32;
            bmi.Header.BiCompression = 0; // BI_RGB

            // 缩放贴入整面（HALFTONE）：源 2x2 → 表面 = 客户区尺寸
            _ = SetStretchBltMode(hdc, 4 /* HALFTONE */);
            hr = StretchDIBits(
                hdc, offset.X, offset.Y, clientW, clientH, 0, 0, 2, 2,
                pixelBits, ref bmi, 0 /* DIB_RGB_COLORS */, 0x00CC0020 /* SRCCOPY */);
            Console.WriteLine($"   StretchDIBits: {hr}（GDI 复制行数）");

            hr = surface1.ReleaseDC(IntPtr.Zero);
            Console.WriteLine($"   ReleaseDC: 0x{hr:X8}");
            hr = surface.EndDraw();
            Console.WriteLine($"   EndDraw: 0x{hr:X8}");
        }
        finally
        {
            Marshal.FreeHGlobal(updateRect);
        }

        // 8) SetContent + SetRoot + Commit
        hr = visual.SetContent(surface);
        Console.WriteLine($"8 SetContent: 0x{hr:X8}");
        hr = compTarget.SetRoot(visual);
        Console.WriteLine($"9 SetRoot: 0x{hr:X8}");
        hr = device.Commit();
        Console.WriteLine($"10 Commit: 0x{hr:X8}");

        // 11) 屏上采样四象限（表面 = 客户区尺寸，GDI 缩放后象限中心 = 源像素色）
        var (match, ms) = ScanQuadrants(target, (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), 2500);
        Console.WriteLine($"11 象限采样匹配 = {match}（{ms}ms）实测色 = {LastSeenColors}");
        Console.WriteLine(match ? "探针全部成功。" : "探针：Commit 成功但屏上颜色不符——跨线程/合成受限待查。");
        return match ? 0 : 1;
    }

    /// <summary>生成 2x2 四象限异色 PNG（DComp 双线性放大后，象限中心点采样 = 精确的源像素色）。</summary>
    private static string WriteQuadPng(
        (byte r, byte g, byte b) tl, (byte r, byte g, byte b) tr,
        (byte r, byte g, byte b) bl, (byte r, byte g, byte b) br)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wp1b-{Guid.NewGuid():N}.png");
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(2, 2);
        image[0, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(tl.r, tl.g, tl.b);
        image[1, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(tr.r, tr.g, tr.b);
        image[0, 1] = new SixLabors.ImageSharp.PixelFormats.Rgba32(bl.r, bl.g, bl.b);
        image[1, 1] = new SixLabors.ImageSharp.PixelFormats.Rgba32(br.r, br.g, br.b);
        image.SaveAsPng(path);
        return path;
    }

    /// <summary>
    /// 屏幕采样宿主画面的四个象限中心，与期望颜色逐一比对（容差 ±8/通道，DWM 双线性重采样在
    /// 象限中心恰好取到源像素中心色）。GetPixel 每次调用开销极小；轮询直到全部匹配或超时。
    /// </summary>
    /// <returns>（是否全部匹配，从调用到全部匹配的毫秒数）。</returns>
    private static (bool matched, long ms) ScanQuadrants(
        IntPtr target,
        (byte r, byte g, byte b) tl, (byte r, byte g, byte b) tr,
        (byte r, byte g, byte b) bl, (byte r, byte g, byte b) br,
        int timeoutMs)
    {
        GetClientRect(target, out var client);
        var w = client.R - client.L;
        var h = client.B - client.T;
        (int x, int y)[] centers =
        {
            (client.L + w / 4, client.T + h / 4),   // 上左象限中心
            (client.L + 3 * w / 4, client.T + h / 4), // 上右
            (client.L + w / 4, client.T + 3 * h / 4), // 下左
            (client.L + 3 * w / 4, client.T + 3 * h / 4), // 下右
        };
        (byte r, byte g, byte b)[] expected = { tl, tr, bl, br };

        var t0 = Environment.TickCount64;
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            while (Environment.TickCount64 - t0 < timeoutMs)
            {
                var allMatch = true;
                for (var i = 0; i < 4 && allMatch; i++)
                {
                    var pt = new POINT { X = centers[i].x, Y = centers[i].y };
                    ClientToScreen(target, ref pt);
                    var colorRef = GetPixel(screenDc, pt.X, pt.Y);
                    var pr = (int) (colorRef & 0xFF);
                    var pg = (int) ((colorRef >> 8) & 0xFF);
                    var pb = (int) ((colorRef >> 16) & 0xFF);
                    if (Math.Abs(pr - expected[i].r) > 8 || Math.Abs(pg - expected[i].g) > 8 || Math.Abs(pb - expected[i].b) > 8)
                        allMatch = false;
                }

                if (allMatch)
                    return (true, Environment.TickCount64 - t0);

                Thread.Sleep(60);
            }

            // 超时未匹配：采集最终实测颜色辅助定位（白 = 目标自身背景、深蓝 = GDI 纯色占位、
            // 其他 = 内容错位 / 翻转 / 缩放异常）
            var seen = new string[4];
            for (var i = 0; i < 4; i++)
            {
                var pt = new POINT { X = centers[i].x, Y = centers[i].y };
                ClientToScreen(target, ref pt);
                var colorRef = GetPixel(screenDc, pt.X, pt.Y);
                seen[i] = $"({colorRef & 0xFF},{(colorRef >> 8) & 0xFF},{(colorRef >> 16) & 0xFF})";
            }

            LastSeenColors = string.Join(" ", seen);
            return (false, Environment.TickCount64 - t0);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>最近一次象限采样失败的实测颜色（诊断输出用）。</summary>
    private static string LastSeenColors = string.Empty;

    /// <summary>用 repkg 的 PackageWriter 构造最小 pkg：scene.json（引用 script → 复杂场景降级）+ preview.png。</summary>
    private static void BuildPkg(string pkgPath, string sceneJson, string previewPng)
    {
        var package = new RePKG.Core.Package.Package { Magic = "PKGV0005" };
        package.Entries.Add(new RePKG.Core.Package.PackageEntry
        {
            FullPath = "scene.json",
            Bytes = Encoding.UTF8.GetBytes(sceneJson),
        });
        package.Entries.Add(new RePKG.Core.Package.PackageEntry
        {
            FullPath = "preview.png",
            Bytes = File.ReadAllBytes(previewPng),
        });

        using var stream = File.Create(pkgPath);
        using var writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true);
        new RePKG.Application.Package.PackageWriter().WriteTo(writer, package);
    }

    /// <summary>SendInput 一组按下 + 抬起（绝对坐标按虚拟屏归一化），模拟真实鼠标点击。</summary>
    private static void SendClick(int x, int y)
    {
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000,
                   MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_VIRTUALDESK = 0x4000;
        var vx = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var vy = GetSystemMetrics(77);
        var vcx = GetSystemMetrics(78);
        var vcy = GetSystemMetrics(79);
        var ax = (x - vx) * 65535 / Math.Max(1, vcx - 1);
        var ay = (y - vy) * 65535 / Math.Max(1, vcy - 1);

        var input = new InputRecord
        {
            Type = 0, // INPUT_MOUSE
            Mouse = new MouseInput
            {
                Dx = ax,
                Dy = ay,
                Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        };
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<InputRecord>());

        input.Mouse.Flags = MOUSEEVENTF_LEFTDOWN;
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<InputRecord>());
        input.Mouse.Flags = MOUSEEVENTF_LEFTUP;
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<InputRecord>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputRecord
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, InputRecord[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(
        IntPtr hdc, int xDst, int yDst, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight,
        byte[] bits, ref BitmapInfo bmi, uint colorUse, uint rop);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    /// <summary>
    /// 裸 Win32 目标窗口工具（render / dcomp 模式共用）：无子控件、纯白自绘背景，WndProc 记录
    /// 收到的 WM_LBUTTONDOWN 坐标（静态字段跨线程可读）。窗口必须在【调用线程】创建，
    /// 且该线程要泵消息——DComp 设备与窗口跨线程绑定时 DWM 不合成视觉树内容（WP1-B 实测，
    /// 见 WORKLOG），render 模式因此把它建到宿主 pump 线程、探针建到主线程并自泵消息。
    /// </summary>
    private static class BareTargetWindow
    {
        /// <summary>窗口类名。</summary>
        public const string ClassName = "Wp1ADiagBareTarget";

        /// <summary>最近一次点击的坐标（-1 = 未收到）。volatile：WndProc 线程写、其他线程读。</summary>
        private static volatile int _clickX = -1;
        private static volatile int _clickY = -1;

        /// <summary>在当前线程创建并显示裸窗口（调用方负责该线程的消息泵）。</summary>
        public static IntPtr CreateOnCurrentThread()
        {
            RegisterClassOnce();

            var hwnd = CreateWindowExW(0, ClassName, null,
                0x80000000 /* WS_POPUP */, 100, 100, 800, 600,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            ShowWindow(hwnd, 5 /* SW_SHOW */);
            return hwnd;
        }

        /// <summary>泵当前线程消息队列 timeoutMs 毫秒（窗口消息分派 + 空闲小睡）。</summary>
        public static void PumpMessages(int timeoutMs)
        {
            var until = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < until)
            {
                if (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                else
                {
                    Thread.Sleep(2);
                }
            }
        }

        /// <summary>读取点击记录（x/y 为客户区坐标；未收到过点击返回 false）。</summary>
        public static bool TryGetClick(out int x, out int y)
        {
            x = _clickX;
            y = _clickY;
            return x >= 0;
        }

        public static void ResetClick()
        {
            _clickX = -1;
            _clickY = -1;
        }

        private static void RegisterClassOnce()
        {
            lock (_classRegistrationGate)
            {
                if (_bareClassRegistered)
                    return;

                var wc = new WndClassW
                {
                    lpfnWndProc = BareTargetWndProc,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = ClassName,
                };
                RegisterClassW(ref wc); // 已注册（1410）按幂等处理
                _bareClassRegistered = true;
            }
        }

        private static IntPtr BareTargetWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_LBUTTONDOWN = 0x0201;
            const uint WM_PAINT = 0x000F;
            const uint WM_ERASEBKGND = 0x0014;

            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    _clickX = (short) ((long) lParam & 0xFFFF); // 客户区 x（低 16 位有符号）
                    _clickY = (short) (((long) lParam >> 16) & 0xFFFF); // 客户区 y（高 16 位）
                    return IntPtr.Zero;

                case WM_PAINT:
                {
                    var ps = new PaintStruct();
                    var hdc = BeginPaintW(hwnd, ref ps);
                    if (hdc != IntPtr.Zero)
                    {
                        var brush = CreateSolidBrush(0x00FFFFFF); // 纯白：与壁纸色区分
                        FillRect(hdc, ref ps.RcPaint, brush);
                        DeleteObject(brush);
                        EndPaintW(hwnd, ref ps);
                    }

                    return IntPtr.Zero;
                }

                case WM_ERASEBKGND:
                {
                    GetClientRect(hwnd, out var rc);
                    var brush = CreateSolidBrush(0x00FFFFFF);
                    FillRect(wParam, ref rc, brush);
                    DeleteObject(brush);
                    return new IntPtr(1);
                }
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll")]
        private static extern bool PeekMessageW(out MsgStruct msg, IntPtr hwnd, uint min, uint max, uint remove);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public bool FErase;
        public RECT RcPaint;
        public bool FRestore;
        public bool FIncUpdate;
        public ulong Reserved0;
        public ulong Reserved1;
        public ulong Reserved2;
        public ulong Reserved3;
    }

    [DllImport("user32.dll", EntryPoint = "BeginPaint")]
    private static extern IntPtr BeginPaintW(IntPtr hwnd, ref PaintStruct ps);

    [DllImport("gdi32.dll")]
    private static extern int SetDIBitsToDevice(
        IntPtr hdc, int xDst, int yDst, uint width, uint height,
        int xSrc, int ySrc, uint startScan, uint scanLines, byte[] bits, ref BitmapInfo bmi, uint colorUse);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    /// <summary>IDXGISurface1（vtable：IUnknown 3 + IDXGIObject 4 + GetDevice 1 + GetDesc/Map/Unmap 3 → GetDC/ReleaseDC）。</summary>
    [ComImport]
    [Guid("4ae63092-6327-4c1b-80ae-bfe12ea32b86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DxgiSurface1Tool
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int GetDevice();
        [PreserveSig] int GetDesc();
        [PreserveSig] int Map();
        [PreserveSig] int Unmap();

        /// <summary>取 GDI 设备上下文（DComp surface 的 GDI 绘制入口）。</summary>
        [PreserveSig]
        int GetDC(int discard, out IntPtr hdc);

        /// <summary>释放 GetDC 取得的 DC（dirtyRect 可为 NULL）。</summary>
        [PreserveSig]
        int ReleaseDC(IntPtr dirtyRect);
    }

    [DllImport("user32.dll", EntryPoint = "EndPaint")]
    private static extern bool EndPaintW(IntPtr hwnd, ref PaintStruct ps);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern bool FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MsgStruct msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MsgStruct msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MsgStruct msg);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgStruct
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    private static bool _bareClassRegistered;
    private static readonly object _classRegistrationGate = new();

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

    /// <summary>汇总打印全部断言并返回退出码（render / video 模式收尾；与 Main 的汇总口径一致）。</summary>
    private static int FinishChecks()
    {
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

    /// <summary>屏幕单点采样（GetPixel），返回 (R,G,B)。</summary>

    /// <summary>颜色三元组 → 采样键字符串（"r,g,b"；避免 string.Join 误绑元组 ToString）。</summary>
    private static string ColorKey((int r, int g, int b) c) => $"{c.r},{c.g},{c.b}";
    private static (int r, int g, int b) SamplePoint(IntPtr hwnd, int clientX, int clientY)
    {
        var pt = new POINT { X = clientX, Y = clientY };
        ClientToScreen(hwnd, ref pt);
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var c = GetPixel(dc, pt.X, pt.Y);
            return ((int) (c & 0xFF), (int) ((c >> 8) & 0xFF), (int) ((c >> 16) & 0xFF));
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }
}
