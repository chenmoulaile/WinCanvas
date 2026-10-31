using System;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Loaders;

/// <summary>
/// 第二层：视频加载器（.mp4）。
/// 底层走 Windows.Media.Playback.MediaPlayer —— 由 Media Foundation 完成硬件加速解码；
/// WinUI 的 MediaPlayerElement 只是该播放器的 XAML 外壳，App 端可用
/// <c>mediaPlayerElement.SetMediaPlayer(source.Player)</c> 直接挂接本服务创建的实例。
/// </summary>
public static class VideoWallpaperLoader
{
    /// <summary>支持的视频扩展名（小写、含点）。</summary>
    public static readonly string[] SupportedExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".wmv", ".mkv" };

    /// <summary>判断路径是否为本层支持的视频。</summary>
    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// 创建视频壁纸播放器（不自动播放，由服务在帧率状态机控制下启停）。
    /// </summary>
    /// <param name="path">视频文件路径。</param>
    /// <returns>已就绪的壁纸资源（Player 非空，IsLoopingEnabled = true）。</returns>
    /// <exception cref="FileNotFoundException">视频文件不存在。</exception>
    public static WallpaperSource Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("壁纸视频文件不存在。", path);

        // Windows.Media.Playback.MediaPlayer：
        // 内部由 Media Foundation（MF MediaSession）驱动，H.264/HEVC/VP9 走 GPU 解码
        var player = new MediaPlayer
        {
            // 壁纸循环播放
            IsLoopingEnabled = true,
            // 壁纸不应出声：Media 类别 + 显式静音（MediaPlayerAudioCategory 无 BackgroundOnly 值）
            AudioCategory = MediaPlayerAudioCategory.Media,
            IsMuted = true,
        };

        // MediaSource.CreateFromUri 内部按需懒加载，不会一次性把视频读进内存
        player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));

        return new WallpaperSource
        {
            Kind = WallpaperKind.Video,
            Player = player,
            VideoPath = Path.GetFullPath(path),
            OriginalPath = Path.GetFullPath(path),
        };
    }
}
