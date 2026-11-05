using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;
using WinCanvas.Core.Native;

namespace WinCanvas.Core.Services;

/// <summary>
/// <see cref="IWindowControlService"/> 的 Win32 实现。
/// 所有原生调用均检查返回值，失败时抛出 <see cref="Win32Exception"/>（携带 GetLastError 的错误码）。
///
/// v0.2.0 收口的五处缺陷：
/// ① 圆角失败时错误码取错：旧代码用 <c>Marshal.GetHRForLastWin32Error()</c>，
///    而 <c>DwmSetWindowAttribute</c> 是【返回 HRESULT】的 API，并不设置 LastError，
///    于是取到的是上一次别的调用留下的残留值；更糟的是它被塞进
///    <c>Win32Exception(int)</c> 后 <c>NativeErrorCode</c> 不再是 5，
///    导致 <see cref="ProcessElevation.DescribeFailure"/> 的 UIPI 专项引导
///    在圆角这条路径上【永远不会触发】。
/// ② 扩展样式读-改-写无互斥：<c>EnableClickThroughAsync</c> 与
///    <c>EnsureLayeredStyle</c>（经 <c>SetTransparencyAsync</c>）各自在独立
///    <c>Task.Run</c> 线程上做 GWL_EXSTYLE 的读-改-写，并发时后写者用【旧快照】
///    覆盖前写者 → 丢样式位（表现为「设了穿透又没了」）。
/// ③ 托管异常穿越 <c>EnumWindows</c> 的非托管回调帧（取消与进程查询都可能抛）。
/// ④ 缺少 hwnd 存活预检：句柄来自上次枚举快照，恢复管线是 200ms 间隔 + 并发 2 派发，
///    中间目标可能已销毁甚至句柄被复用给别的窗口。
/// ⑤ 枚举只按 IsWindowVisible 过滤，被 DWM cloaked 的窗口（挂起的 UWP、虚拟桌面切走的）
///    可见性仍为 true，于是列表里混进一批看不见也操作不了的框架窗口。
/// </summary>
public sealed class WindowControlService : IWindowControlService
{
    /// <summary>GetClassName 用的缓冲区长度（类名一般不超过 256 字符）。</summary>
    private const int ClassNameBufferLength = 256;

    /// <summary>ERROR_ACCESS_DENIED —— UIPI 拦截低权限进程操作高权限窗口的典型错误码。</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// 扩展样式读-改-写的互斥锁。
    ///
    /// GWL_EXSTYLE 是「读出来、改几个位、再整体写回」的复合操作，两个线程并发做时
    /// 后写者会用自己的旧快照覆盖前写者的改动，丢掉对方的样式位。锁的粒度取全局而非
    /// 每窗口：单次临界区只有 1~2 个 P/Invoke（微秒级），而跨窗口并发本来就不是热点，
    /// 用一把锁换来「不可能丢样式位」这个确定性，是划算的。
    /// </summary>
    private static readonly object _exStyleGate = new();

    /// <inheritdoc/>
    public Task<IReadOnlyList<WindowInfo>> EnumerateTopLevelWindowsAsync(CancellationToken cancellationToken = default)
    {
        // EnumWindows 是同步阻塞调用，放到线程池后台线程执行，避免卡住 UI 线程。
        return Task.Run<IReadOnlyList<WindowInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new List<WindowInfo>();

            // 缺陷③的修法：回调【一律不抛】。托管异常穿过 user32 的非托管枚举帧
            // 会让整个进程被 C++ 帧的异常派发策略处置（通常直接终止），
            // 而且 result 已半填、调用方拿不到任何有意义的错误。
            // 取消改成置标志 + 返回 false 提前结束，回到托管帧之后再抛。
            bool aborted = false;

            bool callback(IntPtr hwnd, IntPtr lParam)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    return false; // 终止枚举
                }

                // 过滤：只保留可见的顶层窗口（任务栏/隐藏窗口等被排除）。
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true; // 继续枚举下一个窗口
                }

                // 缺陷⑤：被 DWM cloaked 的窗口 IsWindowVisible 仍为 true
                // （挂起的 UWP/WinUI 应用、被虚拟桌面切走的窗口），
                // 它们对用户不可见也无法正常操作，留在列表里只是噪声。
                // DwmGetWindowAttribute 失败按「未 cloaked」处理：
                // 宁可多列一项，也不要因为一个属性查不到就把窗口整个藏掉。
                if (IsCloaked(hwnd))
                {
                    return true;
                }

                // 读取标题：先取长度，再按长度分配缓冲区（+1 为结尾 '\0'）。
                int titleLength = NativeMethods.GetWindowTextLength(hwnd);
                var titleBuilder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString();

                // 过滤：无标题的可见窗口（多为消息辅助窗、工具窗）不进入列表。
                if (title.Length == 0)
                {
                    return true;
                }

                // 读取类名（固定 256 字符缓冲区足够容纳所有常见类名）。
                var classBuilder = new StringBuilder(ClassNameBufferLength);
                NativeMethods.GetClassName(hwnd, classBuilder, classBuilder.Capacity);
                string className = classBuilder.ToString();

                // 读取进程 Id，再经 Process 查询进程名；目标进程可能刚好退出，失败则跳过。
                uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
                _ = threadId; // 本程序不使用线程 Id

                string? processName = ResolveProcessName(processId);
                if (processName is null)
                {
                    return true; // 进程已退出或无权读取，跳过该窗口
                }

                result.Add(new WindowInfo(hwnd, title, className, processName, (int)processId));
                return true; // 继续枚举
            }

            bool ok = NativeMethods.EnumWindows(callback, IntPtr.Zero);

            // 取消优先于「EnumWindows 返回 false」的失败判定：
            // 回调返回 false 主动终止时 EnumWindows 也会返回 false，那是正常路径不是错误。
            if (aborted || cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows 枚举顶层窗口失败。");
            }

            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// pid → 进程名的本轮缓存。
    ///
    /// 为什么需要：一个进程往往有多个顶层窗口（浏览器每个标签窗口、
    /// 资源管理器每个文件夹都是同一 pid），旧实现对【每个窗口】都调一次
    /// <c>Process.GetProcessById</c> —— 它要开进程句柄并读模块信息，
    /// 百窗口级枚举里这是最贵的一步，而结果高度重复。
    /// 缓存按实例（非 static）：进程名在程序运行期可能因进程退出而失效，
    /// 跨枚举复用会拿到陈旧值；单次枚举内复用则完全安全。
    /// </summary>
    private readonly ConcurrentDictionary<uint, string?> _processNameCache = new();

    /// <summary>
    /// 取进程名；失败返回 null（调用方据此跳过该窗口）。
    /// 缺陷③的另一半：这里【绝不抛】——它运行在非托管回调帧内。
    /// </summary>
    private string? ResolveProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        if (_processNameCache.TryGetValue(processId, out string? cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            // ArgumentException（进程已退出）、InvalidOperationException（读不到名字）、
            // Win32Exception（OpenProcess 被拒，例如目标是以更高权限运行的系统进程）
            // 都归为「这个窗口无法标注进程名」→ 跳过。
            // 旧实现只捕了前两种，Win32Exception 会从非托管回调里逃出去。
            name = null;
        }

        _processNameCache[processId] = name;
        return name;
    }

    /// <summary>判断窗口是否被 DWM cloaked（见枚举处的说明）。查询失败按未 cloaked 处理。</summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        int hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));

        return hr == 0 && cloaked != 0;
    }

    /// <inheritdoc/>
    public Task<bool> ToggleTopmostAsync(IntPtr hwnd)
    {
        return Task.Run(() =>
        {
            // 缺陷④：先确认句柄仍然有效，再动它。
            EnsureWindowAlive(hwnd);

            // 先读当前扩展样式，判断窗口是否已在置顶组（WS_EX_TOPMOST）。
            int exStyle = ReadExStyle(hwnd);
            bool isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;

            IntPtr insertAfter = isTopmost ? NativeMethods.HWND_NOTOPMOST : NativeMethods.HWND_TOPMOST;

            // SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE：只改 Z 序，不动位置尺寸、不抢焦点。
            uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
            if (!NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetWindowPos 切换窗口 0x{hwnd:X} 置顶状态失败。");
            }

            return !isTopmost; // 切换后的置顶状态
        });
    }

    /// <inheritdoc/>
    public Task<bool> GetIsTopmostAsync(IntPtr hwnd)
    {
        return Task.Run(() =>
        {
            // 读取扩展样式并检查 WS_EX_TOPMOST 置顶位（GetWindowLong 是同步调用，放线程池避免卡 UI）。
            int exStyle = ReadExStyle(hwnd);
            return (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        });
    }

    /// <inheritdoc/>
    public Task SetTopmostAsync(IntPtr hwnd, bool topmost)
    {
        return Task.Run(() =>
        {
            EnsureWindowAlive(hwnd);

            // 幂等语义：按目标状态直接把窗口插入 Z 序的置顶组 / 非置顶组。
            IntPtr insertAfter = topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;

            // SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE：只改 Z 序，不动位置尺寸、不抢焦点。
            uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
            if (!NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"SetWindowPos 设置窗口 0x{hwnd:X} 置顶状态({topmost})失败。");
            }
        });
    }

    /// <inheritdoc/>
    public Task SetTransparencyAsync(IntPtr hwnd, byte alpha)
    {
        return Task.Run(() =>
        {
            EnsureWindowAlive(hwnd);

            // 确保窗口带 WS_EX_LAYERED 样式（SetLayeredWindowAttributes 的前提）。
            EnsureLayeredStyle(hwnd);

            // LWA_ALPHA：按 bAlpha 做整体透明度，0 全透明 ~ 255 不透明。
            if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetLayeredWindowAttributes 设置窗口 0x{hwnd:X} 透明度 {alpha} 失败。");
            }
        });
    }

    /// <inheritdoc/>
    public Task EnableClickThroughAsync(IntPtr hwnd, bool enable)
    {
        return Task.Run(() =>
        {
            // hwnd 存活预检只对【启用】方向做。关闭方向被刻意豁免：
            // 「仅清穿透位」在句柄无效时读到的旧样式本就不含该位 → 新旧相等 →
            // 按「已处于目标状态」短路返回。这是既有测试
            // （WindowControlServiceFailureTests.EnableClickThroughAsync_关闭_零句柄_按设计无操作不抛）
            // 明确钉住的幂等语义 —— 注释写着「防将来误改成无效句柄也报错」。
            // 道理也站得住：目标窗口都没了，「取消穿透」这件事自然已经完成，
            // 报错只会让退出/清理路径凭空多出失败提示。
            if (enable)
            {
                EnsureWindowAlive(hwnd);
            }

            // WS_EX_TRANSPARENT 只有在 WS_EX_LAYERED 同时存在时才会生效：
            // - 启用穿透：两个样式位一并置位；
            // - 关闭穿透：只清 WS_EX_TRANSPARENT，保留 WS_EX_LAYERED ——
            //   透明度（SetTransparencyAsync）依赖分层样式，若一并清掉会把用户已设置的
            //   透明效果一起抹掉，导致档案值与窗口真实状态不一致。
            //
            // 缺陷②：整段读-改-写必须在 _exStyleGate 内 —— 与 EnsureLayeredStyle
            // 并发时，旧实现会用各自的旧快照互相覆盖，丢掉对方的样式位。
            lock (_exStyleGate)
            {
                int oldExStyle = NativeMethods.GetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
                int newExStyle = enable
                    ? oldExStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED // 同时置位
                    : oldExStyle & ~NativeMethods.WS_EX_TRANSPARENT; // 仅清穿透位，保留 LAYERED

                if (newExStyle == oldExStyle)
                {
                    return; // 样式已处于目标状态，无需调用 API
                }

                SetExStyleLocked(hwnd, newExStyle);
            }
        });
    }

    // ===== 窗口几何（窗口位置记忆工作包新增） =====

    /// <inheritdoc/>
    public Task<WindowBounds> GetWindowBoundsAsync(IntPtr hwnd)
    {
        return Task.Run(() =>
        {
            EnsureWindowAlive(hwnd);
            return ReadWindowBoundsOrThrow(hwnd);
        });
    }

    /// <inheritdoc/>
    public Task SetWindowBoundsAsync(IntPtr hwnd, WindowBounds bounds)
    {
        return Task.Run(() =>
        {
            EnsureWindowAlive(hwnd);

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                // 尺寸非正是档案损坏的特征值：与其把窗口压成 0×0，不如让调用方拿到明确失败
                throw new ArgumentException(
                    $"目标矩形尺寸非法（{bounds.Width}×{bounds.Height}），已拒绝应用。", nameof(bounds));
            }

            // SWP_NOZORDER|SWP_NOACTIVATE：只动位置尺寸，不重排 Z 序、不抢焦点——
            // 恢复几何是「把窗口放回原处」，不该顺带改变它的层级或打断用户手头操作。
            if (!NativeMethods.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"SetWindowPos 应用窗口 0x{hwnd:X} 几何 {bounds} 失败。");
            }
        });
    }

    /// <inheritdoc/>
    public Task<WindowMinMaxState> GetMinMaxStateAsync(IntPtr hwnd)
    {
        return Task.Run(() => ReadMinMaxState(hwnd));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WindowBounds>> GetMonitorRectsAsync()
    {
        return Task.Run<IReadOnlyList<WindowBounds>>(() =>
        {
            var monitors = new List<WindowBounds>();

            // 回调一律不抛（与 EnumWindows 的纪律相同）：托管异常穿过 user32 的
            // 非托管枚举帧可能直接终止进程；失败置标志、回托管帧后再抛。
            bool callback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT rect, IntPtr dwData)
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(), // 不填 cbSize API 直接拒绝
                };

                if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                {
                    monitors.Add(new WindowBounds(
                        info.rcMonitor.Left, info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top));
                }

                // 单块显示器信息读失败不终止枚举：少一块只影响屏幕外判定的保守程度，
                // 全枚举失败才在下方抛出。
                return true;
            }

            if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors 枚举显示器失败。");
            }

            return monitors;
        });
    }

    /// <inheritdoc/>
    public WindowBounds? TryGetWindowBoundsSync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return null;
        }

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <inheritdoc/>
    public WindowMinMaxState GetMinMaxStateSync(IntPtr hwnd) => ReadMinMaxState(hwnd);

    /// <summary>
    /// GetWindowRect 的失败检查（Async 版共用）：失败抛 Win32Exception（与既有失败语义一致）。
    /// </summary>
    private static WindowBounds ReadWindowBoundsOrThrow(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetWindowRect 读取窗口 0x{hwnd:X} 矩形失败。");
        }

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>IsIconic / IsZoomed 的统一读取（句柄无效两个标志均为 false，与 Win32 语义一致）。</summary>
    private static WindowMinMaxState ReadMinMaxState(IntPtr hwnd) =>
        new(NativeMethods.IsIconic(hwnd), NativeMethods.IsZoomed(hwnd));

    /// <inheritdoc/>
    public Task SetRoundedCornersAsync(IntPtr hwnd, bool enableSmallRadius = false)
    {
        // enableSmallRadius=false -> 标准大圆角(DWMWCP_ROUND=2)；true -> 小圆角(DWMWCP_ROUNDSMALL=3)。
        int preference = enableSmallRadius ? NativeMethods.DWMWCP_ROUNDSMALL : NativeMethods.DWMWCP_ROUND;
        return SetCornerPreferenceCoreAsync(hwnd, preference);
    }

    /// <inheritdoc/>
    public Task SetCornerPreferenceAsync(IntPtr hwnd, CornerPreference preference)
    {
        // 模型枚举的数值与 DWMWCP_* 一致：Default=0(交由 DWM 默认)、DoNotRound=1、Round=2、RoundSmall=3。
        int preferenceValue = (int)preference;
        return SetCornerPreferenceCoreAsync(hwnd, preferenceValue);
    }

    /// <summary>
    /// DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE) 的公共实现。
    /// </summary>
    private static Task SetCornerPreferenceCoreAsync(IntPtr hwnd, int preferenceValue)
    {
        return Task.Run(() =>
        {
            EnsureWindowAlive(hwnd);

            int hr = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, // 属性 Id = 33
                ref preferenceValue,
                sizeof(int)); // 整数属性的字节数

            if (hr < 0) // HRESULT 为负表示失败
            {
                // 缺陷①的修法：DwmSetWindowAttribute 是【返回 HRESULT】的 API，
                // 它不设置 LastError，所以旧代码的 Marshal.GetHRForLastWin32Error()
                // 取到的是别的调用留下的残留值。
                //
                // HRESULT 若属于 FACILITY_WIN32（0x8007xxxx），其低 16 位就是原 Win32 错误码，
                // 取出来交给 Win32Exception 才能让 NativeErrorCode == 5 成立 ——
                // 这正是 ProcessElevation 判定「UIPI 拒绝访问、请提权重试」的唯一依据。
                // 旧写法使这条引导在圆角路径上永不触发。
                int win32Code = hr & 0x0000FFFF;

                throw new Win32Exception(win32Code,
                    $"DwmSetWindowAttribute 设置窗口 0x{hwnd:X} 圆角偏好({preferenceValue})失败，HRESULT=0x{hr:X8}" +
                    (win32Code == ErrorAccessDenied ? "（拒绝访问：目标窗口权限更高，UIPI 拦截）" : string.Empty) + "。");
            }
        });
    }

    /// <summary>
    /// 确保目标窗口带 WS_EX_LAYERED 样式；没有则补上。
    /// 与 <see cref="EnableClickThroughAsync"/> 共用 <see cref="_exStyleGate"/>。
    /// </summary>
    private static void EnsureLayeredStyle(IntPtr hwnd)
    {
        lock (_exStyleGate)
        {
            int exStyle = NativeMethods.GetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
            if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
            {
                SetExStyleLocked(hwnd, exStyle | NativeMethods.WS_EX_LAYERED);
            }
        }
    }

    /// <summary>读扩展样式（统一走这里，便于将来加日志或校验）。</summary>
    private static int ReadExStyle(IntPtr hwnd)
    {
        return NativeMethods.GetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
    }

    /// <summary>
    /// 写回 GWL_EXSTYLE，并按 Win32 约定检查失败：SetWindowLongPtr 返回 0 且
    /// GetLastError 非 0 才视为失败（旧值恰好为 0 是合法情况）。
    /// 【调用方必须已持 <see cref="_exStyleGate"/>】。
    /// </summary>
    private static void SetExStyleLocked(IntPtr hwnd, int newExStyle)
    {
        Marshal.SetLastSystemError(0); // 清零后调用，便于区分"旧值为 0"与"真失败"
        IntPtr previous = NativeMethods.SetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newExStyle));

        if (previous == IntPtr.Zero)
        {
            int error = Marshal.GetLastSystemError();
            if (error != 0)
            {
                throw new Win32Exception(error, $"SetWindowLongPtr 设置窗口 0x{hwnd:X} 扩展样式 0x{newExStyle:X} 失败。");
            }
        }
    }

    /// <summary>
    /// hwnd 存活预检（缺陷④）。目标已销毁时抛 <see cref="Win32Exception"/>
    /// 而不是让 Win32 调用打到废句柄上——后者可能静默作用在【复用了该句柄值的新窗口】上，
    /// 把置顶/穿透/透明加到一个用户根本没选的窗口里。
    /// </summary>
    /// <exception cref="Win32Exception">句柄已失效（Win32 错误 1400 = ERROR_INVALID_WINDOW_HANDLE）。</exception>
    private static void EnsureWindowAlive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            throw new Win32Exception(1400,
                hwnd == IntPtr.Zero
                    ? "窗口句柄为空，操作已取消。"
                    : $"窗口 0x{hwnd:X} 已关闭，操作已取消（不会把属性打到复用该句柄值的其它窗口上）。");
        }
    }
}
