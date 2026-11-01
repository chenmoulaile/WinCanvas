using System.Runtime.InteropServices;
using System.Text;
using WinCanvas.Core.Services;

namespace Wp3RestoreDiag;

/// <summary>
/// WP3「状态重启自动恢复」验收诊断工具（对应 WORKLOG 验证证据通道）：
/// 从【外部进程】客观读出目标窗口的置顶 / 分层透明 / 穿透 / 圆角真实状态与
/// 挂接在本进程 / 目标窗口下的 WinCanvasWallpaperHost 宿主数量，
/// 与应用内 restore-diag.log 的时间戳打点互为印证（外部检查不信任被测程序的自述）。
/// 命令：
///   list                 枚举可见顶层窗口（hwnd / Key / 进程 / 类名 / 标题）；
///   check &lt;标题子串&gt;    检查目标窗口属性状态 + 宿主计数（A1 子窗口 / A2 顶层分开报）；
///   clear &lt;标题子串&gt;    把目标窗口属性复位到系统默认（每轮测试前的基线复位）；
///   hosts                统计全系统残留宿主（应用退出后应为 0）。
/// </summary>
internal static class Program
{
    // ===== 本工具自带的 user32 / dwmapi P/Invoke（仅诊断用；产品代码不含这些调用） =====
    // 注释规格与 src/WinCanvas.Core/Native/NativeMethods.cs 一致：作用 / 参数 / 返回值 / 失败语义。

    /// <summary>GWL_EXSTYLE(-20)：GetWindowLongW / SetWindowLongW 的扩展样式偏移。</summary>
    private const int GWL_EXSTYLE = -20;

    /// <summary>WS_EX_TOPMOST(0x00000008)：窗口置顶样式位。</summary>
    private const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>WS_EX_TRANSPARENT(0x00000020)：鼠标穿透样式位（命中测试跳过本窗口）。</summary>
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_LAYERED(0x00080000)：分层窗口样式位（SetLayeredWindowAttributes 的前提）。</summary>
    private const int WS_EX_LAYERED = 0x00080000;

    /// <summary>LWA_ALPHA(0x2)：GetLayeredWindowAttributes 的标志位——bAlpha 生效。</summary>
    private const int LWA_ALPHA = 0x2;

    /// <summary>SWP_NOMOVE(0x0002) | SWP_NOSIZE(0x0001) | SWP_NOACTIVATE(0x0010)：只改 Z 序不动几何不激活。</summary>
    private const uint SWP_ZORDER_ONLY = 0x0002 | 0x0001 | 0x0010;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE(33)：DWM 圆角偏好属性（Windows 11 22000+）。</summary>
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWMWCP_DONOTROUND(1)：圆角偏好 = 不圆角（系统默认值）。</summary>
    private const int DWMWCP_DONOTROUND = 1;

    /// <summary>壁纸宿主窗口的注册类名（与 Wallpaper 层 WallpaperHostWindow 一致）。</summary>
    private const string HostClassName = "WinCanvasWallpaperHost";

    /// <summary>
    /// 读窗口扩展样式。
    /// 参数：hWnd 目标窗口；nIndex = GWL_EXSTYLE。
    /// 返回：扩展样式位掩码；失败返回 0（用 GetLastError 区分，本工具只读样式不细分）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    /// <summary>
    /// 写窗口扩展样式（本工具只在 clear 命令中清除 LAYERED / TRANSPARENT 位）。
    /// 参数：hWnd 目标窗口；nIndex = GWL_EXSTYLE；dwNewLong 新样式。
    /// 返回：旧样式；失败返回 0 且 GetLastError 非 0。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// 只调整 Z 序：把窗口移到 notTopmostAfter（HWND_NOTOPMOST）之后。
    /// 参数：hWnd 目标窗口；hWndInsertAfter = HWND_NOTOPMOST(-2)（由调用方传入）；
    ///       后四个参数传 0 并带 SWP_NOMOVE|NOSIZE|NOACTIVATE 表示不改几何、不激活。
    /// 返回：成功非 0；失败 0（GetLastError 取原因）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 读分层窗口的透明参数（alpha 生效性由 flags 的 LWA_ALPHA 位指示）。
    /// 参数：hWnd 目标窗口；crKey 颜色键（本工具不关心）；bAlpha 透明度 0~255；dwFlags 生效标志。
    /// 返回：成功 true；目标不是分层窗口（未设 WS_EX_LAYERED）时失败返回 false。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hWnd,
        out int crKey, out byte bAlpha, out int dwFlags);

    /// <summary>
    /// 读 DWM 窗口属性（本工具只读 DWMWA_WINDOW_CORNER_PREFERENCE 圆角偏好）。
    /// 参数：hwnd 目标窗口；dwAttribute 属性 Id；pvAttribute 输出缓冲；cbAttribute 缓冲字节数。
    /// 返回：成功 S_OK(0)；失败为负 HRESULT（如旧系统不支持该属性）。
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute,
        out int pvAttribute, int cbAttribute);

    /// <summary>
    /// 写 DWM 窗口属性（clear 命令把圆角偏好复位为 DONOTROUND）。
    /// 参数：hwnd 目标窗口；dwAttribute 属性 Id；pvAttribute 输入值；cbAttribute 值字节数。
    /// 返回：成功 S_OK(0)；失败为负 HRESULT。
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute,
        ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// 枚举全部顶层窗口，对每个窗口调用回调直到返回 false 或枚举完。
    /// 参数：lpEnumFunc 回调；lParam 调用方上下文（本工具不使用）。
    /// 返回：枚举完成前回调未返回 false 时为 true；失败 false。
    /// </summary>
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// 枚举指定父窗口的直接子窗口（本工具用于找 A1 模式嵌入目标客户区的壁纸宿主）。
    /// 参数：hWndParent 父窗口；lpEnumFunc 回调；lParam 上下文。
    /// 返回：同 EnumWindows。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// 按类名顺序查找窗口（本工具用于扫顶层残留宿主：类名 = WinCanvasWallpaperHost）。
    /// 参数：hWndParent null = 顶层；hWndAfter null = 从头；lpszClass 类名；lpszWindow null = 不按标题过滤。
    /// 返回：命中窗口句柄；找不到返回 IntPtr.Zero。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndAfter, string? lpszClass, string? lpszWindow);

    /// <summary>
    /// 取窗口标题文本。参数：hWnd 目标；lpString 缓冲；nMaxCount 缓冲字符数。
    /// 返回：实际复制的字符数（不含终止符）；失败 0。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>
    /// 取窗口类名。参数：hWnd 目标；lpClassName 缓冲；nMaxCount 缓冲字符数。
    /// 返回：实际复制的字符数（不含终止符）；失败 0。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>
    /// 向窗口投递消息且不等待处理（本工具的 close 命令用于测试现场清理：投递 WM_CLOSE 优雅关窗）。
    /// 参数：hWnd 目标窗口；Msg 消息 Id（WM_CLOSE = 0x0010）；wParam / lParam 消息参数（WM_CLOSE 均为 0）。
    /// 返回：投递成功 true（消息已被放入目标队列即算成功）；失败 false（GetLastError 取原因）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法：Wp3RestoreDiag <list|check|clear|hosts> [标题子串]");
            return 1;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                return await ListAsync().ConfigureAwait(false);
            case "check" when args.Length >= 2:
                return await CheckAsync(args[1]).ConfigureAwait(false);
            case "clear" when args.Length >= 2:
                return await ClearAsync(args[1]).ConfigureAwait(false);
            case "hosts":
                CountHosts();
                return 0;
            case "close" when args.Length >= 2:
                // 测试现场清理：按句柄投递 WM_CLOSE 优雅关闭（不强制杀进程）
                foreach (var arg in args.Skip(1))
                {
                    var h = (IntPtr)Convert.ToInt64(arg.Replace("0x", ""), 16);
                    var okClose = PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine($"WM_CLOSE -> 0x{h.ToInt64():X}: {okClose}");
                }
                return 0;
            default:
                Console.WriteLine("未知命令。用法：Wp3RestoreDiag <list|check|clear|hosts> [标题子串]");
                return 1;
        }
    }

    /// <summary>list：枚举可见顶层窗口（复用 Core 的 WindowControlService，输出档案 Key 便于构造 config.json）。</summary>
    private static async Task<int> ListAsync()
    {
        var windows = await new WindowControlService()
            .EnumerateTopLevelWindowsAsync()
            .ConfigureAwait(false);

        foreach (var w in windows)
        {
            Console.WriteLine($"hwnd=0x{w.Hwnd.ToInt64():X}  Key=\"{w.Key}\"  {w.ProcessName}({w.ProcessId})  [{w.ClassName}]  {w.Title}");
        }

        Console.WriteLine($"-- 共 {windows.Count} 个可见顶层窗口");
        return 0;
    }

    /// <summary>
    /// check：按标题子串找目标窗口，输出其真实属性状态（置顶 / 分层 alpha / 穿透 / 圆角）
    /// 与挂接的壁纸宿主计数（A1 = 目标的子窗口；A2 = 全系统顶层宿主，需结合位置判断归属）。
    /// </summary>
    private static async Task<int> CheckAsync(string titleSubstring)
    {
        var hwnd = await FindByTitleAsync(titleSubstring).ConfigureAwait(false);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[FAIL] 未找到标题包含「{titleSubstring}」的可见顶层窗口");
            return 2;
        }

        Console.WriteLine($"== 目标窗口 hwnd=0x{hwnd.ToInt64():X} ==");

        var exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        Console.WriteLine($"WS_EX_TOPMOST   = {((exStyle & WS_EX_TOPMOST) != 0)}（置顶）");
        Console.WriteLine($"WS_EX_TRANSPARENT = {((exStyle & WS_EX_TRANSPARENT) != 0)}（鼠标穿透）");
        Console.WriteLine($"WS_EX_LAYERED   = {((exStyle & WS_EX_LAYERED) != 0)}（分层）");

        if ((exStyle & WS_EX_LAYERED) != 0
            && GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
            && (flags & LWA_ALPHA) != 0)
        {
            Console.WriteLine($"Layered alpha   = {alpha}（0 全透明 ~ 255 不透明）");
        }
        else
        {
            Console.WriteLine("Layered alpha   = 255（未设置 alpha 或未分层）");
        }

        var hr = DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out var corner, sizeof(int));
        Console.WriteLine(hr == 0
            ? $"Corner pref     = {corner}（0 默认 / 1 不圆角 / 2 大圆角 / 3 小圆角）"
            : $"Corner pref     = 不可读（HRESULT=0x{hr:X8}，系统不支持）");

        // A1 嵌入模式：宿主是目标的【子窗口】（WS_CHILD），随目标客户区铺排
        var childHosts = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (GetClassNameSafe(child) == HostClassName)
            {
                childHosts++;
                Console.WriteLine($"  [A1 宿主] hwnd=0x{child.ToInt64():X}（目标的子窗口）");
            }

            return true;
        }, IntPtr.Zero);

        // A2 跟随模式：宿主是本进程的顶层 WS_POPUP 窗口，按类名全系统计数
        //（数量大于 0 即存在跟随宿主；归属判断需结合矩形位置，此处作为存在性证据）
        CountHosts();

        Console.WriteLine($"[结果] A1 子窗口宿主 = {childHosts}；A2 顶层宿主总数见上行");
        return 0;
    }

    /// <summary>
    /// clear：把目标窗口属性复位到系统默认（每轮测试前的基线复位）——
    /// 取消置顶（HWND_NOTOPMOST）、清除 LAYERED / TRANSPARENT 样式位、圆角复位 DONOTROUND。
    /// </summary>
    private static async Task<int> ClearAsync(string titleSubstring)
    {
        var hwnd = await FindByTitleAsync(titleSubstring).ConfigureAwait(false);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[FAIL] 未找到标题包含「{titleSubstring}」的可见顶层窗口");
            return 2;
        }

        // 1) 取消置顶：HWND_NOTOPMOST(-2) 只改 Z 序（几何参数 0 + 组合标志）
        var okTop = SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, SWP_ZORDER_ONLY);

        // 2) 清除分层与穿透样式位：按位与取反（保留其他 EXSTYLE 位不动），随后回读校验生效
        var exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        _ = SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED & ~WS_EX_TRANSPARENT);
        var exAfter = GetWindowLongW(hwnd, GWL_EXSTYLE);
        var okStyle = (exAfter & WS_EX_LAYERED) == 0 && (exAfter & WS_EX_TRANSPARENT) == 0;

        // 3) 圆角复位（旧系统不支持该属性时忽略失败）
        var pref = DWMWCP_DONOTROUND;
        var hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        Console.WriteLine($"[clear] hwnd=0x{hwnd.ToInt64():X}：取消置顶={okTop}，清分层/穿透样式={okStyle}（EXSTYLE 回读=0x{exAfter:X8}），圆角复位 HRESULT=0x{hrCorner:X8}");
        return okTop ? 0 : 3;
    }

    /// <summary>hosts：统计全系统顶层残留宿主（应用退出后应为 0——WP1「退出零残留」口径的复用）。</summary>
    private static void CountHosts()
    {
        var count = 0;
        var cursor = IntPtr.Zero;
        while (true)
        {
            // FindWindowExW 从 cursor 之后继续找同类名窗口，直到枚举尽
            cursor = FindWindowExW(IntPtr.Zero, cursor, HostClassName, null);
            if (cursor == IntPtr.Zero)
            {
                break;
            }

            count++;
            Console.WriteLine($"  [顶层宿主] hwnd=0x{cursor.ToInt64():X}");
        }

        Console.WriteLine($"WinCanvasWallpaperHost 顶层窗口总数 = {count}");
    }

    /// <summary>按标题子串（不区分大小写）在可见顶层窗口中找目标（复用 Core 枚举，与产品同一口径）。</summary>
    private static async Task<IntPtr> FindByTitleAsync(string titleSubstring)
    {
        var windows = await new WindowControlService()
            .EnumerateTopLevelWindowsAsync()
            .ConfigureAwait(false);

        var hit = windows.FirstOrDefault(w =>
            w.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase));

        return hit?.Hwnd ?? IntPtr.Zero;
    }

    /// <summary>读类名的容错包装（GetClassNameW 失败返回空串，不抛异常）。</summary>
    private static string GetClassNameSafe(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }
}
