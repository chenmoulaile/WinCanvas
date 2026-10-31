using System;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Rendering;

/// <summary>
/// 动态帧率控制器：纯状态机，不持有线程资源。
/// 三档策略：
///   窗口激活   -> 60fps
///   窗口非激活 -> 15fps
///   全屏前台窗口 / 用户暂停 -> 完全暂停渲染
/// 状态变化通过 <see cref="StateChanged"/> 通知（触发线程 = 调用 Set* 的线程）。
/// </summary>
public sealed class DynamicFrameRateController
{
    private volatile bool _windowActive = true;
    private volatile bool _fullscreenDetected;
    private volatile bool _pausedByUser;

    /// <summary>窗口激活时的目标帧率。</summary>
    public const double ActiveFps = 60.0;

    /// <summary>窗口非激活时的目标帧率（省电档）。</summary>
    public const double InactiveFps = 15.0;

    /// <summary>状态变化事件：参数为新的渲染状态。触发线程 = 调用 Set* 方法的线程。</summary>
    public event Action<WallpaperRenderState>? StateChanged;

    /// <summary>当前渲染状态。</summary>
    public WallpaperRenderState CurrentState
    {
        get
        {
            if (_pausedByUser)
                return WallpaperRenderState.PausedByUser;
            if (_fullscreenDetected)
                return WallpaperRenderState.PausedFullscreen;
            return _windowActive
                ? WallpaperRenderState.Active60Fps
                : WallpaperRenderState.Inactive15Fps;
        }
    }

    /// <summary>是否应当渲染本帧（两种暂停态均返回 false）。</summary>
    public bool ShouldRender => CurrentState is WallpaperRenderState.Active60Fps or WallpaperRenderState.Inactive15Fps;

    /// <summary>当前目标帧间隔；暂停态返回较大间隔（供循环休眠用）。</summary>
    public TimeSpan FrameInterval
    {
        get
        {
            var fps = CurrentState switch
            {
                WallpaperRenderState.Active60Fps => ActiveFps,
                WallpaperRenderState.Inactive15Fps => InactiveFps,
                // 暂停态不渲染；循环按 ~8fps 的频率轮询状态恢复条件
                _ => 8.0,
            };

            return TimeSpan.FromMilliseconds(1000.0 / fps);
        }
    }

    /// <summary>设置承载壁纸的窗口是否处于前台激活状态。</summary>
    /// <param name="active">true = 激活（60fps）；false = 非激活（15fps）。</param>
    public void SetWindowActive(bool active)
    {
        if (_windowActive == active)
            return;

        _windowActive = active;
        StateChanged?.Invoke(CurrentState);
    }

    /// <summary>由全屏监视器回填：当前是否存在全屏前台窗口。</summary>
    /// <param name="fullscreen">true = 存在全屏前台窗口，应暂停渲染。</param>
    public void SetFullscreenDetected(bool fullscreen)
    {
        if (_fullscreenDetected == fullscreen)
            return;

        _fullscreenDetected = fullscreen;
        StateChanged?.Invoke(CurrentState);
    }

    /// <summary>用户/系统显式暂停（对应 IWallpaperService.PauseRendering）。</summary>
    public void SetPausedByUser(bool paused)
    {
        if (_pausedByUser == paused)
            return;

        _pausedByUser = paused;
        StateChanged?.Invoke(CurrentState);
    }
}
