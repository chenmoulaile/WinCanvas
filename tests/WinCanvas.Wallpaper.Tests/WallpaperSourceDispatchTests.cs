using WinCanvas.Wallpaper.Models;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// 壁纸资源「走哪条呈现路径」的判定口径用例（v0.2.0 引入）。
///
/// 背景缺陷：WallpaperService 原先用 <c>Kind == WallpaperKind.Video</c> 决定
/// 要不要做视频组合呈现，而 pkg 内嵌视频场景经 PkgWallpaperLoader 处理后
/// Kind 被改写为 <c>Pkg</c>（这是必须的：Kind 驱动 UI 的背景类型标签与
/// ExtractedRoot / SceneJsonPath 等 pkg 元数据语义）。结果三处判定全永假，
/// BeginVideo / Player.Play() 从不执行，而纯视频 pkg 的 ImageFrame 又为空 →
/// 目标窗口内什么都不显示，README「纯视频场景走视频层」的承诺失效。
///
/// 这些用例把「Kind 与呈现路径互不蕴含」这条不变式钉死，防止再被混用。
/// </summary>
public sealed class WallpaperSourceDispatchTests
{
    [Fact]
    public void Pkg内嵌视频_Kind是Pkg但仍有播放器_必须走视频呈现()
    {
        // 复现 PkgWallpaperLoader 对纯视频场景的产物形态：
        // 播放器来自 VideoWallpaperLoader，Kind 被 with 改写成 Pkg
        using var player = new Windows.Media.Playback.MediaPlayer();

        var pkgVideo = new WallpaperSource
        {
            Kind = WallpaperKind.Pkg,
            Player = player,
            ExtractedRoot = @"C:\temp\wallpaper-cache\demo-abc",
            VideoPath = @"C:\temp\wallpaper-cache\demo-abc\main.mp4",
        };

        Assert.Equal(WallpaperKind.Pkg, pkgVideo.Kind);
        Assert.True(pkgVideo.HasVideoPlayer);

        // 旧口径（Kind == Video）在这里为假 —— 那正是缺陷的根因
        Assert.NotEqual(WallpaperKind.Video, pkgVideo.Kind);
    }

    [Fact]
    public void 直接视频文件_Kind与播放器同时成立()
    {
        using var player = new Windows.Media.Playback.MediaPlayer();

        var video = new WallpaperSource
        {
            Kind = WallpaperKind.Video,
            Player = player,
            VideoPath = @"C:\media\loop.mp4",
        };

        Assert.True(video.HasVideoPlayer);
        Assert.Equal(WallpaperKind.Video, video.Kind);
    }

    [Fact]
    public void pkg降级封面_有图无播放器_走图像呈现()
    {
        // SceneScript 复杂场景降级：只显示封面图，此时绝不能去碰视频组合路径
        // SoftwareBitmap 的公开构造在投影层不可直接用，走 CreateCopyFromBuffer 工厂
        var pixels = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(
            new byte[2 * 2 * 4]);
        using var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            pixels,
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            2, 2,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var cover = new WallpaperSource
        {
            Kind = WallpaperKind.Pkg,
            ImageFrame = bitmap,
            CoverImagePath = @"C:\temp\wallpaper-cache\demo\preview.png",
        };

        Assert.False(cover.HasVideoPlayer);
        Assert.NotNull(cover.ImageFrame);
    }

    [Fact]
    public void 静态图片_既非视频也无pkg元数据()
    {
        var image = new WallpaperSource { Kind = WallpaperKind.Image };

        Assert.False(image.HasVideoPlayer);
        Assert.Null(image.ExtractedRoot);
    }

    [Fact]
    public void 释放后播放器引用不复活_HasVideoPlayer随Player走()
    {
        var player = new Windows.Media.Playback.MediaPlayer();
        var source = new WallpaperSource { Kind = WallpaperKind.Video, Player = player };

        Assert.True(source.HasVideoPlayer);

        // with 表达式换掉 Player 之后，判定必须跟着变 ——
        // 服务层靠 EndVideo/BeginVideo 的切换就是吃这个语义
        var switched = source with { Player = null, Kind = WallpaperKind.Image };

        Assert.False(switched.HasVideoPlayer);
        Assert.True(source.HasVideoPlayer); // 原实例不受影响（record 值语义）

        player.Dispose();
        source.Dispose();
        switched.Dispose();
    }

    // ---------- 扩展名口径（单一来源）与呈现路径必须一致 ----------

    [Theory]
    [InlineData("a.gif", WallpaperKind.Image)]
    [InlineData("a.PNG", WallpaperKind.Image)]
    [InlineData("a.webp", WallpaperKind.Image)]
    [InlineData("a.mp4", WallpaperKind.Video)]
    [InlineData("a.mkv", WallpaperKind.Video)]
    [InlineData("a.pkg", WallpaperKind.Pkg)]
    public void 扩展名分派结果与预期一致(string fileName, WallpaperKind expected)
    {
        // DetectKind 对无法识别的扩展名返回 null（由调用方决定怎么报错），
        // 这里断言的是可识别集合的口径
        Assert.Equal(expected, WallpaperFormats.DetectKind(fileName));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.exe")]
    [InlineData("noextension")]
    public void 未知扩展名返回null而不是猜一个类型(string fileName)
    {
        // 把未知文件当成图片去解码，失败信息会误导用户；返回 null 才能给出「不支持的格式」
        Assert.Null(WallpaperFormats.DetectKind(fileName));
    }

    [Fact]
    public void 图片类扩展名不会误判为需要视频呈现()
    {
        // WallpaperFormats 是扩展名的单一来源；这里守住「图片层不产生 Player」的口径，
        // 与上面的视频判定共同构成完整的分派契约
        foreach (var ext in WallpaperFormats.ImageExtensions)
        {
            Assert.NotEqual(WallpaperKind.Video, WallpaperFormats.DetectKind("file" + ext));
        }
    }
}
