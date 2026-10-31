using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using WinCanvas.Core.Interfaces;
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

    /// <summary>状态上报（pkg 降级提示、加载错误等），供 UI 层订阅展示。触发线程可能是工作线程。</summary>
    public event Action<string>? StatusReported;

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

    /// <summary>单次壁纸更新的完整流程（已由调用方按 hwnd 串行化）。</summary>
    private Task UpdateCoreAsync(IntPtr hwnd, string path)
    {
        WallpaperSource source;
        try
        {
            source = PrepareSource(hwnd, path);
        }
        catch (Exception ex)
        {
            Report($"壁纸资源加载失败（{Path.GetFileName(path)}）：{ex.Message}");
            return Task.CompletedTask;
        }

        if (source.StatusMessage != null)
            Report(source.StatusMessage);

        if (source.Kind == WallpaperKind.Image && source.ImageFrame == null)
        {
            Report($"壁纸资源不可渲染（{Path.GetFileName(path)}）。");
            source.Dispose();
            return Task.CompletedTask;
        }

        WallpaperSession? oldSession;
        lock (_gate)
        {
            _sessions.TryGetValue(hwnd, out oldSession);

            // 复用已有渲染循环（每窗口一个循环）；没有则新建并启动。
            // 整个「查旧会话 → 建/复用循环 → 写 _sessions → 启动」序列都在锁内完成，
            // 并发调用下不会出现两个循环同时新建（会话替换的先后顺序另由 _updateGates 保证）。
            var loop = oldSession?.RenderLoop ?? new WallpaperRenderLoop();
            var newSession = new WallpaperSession(hwnd, loop, source, RaiseImageFrameReady);
            _sessions[hwnd] = newSession;

            if (oldSession == null)
                loop.Start();
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
        return Task.CompletedTask;
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
    /// 一个承载窗口的壁纸会话：渲染循环 + 当前资源。Dispose 时摘除回调并释放资源。
    /// </summary>
    private sealed class WallpaperSession : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly Action<IntPtr, SoftwareBitmap> _imageFrameReady;

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
            }
        }
    }
}
