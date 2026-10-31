using System;
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

    /// <summary>状态上报（pkg 降级提示、加载错误等），供 UI 层订阅展示。触发线程可能是工作线程。</summary>
    public event Action<string>? StatusReported;

    /// <summary>
    /// 图片层新帧就绪：参数为窗口句柄与导入时已缩放好的 SoftwareBitmap。
    /// 在工作线程触发；UI 层需自行封送到 Dispatcher 后转 WriteableBitmap / CanvasBitmap 上屏。
    /// </summary>
    public event Action<IntPtr, SoftwareBitmap>? ImageFrameReady;

    /// <inheritdoc />
    public Task InitializeWallpaperAsync(IntPtr hwnd, string path)
    {
        // 与切换同一实现：已有会话则先释放旧资源再建新的
        return UpdateWallpaperAsync(hwnd, path);
    }

    /// <inheritdoc />
    public Task UpdateWallpaperAsync(IntPtr hwnd, string path)
    {
        // 准备资源是潜在重活（图片解码缩放 / pkg 解包），放到线程池执行，绝不占用调用方（UI）线程
        return Task.Run(() =>
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

                // 复用已有渲染循环（每窗口一个循环）；没有则新建并启动
                var loop = oldSession?.RenderLoop ?? new WallpaperRenderLoop();
                newSession = new WallpaperSession(hwnd, loop, source, RaiseImageFrameReady);
                _sessions[hwnd] = newSession;

                if (oldSession == null)
                    loop.Start();
            }

            // 旧资源用毕即释放：先摘掉旧回调（避免释放期间在途帧回调摸到已释放资源），再 Dispose
            if (oldSession != null)
                oldSession.Dispose();

            // 恢复正常帧率状态（此前可能处于用户暂停）
            ResumeRendering();
        });
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
        private volatile bool _framePushed;
        private bool _disposed;

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
        /// </summary>
        public void OnFrameTick()
        {
            if (_disposed || _framePushed)
                return;

            var frame = Source.ImageFrame;
            if (frame == null)
                return;

            _framePushed = true;
            _imageFrameReady(_hwnd, frame);
        }

        /// <summary>帧率状态变化：暂停渲染时同步暂停视频解码，恢复时继续播放，省电且省解码器占用。</summary>
        public void OnStateChanged(WallpaperRenderState state)
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

        public void Dispose()
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
