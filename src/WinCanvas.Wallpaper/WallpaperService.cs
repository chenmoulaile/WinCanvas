using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
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
            newSession = new WallpaperSession(hwnd, loop, source, RaiseImageFrameReady);
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
        }

        // 视频层：先把播放器交给订阅方挂输出面（MediaPlayerElement.SetMediaPlayer），
        // 必须在旧会话 Dispose 之前发出，让 UI 尽快从旧播放器切换到新播放器。
        if (source.Kind == WallpaperKind.Video && source.Player != null)
            VideoPlayerReady?.Invoke(hwnd, source.Player);

        // 旧资源用毕即释放：先摘掉旧回调（避免释放期间在途帧回调摸到已释放资源），再 Dispose
        if (oldSession != null)
            oldSession.Dispose();

        // 恢复正常帧率状态（此前可能处于用户暂停）
        ResumeRendering();
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
            // 文案三要素：失败 + 原因 + 用户能做什么（挂接失败时画面仍走应用内预览旁路）
            Report($"背景宿主挂接失败：{error}背景暂只在应用内预览显示；" +
                   "可重试，或检查目标窗口是否以更高权限运行。");
            return null;
        }

        // 目标销毁 → 后台清理该会话与宿主。转线程池执行是死锁规避的关键：
        // TargetLost 在宿主 pump 线程上触发，若就地等待 _updateGates，而持闸门的
        // UpdateCoreAsync 正同步等待 pump 往返，两端互等即死锁（详见 WORKLOG 决策 4）。
        host.TargetLost += target => _ = Task.Run(() => CleanupSessionCoreAsync(target));

        // 挂接模式变化（A1 降级 A2）→ 透传给 UI（pump 线程触发，订阅方封送）
        host.AttachModeChanged += (target, mode) => SessionHostModeChanged?.Invoke(target, mode);

        lock (_gate)
        {
            _hosts[hwnd] = host;
        }

        return host;
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
            }

            session?.Dispose();
            host?.Dispose();
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
        }

        foreach (var session in sessions)
            session.Dispose();

        foreach (var host in hosts)
            host.Dispose();
    }

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
    /// </summary>
    private WallpaperSource PrepareSource(IntPtr hwnd, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // 第一层：静态图片（导入时缩放到承载窗口所在显示器的分辨率）
        if (Loaders.ImageWallpaperLoader.IsSupported(path))
        {
            Native.WallpaperNativeMethods.TryGetMonitorResolutionForWindow(hwnd, out var w, out var h);
            return Loaders.ImageWallpaperLoader.Load(path, w, h);
        }

        // 第二层：视频（Media Foundation 硬件解码）
        if (Loaders.VideoWallpaperLoader.IsSupported(path))
            return Loaders.VideoWallpaperLoader.Load(path);

        // 第三层：pkg 包（repkg 解包 + 纹理解码 + 场景分析）
        if (ext == ".pkg")
            return Loaders.PkgWallpaperLoader.Load(path);

        throw new NotSupportedException($"不支持的壁纸类型：{ext}。支持图片 / 视频 / .pkg。");
    }

    private void Report(string message) => StatusReported?.Invoke(message);

    private void RaiseImageFrameReady(IntPtr hwnd, SoftwareBitmap frame) =>
        ImageFrameReady?.Invoke(hwnd, frame);

    /// <summary>
    /// 一个承载窗口的壁纸会话：渲染循环 + 当前资源 + 宿主窗口引用。Dispose 时摘除回调并释放资源。
    /// </summary>
    private sealed class WallpaperSession : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly Action<IntPtr, SoftwareBitmap> _imageFrameReady;

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

        /// <summary>是否已推过图片首帧（仅在 _tickGate 锁内读写）。</summary>
        private bool _framePushed;

        /// <summary>会话是否已释放。volatile：渲染线程必须及时看到其他线程写入的最新值。</summary>
        private volatile bool _disposed;

        /// <summary>会话内部锁：把 Dispose（线程池线程）与渲染线程上的在途回调互斥，
        /// 确保摘除回调与释放资源之间没有正在执行的 OnFrameTick / OnStateChanged。</summary>
        private readonly object _tickGate = new();

        public WallpaperSession(
            IntPtr hwnd,
            WallpaperRenderLoop loop,
            WallpaperSource source,
            Action<IntPtr, SoftwareBitmap> imageFrameReady)
        {
            _hwnd = hwnd;
            RenderLoop = loop;
            Source = source;
            _imageFrameReady = imageFrameReady;

            loop.FrameTick += OnFrameTick;
            loop.StateChanged += OnStateChanged;
        }

        public WallpaperRenderLoop RenderLoop { get; }

        public WallpaperSource Source { get; }

        /// <summary>
        /// 渲染线程每帧回调（60/15fps 由控制器决定）：
        /// 图片层只在首次出帧时把已缩放帧推给订阅方（SoftwareBitmap 内容不变，无需重复上屏，
        /// 保留帧回调是为后续自定义动画效果接入）；视频层由 MF 自主出帧，无需逐帧干预。
        /// 订阅方应在回调内【同步】复制像素 —— 本锁保证复制完成后才可能发生 Dispose 释放资源。
        /// </summary>
        public void OnFrameTick()
        {
            lock (_tickGate)
            {
                if (_disposed || _framePushed)
                    return;

                var frame = Source.ImageFrame;
                if (frame == null)
                    return;

                _framePushed = true;
                _imageFrameReady(_hwnd, frame);
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
