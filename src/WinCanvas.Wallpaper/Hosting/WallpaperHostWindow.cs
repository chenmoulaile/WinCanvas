using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.Playback;
using WinCanvas.Wallpaper.Rendering;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 壁纸宿主窗口：每个壁纸会话拥有一个、属于本进程的 Win32 窗口，是壁纸画面的真正承载面。
/// 阶段 A 以 GDI 纯色填充验证「挂接正确」（阶段 B 将在其上构建 DComp / Direct2D 渲染，
/// 纯色填充届时移除，窗口类与挂接逻辑保留复用）。
///
/// 双路径挂接（降级规则见 <see cref="HostAttachMode"/>）：
/// - A1 <see cref="HostAttachMode.Embed"/>：CreateWindowExW 以 WS_CHILD 直接以目标窗口为父创建
///   （跨进程，等效 SetParent），尺寸 = GetClientRect(target)，创建后立即沉到目标子窗口 Z 序底部
///   （HWND_BOTTOM）——目标既有控件（编辑框等兄弟）位于其上继续可点，目标自绘客户区背景被覆盖。
///   子窗口随父移动自动跟随，钩子只需处理客户区尺寸变化（LOCATIONCHANGE）与 Z 序被调整（REORDER）。
/// - A2 <see cref="HostAttachMode.Follow"/>：WS_POPUP 顶层窗口 + NOACTIVATE|TOOLWINDOW|LAYERED|TRANSPARENT，
///   跟随目标外接矩形（优先 DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)，失败回退 GetWindowRect），
///   Z 序钉在目标正下方；监听 SHOW/HIDE/MINIMIZESTART/END 同步显隐。
///
/// 线程模型：全部原生句柄操作（创建 / 销毁 / SetWindowPos / 钩子装卸）一律在
/// <see cref="WallpaperHostPump"/> 专用消息线程上串行执行——跨线程 DestroyWindow 非法、
/// WINEVENT_OUTOFCONTEXT 回调要求安装线程跑消息循环，两者由 pump 一并满足。
/// 因此本类内部不再加锁：句柄字段的读写天然被 pump 串行化；跨线程可见性通过事件传出
/// （事件在 pump 线程触发，订阅方自行封送）。
/// </summary>
internal sealed class WallpaperHostWindow : IDisposable
{
    /// <summary>宿主窗口类名（规格固定值；DoD 验证按此名枚举计数残留）。</summary>
    public const string WindowClassName = "WinCanvasWallpaperHost";

    /// <summary>A1 挂接后的焦点异常观察窗（毫秒）。观察语义比规格的「30 秒观察期」更保守：
    /// 跨进程 SetParent 的输入队列问题若发生，几乎都在挂接初期显现，但持续监测只是一个事件过滤分支，
    /// 没有理由在 30s 后关闭监测——因此焦点异常在任何时刻都触发降级（决策记录在 WORKLOG）。</summary>
    private const long FocusObservationMs = 30_000;

    /// <summary>窗口类是否已注册（进程级一次）。由 <see cref="_classGate"/> 保护。</summary>
    private static bool _classRegistered;

    /// <summary>保护窗口类注册竞态的锁（并发首建会话时只允许注册一次）。</summary>
    private static readonly object _classGate = new();

    /// <summary>WndProc 委托的常驻引用（防 GC 回收导致原生回调悬空；进程生命周期内持有）。</summary>
    private static readonly HostNativeMethods.WndProc WndProcKeepAlive = WndProc;

    /// <summary>纯色填充画刷（阶段 A 验证用；进程级缓存一个 GDI 画刷，进程退出统一回收）。
    /// 由 <see cref="_fillBrushGate"/> 保护，惰性创建。</summary>
    private static IntPtr _fillBrush;

    /// <summary>保护 <see cref="_fillBrush"/> 惰性创建的锁。</summary>
    private static readonly object _fillBrushGate = new();

    /// <summary>目标窗口句柄（挂接对象）。只读。</summary>
    public IntPtr TargetHwnd { get; }

    /// <summary>宿主窗口句柄；未创建成功 / 已销毁为 IntPtr.Zero。pump 线程外读取仅为诊断参考。</summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>当前挂接模式；降级后由 pump 线程改写，跨线程读取为诊断参考（变化经事件传出）。</summary>
    public HostAttachMode Mode => _mode;

    /// <summary>
    /// 宿主是否已进入销毁流程。volatile 读，供服务层在复用旧宿主前快速判断
    /// （TargetLost 清理与壁纸更新存在无锁协同的窄窗口，见 WallpaperService.UpdateCoreAsync）。
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 挂接模式变化（仅 A1 降级 A2 时触发；创建时的初始模式由调用方创建完成后读
    /// <see cref="Mode"/> 获得，事件订阅晚于创建，初始值不走事件）。pump 线程触发；
    /// 参数为（目标 hwnd, 新模式）。订阅方（WallpaperService）转发 UI 层并自行封送。
    /// </summary>
    public event Action<IntPtr, HostAttachMode>? AttachModeChanged;

    /// <summary>
    /// 目标窗口已销毁（EVENT_OBJECT_DESTROY 或宿主随父销毁收到 WM_DESTROY）。
    /// pump 线程触发；服务据此 Dispose 会话并销毁宿主（WM_DESTROY 与事件双信号由本类
    /// <see cref="_targetLostRaised"/> 去重，服务层再按会话字典判重兜底）。
    /// </summary>
    public event Action<IntPtr>? TargetLost;

    /// <summary>宿主窗口句柄（pump 线程内读写；pump 外读取仅诊断参考）。</summary>
    private IntPtr _hwnd;

    /// <summary>当前挂接模式（pump 线程内读写）。</summary>
    private HostAttachMode _mode;

    /// <summary>已安装的 WinEvent 钩子句柄（pump 线程内读写；卸载时逐一 UnhookWinEvent）。</summary>
    private readonly List<IntPtr> _hooks = new();

    /// <summary>WinEventProc 委托的常驻引用（防 GC；钩子句柄持有的原生函数指针指向它）。</summary>
    private readonly HostNativeMethods.WineventProc _wineventKeepAlive;

    /// <summary>目标窗口的创建线程 Id（钩子过滤参数，创建时确定后只读）。</summary>
    private readonly uint _targetThreadId;

    /// <summary>目标窗口的进程 Id（钩子过滤参数，创建时确定后只读）。</summary>
    private readonly uint _targetProcessId;

    /// <summary>A1 挂接建立时刻（TickCount64 毫秒；焦点异常观察记录用）。</summary>
    private long _embedSinceMs;

    /// <summary>是否已进入销毁流程。volatile：pump 线程写、任意线程读，必须及时可见。</summary>
    private volatile bool _disposed;

    /// <summary>TargetLost 是否已上报（WM_DESTROY 与 EVENT_OBJECT_DESTROY 双信号去重；pump 线程内读写）。</summary>
    private bool _targetLostRaised;

    /// <summary>WM_NCCREATE 是否已把实例 GCHandle 收养进 GWLP_USERDATA（决定创建失败时由谁释放）。</summary>
    private bool _handleAdopted;

    /// <summary>创建参数 GCHandle（pump 线程内读写）：WM_NCCREATE 收养后由 WM_NCDESTROY 释放。</summary>
    private GCHandle _createHandle;

    /// <summary>DComp 渲染器（WP1-B，pump 线程内读写）：首次 RenderImage 时惰性创建，
    /// 与宿主同生命周期；尺寸变化回调（SyncEmbedClientSize / SyncFollowBounds）也在 pump 线程直呼。</summary>
    private WallpaperHostRenderer? _renderer;

    /// <summary>视频组合呈现器（WP1-C，pump 线程内读写）：视频会话经 BeginVideo 惰性创建，
    /// EndVideo / Dispose 释放；与 DComp 渲染器各持各的合成目标，按壁纸类型互斥生效。</summary>
    private CompositionVideoPresenter? _videoPresenter;

    private WallpaperHostWindow(IntPtr target, uint targetThreadId, uint targetProcessId)
    {
        TargetHwnd = target;
        _targetThreadId = targetThreadId;
        _targetProcessId = targetProcessId;
        // 绑定实例的委托（target = this），钩子回调经它路由到本实例
        _wineventKeepAlive = OnWinEvent;
    }

    /// <summary>
    /// 创建宿主并挂接到目标窗口（同步阻塞，内部经 pump 线程往返；调用方必须在工作线程，
    /// 本方法自身不做线程检查——既有的 UpdateWallpaperAsync 已把调用方固定在线程池）。
    /// 先尝试 A1，失败自动降级 A2，两者都失败返回 null。
    /// </summary>
    /// <param name="target">目标窗口句柄。</param>
    /// <param name="error">输出：失败原因（成功为 null，中文，含 Win32 错误码释义）。</param>
    /// <returns>宿主实例；两路径都失败返回 null。</returns>
    public static WallpaperHostWindow? TryCreate(IntPtr target, out string? error)
    {
        return TryCreate(target, HostAttachMode.Embed, out error);
    }

    /// <summary>
    /// 创建宿主并挂接到目标窗口（同步阻塞，内部经 pump 线程往返；调用方必须在工作线程，
    /// 本方法自身不做线程检查——既有的 UpdateWallpaperAsync 已把调用方固定在线程池）。
    /// preferred = Embed：先 A1，失败自动降级 A2（产品常规路径）；preferred = Follow：直接 A2
    /// （诊断与降级可达性验证用——规格 WP1-D 要求构造拒绝 SetParent 的场景验证降级路径）。
    /// </summary>
    /// <param name="target">目标窗口句柄。</param>
    /// <param name="preferred">首选挂接路径。</param>
    /// <param name="error">输出：失败原因（成功为 null，中文，含 Win32 错误码释义）。</param>
    /// <returns>宿主实例；两路径都失败返回 null。</returns>
    public static WallpaperHostWindow? TryCreate(IntPtr target, HostAttachMode preferred, out string? error)
    {
        error = null;

        try
        {
            RegisterClassOnce();
        }
        catch (Exception ex)
        {
            error = $"注册宿主窗口类失败：{ex.Message}";
            return null;
        }

        if (!WallpaperHostPump.Shared.EnsureStarted())
        {
            error = "宿主消息线程启动失败。";
            return null;
        }

        // 创建前置信息在线程池线程读取（GetWindowThreadProcessId 是无阻塞查询）
        var threadId = HostNativeMethods.GetWindowThreadProcessId(target, out var processId);
        if (threadId == 0)
        {
            error = "目标窗口句柄无效（GetWindowThreadProcessId 返回 0），无法挂接。";
            return null;
        }

        var host = new WallpaperHostWindow(target, threadId, processId);
        string? createError = null;
        var created = false;

        // 原生创建全部在 pump 线程执行（窗口归属该线程；15s 超时防目标线程假死把调用方拖死）
        var done = WallpaperHostPump.Shared.Execute(() =>
        {
            try
            {
                created = host.CreateNative(preferred, out createError);
            }
            catch (Exception ex)
            {
                // CreateNative 内部已兜尽常规失败；此处兜底任何意外异常，保证 caller 拿到明确错误
                createError = $"宿主创建过程出现未预期异常：{ex.GetType().Name}: {ex.Message}";
                created = false;
            }
        }, timeoutMs: 15000);

        if (!done || !created)
        {
            error = createError ?? "宿主窗口创建超时或任务投递失败（pump 线程无响应）。";
            host.Dispose(); // 兜底：若 pump 任务实际仍在执行，Dispose 的清理任务会在其后完成销毁
            return null;
        }

        HostDiagnostics.Log($"宿主创建成功 target=0x{target:X} mode={host._mode} host=0x{host._hwnd:X}");
        return host;
    }

    /// <summary>
    /// 统计当前存活的宿主窗口数量（DoD 验证：目标关闭 / 应用退出后按类名计数应归零）。
    /// 枚举口径：EnumWindows（顶层）+ EnumChildWindows（递归后代）——A2 宿主是顶层窗口，
    /// A1 宿主是目标的 WS_CHILD 子窗口，两者都要数到才算完整。
    /// </summary>
    /// <param name="processIdFilter">
    /// 进程 Id 过滤：null = 全系统按类名计数（规格验收口径；多应用实例共存时会把彼此的宿主一起计入，
    /// 验收时保持单实例运行即可）；指定 Id 时只统计该进程的宿主。
    /// </param>
    /// <returns>匹配的窗口数（顶层 + 全部后代子窗口中类名为 <see cref="WindowClassName"/> 者）。</returns>
    public static int CountAliveHosts(int? processIdFilter = null)
    {
        var count = 0;

        bool countIfMatch(IntPtr hwnd, IntPtr lParam)
        {
            if (processIdFilter is { } pid
                && HostNativeMethods.GetWindowThreadProcessId(hwnd, out var ownerPid) != 0
                && ownerPid != pid)
            {
                return true; // 非目标进程的窗口，继续
            }

            var builder = new StringBuilder(256);
            HostNativeMethods.GetClassNameW(hwnd, builder, builder.Capacity);
            if (builder.ToString() == WindowClassName)
                count++;

            return true; // 继续枚举
        }

        bool topLevelCallback(IntPtr hwnd, IntPtr lParam)
        {
            countIfMatch(hwnd, lParam);
            // 递归枚举后代：覆盖 A1 形态（WS_CHILD 宿主不进 EnumWindows 顶层序列）
            HostNativeMethods.EnumChildWindows(hwnd, countIfMatch, lParam);
            return true; // 继续枚举
        }

        HostNativeMethods.EnumWindows(topLevelCallback, IntPtr.Zero);
        return count;
    }

    /// <summary>
    /// 注册宿主窗口类（进程级一次；ERROR_CLASS_ALREADY_EXISTS(1410) 按已注册处理）。
    /// </summary>
    private static void RegisterClassOnce()
    {
        lock (_classGate)
        {
            if (_classRegistered)
                return;

            var wc = new HostNativeMethods.WNDCLASSW
            {
                style = 0, // 绘制完全自管（WM_PAINT/WM_ERASEBKGND 填充），无需 CS_HREDRAW/CS_VREDRAW 自动重绘
                lpfnWndProc = WndProcKeepAlive,
                hInstance = HostNativeMethods.GetModuleHandleW(null),
                lpszClassName = WindowClassName,
            };

            var atom = HostNativeMethods.RegisterClassW(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410) // 1410 = ERROR_CLASS_ALREADY_EXISTS：并发注册被抢先，视为成功
                    throw new System.ComponentModel.Win32Exception(err, $"RegisterClassW({WindowClassName}) 失败。");
            }

            _classRegistered = true;
        }
    }

    /// <summary>
    /// pump 线程上的原生创建主流程：首选路径 → 失败降级 → 都失败报错（调用方已捕获异常）。
    /// </summary>
    private bool CreateNative(HostAttachMode preferred, out string? error)
    {
        if (_disposed)
        {
            error = "宿主在创建前已被释放。";
            return false;
        }

        if (!HostNativeMethods.IsWindow(TargetHwnd))
        {
            error = "目标窗口已销毁，无法挂接。";
            return false;
        }

        string? embedError = null;

        // 路径 A1：跨进程子窗口嵌入（首选；preferred = Follow 时跳过——诊断 / 降级验证直取 A2）
        if (preferred == HostAttachMode.Embed)
        {
            if (TryCreateEmbed(out embedError))
            {
                if (InstallHooks())
                {
                    error = null;
                    return true;
                }

                // 钩子装不上（目标进程受保护且全局钩子也被拒）：半挂接状态不可接受，销毁宿主走 A2
                HostDiagnostics.Log($"A1 钩子安装失败 target=0x{TargetHwnd:X}，销毁嵌入宿主改试 A2");
                HostNativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            else
            {
                HostDiagnostics.Log($"A1 嵌入失败 target=0x{TargetHwnd:X} 原因：{embedError}，降级 A2 跟随模式");
            }
        }

        // 路径 A2：跟随窗口（回退）
        if (TryCreateFollow(out var followError))
        {
            if (InstallHooks())
            {
                error = null;
                return true;
            }

            HostDiagnostics.Log($"A2 钩子安装失败 target=0x{TargetHwnd:X}，挂接放弃");
            HostNativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            error = "WinEvent 钩子安装失败（跟随模式下宿主无法跟随目标），挂接放弃。";
            return false;
        }

        error = $"嵌入模式失败：{embedError ?? "（首选路径即为 A2，未尝试）"}；跟随模式失败：{followError}";
        return false;
    }

    /// <summary>
    /// 路径 A1：跨进程子窗口嵌入（两步挂接），沉底并激活分层合成。
    /// 实现说明：规格字面的「CreateWindowExW 以 WS_CHILD 直接以目标窗口为父创建」被 OS 拒绝——
    /// 实测（本机 Windows 11 26300，两进程同为 PerMonitorV2）：直接跨进程 CreateWindowExW(WS_CHILD,
    /// parent=目标) 返回 NULL 且 GetLastError=0（不设错误码的策略性拒绝，见 tools/Wp1AHostDiag probe
    /// 模式与 WORKLOG 实测结论）；而「自建 WS_POPUP → 改样式为 WS_CHILD → SetParent(目标)」成功且
    /// GetAncestor(GA_PARENT) == 目标。故按语义等价的两步路径实现，最终形态与规格一致（WS_CHILD 子窗口）。
    /// </summary>
    private bool TryCreateEmbed(out string? error)
    {
        if (!HostNativeMethods.GetClientRect(TargetHwnd, out var client))
        {
            error = "GetClientRect 读取目标客户区失败（目标可能已销毁或不可见）。";
            return false;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;

        // 第一步：本进程自建（隐藏，无 WS_VISIBLE——挂接完成前不闪现于屏幕）
        _createHandle = GCHandle.Alloc(this);
        var hwnd = HostNativeMethods.CreateWindowExW(
            (uint) (HostNativeMethods.WS_EX_LAYERED | HostNativeMethods.WS_EX_TRANSPARENT),
            WindowClassName,
            null,
            HostNativeMethods.WS_POPUP, // 先建顶层隐藏窗口，第二步转正为 WS_CHILD
            0, 0, width, height,
            IntPtr.Zero, IntPtr.Zero,
            HostNativeMethods.GetModuleHandleW(null),
            GCHandle.ToIntPtr(_createHandle));

        if (hwnd == IntPtr.Zero)
        {
            FreeCreateHandleIfUnadopted();
            error = $"CreateWindowExW(WS_POPUP) 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        // 第二步 a：样式转正（WS_POPUP → WS_CHILD）。先改样式再 SetParent，
        // 保证进入父子关系时即为真正的子窗口语义（裁剪 / 绘制 / 焦点归父）
        var style = HostNativeMethods.GetWindowLongPtrCompat(hwnd, HostNativeMethods.GWL_STYLE).ToInt64();
        HostNativeMethods.SetWindowLongPtrCompat(
            hwnd, HostNativeMethods.GWL_STYLE,
            new IntPtr((style & ~(long) HostNativeMethods.WS_POPUP) | HostNativeMethods.WS_CHILD));

        // 第二步 b：跨进程 SetParent 建立父子关系（失败常见错误码 5 = ERROR_ACCESS_DENIED，UIPI 拒绝）
        Marshal.SetLastSystemError(0);
        if (HostNativeMethods.SetParent(hwnd, TargetHwnd) == IntPtr.Zero)
        {
            var err = Marshal.GetLastSystemError();
            HostNativeMethods.DestroyWindow(hwnd);
            FreeCreateHandleIfUnadopted();
            error = err == 5
                ? "SetParent 被拒绝（Win32 错误 5 = 拒绝访问，目标窗口权限更高，UIPI 阻止跨进程嵌入）。"
                : $"SetParent 失败（Win32 错误 {err}）。";
            return false;
        }

        // 挂接真实性校验：父子关系必须真实建立（SetParent 成功但关系异常的防御性检查）
        if (HostNativeMethods.GetAncestor(hwnd, HostNativeMethods.GA_PARENT) != TargetHwnd)
        {
            HostNativeMethods.DestroyWindow(hwnd);
            FreeCreateHandleIfUnadopted();
            error = "宿主父子关系校验失败（GetAncestor(GA_PARENT) 不等于目标窗口）。";
            return false;
        }

        // 激活分层合成：WS_EX_LAYERED 窗口在首次 SetLayeredWindowAttributes 前不参与合成（内容不可见）
        if (!HostNativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, HostNativeMethods.LWA_ALPHA))
        {
            HostNativeMethods.DestroyWindow(hwnd);
            FreeCreateHandleIfUnadopted();
            error = $"SetLayeredWindowAttributes 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        // 沉底：目标既有子控件（兄弟窗口）位于宿主之上继续可见可点，目标自绘客户区背景被宿主覆盖
        if (!HostNativeMethods.SetWindowPos(
                hwnd, HostNativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                HostNativeMethods.SWP_NOMOVE | HostNativeMethods.SWP_NOSIZE | HostNativeMethods.SWP_NOACTIVATE))
        {
            HostNativeMethods.DestroyWindow(hwnd);
            FreeCreateHandleIfUnadopted();
            error = $"宿主沉底 SetWindowPos(HWND_BOTTOM) 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        // 最后显示：子窗口显示不参与激活（不打断目标焦点）
        HostNativeMethods.ShowWindow(hwnd, HostNativeMethods.SW_SHOWNOACTIVATE);

        _hwnd = hwnd;
        _mode = HostAttachMode.Embed;
        _embedSinceMs = Environment.TickCount64;

        LogRectComparison("A1 建立完成");
        error = null;
        return true;
    }

    /// <summary>
    /// 路径 A2：WS_POPUP 顶层窗口，跟随目标外接矩形、Z 序钉在目标正下方。
    /// </summary>
    private bool TryCreateFollow(out string? error)
    {
        var iconic = HostNativeMethods.IsIconic(TargetHwnd);
        var bounds = default(HostNativeMethods.RECT);

        if (!iconic)
        {
            bounds = TryGetTargetOuterBounds(TargetHwnd, out var boundsOk, out _);
            if (!boundsOk)
            {
                error = "读取目标外接矩形失败（DWM 与 GetWindowRect 均失败，目标可能已销毁）。";
                return false;
            }
        }
        // 目标最小化时外接矩形无意义（-32000 幽灵坐标）：按 0 尺寸创建并保持隐藏，
        // 待 EVENT_SYSTEM_MINIMIZEEND / SHOW 事件再定位显示

        _createHandle = GCHandle.Alloc(this);
        var hwnd = HostNativeMethods.CreateWindowExW(
            (uint) (HostNativeMethods.WS_EX_LAYERED | HostNativeMethods.WS_EX_TRANSPARENT
                    | HostNativeMethods.WS_EX_NOACTIVATE | HostNativeMethods.WS_EX_TOOLWINDOW),
            WindowClassName,
            null,
            HostNativeMethods.WS_POPUP, // 顶层无边框；不进任务栏 / Alt-Tab 靠 TOOLWINDOW，不抢焦点靠 NOACTIVATE
            bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            IntPtr.Zero, IntPtr.Zero,
            HostNativeMethods.GetModuleHandleW(null),
            GCHandle.ToIntPtr(_createHandle));

        if (hwnd == IntPtr.Zero)
        {
            FreeCreateHandleIfUnadopted();
            error = $"CreateWindowExW(WS_POPUP) 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        if (!HostNativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, HostNativeMethods.LWA_ALPHA))
        {
            HostNativeMethods.DestroyWindow(hwnd);
            FreeCreateHandleIfUnadopted();
            error = $"SetLayeredWindowAttributes 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        if (!iconic)
        {
            // Z 序钉位 + 定位：置于目标正下方（壁纸在目标「身后」，目标半透明时透出壁纸）。
            // 规格字面给出 SetWindowPos(host, GetWindow(target, GW_HWNDNEXT), …)——按 Win32 语义
            // hWndInsertAfter 是「宿主插到它后面」，GW_HWNDNEXT 取的是已位于目标下方的相邻窗口，
            // 该式会把宿主放到「目标下方第二格」，与规格自己声明的目标「宿主始终位于目标正下方」矛盾。
            // 故按语义正确实现：锚点直接传目标自身；此偏差与理由已记入 WORKLOG（关键决策 2）。
            if (!HostNativeMethods.SetWindowPos(
                    hwnd, TargetHwnd,
                    bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                    HostNativeMethods.SWP_NOACTIVATE))
            {
                HostNativeMethods.DestroyWindow(hwnd);
                FreeCreateHandleIfUnadopted();
                error = $"宿主钉位 SetWindowPos 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
                return false;
            }

            HostNativeMethods.ShowWindow(hwnd, HostNativeMethods.SW_SHOWNOACTIVATE);
        }

        _hwnd = hwnd;
        _mode = HostAttachMode.Follow;
        LogRectComparison("A2 建立完成");
        error = null;
        return true;
    }

    /// <summary>
    /// 安装跟随所需的全部 WinEvent 钩子（pump 线程调用）。
    /// 事件分三段安装（区间越窄，无关事件越少）：MINIMIZESTART~END、DESTROY~FOCUS（含 SHOW/HIDE/REORDER）、
    /// LOCATIONCHANGE。优先按目标进程 / 线程过滤（回调量最小）；被拒（如目标进程受保护）时回退全局钩子 +
    /// 回调内自行过滤。任一钩子两条路径都装不上返回 false（调用方负责销毁宿主，不留半挂接状态）。
    /// </summary>
    private bool InstallHooks()
    {
        var ok = TryInstallRange(HostNativeMethods.EVENT_SYSTEM_MINIMIZESTART, HostNativeMethods.EVENT_SYSTEM_MINIMIZEEND)
                 && TryInstallRange(HostNativeMethods.EVENT_OBJECT_DESTROY, HostNativeMethods.EVENT_OBJECT_FOCUS)
                 && TryInstallRange(HostNativeMethods.EVENT_OBJECT_LOCATIONCHANGE, HostNativeMethods.EVENT_OBJECT_LOCATIONCHANGE);

        if (!ok)
            UnhookAll(); // 已装上的部分一并卸掉，避免泄漏钩子

        return ok;
    }

    /// <summary>安装一段事件区间的钩子（带全局回退）。返回 false 表示两条路径都失败。</summary>
    private bool TryInstallRange(uint eventMin, uint eventMax)
    {
        // 第一选择：只收目标进程 / 目标线程的事件
        var hook = HostNativeMethods.SetWinEventHook(
            eventMin, eventMax, IntPtr.Zero, _wineventKeepAlive,
            _targetProcessId, _targetThreadId,
            HostNativeMethods.WINEVENT_OUTOFCONTEXT | HostNativeMethods.WINEVENT_SKIPOWNPROCESS);

        // 回退：目标进程受保护（如反作弊 / 高权限进程拒绝被定向挂钩）时退全局钩子，回调内自行过滤
        if (hook == IntPtr.Zero)
        {
            hook = HostNativeMethods.SetWinEventHook(
                eventMin, eventMax, IntPtr.Zero, _wineventKeepAlive,
                0, 0,
                HostNativeMethods.WINEVENT_OUTOFCONTEXT | HostNativeMethods.WINEVENT_SKIPOWNPROCESS);
        }

        if (hook == IntPtr.Zero)
            return false;

        _hooks.Add(hook);
        return true;
    }

    /// <summary>卸载全部钩子（pump 线程调用；UnhookWinEvent 对已卸载句柄返回 false，幂等安全）。</summary>
    private void UnhookAll()
    {
        foreach (var hook in _hooks)
            HostNativeMethods.UnhookWinEvent(hook);

        _hooks.Clear();
    }

    // ================================================================================
    // WinEvent 回调（pump 线程）：跟随 / 显隐 / 降级 / 清理的事件驱动中枢
    // ================================================================================

    /// <summary>WinEvent 回调入口（pump 线程）。绝不抛异常——异常会沿 DispatchMessageW 涌入 pump 循环杀死线程。</summary>
    private void OnWinEvent(IntPtr hHook, uint @event, IntPtr hwnd, int idObject, int idChild, uint threadId, uint timeMs)
    {
        try
        {
            HandleWinEvent(@event, hwnd, idObject);
        }
        catch (Exception ex)
        {
            HostDiagnostics.Log($"WinEvent 回调异常 target=0x{TargetHwnd:X} event=0x{@event:X}：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>WinEvent 事件分派（pump 线程；调用方已捕获异常）。</summary>
    private void HandleWinEvent(uint @event, IntPtr hwnd, int idObject)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
            return;

        // 只处理窗口本体事件（idObject == OBJID_WINDOW(0)），排除目标内部子控件（编辑框等）的高频事件
        if (idObject != HostNativeMethods.OBJID_WINDOW)
            return;

        if (@event == HostNativeMethods.EVENT_OBJECT_DESTROY && hwnd == TargetHwnd)
        {
            // 目标销毁：A1 宿主会随父收到 WM_DESTROY（同样上报）；此处先上报，本类与服务层双去重
            RaiseTargetLost("EVENT_OBJECT_DESTROY");
            return;
        }

        if (_mode == HostAttachMode.Embed)
            HandleEmbedEvent(@event, hwnd);
        else
            HandleFollowEvent(@event, hwnd);
    }

    /// <summary>A1（嵌入模式）事件处理（pump 线程）。</summary>
    private void HandleEmbedEvent(uint @event, IntPtr hwnd)
    {
        switch (@event)
        {
            case HostNativeMethods.EVENT_OBJECT_LOCATIONCHANGE when hwnd == TargetHwnd:
                // 子窗口随父移动自动跟随；这里只需处理客户区尺寸变化（缩放）。
                // 最小化时子窗口自动隐藏，无需干预（规格 A1.5）。
                if (HostNativeMethods.IsIconic(TargetHwnd))
                    return;

                SyncEmbedClientSize();
                break;

            case HostNativeMethods.EVENT_OBJECT_REORDER when hwnd == TargetHwnd:
                // 目标调整子窗口 Z 序（重设控件父级 / 提升某控件等）→ 宿主重新沉底保持「背景」语义
                VerifyEmbedIntact(); // 先校验父子关系未被外力破坏，再沉底
                if (_hwnd != IntPtr.Zero)
                {
                    HostNativeMethods.SetWindowPos(
                        _hwnd, HostNativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                        HostNativeMethods.SWP_NOMOVE | HostNativeMethods.SWP_NOSIZE | HostNativeMethods.SWP_NOACTIVATE);
                }

                break;

            case HostNativeMethods.EVENT_OBJECT_FOCUS:
                // 焦点异常检测：宿主带 WS_EX_TRANSPARENT，永远不该获得键盘焦点。
                // 若获得（跨进程父子关系干扰了目标的输入队列），按降级规则转 A2（观察语义见 FocusObservationMs）。
                if (hwnd == _hwnd)
                    DowngradeToFollow("宿主意外获得键盘焦点（跨进程父子关系干扰目标输入队列）");

                break;

            case HostNativeMethods.EVENT_OBJECT_SHOW when hwnd == TargetHwnd:
            case HostNativeMethods.EVENT_OBJECT_HIDE when hwnd == TargetHwnd:
                // 子窗口可见性随父自动联动，无需处理；借低频事件顺带校验挂接完整性
                VerifyEmbedIntact();
                break;
        }
    }

    /// <summary>A1 客户区尺寸同步：目标客户区变化 → 宿主矩形同步（宿主位置恒为客户区左上 0,0）。</summary>
    private void SyncEmbedClientSize()
    {
        if (!HostNativeMethods.GetClientRect(TargetHwnd, out var client))
        {
            // 读客户区失败：目标极可能已销毁（销毁事件稍后到达）；直接走清理
            RaiseTargetLost("GetClientRect 失败（疑似目标已销毁）");
            return;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;

        // GetWindowRect 对子窗口返回「父客户区坐标系」矩形，可与 GetClientRect 宽高直接比对
        if (!HostNativeMethods.GetWindowRect(_hwnd, out var hostRect))
        {
            RaiseTargetLost("GetWindowRect(宿主) 失败（宿主已失效）");
            return;
        }

        var hostW = hostRect.Right - hostRect.Left;
        var hostH = hostRect.Bottom - hostRect.Top;
        if (hostW == width && hostH == height)
            return; // 尺寸未变（纯移动事件），跳过同步
        // 坐标系说明：实测 Win11/DWM 下子窗口 GetWindowRect 返回屏幕物理坐标（非文档所述父客户区坐标），
        // 但宽高不受坐标系影响，此处只比宽高、只改尺寸（SWP_NOMOVE），位置由「子窗口恒在客户区 (0,0)」语义保证

        var t0 = Environment.TickCount64;
        HostNativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, 0, 0, width, height,
            HostNativeMethods.SWP_NOMOVE | HostNativeMethods.SWP_NOACTIVATE | HostNativeMethods.SWP_NOZORDER);
        var delta = Environment.TickCount64 - t0;

        // 图像表面按新客户区重建并重贴（pump 线程直呼）；视频模式则重算加边布局
        _renderer?.UpdateClientSize(width, height);
        _videoPresenter?.LayoutVideo(width, height);

        HostDiagnostics.LogFollow(
            $"A1 尺寸跟随 delta={delta}ms 目标客户区=({width}x{height}) 宿主旧矩形=({hostW}x{hostH}) target=0x{TargetHwnd:X}");
    }

    /// <summary>
    /// A1 挂接完整性校验：父子关系被外力破坏（目标把宿主移出子窗口序列 / 重新 SetParent）即降级 A2；
    /// 目标已销毁则直接清理。pump 线程调用。
    /// </summary>
    private void VerifyEmbedIntact()
    {
        if (_mode != HostAttachMode.Embed || _hwnd == IntPtr.Zero)
            return;

        if (!HostNativeMethods.IsWindow(TargetHwnd))
        {
            RaiseTargetLost("IsWindow(目标) = false");
            return;
        }

        if (HostNativeMethods.GetAncestor(_hwnd, HostNativeMethods.GA_PARENT) != TargetHwnd)
            DowngradeToFollow("宿主父子关系被目标侧改动（GetAncestor(GA_PARENT) 不再等于目标）");
    }

    /// <summary>A2（跟随模式）事件处理（pump 线程）。</summary>
    private void HandleFollowEvent(uint @event, IntPtr hwnd)
    {
        if (hwnd != TargetHwnd)
            return; // 目标的子控件事件不处理（外接矩形只由目标自身变化驱动）

        switch (@event)
        {
            case HostNativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                SyncFollowBounds(ensureVisible: true);
                break;

            case HostNativeMethods.EVENT_OBJECT_REORDER:
                // 目标被移动 Z 序 → 宿主重新钉到目标正下方（保持「身后」语义；锚点语义见 TryCreateFollow 注释）
                HostNativeMethods.SetWindowPos(
                    _hwnd, TargetHwnd, 0, 0, 0, 0,
                    HostNativeMethods.SWP_NOMOVE | HostNativeMethods.SWP_NOSIZE | HostNativeMethods.SWP_NOACTIVATE);
                break;

            case HostNativeMethods.EVENT_OBJECT_SHOW:
                if (!HostNativeMethods.IsIconic(TargetHwnd) && HostNativeMethods.IsWindowVisible(TargetHwnd))
                    SyncFollowBounds(ensureVisible: true);

                break;

            case HostNativeMethods.EVENT_OBJECT_HIDE:
                HostNativeMethods.ShowWindow(_hwnd, HostNativeMethods.SW_HIDE);
                break;

            case HostNativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                HostNativeMethods.ShowWindow(_hwnd, HostNativeMethods.SW_HIDE);
                break;

            case HostNativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                if (!HostNativeMethods.IsIconic(TargetHwnd))
                    SyncFollowBounds(ensureVisible: true);

                break;
        }
    }

    /// <summary>
    /// A2 矩形跟随：目标外接矩形（DWM 优先）→ 宿主 SetWindowPos（不改 Z 序、不激活）。
    /// </summary>
    /// <param name="ensureVisible">true 时若宿主隐藏且目标可见非最小化，顺带恢复显示。</param>
    private void SyncFollowBounds(bool ensureVisible)
    {
        if (HostNativeMethods.IsIconic(TargetHwnd))
        {
            HostNativeMethods.ShowWindow(_hwnd, HostNativeMethods.SW_HIDE);
            return;
        }

        TryGetTargetOuterBounds(TargetHwnd, out var boundsOk, out var bounds);
        if (!boundsOk)
        {
            RaiseTargetLost("读取目标外接矩形失败（DWM 与 GetWindowRect 均失败，疑似目标已销毁）");
            return;
        }

        var t0 = Environment.TickCount64;
        HostNativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero,
            bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            HostNativeMethods.SWP_NOACTIVATE | HostNativeMethods.SWP_NOZORDER);
        var delta = Environment.TickCount64 - t0;

        if (!HostNativeMethods.GetWindowRect(_hwnd, out var hostRect))
        {
            RaiseTargetLost("GetWindowRect(宿主) 失败（宿主已失效）");
            return;
        }

        // 图像表面按新尺寸重建重贴（A2 宿主无边框，客户区 = 窗口矩形）；视频模式重算加边布局
        _renderer?.UpdateClientSize(hostRect.Right - hostRect.Left, hostRect.Bottom - hostRect.Top);
        _videoPresenter?.LayoutVideo(hostRect.Right - hostRect.Left, hostRect.Bottom - hostRect.Top);

        HostDiagnostics.LogFollow(
            $"A2 跟随 delta={delta}ms 目标外接=({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}) " +
            $"宿主矩形=({hostRect.Left},{hostRect.Top},{hostRect.Right},{hostRect.Bottom}) target=0x{TargetHwnd:X}");

        if (ensureVisible && !HostNativeMethods.IsWindowVisible(_hwnd))
            HostNativeMethods.ShowWindow(_hwnd, HostNativeMethods.SW_SHOWNOACTIVATE);
    }

    // ================================================================================
    // 降级与矩形纯函数
    // ================================================================================

    /// <summary>
    /// A1 → A2 降级（pump 线程调用）：卸钩子 → 销毁 A1 宿主 → 按 A2 重建 → 换装钩子 → 上报模式变化。
    /// A2 重建或钩子安装失败则上报 TargetLost 让服务彻底清理会话（宁可无背景也不留僵尸宿主）。
    /// </summary>
    private void DowngradeToFollow(string reason)
    {
        if (_disposed || _mode != HostAttachMode.Embed)
            return;

        // 挂接时长打点：观察窗内（前 30s）降级属规格预期的早期异常；越晚降级越值得排查目标行为
        var embedAgeMs = Environment.TickCount64 - _embedSinceMs;
        HostDiagnostics.Log(
            $"A1 降级 A2 target=0x{TargetHwnd:X} 挂接时长={embedAgeMs}ms（焦点观察窗={FocusObservationMs}ms，" +
            "本实现全程监测）原因：" + reason);

        UnhookAll();
        if (HostNativeMethods.IsWindow(_hwnd))
            HostNativeMethods.DestroyWindow(_hwnd);

        _hwnd = IntPtr.Zero;

        if (!TryCreateFollow(out var followError) || !InstallHooks())
        {
            HostDiagnostics.Log($"降级 A2 失败 target=0x{TargetHwnd:X}：{followError ?? "钩子安装失败"}");
            RaiseTargetLost($"A1 异常后降级 A2 仍失败：{followError ?? "WinEvent 钩子安装失败"}");
            return;
        }

        _mode = HostAttachMode.Follow;
        LogRectComparison("A2 降级重建完成");
        AttachModeChanged?.Invoke(TargetHwnd, _mode);
    }

    /// <summary>
    /// 【纯函数】A1 宿主矩形：目标客户区 → 宿主在父客户区坐标系中的矩形（左上恒 0,0，宽高一致）。
    /// 抽成纯静态函数供 WP4 单元测试钉住语义（规格 WP4 用例表：挂接矩形计算纯函数）。
    /// </summary>
    /// <param name="targetClientRect">GetClientRect 得到的目标客户区（Left/Top 恒为 0，宽高为实际值）。</param>
    /// <returns>宿主应处的矩形。</returns>
    public static HostNativeMethods.RECT ComputeEmbedHostRect(HostNativeMethods.RECT targetClientRect)
    {
        return new HostNativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = targetClientRect.Right - targetClientRect.Left,
            Bottom = targetClientRect.Bottom - targetClientRect.Top,
        };
    }

    /// <summary>
    /// 【纯函数】A2 宿主矩形：目标外接矩形 → 宿主屏幕矩形（平移不缩放：跟随语义下宿主与目标等大同位）。
    /// 输入输出均为屏幕物理像素坐标（DWMWA_EXTENDED_FRAME_BOUNDS / GetWindowRect 同一坐标系）。
    /// </summary>
    /// <param name="targetOuterBounds">目标外接矩形。</param>
    /// <returns>宿主应处的屏幕矩形。</returns>
    public static HostNativeMethods.RECT ComputeFollowHostRect(HostNativeMethods.RECT targetOuterBounds)
    {
        return new HostNativeMethods.RECT
        {
            Left = targetOuterBounds.Left,
            Top = targetOuterBounds.Top,
            Right = targetOuterBounds.Right,
            Bottom = targetOuterBounds.Bottom,
        };
    }

    /// <summary>【纯函数】矩形相等判断（宿主矩形与目标矩形比对用；WP4 测试配套）。</summary>
    public static bool RectsEqual(in HostNativeMethods.RECT a, in HostNativeMethods.RECT b)
    {
        return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    }

    /// <summary>
    /// 读目标外接矩形：优先 DWMWA_EXTENDED_FRAME_BOUNDS（DWM 实际可视框，排除阴影边距），
    /// 失败回退 GetWindowRect。两者都是屏幕物理像素坐标，可直接互为回退。
    /// </summary>
    /// <param name="target">目标窗口句柄。</param>
    /// <param name="ok">输出：是否成功读到（两 API 均失败为 false）。</param>
    /// <param name="bounds">输出：外接矩形（ok=false 时无意义）。</param>
    /// <returns>读到的矩形（便于链式使用）。</returns>
    private static HostNativeMethods.RECT TryGetTargetOuterBounds(IntPtr target, out bool ok, out HostNativeMethods.RECT bounds)
    {
        var hr = HostNativeMethods.DwmGetWindowAttribute(
            target, HostNativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var dwmBounds,
            Marshal.SizeOf<HostNativeMethods.RECT>());

        if (hr >= 0)
        {
            ok = true;
            bounds = dwmBounds;
            return bounds;
        }

        ok = HostNativeMethods.GetWindowRect(target, out var rectBounds);
        bounds = rectBounds;
        return bounds;
    }

    // ================================================================================
    // 窗口过程与绘制（阶段 A 纯色填充验证）
    // ================================================================================

    /// <summary>
    /// 类级窗口过程（静态）。实例路由：WM_NCCREATE 时从 CREATESTRUCT.lpCreateParams 取回
    /// 创建方预置的 GCHandle 存入 GWLP_USERDATA；后续消息经 GWLP_USERDATA 找回实例。
    /// 绝不抛异常（异常会沿 DispatchMessageW 涌入 pump 循环杀死线程）。
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == HostNativeMethods.WM_NCCREATE)
            {
                // 创建参数 = GCHandle&lt;WallpaperHostWindow&gt;（CreateWindowExW 的 lpParam 原样回传）
                var cs = Marshal.PtrToStructure<CreateStructW>(lParam);
                if (GCHandle.FromIntPtr(cs.lpCreateParams).Target is WallpaperHostWindow instance)
                {
                    instance._handleAdopted = true;
                    instance._hwnd = hwnd; // 尽早记录：创建过程内的后续消息（WM_ERASEBKGND 等）即可路由
                    HostNativeMethods.SetWindowLongPtrCompat(hwnd, HostNativeMethods.GWLP_USERDATA, cs.lpCreateParams);
                }

                return HostNativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
            }

            var stored = HostNativeMethods.GetWindowLongPtrCompat(hwnd, HostNativeMethods.GWLP_USERDATA);
            var instance2 = stored != IntPtr.Zero
                ? GCHandle.FromIntPtr(stored).Target as WallpaperHostWindow
                : null;

            switch (msg)
            {
                case HostNativeMethods.WM_PAINT:
                    instance2?.PaintHostSurface();
                    return IntPtr.Zero; // 已绘制，无需默认处理

                case HostNativeMethods.WM_ERASEBKGND:
                    instance2?.EraseHostSurface(wParam);
                    return new IntPtr(1); // 返回 1 = 背景已擦除，抑制默认白闪

                case HostNativeMethods.WM_DESTROY:
                    instance2?.OnNativeDestroyed();
                    break;

                case HostNativeMethods.WM_NCDESTROY:
                    instance2?.OnNativeNcDestroy(hwnd);
                    break;

                case 0x02E0: // WM_DPICHANGED：DPI 变更处理属于阶段 D（规格 WP1-D 第 2 条），阶段 A 仅透传默认处理
                    break;
            }

            return HostNativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        catch
        {
            // 窗口过程绝不向原生层抛异常：吞掉并走默认处理（诊断细节由具体路径自行记录）
            return HostNativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>WM_PAINT：纯色填充（阶段 A 验证；阶段 B 替换为 DComp 渲染树）。</summary>
    private void PaintHostSurface()
    {
        var ps = new HostNativeMethods.PAINTSTRUCT();
        var hdc = HostNativeMethods.BeginPaintW(_hwnd, ref ps);
        if (hdc == IntPtr.Zero)
            return; // BeginPaint 失败：无法取 DC，放弃本帧（系统稍后重发 WM_PAINT）

        try
        {
            var brush = GetFillBrush();
            HostNativeMethods.FillRect(hdc, ref ps.rcPaint, brush);
        }
        finally
        {
            HostNativeMethods.EndPaintW(_hwnd, ref ps); // 必须配对，否则无效区不清、WM_PAINT 风暴
        }
    }

    /// <summary>WM_ERASEBKGND：同样纯色填充并返回 1（调用方），避免绘制前白色闪烁。</summary>
    private void EraseHostSurface(IntPtr hdc)
    {
        if (!HostNativeMethods.GetClientRect(_hwnd, out var client))
            return;

        var brush = GetFillBrush();
        HostNativeMethods.FillRect(hdc, ref client, brush);
    }

    /// <summary>惰性创建进程级共享画刷（阶段 A 填充色；GDI 对象进程退出统一回收）。</summary>
    private static IntPtr GetFillBrush()
    {
        lock (_fillBrushGate)
        {
            if (_fillBrush == IntPtr.Zero)
                _fillBrush = HostNativeMethods.CreateSolidBrush(HostNativeMethods.HostPhaseAFillColor);

            return _fillBrush;
        }
    }

    // ================================================================================
    // 销毁与生命周期上报
    // ================================================================================

    /// <summary>WM_DESTROY（pump 线程）：宿主窗口进入销毁。主动 Dispose 与随目标链式销毁都会到达。</summary>
    private void OnNativeDestroyed()
    {
        if (_disposed)
            return; // 服务主动 Dispose 触发的销毁：不再上报 TargetLost（清理已在进行）

        RaiseTargetLost("宿主 WM_DESTROY（随目标链式销毁或外力销毁）");
    }

    /// <summary>上报目标丢失（幂等：WM_DESTROY 与 EVENT_OBJECT_DESTROY 双信号去重；pump 线程内调用）。</summary>
    private void RaiseTargetLost(string reason)
    {
        if (_targetLostRaised || _disposed)
            return;

        _targetLostRaised = true;
        HostDiagnostics.Log($"目标丢失 target=0x{TargetHwnd:X} 原因：{reason}");
        TargetLost?.Invoke(TargetHwnd);
    }

    /// <summary>WM_NCDESTROY（pump 线程）：窗口销毁最后一站，释放创建参数 GCHandle 并清空 GWLP_USERDATA。</summary>
    private void OnNativeNcDestroy(IntPtr hwnd)
    {
        var stored = HostNativeMethods.GetWindowLongPtrCompat(hwnd, HostNativeMethods.GWLP_USERDATA);
        if (stored != IntPtr.Zero)
        {
            HostNativeMethods.SetWindowLongPtrCompat(hwnd, HostNativeMethods.GWLP_USERDATA, IntPtr.Zero);
            GCHandle.FromIntPtr(stored).Free();
        }
    }

    /// <summary>
    /// 销毁宿主（任意线程调用）：置失效标志 → pump 线程卸钩子 + DestroyWindow。
    /// 与 WinEvent 回调同在 pump 线程串行，天然无竞争；pump 外只写 volatile 的 _disposed
    /// 与事件字段（.NET 委托字段赋值原子，回调侧读取快照，最坏是多收到一次已失效通知，
    /// 由服务层会话字典判重兜底）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        AttachModeChanged = null;
        TargetLost = null;

        // 目标已销毁时 A1 宿主多半已被链式销毁（IsWindow=false 直接跳过）；A2 宿主独立存在必须显式销毁
        WallpaperHostPump.Shared.Execute(() =>
        {
            _videoPresenter?.Dispose(); // 视频组合树先于窗口销毁释放
            _videoPresenter = null;
            _renderer?.Dispose(); // DComp 树先于窗口销毁释放（target 绑定的 HWND 不能先亡）
            _renderer = null;
            UnhookAll();
            if (HostNativeMethods.IsWindow(_hwnd))
                HostNativeMethods.DestroyWindow(_hwnd);

            _hwnd = IntPtr.Zero;
        });
    }

    /// <summary>
    /// 渲染一张静态图到宿主（WP1-B）。任意工作线程调用：内部经 pump 封送，把共享快照逻辑
    /// （BitmapSnapshot.CopyFrom）的像素一次性缩放贴入 DComp surface（尺寸 = 当前客户区）并 Commit。
    /// 渲染器惰性创建（首次渲染）；失败返回 false（原因见诊断日志，宿主保留纯色占位）。
    /// </summary>
    /// <param name="snapshot">紧密排列 PBgra8 像素快照。</param>
    /// <returns>true = 已 Commit；false = 已释放 / 封送失败 / 渲染失败。</returns>
    public bool RenderImage(BgraSnapshot snapshot)
    {
        if (_disposed)
            return false;

        var ok = false;
        var done = WallpaperHostPump.Shared.Execute(() =>
        {
            if (_disposed || !HostNativeMethods.IsWindow(_hwnd))
                return;

            // 客户区尺寸取当前值（后续尺寸变化由跟随路径直呼 UpdateClientSize 重建表面）
            if (!HostNativeMethods.GetClientRect(_hwnd, out var client))
                return;

            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;

            _renderer ??= new WallpaperHostRenderer(_hwnd);
            ok = _renderer.Render(snapshot, width, height);
        });

        return done && ok;
    }

    /// <summary>
    /// 进入视频模式：在 pump 线程为播放器建 WinRT 组合呈现（GetSurface + DesktopWindowTarget）。
    /// 任意工作线程调用（服务在视频会话挂接后调用）；幂等。
    /// </summary>
    /// <param name="player">视频会话的播放器（组合呈现路径，帧服务器标志须为关）。</param>
    /// <returns>true = 已附着；false = 已释放 / 封送失败 / 附着失败（详见诊断日志）。</returns>
    public bool BeginVideo(MediaPlayer player)
    {
        if (_disposed)
            return false;

        var ok = false;
        var done = WallpaperHostPump.Shared.Execute(() =>
        {
            if (_disposed || !HostNativeMethods.IsWindow(_hwnd))
                return;

            if (!HostNativeMethods.GetClientRect(_hwnd, out var client))
                return;

            _videoPresenter ??= new CompositionVideoPresenter(_hwnd);
            ok = _videoPresenter.Attach(player, client.Right - client.Left, client.Bottom - client.Top);
        });

        return done && ok;
    }

    /// <summary>
    /// 视频客户区尺寸变化（宿主尺寸同步路径调用）：重算视频视觉的等比加边布局。pump 封送。
    /// </summary>
    public void UpdateVideoLayout(int width, int height)
    {
        if (_disposed)
            return;

        _ = WallpaperHostPump.Shared.Execute(() => _videoPresenter?.LayoutVideo(width, height));
    }

    /// <summary>
    /// 退出视频模式（组合树 Root 清空、释放播放器表面与画刷）。pump 封送；幂等。
    /// </summary>
    public void EndVideo()
    {
        if (_disposed)
            return;

        _ = WallpaperHostPump.Shared.Execute(() => _videoPresenter?.Detach());
    }

    /// <summary>创建失败但窗口过程未收养句柄时释放 GCHandle（收养后由 WM_NCDESTROY 释放）。</summary>
    private void FreeCreateHandleIfUnadopted()
    {
        if (!_handleAdopted && _createHandle.IsAllocated)
            _createHandle.Free();
    }

    /// <summary>挂接建立时打出目标矩形 vs 宿主矩形的对照日志（DoD 验证点：矩形一致性）。</summary>
    private void LogRectComparison(string label)
    {
        if (!HostNativeMethods.GetWindowRect(_hwnd, out var hostRect))
            return;

        if (_mode == HostAttachMode.Embed)
        {
            // 实测 Win11/DWM 下子窗口 GetWindowRect 返回屏幕物理坐标；宽高与坐标系无关，宽高比对有效
            var ok = HostNativeMethods.GetClientRect(TargetHwnd, out var client);
            var clientW = client.Right - client.Left;
            var clientH = client.Bottom - client.Top;
            var hostW = hostRect.Right - hostRect.Left;
            var hostH = hostRect.Bottom - hostRect.Top;
            HostDiagnostics.Log(
                $"{label} target=0x{TargetHwnd:X} 目标客户区=({clientW}x{clientH}) " +
                $"宿主矩形=(屏幕坐标 {hostRect.Left},{hostRect.Top}，{hostW}x{hostH}) " +
                $"宽高一致={(ok && clientW == hostW && clientH == hostH)}");
        }
        else
        {
            var bounds = TryGetTargetOuterBounds(TargetHwnd, out var ok, out _);
            HostDiagnostics.Log(
                $"{label} target=0x{TargetHwnd:X} 目标外接=({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}) " +
                $"宿主矩形=({hostRect.Left},{hostRect.Top},{hostRect.Right},{hostRect.Bottom}) " +
                $"一致={(ok && RectsEqual(bounds, hostRect))}");
        }
    }

    /// <summary>
    /// CREATESTRUCTW：WM_NCCREATE 的 lParam 指向的结构（本实现只读首字段 lpCreateParams，
    /// 其余字段按 Win32 声明顺序以指针宽度类型排列，保证 x64/x86 偏移正确）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStructW
    {
        public IntPtr lpCreateParams; // CreateWindowExW 的 lpParam 原样回传（GCHandle 指针）
        public IntPtr hInstance;      // 模块实例
        public IntPtr hMenu;          // 菜单 / 子窗口 Id
        public IntPtr hwndParent;     // 父窗口
        public int cy;                // 高
        public int cx;                // 宽
        public int y;                 // y
        public int x;                 // x
        public IntPtr style;          // 样式（C 里是 LONG，按指针宽度声明不影响后续字段偏移）
        public IntPtr lpszName;       // 窗口名字符串指针
        public IntPtr lpszClass;      // 类名字符串指针
        public IntPtr dwExStyle;      // 扩展样式（C 里是 DWORD，同上）
    }
}
