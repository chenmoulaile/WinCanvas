using System;

namespace WinCanvas.Wallpaper.Models;

/// <summary>
/// 壁纸渲染状态（动态帧率控制器的输出）。
/// </summary>
public enum WallpaperRenderState
{
    /// <summary>承载壁纸的窗口处于前台激活状态：目标 60fps。</summary>
    Active60Fps = 0,

    /// <summary>窗口存在但非激活（后台）：目标 15fps 省电。</summary>
    Inactive15Fps = 1,

    /// <summary>检测到全屏前台窗口：暂停渲染。</summary>
    PausedFullscreen = 2,

    /// <summary>用户/系统显式暂停（省电模式、窗口不可见等）。</summary>
    PausedByUser = 3,
}
