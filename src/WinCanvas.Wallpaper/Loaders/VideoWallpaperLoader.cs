using System;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Loaders;

/// <summary>
/// 第二层：视频加载器（扩展名口径见 <see cref="WallpaperFormats.VideoExtensions"/>）。
/// 底层走 Windows.Media.Playback.MediaPlayer —— 由 Media Foundation 完成硬件加速解码；
/// WinUI 的 MediaPlayerElement 只是该播放器的 XAML 外壳，App 端可用
/// <c>mediaPlayerElement.SetMediaPlayer(source.Player)</c> 直接挂接本服务创建的实例。
/// </summary>
public static class VideoWallpaperLoader
{
    /// <summary>
    /// 支持的视频扩展名（小写、含点）。
    /// WP5 起【不再本类自定义】，直接别名 <see cref="WallpaperFormats.VideoExtensions"/>——
    /// 扩展名口径全仓库单一事实来源，此处仅为既有公共 API 兼容保留。
    /// </summary>
    public static readonly string[] SupportedExtensions = WallpaperFormats.VideoExtensions;

    /// <summary>判断路径是否为本层支持的视频（口径同 <see cref="WallpaperFormats.IsVideo"/>）。</summary>
    public static bool IsSupported(string path) => WallpaperFormats.IsVideo(path);

    /// <summary>
    /// 允许发声时使用的音量（0~1）。
    /// 刻意不满值 1.0：壁纸是【背景】，长期满音量播放带配乐的素材会盖过前台应用的音频，
    /// 0.6 是「能听清但不抢戏」的折中，用户仍可在系统音量合成器里单独调本进程。
    /// </summary>
    public const double DefaultPlaybackVolume = 0.6;

    /// <summary>
    /// 创建视频壁纸播放器（不自动播放，由服务在帧率状态机控制下启停）。
    /// </summary>
    /// <param name="path">视频文件路径。</param>
    /// <param name="audioEnabled">
    /// 是否允许发声。<b>默认 false（静音）</b>。
    /// v0.3.0 前这里是【无条件静音】（旧注释写「壁纸不应出声」，直接把 IsMuted 写死 true），
    /// 于是「壁纸带音乐却听不见」没有任何开关可以改变 —— 而 Wallpaper Engine 的视频壁纸
    /// 普遍自带音轨（实测库里 33 个视频项目都是）。现在由调用方（WallpaperService）
    /// 按用户的全局设置传入，默认值保持静音不变，不改变既有行为。
    /// </param>
    /// <returns>已就绪的壁纸资源（Player 非空，IsLoopingEnabled = true）。</returns>
    /// <exception cref="FileNotFoundException">视频文件不存在。</exception>
    public static WallpaperSource Load(string path, bool audioEnabled = false)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("壁纸视频文件不存在。", path);

        // Windows.Media.Playback.MediaPlayer：
        // 内部由 Media Foundation（MF MediaSession）驱动，H.264/HEVC/VP9 走 GPU 解码。
        // 视频呈现走组合呈现路径（WP1-C ADR：player.GetSurface(compositor) + SpriteVisual），
        // MF 直接把解码帧渲染进组合表面——不启用帧服务器（IsVideoFrameServerEnabled 保持 false：
        // 帧服务器路径的 CopyFrameToVideoSurface 对外部交换链缓冲实测 AV，见 WORKLOG 阶段 C ADR）。
        var player = new MediaPlayer
        {
            // 壁纸循环播放
            IsLoopingEnabled = true,
            // Media 类别：走系统主音量通道，用户可在音量合成器里单独调这个进程的音量
            AudioCategory = MediaPlayerAudioCategory.Media,
            // 默认静音；允许发声时也要留一个明确的音量值而不是依赖系统默认
            IsMuted = !audioEnabled,
            Volume = audioEnabled ? DefaultPlaybackVolume : 0d,
        };

        // MediaSource.CreateFromUri 内部按需懒加载，不会一次性把视频读进内存
        player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));

        return new WallpaperSource
        {
            Kind = WallpaperKind.Video,
            Player = player,
            VideoPath = Path.GetFullPath(path),
            OriginalPath = Path.GetFullPath(path),
            // 视频壁纸一律「可能发声」：是否真有声由上面的 IsMuted/Volume 决定
            HasAudioTrack = true,
        };
    }
}
