using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Playback;
using WinCanvas.Core.Interfaces;
using WinCanvas.Wallpaper.Hosting;
using WinCanvas.Wallpaper.Models;
using WinCanvas.Wallpaper.Rendering;

namespace WinCanvas.Wallpaper;

/// <summary>
/// IWallpaperService 的三层策略实现：
///   第一层 图片 —— 直接加载，导入时缩放到目标显示器分辨率（避免 4K/8K 原图直送 GPU）；
///   第二层 视频 —— MediaPlayer（Media Foundation 硬件解码）；
///   第三层 pkg  —— repkg 解包提取 scene.json 与纹理，复杂 SceneScript 场景降级为封面图。
/// 渲染节拍由专用的 <see cref="WallpaperRenderLoop"/> 后台线程驱动，
/// 帧率由 <see cref="DynamicFrameRateController"/> 三档控制（激活 60fps / 非激活 15fps / 全屏暂停），
/// 资源准备与逐帧工作全部在工作线程执行，绝不阻塞 UI 线程。
/// WP1-A 起，每个会话还持有一个「宿主窗口」（<see cref="Hosting.WallpaperHostWindow"/>，
/// 本进程内的 Win32 窗口），壁纸画面经由宿主挂接到目标窗口呈现：
/// A1 跨进程子窗口嵌入（首选）→ 失败自动降级 A2 跟随窗口（回退）。
/// </summary>
public sealed class WallpaperService : IWallpaperService
{
    /// <summary>每个承载窗口一份会话：当前资源 + 渲染循环。窗口句柄 -> 会话。</summary>
    private readonly Dictionary<IntPtr, WallpaperSession> _sessions = new();

    /// <summary>保护 _sessions 与会话替换操作的锁（初始化/切换可能在 UI 线程调用，临界区必须极短）。</summary>
    private readonly object _gate = new();

    /// <summary>
    /// 每个承载窗口一把更新闸门：同一窗口的壁纸更新串行执行。
    /// 防止快速连续换壁纸时「旧请求的线程池任务后完成、把新请求刚建立的会话再覆盖回去」，
    /// 保证同一 hwnd 的会话替换严格按调用顺序进行。
    /// </summary>
    private readonly ConcurrentDictionary<IntPtr, SemaphoreSlim> _updateGates = new();

    /// <summary>
    /// 每个承载窗口的宿主窗口（WP1-A 新增）：窗口句柄 -> 宿主。
    /// 由 <see cref="_gate"/> 保护（与 _sessions 同锁：宿主与会话同生命周期、同时机增删，
    /// 拆成两把锁只会引入「会话已换宿主未删」的中间态，无收益）。
    /// 宿主销毁只发生在两条路径：TargetLost 清理（目标销毁）与 <see cref="ShutdownAll"/>（应用退出）；
    /// 换壁纸时旧会话的宿主被 <see cref="WallpaperSession.DetachHost"/> 摘出复用，不销毁。
    /// </summary>
    private readonly Dictionary<IntPtr, WallpaperHostWindow> _hosts = new();

    /// <summary>
    /// 图片加载时的显示器分辨率（WP1-D 跨屏重缩放依据）：窗口句柄 -> (宽, 高)。
    /// 由 <see cref="_gate"/> 保护；图片会话挂接时记录，宿主客户区变化时比对，
    /// 分辨率变化（跨屏移动 / DPI 变化）即按新分辨率重新走加载管线。
    /// </summary>
    private readonly Dictionary<IntPtr, (int W, int H)> _monitorResAtLoad = new();

    /// <summary>
    /// 窗口控制服务（可选注入，WP1-A）：A2 跟随模式下目标不透明时自动应用轻度透明的执行者。
    /// 未注入（如诊断工具场景）时跳过自动透明并提示用户手动调整，挂接功能不受影响。
    /// </summary>
    private readonly IWindowControlService? _windowControl;

    /// <summary>状态上报（pkg 降级提示、加载错误等），供 UI 层订阅展示。触发线程可能是工作线程。</summary>
    public event Action<string>? StatusReported;

    /// <summary>
    /// 会话的宿主挂接模式确定 / 变化（WP1-A 新增）：创建成功后与 A1 降级 A2 时触发。
    /// 在宿主 pump 线程或线程池线程触发；订阅方（UI 层）自行封送到 UI 线程。
    /// </summary>
    public event Action<IntPtr, HostAttachMode>? SessionHostModeChanged;

    /// <summary>
    /// 图片层新帧就绪：参数为窗口句柄与导入时已缩放好的 SoftwareBitmap。
    /// 在渲染线程触发；订阅方应在回调线程内【同步】把像素复制出来（本事件由会话内部锁保护，
    /// 复制完成后才可能发生资源释放），再自行封送到 Dispatcher 转 WriteableBitmap 等上屏。
    /// </summary>
    public event Action<IntPtr, SoftwareBitmap>? ImageFrameReady;

    /// <summary>
    /// 视频层播放器就绪：参数为窗口句柄与已配置好循环 / 静音的 MediaPlayer（不自动播放，
    /// 播放 / 暂停由渲染状态机驱动）。在线程池线程触发；订阅方需自行封送，
    /// 并把播放器挂到输出面（如 MediaPlayerElement.SetMediaPlayer）。
    /// </summary>
    public event Action<IntPtr, Windows.Media.Playback.MediaPlayer>? VideoPlayerReady;

    /// <summary>
    /// 构造服务。<paramref name="windowControl"/> 可选：注入后 A2 跟随模式会对不透明目标
    /// 自动应用轻度透明（alpha=220，规格 A2.4）；不传则跳过自动透明仅提示。
    /// </summary>
    public WallpaperService(IWindowControlService? windowControl = null)
    {
        _windowControl = windowControl;
    }

    /// <inheritdoc />
    public Task InitializeWallpaperAsync(IntPtr hwnd, string path)
    {
        // 与切换同一实现：已有会话则先释放旧资源再建新的
        return UpdateWallpaperAsync(hwnd, path);
    }

    /// <inheritdoc />
    public Task UpdateWallpaperAsync(IntPtr hwnd, string path)
    {
        // 准备资源是潜在重活（图片解码缩放 / pkg 解包），放到线程池执行，绝不占用调用方（UI）线程；
        // 同一窗口的多次更新经 _updateGates 串行化，会话替换严格按请求顺序完成。
        var gate = _updateGates.GetOrAdd(hwnd, _ => new SemaphoreSlim(1, 1));
        return Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await UpdateCoreAsync(hwnd, path).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <summary>
    /// 移除目标窗口的背景：销毁宿主与会话，让目标窗口恢复自绘内容。
    /// 与 <see cref="CleanupSessionCoreAsync"/> 共用同一闸门，保证不与在途更新互踩。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    public Task RemoveWallpaperAsync(IntPtr hwnd)
    {
        var gate = _updateGates.GetOrAdd(hwnd, _ => new SemaphoreSlim(1, 1));
        return Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CleanupSessionCoreAsync(hwnd).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <inheritdoc />
    public bool IsBackgroundActive(IntPtr hwnd, string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // 判重基准统一取完整路径：档案里存的是文件选择器给出的绝对路径，
        // 各加载器登记的 OriginalPath 是 Path.GetFullPath 规范化后的形式（见 PrepareSource），
        // 大小写不敏感比较（Windows 路径不区分大小写）。
        // GetFullPath 失败（非法字符等极端情况）按「未激活」处理——宁可多加载一次，不误判已恢复。
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        // 只读快照式查询：与 UpdateCoreAsync / CleanupSessionCoreAsync 同用 _gate 短临界区，
        // 不引入新锁（复用既有会话字典，规格 WP3「用会话字典判重」）。
        lock (_gate)
        {
            return _sessions.TryGetValue(hwnd, out var session)
                   && string.Equals(session.Source.OriginalPath, normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>单次壁纸更新的完整流程（已由调用方按 hwnd 串行化；在线程池后台线程执行）。</summary>
    private async Task UpdateCoreAsync(IntPtr hwnd, string path)
    {
        WallpaperSource source;
        try
        {
            source = PrepareSource(hwnd, path);
        }
        catch (Exception ex)
        {
            Report($"壁纸资源加载失败（{Path.GetFileName(path)}）：{ex.Message}");
            return;
        }

        if (source.StatusMessage != null)
            Report(source.StatusMessage);

        if (source.Kind == WallpaperKind.Image && source.ImageFrame == null)
        {
            Report($"壁纸资源不可渲染（{Path.GetFileName(path)}）。");
            source.Dispose();
            return;
        }

        WallpaperSession? oldSession;
        WallpaperSession newSession;
        lock (_gate)
        {
            _sessions.TryGetValue(hwnd, out oldSession);

            // 复用已有渲染循环（每窗口一个循环）；没有则新建并启动。
            // 整个「查旧会话 → 建/复用循环 → 写 _sessions → 启动」序列都在锁内完成，
            // 并发调用下不会出现两个循环同时新建（会话替换的先后顺序另由 _updateGates 保证）。
            var loop = oldSession?.RenderLoop ?? new WallpaperRenderLoop();

            // v0.2.0 接线「非激活 15fps」档：旧实现 DynamicFrameRateController.SetWindowActive
            // 全仓只有测试调用、产品代码一处没有 → 壁纸恒 60fps，README 承诺的省电档从未生效。
            // 探针在渲染线程每 250ms 调一次，只做 GetForegroundWindow + GetAncestor，无分配无阻塞。
            if (oldSession == null)
            {
                IntPtr probeTarget = hwnd;
                loop.ActiveProbe = () => Native.WallpaperNativeMethods.IsWindowForeground(probeTarget);
            }

            newSession = new WallpaperSession(hwnd, loop, source, RaiseImageFrameReady, RenderHostFrame);
            _sessions[hwnd] = newSession;

            if (oldSession == null)
                loop.Start();
        }

        // —— 宿主创建 / 复用（WP1-A）：在会话替换后、旧会话 Dispose 之前完成（规格接线要求）——
        // 本方法已在线程池后台线程执行，此处同步创建宿主不占 UI 线程。
        // 复用语义：换壁纸不换宿主（旧会话摘出引用移交新会话），避免目标窗口画面闪断。
        var host = oldSession?.DetachHost();
        if (host is { IsDisposed: true })
            host = null; // TargetLost 清理恰好先一步销毁了旧宿主（与 _updateGates 无锁协同的窄窗口），重建

        if (host == null)
            host = CreateHost(hwnd);

        newSession.AttachHost(host);

        if (host != null)
        {
            // 挂接模式上报（UI 状态文本可见「背景：嵌入模式 / 跟随模式」，规格降级规则第 5 条）
            SessionHostModeChanged?.Invoke(hwnd, host.Mode);

            // A2 语义：壁纸在目标「身后」，目标不透明时画面不可见 → 自动应用轻度透明（规格 A2.4）
            if (host.Mode == HostAttachMode.Follow)
                await EnsureFollowVisibilityAsync(hwnd).ConfigureAwait(false);

            // 切出视频（视频 → 图片 / pkg / 无）：先解除视频组合呈现，视觉内容交还图像路径。
            // EndVideo 幂等，非视频会话调用为 no-op。
            //
            // v0.2.0 修正：判定依据从 Kind == Video 换成 Player is null。
            // PkgWallpaperLoader 对「pkg 内嵌视频」的处理是 VideoWallpaperLoader.Load() 之后
            // 用 with 表达式把 Kind 改写为 Pkg（Kind 必须保持 Pkg：它驱动 UI 的背景类型标签
            // 与 ExtractedRoot/SceneJsonPath 等 pkg 元数据语义），于是这三处写死
            // Kind == Video 的判定全部永假 → BeginVideo / Player.Play() 从不执行，
            // 而纯视频 pkg 的 ImageFrame 又为空 → 目标窗口内什么都不显示（纯色占位）。
            // 即 README「pkg 层：纯视频场景走视频层」的承诺是失效的。
            // 播放器存在与否才是「有没有视频要上屏」的真相，Kind 只管「资源从哪来」。
            if (!source.HasVideoPlayer)
                host.EndVideo();

            // 视频（规格 WP1-C ADR：组合呈现路径）：播放器经 GetSurface(compositor) 直接渲染进
            // 组合表面，SpriteVisual 按视频比例加边布局——零拷贝、零逐帧代码；帧率联动复用既有
            // 状态机（暂停态 → player.Pause() → 画面定格），无任何轮询。
            if (source.HasVideoPlayer && source.Player is { } videoPlayer)
            {
                if (host.BeginVideo(videoPlayer))
                {
                    // 关键：显式起播。渲染循环的 StateChanged 仅在暂停分支触发（活跃态无状态迁移事件），
                    // 新视频会话若不在此处 Play，播放器会停在「就绪未播」状态（WP1-C 实测定位，
                    // 见 WORKLOG）。后续的暂停/恢复联动仍由 OnStateChanged 的状态迁移驱动。
                    if (newSession.RenderLoop.Controller.ShouldRender)
                        videoPlayer.Play();
                }
                else
                {
                    Report("视频背景渲染初始化失败：组合呈现附着未成功（详见诊断日志）。" +
                           "已保留纯色占位，可尝试更新显卡驱动后重试。");
                }
            }

            // 图片 / pkg 降级封面（ImageFrame 非空，规格 B.3）：宿主 DComp 一次性上屏（动画 GIF 此处
            // 上的是首帧，后续帧由会话 OnFrameTick 按帧延迟推进，WP5）。
            // 像素快照走与预览共用的 BitmapSnapshot.CopyFrom（规格 B.3 要求逻辑下沉共享，勿复制两份）；
            // 静态图此后无逐帧工作——仅在尺寸变化 / 切换图片 / 设备失陷时由事件驱动重绘。
            if (source.ImageFrame is { } frame)
            {
                var snapshot = BitmapSnapshot.CopyFrom(frame);
                if (!host.RenderImage(snapshot))
                {
                    Report("背景画面渲染失败：DirectComposition 初始化未成功（详见诊断日志）。" +
                           "已保留纯色占位，可尝试更新显卡驱动后重试。");
                }

                // 记录加载时的显示器分辨率（WP1-D 跨屏重缩放依据）：Image 层的快照在导入时
                // 缩放到了「当时所在显示器」的分辨率（loader 目标分辨率参数）；跨屏移动后由
                // HandleClientSizeChangedAsync 比对，分辨率变化即按新显示器重新加载。
                if (source.Kind == WallpaperKind.Image
                    && Native.WallpaperNativeMethods.TryGetMonitorResolutionForWindow(hwnd, out var mw, out var mh))
                {
                    lock (_gate)
                    {
                        _monitorResAtLoad[hwnd] = (mw, mh);
                    }
                }
            }
        }

        // 视频层：先把播放器交给订阅方挂输出面（MediaPlayerElement.SetMediaPlayer），
        // 必须在旧会话 Dispose 之前发出，让 UI 尽快从旧播放器切换到新播放器。
        // 判定同样以 Player 为准（pkg 内嵌视频的 Kind 是 Pkg，见上文注释）。
        if (source.HasVideoPlayer && source.Player is { } readyPlayer)
            VideoPlayerReady?.Invoke(hwnd, readyPlayer);

        // 旧资源用毕即释放：先摘掉旧回调（避免释放期间在途帧回调摸到已释放资源），再 Dispose
        if (oldSession != null)
            oldSession.Dispose();

        // 恢复正常帧率状态（此前可能处于用户暂停）。
        //
        // v0.2.0 修正作用域：旧代码在这里调的是全局 ResumeRendering()，它会遍历
        // 【所有】会话清掉 PausedByUser —— 于是「给 A 窗口换一张壁纸」会把
        // B/C/D 窗口上用户手动按下的暂停全部解掉，用户没有任何提示就发现
        // 别的窗口壁纸又开始动了。刚换壁纸的只有 hwnd 这一个窗口，就只复位它。
        lock (_gate)
        {
            if (_sessions.TryGetValue(hwnd, out var currentSession))
                currentSession.RenderLoop.Controller.SetPausedByUser(false);
        }
    }

    /// <summary>
    /// 为目标窗口创建宿主（A1 → 失败自动降级 A2 在宿主内部完成）并登记到 <see cref="_hosts"/>。
    /// 在线程池后台线程调用（UpdateCoreAsync 已保证）；失败返回 null 并已上报原因。
    /// </summary>
    private WallpaperHostWindow? CreateHost(IntPtr hwnd)
    {
        var host = WallpaperHostWindow.TryCreate(hwnd, out var error);
        if (host == null)
        {
            // 文案三要素：失败 + 原因 + 用户能做什么（挂接失败时画面仍走应用内预览旁路）；
            // 目标权限更高（UIPI 错误 5，error 文本已注明）时给「管理员重启」引导（WP5）
            Report($"背景宿主挂接失败：{error}背景暂只在应用内预览显示；" +
                   "可重试；若目标以管理员权限运行，请以管理员身份重启 WinCanvas 后重试。");
            return null;
        }

        // 目标销毁 → 后台清理该会话与宿主。转线程池执行是死锁规避的关键：
        // TargetLost 在宿主 pump 线程上触发，若就地等待 _updateGates，而持闸门的
        // UpdateCoreAsync 正同步等待 pump 往返，两端互等即死锁（详见 WORKLOG 决策 4）。
        host.TargetLost += target => _ = Task.Run(() => CleanupSessionCoreAsync(target));

        // 宿主客户区变化（WinEvent 尺寸同步 / WM_DPICHANGED 之后）→ 跨屏分辨率重缩放检查（WP1-D）。
        // WinEvent 回调在 pump 线程触发：转线程池执行（重活不占 pump），判定与重挂在由
        // _updateGates 串行化，不会与在途壁纸更新互踩。
        host.ClientSizeChanged += (target, width, height) => _ = Task.Run(
            () => HandleClientSizeChangedAsync(target, width, height));

        // 挂接模式变化（A1 降级 A2）→ 透传给 UI（pump 线程触发，订阅方封送）
        host.AttachModeChanged += (target, mode) => SessionHostModeChanged?.Invoke(target, mode);

        lock (_gate)
        {
            _hosts[hwnd] = host;
        }

        return host;
    }

    /// <summary>
    /// 宿主客户区变化后的跨屏分辨率重缩放检查（规格 WP1-D：图片按新显示器分辨率重新缩放，
    /// 复用 <see cref="Loaders.ImageWallpaperLoader"/> 的目标分辨率参数）。
    /// 仅图片会话处理：加载时的显示器分辨率记录在 <see cref="_monitorResAtLoad"/>，
    /// 与当前显示器分辨率不一致（跨屏移动 / DPI 变化）时，用源路径重新走一次
    /// <see cref="UpdateWallpaperAsync"/>（新分辨率下重新解码缩放；同分辨率幂等跳过）。
    /// 视频会话无需处理——组合呈现的加边布局已随尺寸同步重算。
    /// 在线程池线程执行；与壁纸更新共用 _updateGates 闸门，不会与在途更新互踩。
    /// </summary>
    private Task HandleClientSizeChangedAsync(IntPtr hwnd, int clientWidth, int clientHeight)
    {
        WallpaperSession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(hwnd, out session))
                return Task.CompletedTask; // 会话已不在（切走 / 清理）
        }

        // 仅图片层按显示器分辨率重解码；pkg 封面与视频由各自的尺寸路径处理
        if (session.Source.Kind != WallpaperKind.Image || session.Host == null)
            return Task.CompletedTask;

        var originalPath = session.Source.OriginalPath;
        if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath))
            return Task.CompletedTask; // 源文件已被移走：档案保留，不重缩放

        if (!Native.WallpaperNativeMethods.TryGetMonitorResolutionForWindow(hwnd, out var monitorW, out var monitorH))
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_monitorResAtLoad.TryGetValue(hwnd, out var atLoad)
                && atLoad.W == monitorW && atLoad.H == monitorH)
            {
                return Task.CompletedTask; // 分辨率未变（同屏尺寸调整 / 移动），幂等跳过
            }

            _monitorResAtLoad[hwnd] = (monitorW, monitorH); // 先记账再重挂，防重复触发
        }

        HostDiagnostics.Log(
            $"跨屏重缩放 target=0x{hwnd:X} → 新显示器 {monitorW}x{monitorH}，按源路径重新加载：{originalPath}");
        return UpdateWallpaperAsync(hwnd, originalPath);
    }

    /// <summary>
    /// 判断某条渲染循环是否仍被其它在世会话持有（调用方须已持 <see cref="_gate"/>）。
    /// 循环按 hwnd 一对一创建，正常情况下移除本 hwnd 的会话后无人复用；
    /// 这里做一遍确认，纯粹是为了不给「清理与更新在闸门之外交错」留死角。
    /// </summary>
    private bool _loopsInUse(WallpaperRenderLoop loop)
    {
        foreach (var other in _sessions.Values)
        {
            if (ReferenceEquals(other.RenderLoop, loop))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 目标销毁后的会话清理（与壁纸更新共用 <see cref="_updateGates"/> 闸门串行，
    /// 防止与在途更新互踩：更新刚建好宿主、清理又把它销毁的交错）。
    /// 在线程池线程执行；会话与宿主幂等 Dispose。
    /// </summary>
    private async Task CleanupSessionCoreAsync(IntPtr hwnd)
    {
        var gate = _updateGates.GetOrAdd(hwnd, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            WallpaperSession? session;
            WallpaperHostWindow? host;
            lock (_gate)
            {
                _sessions.Remove(hwnd, out session);
                _hosts.Remove(hwnd, out host);
                _monitorResAtLoad.Remove(hwnd); // WP1-D：跨屏重缩放记账随会话清理
            }

            session?.Dispose();
            host?.Dispose();

            // v0.2.0 修线程泄漏：WallpaperRenderLoop 实现了 IDisposable，但旧代码在
            // 会话移除路径上只 Dispose 会话与宿主，【从不 Dispose 循环】——而会话的
            // Dispose 只摘事件不叫循环的 Dispose。于是目标窗口关闭后那条渲染线程
            // 永久存活：每 250ms 查一次前台/全屏、ShouldRender 为真时还按 60fps
            // 空转触发一个已经没人订阅的 FrameTick。壁纸开几个窗口再全关掉，
            // 桌面上就留下几条这样的僵尸线程（会话数 = 泄漏线程数）。
            // 只在「会话被移除」处收，不在 WallpaperSession.Dispose 里收——
            // 换壁纸时 UpdateCoreAsync 会复用同一个循环（见其 oldSession?.RenderLoop）。
            if (session != null)
            {
                lock (_gate)
                {
                    // 复用保护：若期间已有新会话接管了同一条循环，则不能停它
                    if (!_loopsInUse(session.RenderLoop))
                        session.RenderLoop.Dispose();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A2 跟随模式的可见性保障（规格 A2.4）：目标当前 alpha == 255 时自动 SetTransparencyAsync(220)
    /// 并提示；已有透明度（&lt; 255）则尊重用户设置不动。在线程池后台线程调用。
    /// </summary>
    private async Task EnsureFollowVisibilityAsync(IntPtr hwnd)
    {
        // 读取当前 alpha：目标未分层 / 从未设置过分层属性时读取失败，按不透明（255）处理
        if (HostNativeMethods.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
            && (flags & HostNativeMethods.LWA_ALPHA) != 0
            && alpha < 255)
        {
            return; // 用户已设置透明度，尊重现有值
        }

        if (_windowControl == null)
        {
            // 诊断工具等未注入窗口控制服务的场景：挂接照常，透明交由用户手动处理
            Report("背景挂接为跟随模式：目标需轻度透明才能透出背景（当前环境未接窗口控制服务，" +
                   "请在主程序中调低「透明度」滑块）。");
            return;
        }

        try
        {
            await _windowControl.SetTransparencyAsync(hwnd, 220).ConfigureAwait(false);
            Report("背景挂接为跟随模式：已自动应用轻度透明（alpha=220）以显示背景，可在滑块调整。");
        }
        catch (Exception ex)
        {
            // 目标可能刚好退出或权限不足：挂接本身已成功，透明失败只影响可见性，如实提示
            Report($"跟随模式自动透明失败：{ex.Message}。可手动调低「透明度」滑块以显示背景。");
        }
    }

    /// <inheritdoc />
    public void ShutdownAll()
    {
        // 应用退出路径：快照后清字典，再逐个 Dispose（幂等）。
        // Dispose 内部经宿主 pump 线程往返完成 Unhook + DestroyWindow（同步等待），
        // 返回时全部钩子已卸载、全部宿主窗口已销毁——满足「应用退出零残留」DoD。
        List<WallpaperSession> sessions;
        List<WallpaperHostWindow> hosts;
        lock (_gate)
        {
            sessions = new List<WallpaperSession>(_sessions.Values);
            hosts = new List<WallpaperHostWindow>(_hosts.Values);
            _sessions.Clear();
            _hosts.Clear();
            _monitorResAtLoad.Clear(); // WP1-D：跨屏重缩放记账随退出清理
        }

        foreach (var session in sessions)
            session.Dispose();

        foreach (var host in hosts)
            host.Dispose();

        // 退出路径同样收掉渲染循环：会话已 Dispose（事件摘净），循环此时无人引用，
        // 停掉它保证「应用退出零残留」不只是靠后台线程被进程回收兜底。
        foreach (var session in sessions)
            session.RenderLoop.Dispose();
    }

    /// <summary>
    /// 是否允许壁纸发声（v0.3.0 新增，<b>默认 false = 静音</b>）。
    ///
    /// 为什么默认关：壁纸是【背景】。让它默认带声音会在用户不知情时突然外放，
    /// 而且创意工坊素材的音量、曲目完全不可控 —— 静音必须是默认态，
    /// 出声是用户显式选择的例外。这也与迁移前「一律硬编码静音」的实际观感一致，
    /// 只是现在多了一个能打开的开关。
    ///
    /// 设置时【立即作用于已在播的会话】：用户打开开关就应该马上听到，
    /// 而不是等下次换壁纸才生效。
    /// </summary>
    public bool AudioEnabled
    {
        get => _audioEnabled;
        set
        {
            if (_audioEnabled == value)
            {
                return;
            }

            _audioEnabled = value;

            WallpaperSession[] snapshot;
            lock (_gate)
            {
                snapshot = _sessions.Values.ToArray();
            }

            foreach (WallpaperSession session in snapshot)
            {
                session.ApplyAudio(value);
            }
        }
    }

    private volatile bool _audioEnabled;

    /// <inheritdoc />
    public void PauseRendering()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
                session.RenderLoop.Controller.SetPausedByUser(true);
        }
    }

    /// <inheritdoc />
    public void ResumeRendering()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
                session.RenderLoop.Controller.SetPausedByUser(false);
        }
    }

    /// <summary>
    /// 按扩展名分派三层加载策略（在后台线程执行）。
    /// 扩展名口径统一取 <see cref="WallpaperFormats"/>（WP5 起的单一事实来源，勿再回填字面列表）。
    /// </summary>
    private WallpaperSource PrepareSource(IntPtr hwnd, string path)
    {
        // 第〇层：Wallpaper Engine 项目目录（v0.3.0 新增）。
        // 必须排在扩展名判定之前：项目目录的「扩展名」是空（目录名常是纯数字的工坊 Id），
        // 走下面的分支会一路掉到 NotSupportedException。
        // 判据是「该目录里有 project.json」，不是「它是个目录」——
        // 用户随便指一个图片文件夹时，仍应按后面的文件规则处理。
        if (Directory.Exists(path) && Loaders.WallpaperProjectLoader.IsProjectDirectory(path))
        {
            return Loaders.WallpaperProjectLoader.Load(path, AudioEnabled);
        }

        // 第一层：图片（含 GIF 多帧动画，WP5；导入时缩放到承载窗口所在显示器的分辨率）
        if (WallpaperFormats.IsImage(path))
        {
            Native.WallpaperNativeMethods.TryGetMonitorResolutionForWindow(hwnd, out var w, out var h);
            return Loaders.ImageWallpaperLoader.Load(path, w, h);
        }

        // 第二层：视频（Media Foundation 硬件解码）
        if (WallpaperFormats.IsVideo(path))
            return Loaders.VideoWallpaperLoader.Load(path, AudioEnabled);

        // 第三层：pkg 包（repkg 解包 + 纹理解码 + 场景分析）
        if (WallpaperFormats.IsPkg(path))
            return Loaders.PkgWallpaperLoader.Load(path);

        // 目录但不是 WE 项目：给出可操作的提示，而不是把目录路径当扩展名甩在错误信息里
        if (Directory.Exists(path))
        {
            throw new NotSupportedException(
                "所选文件夹不是 Wallpaper Engine 项目（里面没有 project.json）。" +
                "请直接选择图片 / 视频 / .pkg 文件，或选择含 project.json 的项目目录。");
        }

        throw new NotSupportedException(
            $"不支持的壁纸类型：{Path.GetExtension(path)}。支持图片（{string.Join(" ", WallpaperFormats.ImageExtensions)}）、" +
            $"视频（{string.Join(" ", WallpaperFormats.VideoExtensions)}）、.pkg，" +
            "以及 Wallpaper Engine 项目目录（含 project.json）。");
    }

    private void Report(string message) => StatusReported?.Invoke(message);

    private void RaiseImageFrameReady(IntPtr hwnd, SoftwareBitmap frame) =>
        ImageFrameReady?.Invoke(hwnd, frame);

    /// <summary>
    /// 会话渲染线程的动画帧上屏回调（WP5 GIF）：按 hwnd 找到当前宿主，把新帧快照交给
    /// DComp 渲染器重贴 + Commit（RenderImage 内部经 pump 封送并等待完成）。
    /// _gate 短临界区只读 <see cref="_hosts"/>；宿主缺失（挂接失败 / 已被清理）静默跳过——
    /// 无宿主即无上屏面，帧推进照常走（首帧仍可经预览旁路显示）。
    /// </summary>
    private void RenderHostFrame(IntPtr hwnd, BgraSnapshot snapshot)
    {
        WallpaperHostWindow? host;
        lock (_gate)
        {
            _hosts.TryGetValue(hwnd, out host);
        }

        host?.RenderImage(snapshot);
    }

    /// <summary>
    /// 一个承载窗口的壁纸会话：渲染循环 + 当前资源 + 宿主窗口引用。Dispose 时摘除回调并释放资源。
    /// </summary>
    private sealed class WallpaperSession : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly Action<IntPtr, SoftwareBitmap> _imageFrameReady;

        /// <summary>
        /// 动画帧上屏回调（WP5 GIF，由服务层注入 <see cref="WallpaperService.RenderHostFrame"/>）：
        /// 渲染线程推进到新帧时调用，把快照交给宿主 DComp 渲染器。null = 无宿主可用（诊断场景），跳过上屏。
        /// </summary>
        private readonly Action<IntPtr, BgraSnapshot>? _renderHostFrame;

        /// <summary>会话是否已释放（volatile：MF 事件线程的视频帧闭包须及时看到 Dispose）。仅守卫用途。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 宿主窗口引用（WP1-A）：由服务层在会话替换后、旧会话释放前挂接（AttachHost），
        /// 换壁纸时经 <see cref="DetachHost"/> 摘出复用。读写只发生在 _updateGates 串行化的
        /// 更新路径与 _tickGate 保护的 Dispose 路径，无需额外锁。
        /// 宿主的销毁权在服务层（TargetLost 清理 / ShutdownAll），会话 Dispose 只解除引用。
        /// </summary>
        public WallpaperHostWindow? Host { get; private set; }

        /// <summary>挂接宿主（服务层在会话替换完成后调用）。</summary>
        public void AttachHost(WallpaperHostWindow? host) => Host = host;

        /// <summary>摘下宿主引用但不销毁（换壁纸复用宿主时由服务层调用）。</summary>
        public WallpaperHostWindow? DetachHost()
        {
            var host = Host;
            Host = null;
            return host;
        }

        /// <summary>是否已推过图片首帧 / 动画是否已起表（仅在 _tickGate 锁内读写）。</summary>
        private bool _framePushed;

        /// <summary>动画当前帧索引（WP5 GIF；仅在 _tickGate 锁内读写——渲染线程串行访问）。</summary>
        private int _frameIndex;

        /// <summary>
        /// 动画下一帧到期时刻（TickCount64 毫秒；仅在 _tickGate 锁内读写）。
        /// 到期才推进并重贴新帧——FrameTick 以 60/15fps 高频触发，按延迟门槛过滤，绝无逐帧重贴。
        /// </summary>
        private long _nextFrameDueMs;

        /// <summary>会话是否已释放。volatile：渲染线程必须及时看到其他线程写入的最新值。</summary>
        private volatile bool _disposed;

        /// <summary>会话内部锁：把 Dispose（线程池线程）与渲染线程上的在途回调互斥，
        /// 确保摘除回调与释放资源之间没有正在执行的 OnFrameTick / OnStateChanged。</summary>
        private readonly object _tickGate = new();

        public WallpaperSession(
            IntPtr hwnd,
            WallpaperRenderLoop loop,
            WallpaperSource source,
            Action<IntPtr, SoftwareBitmap> imageFrameReady,
            Action<IntPtr, BgraSnapshot>? renderHostFrame)
        {
            _hwnd = hwnd;
            RenderLoop = loop;
            Source = source;
            _imageFrameReady = imageFrameReady;
            _renderHostFrame = renderHostFrame;

            loop.FrameTick += OnFrameTick;
            loop.StateChanged += OnStateChanged;
        }

        public WallpaperRenderLoop RenderLoop { get; }

        public WallpaperSource Source { get; }

        /// <summary>
        /// 渲染线程每帧回调（60/15fps 由控制器决定）：
        /// 静态图片只在首次出帧时把已缩放帧推给订阅方（SoftwareBitmap 内容不变，无需重复上屏）；
        /// 动画（GIF 多帧，WP5）按 <see cref="WallpaperAnimationFrame.Delay"/> 推进——到期才重贴新帧
        /// 到宿主 DComp 表面（<see cref="AdvanceAnimationFrame"/>），无帧到期则本回调空转返回；
        /// 暂停态（全屏 / 用户暂停）渲染循环不出帧，动画自然定格，恢复后继续推进；
        /// 视频层由 MF 自主出帧，无需逐帧干预。
        /// 预览旁路保持「只显示首帧」（ImageFrameReady 仅静态首帧触发一次；规格 WP5 明示预览可只显首帧）。
        /// 订阅方 / 上屏回调应在回调内【同步】复制像素 —— 本锁保证复制完成后才可能发生 Dispose 释放资源。
        /// </summary>
        public void OnFrameTick()
        {
            lock (_tickGate)
            {
                if (_disposed)
                    return;

                // 动画路径：多帧源（GIF）按帧延迟推进上屏；单帧路径：维持「只推一次」的既有语义
                if (Source.ImageFrames is { Count: > 1 } frames)
                {
                    AdvanceAnimationFrame(frames);
                    return;
                }

                if (_framePushed)
                    return;

                var frame = Source.ImageFrame;
                if (frame == null)
                    return;

                _framePushed = true;
                _imageFrameReady(_hwnd, frame);
            }
        }

        /// <summary>
        /// 动画帧推进（渲染线程，_tickGate 锁内调用）：首帧由 UpdateCoreAsync 的宿主渲染路径
        /// 一次性上屏（ImageFrame = frames[0]，此处只起表计时），此后到期即推进索引、
        /// 同步复制新帧像素（与 ImageFrameReady 的复制纪律一致：在会话锁内复制，
        /// Dispose 不可能并发释放）并经服务层回调交宿主重贴。
        /// 上屏失败（宿主缺失 / DComp 失败）不致命：下一帧到期自动重试，帧推进节拍不受影响。
        /// </summary>
        private void AdvanceAnimationFrame(IReadOnlyList<WallpaperAnimationFrame> frames)
        {
            var now = Environment.TickCount64;

            if (!_framePushed)
            {
                // 起表：首帧此刻已上屏（静态路径），从首帧展示时长起算第一次推进
                _framePushed = true;
                _frameIndex = 0;
                _nextFrameDueMs = now + (long) frames[0].Delay.TotalMilliseconds;
                return;
            }

            if (now < _nextFrameDueMs)
                return; // 当前帧展示期未到：FrameTick 高频触发在这里被门槛过滤，绝无逐帧重贴

            _frameIndex = (_frameIndex + 1) % frames.Count;
            var animationFrame = frames[_frameIndex];

            // 展示期下限 1ms：防 0 延迟帧把推进间隔归零（加载器已兜底 100ms，此处双保险）
            _nextFrameDueMs = now + Math.Max(1, (long) animationFrame.Delay.TotalMilliseconds);

            if (_renderHostFrame == null)
                return;

            try
            {
                // 渲染线程内同步复制像素（Bgra8 快照），再经 pump 封送上屏（重活不占 UI 线程）
                var snapshot = BitmapSnapshot.CopyFrom(animationFrame.Frame);
                _renderHostFrame(_hwnd, snapshot);
            }
            catch
            {
                // 单帧快照 / 上屏异常不杀死渲染线程（渲染循环本就吞 FrameTick 异常；
                // 这里捕获是为了不让异常打断 _nextFrameDueMs 的推进节奏）
            }
        }

        /// <summary>
        /// 应用全局声音开关到本会话的播放器（v0.3.0）。
        /// 无播放器（图片 / GIF / pkg 封面降级）时为 no-op —— 那些本来就不发声。
        /// 加锁与帧回调互斥，避免在途回调摸到正在改的播放器。
        /// </summary>
        /// <param name="enabled">true = 允许发声。</param>
        public void ApplyAudio(bool enabled)
        {
            lock (_tickGate)
            {
                if (_disposed)
                {
                    return;
                }

                MediaPlayer? player = Source.Player;
                if (player == null)
                {
                    return;
                }

                try
                {
                    player.IsMuted = !enabled;
                    player.Volume = enabled ? Loaders.VideoWallpaperLoader.DefaultPlaybackVolume : 0d;
                }
                catch (Exception ex)
                {
                    // 播放器可能正在重建 Source：音量设置失败不影响画面
                    HostDiagnostics.Log($"壁纸音量设置失败 host=0x{_hwnd:X}：{ex.GetType().Name}");
                }
            }
        }

        /// <summary>帧率状态变化：暂停渲染时同步暂停视频解码，恢复时继续播放，省电且省解码器占用。</summary>
        public void OnStateChanged(WallpaperRenderState state)
        {
            lock (_tickGate)
            {
                if (_disposed)
                    return;

                var player = Source.Player;
                if (player == null)
                    return;

                try
                {
                    switch (state)
                    {
                        case WallpaperRenderState.Active60Fps:
                        case WallpaperRenderState.Inactive15Fps:
                            // 仅在播放器处于暂停态时恢复，避免重复 Play
                            if (player.PlaybackSession.PlaybackState
                                is Windows.Media.Playback.MediaPlaybackState.Paused
                                    or Windows.Media.Playback.MediaPlaybackState.Opening)
                            {
                                player.Play();
                            }

                            break;

                        case WallpaperRenderState.PausedFullscreen:
                        case WallpaperRenderState.PausedByUser:
                            if (player.PlaybackSession.CanPause)
                                player.Pause();

                            break;
                    }
                }
                catch
                {
                    // 播放器可能正在重建 Source；状态同步失败可安全忽略（下个状态变化再试）
                }
            }
        }

        public void Dispose()
        {
            // 与渲染线程的在途回调互斥：先等当前 OnFrameTick / OnStateChanged 执行完，
            // 再摘回调、置失效标志并释放资源，保证已推给订阅方的帧不会被并发 Dispose。
            lock (_tickGate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                RenderLoop.FrameTick -= OnFrameTick;
                RenderLoop.StateChanged -= OnStateChanged;
                Source.Dispose();
                Host = null; // 只解除引用：宿主销毁由服务层统一管理（复用 / TargetLost 清理 / ShutdownAll）
            }
        }
    }
}
