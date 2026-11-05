using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PrivacyMaskDiag;

/// <summary>
/// 隐私窗口工作包验证探针（不进 WinCanvas.sln，单独 dotnet run 运行）。
///
/// 实测两件事（单元测试无法覆盖的视觉链路）：
/// ① 方案 (a) alpha 遮蔽在 DWM 缩略图（任务栏悬停 / Alt-Tab 预览的采样机制）里是否呈现暗态——
///    目标窗口白底，分「未分层 / 分层 alpha=255 / 分层 alpha=30」三种形态各截一次
///    「注册了该窗口缩略图的预览窗」，比平均亮度；
/// ② 主窗口 XAML 遮罩在真实 WinCanvas.App 上是否生效且 Alt-Tab 类预览同步呈现——
///    启动真实 app，激活态 / 失焦态 / 重聚焦态各截一次预览窗亮度，要求遮蔽显著变暗、恢复即回亮。
///
/// 线程模型（首版踩坑记录）：
/// - 预览窗 / 目标窗由专用泵线程创建并泵消息（不泵消息的线程上创建的窗口永远不会收到
///   WM_PAINT，DWM 缩略图采样到的永远是未初始化的黑表面——首版实测亮度恒为 56.8 即此坑）；
/// - 探针主逻辑跑在调用线程上，对窗口的跨线程操作（ShowWindow / SetWindowPos /
///   SetLayeredWindowAttributes）依赖泵线程正常处理同步消息。
///
/// 前台切换统一用「最小化 → 还原」原语（还原会激活窗口，不受后台进程前台锁定限制）。
/// 所有判定输出 PASS / FAIL 行，退出码 0 / 1。
/// </summary>
internal static class Program
{
    // ===== 窗口创建 / 显示 =====
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>
    /// 声明进程 PerMonitorV2 DPI 感知（必须在任何窗口 / 坐标 API 之前调用）。
    /// 不声明的话本进程是 DPI-unaware：GetWindowRect / CreateWindowExW 坐标会被系统虚拟化，
    /// 与被测进程（WinUI PerMonitorV2，物理像素）和 DWM 缩略图（物理像素）错位，
    /// 采样亮度全部失真——首版实测的「恒定亮度」即此坑。
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>取当前前台窗口（诊断日志：验证前台切换真的移交了）。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>把窗口设为前台（受前台锁定限制，需配合 AttachThreadInput 使用）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 把调用线程的输入处理附加到另一线程（经典「解锁 SetForegroundWindow」技巧：
    /// 共享输入队列后，调用线程就像前台进程一样有权限设置前台窗口）。用完必须分离。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>把窗口内容渲染进 DC（不受遮挡与前台状态影响，直接取 DWM 合成表面）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    /// <summary>PW_RENDERFULLCONTENT(0x2)：包含 DirectComposition 内容（WinUI/UWP 必需）。</summary>
    private const uint PW_RENDERFULLCONTENT = 0x2;

    // ===== 分层窗口 =====
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // ===== DWM 缩略图（任务栏 / Alt-Tab 预览的采样机制复现） =====
    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    // ===== 屏幕采样 =====
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>创建纯色 GDI 画刷（目标窗白底用）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateSolidBrush(uint color);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight; // 正数 = 自底向上
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // 不用调色板（24bpp）
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const int SW_SHOWNORMAL = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint LWA_ALPHA = 0x2;
    private const uint DWM_TNP_RECTDESTINATION = 0x1;
    private const uint DWM_TNP_OPACITY = 0x4;
    private const uint DWM_TNP_VISIBLE = 0x8;
    private const uint WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static int _failures;
    private static IntPtr _previewHwnd;
    private static IntPtr _targetHwnd;   // 步骤 1 的被测窗口（白底，缩略图源）
    private static IntPtr _alphaProbeHwnd; // 屏幕级 alpha 合成验证窗（白底 TOPMOST，无缩略图干扰）
    private static IntPtr _appHwnd;      // 步骤 2 的 WinCanvas.App 主窗口
    private static readonly ManualResetEventSlim WindowsReady = new(false);

    private static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label} —— {detail}");
        if (!ok)
        {
            _failures++;
        }
    }

    private static int Main()
    {
        _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        IntPtr console = GetConsoleWindow();
        if (console != IntPtr.Zero)
        {
            ShowWindow(console, SW_HIDE);
        }

        try
        {
            // 泵线程：创建并养护全部自建窗口（WM_PAINT / 跨线程同步消息都归它处理）
            var pump = new Thread(PumpThreadMain) { IsBackground = true, Name = "DiagPump" };
            pump.Start();
            if (!WindowsReady.Wait(5000))
            {
                Console.WriteLine("[FAIL] 泵线程 5 秒内未建好窗口，探针终止");
                return 1;
            }

            IntPtr targetThumb = IntPtr.Zero, appThumb = IntPtr.Zero;
            Process? app = null;

            try
            {
                Console.WriteLine("== 步骤 1：DWM 缩略图对 alpha 遮蔽的呈现（方案 a 的 Alt-Tab 类预览证据） ==");
                targetThumb = RegisterThumb(_previewHwnd, _targetHwnd, 20, 20, 180, 120);
                RECT pr = PreviewRect();
                RECT thumb1 = Offset(pr, 28, 28, 164, 104); // 缩略图矩形内缩 8px，避开边框渗色

                // 形态 A：未分层白底窗口 → 缩略图应呈现白色（缩略图机制基准）
                Thread.Sleep(300);
                DumpTargetDiagnostics("未分层白底", _targetHwnd);
                SaveScreenShot(Path.Combine(Path.GetTempPath(), "priv-diag-1-plain.bmp"));
                double plainThumb = SampleRectLuminance(thumb1);
                Console.WriteLine($"      未分层白底：缩略图亮度 {plainThumb:F1}");
                Check("DWM 缩略图能采样普通窗口", plainThumb > 150, $"缩略图亮度 {plainThumb:F1}（白底应显著偏亮）");

                // 形态 B/C：目标窗分层（alpha 255 / 30）→ 读缩略图（OS 行为记录）
                SetAlpha(_targetHwnd, 255);
                Thread.Sleep(600);
                double opaqueThumb = SampleRectLuminance(thumb1);
                SetAlpha(_targetHwnd, 30);
                Thread.Sleep(600);
                double dimmedThumb = SampleRectLuminance(thumb1);
                SetAlpha(_targetHwnd, 255); // 复位
                Console.WriteLine($"      分层窗口缩略图：alpha=255 → {opaqueThumb:F1}，alpha=30 → {dimmedThumb:F1}");

                // 屏幕级 alpha 合成验证：白底分层 target 窗，采样函数内即时置顶
                // （桌面置顶层在持续重新置顶，早先置顶的窗口会被盖回去——实测踩坑）。
                SetAlpha(_targetHwnd, 255);
                Thread.Sleep(400);
                double brightSelf = SampleWindowScreenLuminance(_targetHwnd);
                SaveScreenShot(Path.Combine(Path.GetTempPath(), "priv-diag-2a-bright.bmp"));
                SetAlpha(_targetHwnd, 30);
                Thread.Sleep(400);
                double dimSelf = SampleWindowScreenLuminance(_targetHwnd);
                SaveScreenShot(Path.Combine(Path.GetTempPath(), "priv-diag-2b-dim.bmp"));
                double alphaDiff = DiffScreens(
                    Path.Combine(Path.GetTempPath(), "priv-diag-2a-bright.bmp"),
                    Path.Combine(Path.GetTempPath(), "priv-diag-2b-dim.bmp"),
                    Central(WindowRect(_targetHwnd)));
                Console.WriteLine($"      alpha 验证：alpha=255 → 屏幕亮度 {brightSelf:F1}，alpha=30 → {dimSelf:F1}，像素差 {alphaDiff:F1}");
                Check("SetLayeredWindowAttributes 使屏幕显著变暗（方案 a 屏幕效果）",
                    dimSelf < brightSelf * 0.6 || alphaDiff > 20, $"亮度 {brightSelf:F1}→{dimSelf:F1}，像素差 {alphaDiff:F1}");
                SetAlpha(_targetHwnd, 255);
                Thread.Sleep(300);

                Console.WriteLine("== 步骤 2：真实 WinCanvas.App 主窗口遮罩（激活 / 失焦 / 重聚焦） ==");
                app = LaunchApp();
                if (app is null || app.HasExited)
                {
                    Check("WinCanvas.App 启动", false, "进程未能启动");
                    return 1;
                }

                _appHwnd = WaitForAppWindow(app.Id);
                Check("WinCanvas.App 主窗口定位", _appHwnd != IntPtr.Zero, $"hwnd=0x{_appHwnd:X}");
                if (_appHwnd == IntPtr.Zero)
                {
                    return 1;
                }

                // 置顶 App：桌面上若有全屏置顶窗口（实测本机有全屏置顶的 Chrome），App 会被完全
                // 遮挡 → WinUI 对被遮挡窗口暂停合成 → 遮罩帧不渲染、缩略图不更新。置顶后可测。
                SetWindowPos(_appHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                Thread.Sleep(1200); // 等首帧渲染完成
                DumpTargetDiagnostics("App 就绪", _appHwnd);
                appThumb = RegisterThumb(_previewHwnd, _appHwnd, 20, 150, 180, 120);

                RECT pr2 = PreviewRect();
                RECT thumb2 = Offset(pr2, 28, 158, 164, 104); // 第二块缩略图区（app）

                // 激活态基线：强制把前台给 app（AttachThreadInput 解前台锁）
                ShowWindow(_appHwnd, SW_RESTORE);
                bool activated = ForceForeground(_appHwnd);
                Thread.Sleep(900);
                LogForeground("激活态");
                SaveScreenShot(Path.Combine(Path.GetTempPath(), "priv-diag-3-active.bmp"));
                (double thumbA, double contentA) = SampleAppState(thumb2);
                Console.WriteLine($"      激活态：缩略图亮度 {thumbA:F1}，App 屏幕区域亮度 {contentA:F1}（前台移交成功={activated}）");

                // 失焦：强制把前台给探针预览窗（app 失焦 → 遮罩出现）
                bool deactivated = ForceForeground(_previewHwnd);
                Thread.Sleep(900);
                LogForeground("失焦态");
                SaveScreenShot(Path.Combine(Path.GetTempPath(), "priv-diag-4-deactivated.bmp"));
                (double thumbD, double contentD) = SampleAppState(thumb2);
                Console.WriteLine($"      失焦态：缩略图亮度 {thumbD:F1}，App 屏幕区域亮度 {contentD:F1}（前台移交成功={deactivated}）");

                // 重聚焦：前台还给 app，遮罩应立即消失
                bool refocused = ForceForeground(_appHwnd);
                Thread.Sleep(900);
                LogForeground("重聚焦态");
                SaveScreenShot(Path.Combine(Path.GetTempPath(), "priv-diag-5-refocused.bmp"));
                (double thumbR, double contentR) = SampleAppState(thumb2);
                Console.WriteLine($"      重聚焦态：缩略图亮度 {thumbR:F1}，App 屏幕区域亮度 {contentR:F1}");

                // 判定改用【截图像素差分】（内容无关；亮度数值在暗色主题 + 遮罩含白字的场景不可靠）：
                string pActive = Path.Combine(Path.GetTempPath(), "priv-diag-3-active.bmp");
                string pDeact = Path.Combine(Path.GetTempPath(), "priv-diag-4-deactivated.bmp");
                string pRefocus = Path.Combine(Path.GetTempPath(), "priv-diag-5-refocused.bmp");

                RECT appRect = WindowRect(_appHwnd);
                double appDiff = DiffScreens(pActive, pDeact, Central(appRect));
                Check("主窗口失焦 → 遮罩呈现（App 区域画面显著变化）", appDiff > 10, $"激活 vs 失焦像素差 {appDiff:F1}");

                double thumbDiff = DiffScreens(pActive, pDeact, thumb2);
                Check("主窗口失焦 → Alt-Tab 类缩略图同步呈现遮罩", thumbDiff > 10, $"缩略图区域像素差 {thumbDiff:F1}");

                double restoreDiff = DiffScreens(pActive, pRefocus, Central(appRect));
                Check("主窗口重聚焦 → 遮罩即时移除（画面回到激活态）", restoreDiff < appDiff * 0.75,
                    $"激活 vs 重聚焦像素差 {restoreDiff:F1}（须显著小于失焦差 {appDiff:F1}）");

                return _failures == 0 ? 0 : 1;
            }
            finally
            {
                if (targetThumb != IntPtr.Zero) _ = DwmUnregisterThumbnail(targetThumb);
                if (appThumb != IntPtr.Zero) _ = DwmUnregisterThumbnail(appThumb);
                if (_previewHwnd != IntPtr.Zero) _ = PostMessageW(_previewHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (_targetHwnd != IntPtr.Zero) _ = PostMessageW(_targetHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (_alphaProbeHwnd != IntPtr.Zero) _ = PostMessageW(_alphaProbeHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (app is not null && !app.HasExited)
                {
                    app.Kill();
                    app.WaitForExit(3000);
                }
                app?.Dispose();
            }
        }
        finally
        {
            if (console != IntPtr.Zero)
            {
                ShowWindow(console, SW_SHOWNA);
            }
        }
    }

    /// <summary>泵线程主体：注册类 → 建预览窗与目标窗 → 通知主线程 → 泵消息。</summary>
    private static void PumpThreadMain()
    {
        try
        {
            var previewClass = new WndClassW
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(WndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "DiagPreviewWindow",
                hbrBackground = CreateSolidBrush(0x00000000), // 纯黑底：缩略图区之外的亮度恒为 0，测量不歧义
            };
            _ = RegisterClassW(ref previewClass); // 1410 = 已注册按成功处理

            var targetClass = new WndClassW
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(WndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "DiagWhiteTarget",
                hbrBackground = CreateSolidBrush(0x00FFFFFF), // 白底：亮度对比敏感
            };
            _ = RegisterClassW(ref targetClass);

            // 预览窗：黑底 + TOPMOST（采样期间不被遮挡），位置固定 (60,60)。
            // 高度 440：上半两块缩略图区（黑底利于亮度对比），下半留给 alpha 验证子窗。
            _previewHwnd = CreateWindowExW(
                0, "DiagPreviewWindow", "PrivacyMaskDiag 预览窗",
                WS_POPUP | WS_VISIBLE, 60, 60, 220, 440,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

            // 目标窗：白底、自带 WS_EX_LAYERED（SetLayeredWindowAttributes 的前提）
            _targetHwnd = CreateWindowExW(
                (uint)WS_EX_LAYERED, "DiagWhiteTarget", "DiagWhiteTarget",
                WS_POPUP | WS_VISIBLE, 700, 60, 300, 200,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

            // 屏幕级 alpha 验证：直接用白底 target 窗（分层）。独立顶层窗口会被桌面的
            // 全屏置顶层（ZCode/浏览器覆盖层，且它在持续重新置顶）盖住——实测踩坑；
            // 解法是【采样函数内即时置顶】（与预览窗一直正常的原因相同）。
            _alphaProbeHwnd = IntPtr.Zero; // 已并入 target 窗验证，不再单开

            if (_previewHwnd == IntPtr.Zero || _targetHwnd == IntPtr.Zero)
            {
                Console.WriteLine($"[FAIL] 窗口创建失败：preview={_previewHwnd} target={_targetHwnd}（Win32 错误 {Marshal.GetLastWin32Error()}）");
                return;
            }

            SetWindowPos(_previewHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            _ = ShowWindow(_previewHwnd, SW_SHOWNORMAL);
            _ = ShowWindow(_targetHwnd, SW_SHOWNORMAL);

            WindowsReady.Set();

            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref _dummy);
                DispatchMessageW(ref _dummy);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 泵线程异常：{ex.GetType().Name}: {ex.Message}");
            WindowsReady.Set();
        }
    }

    private static MSG _dummy;

    private static IntPtr RegisterThumb(IntPtr preview, IntPtr source, int x, int y, int w, int h)
    {
        int hr = DwmRegisterThumbnail(preview, source, out IntPtr thumb);
        if (hr != 0)
        {
            throw new InvalidOperationException($"DwmRegisterThumbnail 失败 HRESULT=0x{hr:X8}");
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
            rcDestination = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
            opacity = 255,
            fVisible = 1,
        };
        hr = DwmUpdateThumbnailProperties(thumb, ref props);
        if (hr != 0)
        {
            throw new InvalidOperationException($"DwmUpdateThumbnailProperties 失败 HRESULT=0x{hr:X8}");
        }

        return thumb;
    }

    private static void SetAlpha(IntPtr hwnd, byte alpha)
    {
        if (!SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA))
        {
            throw new InvalidOperationException($"SetLayeredWindowAttributes({alpha}) 失败：Win32 错误 {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// 截整个主屏并存 BMP（事后经 PowerShell 转 PNG 肉眼核验）。
    /// 数字亮度在很多遮挡/合成场景下有歧义，屏照是一锤定音的证据。
    /// </summary>
    private static void SaveScreenShot(string path)
    {
        int w = GetSystemMetricsSM(0), h = GetSystemMetricsSM(1);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = CreateCompatibleDC(screenDc);
            try
            {
                IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
                try
                {
                    _ = SelectObject(memDc, bmp);
                    _ = BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY);
                    _ = SelectObject(memDc, IntPtr.Zero);

                    var info = new BITMAPINFO();
                    info.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    info.bmiHeader.biWidth = w;
                    info.bmiHeader.biHeight = h;
                    info.bmiHeader.biPlanes = 1;
                    info.bmiHeader.biBitCount = 24;
                    info.bmiHeader.biCompression = 0;

                    var pixels = new byte[w * h * 3];
                    _ = GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref info, 0);

                    // BMP 文件：14 字节文件头 + 40 字节信息头 + 像素（自底向上）
                    int stride = (w * 3 + 3) / 4 * 4;
                    int dataSize = stride * h;
                    using var fs = new FileStream(path, FileMode.Create);
                    using var bw = new BinaryWriter(fs);
                    bw.Write((byte)'B'); bw.Write((byte)'M');
                    bw.Write(14 + 40 + dataSize);
                    bw.Write(0);
                    bw.Write(14 + 40);
                    bw.Write(40);
                    bw.Write(w); bw.Write(h);
                    bw.Write((short)1); bw.Write((short)24);
                    bw.Write(0); bw.Write(dataSize);
                    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                    bw.Write(pixels);
                }
                finally
                {
                    _ = DeleteObject(bmp);
                }
            }
            finally
            {
                _ = DeleteDC(memDc);
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>读 BMP 文件像素（24bpp 自底向上，返回 stride 对齐的原始行数据）。</summary>
    private static byte[] LoadBmpPixels(string path, out int width, out int height, out int stride)
    {
        byte[] file = File.ReadAllBytes(path);
        width = BitConverter.ToInt32(file, 18);
        height = BitConverter.ToInt32(file, 22);
        stride = (width * 3 + 3) / 4 * 4;
        int dataStart = 14 + BitConverter.ToInt32(file, 10) - 14; // bfOffBits
        int expected = stride * height;
        var pixels = new byte[expected];
        Buffer.BlockCopy(file, dataStart, pixels, 0, Math.Min(expected, file.Length - dataStart));
        return pixels;
    }

    /// <summary>
    /// 两张全屏 BMP 在指定矩形内的平均像素差（0~255/通道）。
    /// 内容无关的稳健判定：遮罩呈现 / 恢复都会造成大差异，与主题亮暗无关
    /// （数值亮度判定在暗色主题 + 遮罩含白色文字的场景下不可靠，实测踩坑）。
    /// </summary>
    private static double DiffScreens(string pathA, string pathB, RECT rect)
    {
        byte[] a = LoadBmpPixels(pathA, out int w, out int h, out int stride);
        byte[] b = LoadBmpPixels(pathB, out int w2, out int h2, out int stride2);
        if (w != w2 || h != h2)
        {
            throw new InvalidOperationException("两张截图尺寸不一致");
        }

        double total = 0;
        long count = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            if (y < 0 || y >= h)
            {
                continue;
            }

            int rowBase = (h - 1 - y) * stride;
            for (int x = rect.Left; x < rect.Right; x++)
            {
                if (x < 0 || x >= w)
                {
                    continue;
                }

                int i = rowBase + x * 3;
                total += (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3.0;
                count++;
            }
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>打印当前前台窗口归属（诊断：验证前台切换舞步真的生效）。</summary>
    private static void LogForeground(string label)
    {
        IntPtr fg = GetForegroundWindow();
        _ = GetWindowThreadProcessId(fg, out uint pid);
        string owner = pid == (uint)Environment.ProcessId
            ? "探针"
            : pid == (_appHwnd != IntPtr.Zero ? OwnerPidOf(_appHwnd) : 0)
                ? "WinCanvas.App"
                : $"pid={pid}";
        Console.WriteLine($"      [{label}] 前台窗口 0x{fg:X} 属于 {owner}");
    }

    /// <summary>诊断：窗口可见性 / 矩形 / Cloak / 最小化 / 该位置最顶层窗口归属（解释屏幕采样异常用）。</summary>
    private static void DumpTargetDiagnostics(string label, IntPtr hwnd)
    {
        bool visible = IsWindowVisible(hwnd);
        RECT r = WindowRect(hwnd);
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        IntPtr top = WindowFromPoint(new POINTW { X = cx, Y = cy });
        _ = GetWindowThreadProcessId(top, out uint pid);
        var sb = new StringBuilder(128);
        _ = GetWindowTextW(top, sb, sb.Capacity);

        // DWMWA_CLOAKED(14)：非 0 = 被 DWM 隐藏（挂起 UWP=1 / 虚拟桌面切换=2 / 其它）
        int hr = DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int));

        Console.WriteLine($"      [诊断:{label}] 可见={visible} 最小化={IsIconic(hwnd)} 矩形=({r.Left},{r.Top},{r.Right},{r.Bottom}) " +
                          $"Cloaked={(hr == 0 ? cloaked.ToString() : $"查询失败0x{hr:X8}")} " +
                          $"中心点顶层窗口=0x{top:X}（pid={pid}，标题=「{sb}」，本探针窗口={top == hwnd}） " +
                          $"主屏={GetSystemMetricsSM(0)}x{GetSystemMetricsSM(1)} 虚拟屏=({GetSystemMetricsSM(76)},{GetSystemMetricsSM(77)})+" +
                          $"{GetSystemMetricsSM(78)}x{GetSystemMetricsSM(79)}");
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int cb);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetricsSM(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTW { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINTW point);

    private static uint _appOwnerPid;

    private static uint OwnerPidOf(IntPtr hwnd)
    {
        if (_appOwnerPid == 0)
        {
            _ = GetWindowThreadProcessId(hwnd, out _appOwnerPid);
        }

        return _appOwnerPid;
    }

    /// <summary>取任意窗口的屏幕矩形（失败抛异常，调用方均为关键路径）。</summary>
    private static RECT WindowRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException($"GetWindowRect(0x{hwnd:X}) 失败");
        }

        return rect;
    }

    /// <summary>取矩形中央 60% 区域（避开边框 / 标题栏装饰）。</summary>
    private static RECT Central(RECT r) => new()
    {
        Left = r.Left + (r.Right - r.Left) * 20 / 100,
        Top = r.Top + (r.Bottom - r.Top) * 20 / 100,
        Right = r.Left + (r.Right - r.Left) * 80 / 100,
        Bottom = r.Top + (r.Bottom - r.Top) * 80 / 100,
    };

    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>
    /// 采样某窗口的屏幕区域亮度。采样前【即时重新置顶】：桌面上存在持续重新置顶的
    /// 全屏覆盖层（ZCode/浏览器），早先置顶的窗口会被盖回去；置顶与 BitBlt 之间
    /// 间隔越短越可靠（与预览窗一直采样正常的原因相同）。
    /// </summary>
    private static double SampleWindowScreenLuminance(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        return SampleRectLuminance(Central(WindowRect(hwnd)));
    }

    /// <summary>取预览窗当前屏幕矩形。</summary>
    private static RECT PreviewRect()
    {
        SetWindowPos(_previewHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE); // 确保置顶再采样
        if (!GetWindowRect(_previewHwnd, out var rect))
        {
            throw new InvalidOperationException("GetWindowRect(预览窗) 失败");
        }

        return rect;
    }

    /// <summary>基于预览窗矩形取相对偏移子矩形（缩略图测量区）。</summary>
    private static RECT Offset(RECT baseRect, int dx, int dy, int w, int h) => new()
    {
        Left = baseRect.Left + dx,
        Top = baseRect.Top + dy,
        Right = baseRect.Left + dx + w,
        Bottom = baseRect.Top + dy + h,
    };

    /// <summary>
    /// 强制把指定窗口设为前台（AttachThreadInput 解前台锁）。
    /// 返回是否成功（以前台窗口句柄是否变成目标为准）。
    /// </summary>
    private static bool ForceForeground(IntPtr target)
    {
        IntPtr current = GetForegroundWindow();
        if (current == target)
        {
            return true;
        }

        uint probeThreadId = NativeGetCurrentThreadId();
        uint currentThread = GetWindowThreadProcessId(current, out _);
        uint targetThread = GetWindowThreadProcessId(target, out _);

        bool attached1 = AttachThreadInput(probeThreadId, currentThread, true);
        bool attached2 = AttachThreadInput(probeThreadId, targetThread, true);
        try
        {
            _ = SetForegroundWindow(target);
        }
        finally
        {
            if (attached1) _ = AttachThreadInput(probeThreadId, currentThread, false);
            if (attached2) _ = AttachThreadInput(probeThreadId, targetThread, false);
        }

        return GetForegroundWindow() == target;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint NativeGetCurrentThreadId();

    /// <summary>
    /// 一次采样「App 缩略图（屏幕区域） + App 窗口屏幕中央区域」。
    /// PrintWindow(PW_RENDERFULLCONTENT) 对 WinUI(DComp) 窗口实测返回黑屏，弃用；
    /// App 处于可见态（激活或失焦但未被遮挡），屏幕采样即为其真实像素。
    /// </summary>
    private static (double Thumb, double Content) SampleAppState(RECT thumbRect)
    {
        double thumb = SampleRectLuminance(thumbRect);

        if (!GetWindowRect(_appHwnd, out var appRect))
        {
            throw new InvalidOperationException("GetWindowRect(App) 失败");
        }

        double content = SampleRectLuminance(Central(appRect));
        return (thumb, content);
    }

    /// <summary>
    /// 经 PrintWindow(PW_RENDERFULLCONTENT) 渲染窗口自身内容并算中央区域平均亮度。
    /// 不受遮挡 / 前台状态 / 其它窗口污染（桌面上的真实窗口不再影响测量）。
    /// </summary>
    private static double SampleWindowContentLuminance(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException($"GetWindowRect(0x{hwnd:X}) 失败");
        }

        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        IntPtr memDc = CreateCompatibleDC(GetDC(IntPtr.Zero));
        try
        {
            IntPtr bmp = CreateCompatibleBitmap(memDc, w, h);
            try
            {
                _ = SelectObject(memDc, bmp);
                if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT))
                {
                    throw new InvalidOperationException($"PrintWindow(0x{hwnd:X}) 失败：Win32 错误 {Marshal.GetLastWin32Error()}");
                }

                _ = SelectObject(memDc, IntPtr.Zero);

                var info = new BITMAPINFO();
                info.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                info.bmiHeader.biWidth = w;
                info.bmiHeader.biHeight = h;
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 24;
                info.bmiHeader.biCompression = 0;

                var pixels = new byte[w * h * 3];
                _ = GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref info, 0);

                double total = 0;
                long count = 0;
                int x0 = w * 20 / 100, x1 = w * 80 / 100;
                int y0 = h * 20 / 100, y1 = h * 80 / 100;
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = (h - 1 - y) * w * 3;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowBase + x * 3;
                        total += pixels[i] * 0.114 + pixels[i + 1] * 0.587 + pixels[i + 2] * 0.299;
                        count++;
                    }
                }

                return count == 0 ? 0 : total / count;
            }
            finally
            {
                _ = DeleteObject(bmp);
            }
        }
        finally
        {
            _ = DeleteDC(memDc);
        }
    }

    /// <summary>截指定屏幕矩形并算平均亮度（0~255）。</summary>
    private static double SampleRectLuminance(RECT rect)
    {
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = CreateCompatibleDC(screenDc);
            try
            {
                IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
                try
                {
                    _ = SelectObject(memDc, bmp);
                    _ = BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, SRCCOPY);
                    _ = SelectObject(memDc, IntPtr.Zero);

                    var info = new BITMAPINFO();
                    info.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    info.bmiHeader.biWidth = w;
                    info.bmiHeader.biHeight = h; // 自底向上
                    info.bmiHeader.biPlanes = 1;
                    info.bmiHeader.biBitCount = 24;
                    info.bmiHeader.biCompression = 0; // BI_RGB

                    var pixels = new byte[w * h * 3];
                    _ = GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref info, 0); // DIB_RGB_COLORS

                    double total = 0;
                    long count = 0;
                    int x0 = w * 20 / 100, x1 = w * 80 / 100;
                    int y0 = h * 20 / 100, y1 = h * 80 / 100;
                    for (int y = y0; y < y1; y++)
                    {
                        int rowBase = (h - 1 - y) * w * 3; // 自底向上翻转
                        for (int x = x0; x < x1; x++)
                        {
                            int i = rowBase + x * 3;
                            total += pixels[i] * 0.114 + pixels[i + 1] * 0.587 + pixels[i + 2] * 0.299;
                            count++;
                        }
                    }

                    return count == 0 ? 0 : total / count;
                }
                finally
                {
                    _ = DeleteObject(bmp);
                }
            }
            finally
            {
                _ = DeleteDC(memDc);
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Process? LaunchApp()
    {
        // BaseDirectory = tools/PrivacyMaskDiag/bin/Debug/net…/ → 上跳 5 层回到仓库根再进 src
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "WinCanvas.App", "bin", "x64", "Debug",
            "net10.0-windows10.0.19041.0", "win-x64", "WinCanvas.App.exe"));

        if (!File.Exists(exe))
        {
            Console.WriteLine($"      [诊断] 未找到应用 exe：{exe}");
            return null;
        }

        return Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>轮询等待应用主窗口出现（WinUI 初始化需要数百毫秒到数秒）。</summary>
    private static IntPtr WaitForAppWindow(int processId)
    {
        for (int i = 0; i < 60; i++)
        {
            IntPtr hwnd = FindAppWindow(processId);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindAppWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        bool EnumCallback(IntPtr hwnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != (uint)processId)
            {
                return true;
            }

            var sb = new StringBuilder(128);
            _ = GetWindowTextW(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "WinCanvas")
            {
                found = hwnd;
                return false;
            }

            return true;
        }

        _ = EnumWindows(EnumCallback, IntPtr.Zero);
        return found;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
}
