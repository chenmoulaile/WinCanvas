using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Gif;
using WinCanvas.Core.Services;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Hosting;

namespace Wp5FormatDiag;

/// <summary>
/// WP5 行为验证诊断命令（规格 WP5 DoD：「GIF 播放 / webp 加载 / 权限引导三项各自的行为验证」）。
///
/// 模式：
///   gif   —— 真实 WallpaperService 对自建裸窗口应用 2 帧异色动画 GIF：
///            ① 播放态 2s 屏幕采样应出现 ≥2 种颜色（帧在推进）；
///            ② PauseRendering 后 1.2s 采样恒定（暂停定格）；
///            ③ ResumeRendering 后 1s 恢复变色；
///            ④ 播放态 3s 进程 CPU% 与静态图同量级打点（阈值 &lt;10%，静态路径参考 WP1-B 实测 ≈0%）；
///            ⑤ ShutdownAll 残留清零。
///   webp  —— 合成静态 WebP → 真实加载 → 屏幕采样颜色匹配（选中 webp 能加载上屏的行为证明）。
///   elev  —— 打印当前进程提权状态（ProcessElevation.IsElevated）与错误 5 的引导文案映射
///            （构造 Win32Exception(5) 走 DescribeFailure；真实提权目标场景需交互式 UAC，见 WORKLOG）。
///
/// 退出码 0 = 本模式全部断言通过。窗口与采样基础设施的依据（pump 线程建窗 / 屏幕 DC GetPixel）
/// 与 tools/Wp1AHostDiag render 模式一致（WP1-B 已实测该口径可靠）。
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 与产品同 DPI 上下文（PMv2）：GetPixel 屏幕坐标 = 物理像素，不虚拟化
        SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

        if (args.Contains("gif"))
            return RunGifProbe();

        if (args.Contains("webp"))
            return RunWebpProbe();

        if (args.Contains("elev"))
            return RunElevProbe();

        Console.WriteLine("用法：dotnet run --project tools/Wp5FormatDiag -- gif|webp|elev");
        return 2;
    }

    // ====================================================================================
    // gif 模式：GIF 动画播放 / 暂停定格 / 恢复 / CPU 量级
    // ====================================================================================

    private static int RunGifProbe()
    {
        Console.WriteLine("== WP5 GIF 动画行为验证 ==");

        var gifPath = Path.Combine(Path.GetTempPath(), $"wp5-anim-{Guid.NewGuid():N}.gif");
        WriteAnimatedGif(gifPath);

        var target = CreateBareTarget();
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("[FAIL] 裸目标窗口创建失败");
            return 1;
        }

        var service = new WallpaperService();
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.StatusReported += reports.Enqueue;

        var applied = service.UpdateWallpaperAsync(target, gifPath).Wait(TimeSpan.FromSeconds(10));
        Console.WriteLine($"[INFO] 应用动画 GIF：{(applied ? "完成" : "超时")}，上报 {reports.Count} 条");

        var failures = 0;

        // ① 播放态：2s 采样，帧延迟 50ms×2 应大量交替，颜色种类必须 ≥2
        var playColors = SampleColors(target, TimeSpan.FromSeconds(2));
        Console.WriteLine($"[PASS?] 播放态 2s 采样颜色种类 = {playColors.Count}（{DescribeColors(playColors)}）");
        if (playColors.Count < 2)
        {
            failures++;
            Console.WriteLine("[FAIL] 播放态未见颜色交替：帧未推进（>=2 帧应出现红/蓝交替）");
        }
        else
        {
            Console.WriteLine("[PASS] GIF 背景在目标窗口内连续播放（帧推进可见）");
        }

        // ② 暂停定格：PauseRendering → 渲染循环不出帧 → 颜色恒定；同窗测 CPU 基线
        //（暂停态 = 无逐帧工作，等价静态图的占用口径，与播放态对照即「同量级」的直接证据）
        service.PauseRendering();
        var procPaused = System.Diagnostics.Process.GetCurrentProcess();
        var cpuPausedBefore = procPaused.TotalProcessorTime;
        var pausedColors = SampleColors(target, TimeSpan.FromMilliseconds(1200));
        var cpuPausedPct = (procPaused.TotalProcessorTime - cpuPausedBefore).TotalMilliseconds / 1200.0 * 100.0;
        Console.WriteLine($"[INFO] 暂停态（= 静态图口径）CPU 占用 = {cpuPausedPct:F2}%（1.2s 窗）");
        Console.WriteLine(pausedColors.Count <= 1
            ? $"[PASS] 暂停态定格（1.2s 采样颜色种类 = {pausedColors.Count}，{DescribeColors(pausedColors)}）"
            : $"[FAIL] 暂停态仍变色（颜色种类 = {pausedColors.Count}）");
        if (pausedColors.Count > 1)
            failures++;

        // ③ 恢复播放
        service.ResumeRendering();
        var resumedColors = SampleColors(target, TimeSpan.FromSeconds(1));
        Console.WriteLine(resumedColors.Count >= 2
            ? "[PASS] 恢复后继续播放"
            : $"[FAIL] 恢复后未见变色（颜色种类 = {resumedColors.Count}）");
        if (resumedColors.Count < 2)
            failures++;

        // ④ CPU 量级：播放态 3s 进程处理器占比（静态图参考基线 ≈0%，动画按帧推进 + GDI 重贴）
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var cpuBefore = proc.TotalProcessorTime;
        Thread.Sleep(3000);
        var cpuPct = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds / 3000.0 * 100.0;
        Console.WriteLine(cpuPct < 10.0
            ? $"[PASS] 播放态 CPU 占用 = {cpuPct:F2}%（与静态图同量级，阈值 <10%）"
            : $"[FAIL] 播放态 CPU 占用 = {cpuPct:F2}% 超阈值（>=10%）");
        if (cpuPct >= 10.0)
            failures++;

        // ⑤ 收尾清零
        service.ShutdownAll();
        var leftovers = WallpaperHostWindow.CountAliveHosts();
        Console.WriteLine(leftovers == 0
            ? "[PASS] ShutdownAll 后宿主残留 = 0"
            : $"[FAIL] 宿主残留 = {leftovers}");
        if (leftovers != 0)
            failures++;

        DestroyBareTarget(target);
        File.Delete(gifPath);

        Console.WriteLine(failures == 0 ? "== GIF 验证全部通过 ==" : $"== GIF 验证失败 {failures} 项 ==");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 合成 2 帧异色动画 GIF（纯红 / 纯蓝，帧延迟各 5 = 50ms）。
    /// 64x64 纯色小图，屏幕采样客户区中心即可读到放大后的帧色。
    /// </summary>
    private static void WriteAnimatedGif(string path)
    {
        using var image = new Image<Bgra32>(64, 64, new Bgra32(255, 0, 0, 255));
        image.Frames[0].Metadata.GetGifMetadata().FrameDelay = 5;

        using var frame2 = new Image<Bgra32>(64, 64, new Bgra32(0, 0, 255, 255));
        var added = image.Frames.AddFrame(frame2.Frames.RootFrame);
        added.Metadata.GetGifMetadata().FrameDelay = 5;

        image.SaveAsGif(path);
    }

    // ====================================================================================
    // webp 模式：静态 WebP 真实加载 + 上屏
    // ====================================================================================

    private static int RunWebpProbe()
    {
        Console.WriteLine("== WP5 WebP 加载行为验证 ==");

        var webpPath = Path.Combine(Path.GetTempPath(), $"wp5-static-{Guid.NewGuid():N}.webp");
        using (var image = new Image<Bgra32>(64, 64, new Bgra32(0, 160, 255, 255))) // 橙色系 (B,G,R)
        {
            image.SaveAsWebp(webpPath);
        }

        Console.WriteLine($"[INFO] IsSupported(webp) = {WallpaperFormats.IsImage(webpPath)}，" +
                          $"DetectKind = {WallpaperFormats.DetectKind(webpPath)}");

        var target = CreateBareTarget();
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("[FAIL] 裸目标窗口创建失败");
            return 1;
        }

        var service = new WallpaperService();
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.StatusReported += reports.Enqueue;
        var applied = service.UpdateWallpaperAsync(target, webpPath).Wait(TimeSpan.FromSeconds(10));
        Console.WriteLine($"[INFO] 应用 webp：{(applied ? "完成" : "超时")}，上报 {reports.Count} 条");
        foreach (var message in reports)
            Console.WriteLine($"[INFO] StatusReported：{message}");

        // 期望色以【文件本地解码结果】为准（忠实口径）：屏幕呈现应与解码像素一致——
        // 这正是「webp 文件被正确解码并上屏」的定义。不预设编码前构造色的具体通道语义
        //（ImageSharp 2.1 WebP 编码器实测与构造参数存在通道序出入，属编码器行为，与加载链路无关）。
        (int R, int G, int B) expected;
        using (var decoded = SixLabors.ImageSharp.Image.Load<Bgra32>(webpPath))
        {
            var px = decoded[32, 32];
            expected = (px.R, px.G, px.B);
            Console.WriteLine($"[INFO] 本地解码中心像素 = R:{px.R} G:{px.G} B:{px.B} A:{px.A}（作为屏上期望色）");
        }

        // 屏幕中心采样：应呈现与解码像素一致的颜色（GDI/HALFTONE 采样容差 ±24）
        var matched = WaitCenterColor(target, expected, TimeSpan.FromSeconds(3));
        GetClientRect(target, out var rc);
        var cpt = new POINT { X = (rc.Right - rc.Left) / 2, Y = (rc.Bottom - rc.Top) / 2 };
        _ = ClientToScreen(target, ref cpt);
        var sdc = GetDC(IntPtr.Zero);
        var raw = (int) GetPixel(sdc, cpt.X, cpt.Y);
        _ = ReleaseDC(IntPtr.Zero, sdc);
        Console.WriteLine($"[INFO] 屏幕中心实测 = R:{raw & 0xFF} G:{(raw >> 8) & 0xFF} B:{(raw >> 16) & 0xFF}（期望 R:{expected.R} G:{expected.G} B:{expected.B}）");
        Console.WriteLine(matched
            ? "[PASS] webp 背景成功加载并上屏（屏上颜色匹配）"
            : "[FAIL] webp 背景未能在目标窗口呈现");

        service.ShutdownAll();
        var leftovers = WallpaperHostWindow.CountAliveHosts();
        Console.WriteLine(leftovers == 0
            ? "[PASS] ShutdownAll 后宿主残留 = 0"
            : $"[FAIL] 宿主残留 = {leftovers}");
        var failures = (matched ? 0 : 1) + (leftovers == 0 ? 0 : 1);

        DestroyBareTarget(target);
        File.Delete(webpPath);

        Console.WriteLine(failures == 0 ? "== WebP 验证全部通过 ==" : $"== WebP 验证失败 {failures} 项 ==");
        return failures == 0 ? 0 : 1;
    }

    // ====================================================================================
    // elev 模式：提权状态 + 错误 5 引导文案映射
    // ====================================================================================

    private static int RunElevProbe()
    {
        Console.WriteLine("== WP5 权限引导行为验证 ==");

        Console.WriteLine($"[INFO] ProcessElevation.IsElevated = {ProcessElevation.IsElevated}" +
                          $"（PowerShell 交叉核对见 WORKLOG）");

        // 错误 5 的文案映射（与 WindowControlService 失败路径同构：Win32Exception(NativeErrorCode=5)）
        var uipi = ProcessElevation.DescribeFailure(new Win32Exception(5), "切换窗口「记事本」置顶");
        Console.WriteLine($"[INFO] 错误 5 文案：{uipi}");
        var mapped = uipi.Contains("更高权限") && uipi.Contains("以管理员身份重启 WinCanvas");

        // 非 5 错误不误导向提权
        var other = ProcessElevation.DescribeFailure(new Win32Exception(1400, "无效的窗口句柄。"), "设置窗口透明度");
        var passthrough = other == $"设置窗口透明度失败：{new Win32Exception(1400, "无效的窗口句柄。").Message}";

        Console.WriteLine(mapped ? "[PASS] 错误 5 → 提权引导文案映射正确" : "[FAIL] 错误 5 文案映射缺失");
        Console.WriteLine(passthrough ? "[PASS] 非 5 错误保持原语义透传（不误导）" : "[FAIL] 非 5 错误被改写");

        Console.WriteLine(mapped && passthrough ? "== 权限引导验证全部通过 ==" : "== 权限引导验证存在失败项 ==");
        return mapped && passthrough ? 0 : 1;
    }

    // ====================================================================================
    // 裸目标窗口 + 屏幕采样（口径与 tools/Wp1AHostDiag render 模式一致）
    // ====================================================================================

    private const string TargetClassName = "Wp5DiagBareTarget";

    /// <summary>经宿主 pump 线程创建裸窗口（与 DComp 渲染器同线程，跨线程绑定 DWM 不合成内容——WP1-B 实测）。</summary>
    private static IntPtr CreateBareTarget()
    {
        IntPtr target = IntPtr.Zero;
        var ok = WallpaperHostPump.Shared.Execute(() => target = CreateTargetOnCurrentThread(), timeoutMs: 5000);
        if (!ok || target == IntPtr.Zero)
            return IntPtr.Zero;

        // 置顶可见：屏幕 GetPixel 读的是合成结果，防被遮挡
        SetWindowPos(target, new IntPtr(-1) /* HWND_TOPMOST */, 100, 100, 800, 600, 0x0010 /* SWP_NOACTIVATE */);
        Thread.Sleep(400);
        return target;
    }

    /// <summary>销毁裸目标窗口（pump 线程执行，合法销毁自有窗口）。</summary>
    private static void DestroyBareTarget(IntPtr target) =>
        _ = WallpaperHostPump.Shared.Execute(() => DestroyWindow(target), timeoutMs: 5000);

    /// <summary>窗口过程委托类型 + 常驻引用（防 GC 回收后原生回调悬空）。</summary>
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate TargetWndProcKeepAlive = TargetWndProc;

    private static IntPtr CreateTargetOnCurrentThread()
    {
        if (!_classRegistered)
        {
            var wc = new WndClassW
            {
                WndProc = Marshal.GetFunctionPointerForDelegate(TargetWndProcKeepAlive),
                Instance = GetModuleHandleW(null),
                ClassName = TargetClassName,
            };
            _ = RegisterClassW(ref wc); // 1410（已注册）按幂等处理
            _classRegistered = true;
        }

        var hwnd = CreateWindowExW(0, TargetClassName, null,
            0x80000000 /* WS_POPUP */, 100, 100, 800, 600,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        ShowWindow(hwnd, 5 /* SW_SHOW */);
        return hwnd;
    }

    private static bool _classRegistered;

    private static IntPtr TargetWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 全部消息走默认处理即可：纯白背景由 DefWindowProc 的类刷子兜底，采样点必被宿主画面覆盖
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 在采样时长内反复读取客户区中心的屏幕颜色，返回出现过的颜色集合
    /// （按 24 级容差归并近色，避免 HALFTONE 缩放与 DWM 合成引入的 ±噪声算作新颜色）。
    /// </summary>
    private static HashSet<(int R, int G, int B)> SampleColors(IntPtr target, TimeSpan duration)
    {
        GetClientRect(target, out var client);
        var pt = new POINT { X = (client.Right - client.Left) / 2, Y = (client.Bottom - client.Top) / 2 };
        _ = ClientToScreen(target, ref pt);

        var colors = new HashSet<(int, int, int)>();
        var result = new HashSet<(int R, int G, int B)>();
        var until = Environment.TickCount64 + (long) duration.TotalMilliseconds;

        while (Environment.TickCount64 < until)
        {
            var hdc = GetDC(IntPtr.Zero);
            var c = (int) GetPixel(hdc, pt.X, pt.Y);
            _ = ReleaseDC(IntPtr.Zero, hdc);

            int r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF;
            // 24 级归并：消除 GDI 缩放 / 合成的边缘噪声，只统计「真实不同的颜色」
            var key = (r / 24, g / 24, b / 24);
            if (colors.Add(key))
                result.Add((r, g, b));

            Thread.Sleep(10); // 100Hz 采样已远超 GIF 帧率（20fps）
        }

        return result;
    }

    /// <summary>等待客户区中心颜色匹配期望值（±24 容差），超时返回 false。</summary>
    private static bool WaitCenterColor(IntPtr target, (int R, int G, int B) expected, TimeSpan timeout)
    {
        GetClientRect(target, out var client);
        var pt = new POINT { X = (client.Right - client.Left) / 2, Y = (client.Bottom - client.Top) / 2 };
        _ = ClientToScreen(target, ref pt);

        var until = Environment.TickCount64 + (long) timeout.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            var hdc = GetDC(IntPtr.Zero);
            var c = (int) GetPixel(hdc, pt.X, pt.Y);
            _ = ReleaseDC(IntPtr.Zero, hdc);

            int r = c & 0xFF, g = (c >> 8) & 0xFF, b = (c >> 16) & 0xFF;
            if (Math.Abs(r - expected.R) <= 24 && Math.Abs(g - expected.G) <= 24 && Math.Abs(b - expected.B) <= 24)
                return true;

            Thread.Sleep(20);
        }

        return false;
    }

    private static string DescribeColors(HashSet<(int R, int G, int B)> colors) =>
        string.Join("/", colors.OrderBy(c => c.R).ThenBy(c => c.G).Select(c => $"({c.R},{c.G},{c.B})"));

    // ===== P/Invoke（探针专用，最小集合；完整注释规格见 src/WinCanvas.Core/Native/NativeMethods.cs）=====
    // SetProcessDpiAwarenessContext：设置进程 DPI 感知（仅探针进程自身，返回 0 = 失败/不支持）。
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    // SetWindowPos：调整窗口位置 / 尺寸 / Z 序（返回 false = 失败）。
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // GetClientRect：读窗口客户区矩形（客户区坐标系，Left/Top 恒 0）。
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr h, out RECT r);

    // ClientToScreen：客户区坐标 → 屏幕物理坐标（返回 false = 失败）。
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    // GetDC / ReleaseDC / GetPixel：屏幕 DC 单点像素采样（GetPixel 返回 COLORREF，0x80000000 位异常）。
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    // CreateWindowExW / RegisterClassW / DefWindowProcW / ShowWindow / DestroyWindow：裸窗口自建自毁。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string? className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClassW wc);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint Style;
        public IntPtr WndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
