using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Gif;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Loaders;
using WinCanvas.Wallpaper.Models;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// WP5 扩展名收敛（WallpaperFormats 单一事实来源）与三层加载器口径一致性测试。
/// 背景：.webp 曾只存在于 ViewModel / 文件选择器、不在图片加载器中，导致用户选中后
/// 运行时报「不支持的壁纸类型」——本组用例钉住「各消费方一律引用同一份定义」不再各自漂移。
/// </summary>
public sealed class WallpaperFormatsTests
{
    /// <summary>MemberData 数据源：把口径镜像表逐项投给 Theory。</summary>
    public static IEnumerable<object[]> ImageExtensionData =>
        ImageExtensions.Select(e => new object[] { e });

    /// <summary>口径表本体（与 WallpaperFormats.ImageExtensions 同步维护的唯一镜像，见各用例注释）。</summary>
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    private static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".wmv", ".mkv", ".avi" };

    [Fact]
    public void ImageExtensions_ContainWebp_AndMatchSingleSource()
    {
        // .webp 必须在图片口径里（WP5 缺陷修复的直接断言）
        Assert.Contains(".webp", WallpaperFormats.ImageExtensions);

        // 口径表与单一事实来源完全一致：多一个少一个都算漂移
        Assert.Equal(
            ImageExtensions.OrderBy(e => e).ToArray(),
            WallpaperFormats.ImageExtensions.OrderBy(e => e).ToArray());
    }

    [Fact]
    public void VideoExtensions_MatchSingleSource_IncludingM4vAndAvi()
    {
        Assert.Equal(
            VideoExtensions.OrderBy(e => e).ToArray(),
            WallpaperFormats.VideoExtensions.OrderBy(e => e).ToArray());
    }

    [Theory]
    [MemberData(nameof(ImageExtensionData))]
    public void Loaders_IsSupported_MatchFormatsTable(string extension)
    {
        // 图片加载器不再自带字面列表（别名到单一事实来源）：任何扩展名上两者必须一致
        Assert.Equal(
            WallpaperFormats.IsImage("x" + extension),
            ImageWallpaperLoader.IsSupported("x" + extension));
    }

    [Fact]
    public void VideoLoader_IsSupported_MatchFormatsTable()
    {
        foreach (var extension in WallpaperFormats.VideoExtensions)
        {
            Assert.True(VideoWallpaperLoader.IsSupported("x" + extension), extension);
            Assert.True(WallpaperFormats.IsVideo("x" + extension), extension);
        }

        // 交叉反例：图片扩展名绝不能被视频口径接受（反之亦然）
        Assert.False(VideoWallpaperLoader.IsSupported("x.png"));
        Assert.False(ImageWallpaperLoader.IsSupported("x.mp4"));
    }

    [Theory]
    [InlineData(".gif", WallpaperKind.Image)]
    [InlineData(".WEBP", WallpaperKind.Image)] // 大小写不敏感（文件系统口径）
    [InlineData(".mp4", WallpaperKind.Video)]
    [InlineData(".pkg", WallpaperKind.Pkg)]
    [InlineData(".txt", null)]
    [InlineData("", null)]
    public void DetectKind_MapsByExtension(string extension, WallpaperKind? expected)
    {
        Assert.Equal(expected, WallpaperFormats.DetectKind("file" + extension));
    }

    [Fact]
    public void IsImage_IsCaseInsensitive()
    {
        Assert.True(WallpaperFormats.IsImage("photo.PNG"));
        Assert.True(WallpaperFormats.IsImage("anim.GiF"));
        Assert.False(WallpaperFormats.IsImage("photo.tiff"));
    }

    [Fact]
    public void PkgExtension_IsConstant()
    {
        Assert.Equal(".pkg", WallpaperFormats.PkgExtension);
        Assert.True(WallpaperFormats.IsPkg("a.PKG")); // 大小写不敏感
    }
}

/// <summary>
/// WP5 GIF 多帧解码测试：用 ImageSharp 合成 3 帧动画 GIF（不同纯色 + 不同帧延迟），
/// 经真实 <see cref="ImageWallpaperLoader"/> 往返，验证帧数 / 首帧别名 / 帧延迟换算 / 像素内容。
/// 测试资产为运行期合成的 16x16 纯色小图（总提示词附录 B：测试用小尺寸合成图片自行生成）。
/// </summary>
public sealed class ImageWallpaperLoaderAnimatedTests
{
    private static readonly (byte R, byte G, byte B)[] Palette =
    {
        (255, 0, 0), (0, 255, 0), (0, 0, 255),
    };

    /// <summary>合成 3 帧 GIF：第 n 帧 = Palette[n] 纯色，帧延迟分别 2 / 5 / 10（单位 1/100 秒）。</summary>
    private static string CreateAnimatedGif(string path)
    {
        using var image = new Image<Bgra32>(16, 16, new Bgra32(Palette[0].R, Palette[0].G, Palette[0].B, 255));
        image.Frames[0].Metadata.GetGifMetadata().FrameDelay = 2;

        for (var i = 1; i < Palette.Length; i++)
        {
            var frame = new Image<Bgra32>(16, 16, new Bgra32(Palette[i].R, Palette[i].G, Palette[i].B, 255));
            var added = image.Frames.AddFrame(frame.Frames.RootFrame);
            added.Metadata.GetGifMetadata().FrameDelay = i == 1 ? 5 : 10;
            frame.Dispose();
        }

        image.SaveAsGif(path);
        return path;
    }

    [Fact]
    public void Load_DecodesAllFrames_WithDelays_AndAliasesFirstFrame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wc5gif-{Guid.NewGuid():N}.gif");
        try
        {
            CreateAnimatedGif(path);

            var source = ImageWallpaperLoader.Load(path, 0, 0);
            try
            {
                Assert.NotNull(source.ImageFrames);
                Assert.Equal(3, source.ImageFrames!.Count);

                // 首帧别名契约：ImageFrame 与 ImageFrames[0].Frame 是同一实例（既有「ImageFrame 非空即渲染」判定的兼容面）
                Assert.Same(source.ImageFrame, source.ImageFrames[0].Frame);

                // 帧延迟 1/100 秒 → 毫秒（1/100 秒 = 10ms：2 → 20ms / 5 → 50ms / 10 → 100ms）
                Assert.Equal(20, source.ImageFrames[0].Delay.TotalMilliseconds);
                Assert.Equal(50, source.ImageFrames[1].Delay.TotalMilliseconds);
                Assert.Equal(100, source.ImageFrames[2].Delay.TotalMilliseconds);

                // 像素内容逐帧不同（合成的是三种纯色；证明解码器产出的是合成后整幅画面而非空帧）。
                // 注意：帧的所有权归 source（Dispose 链统一释放），这里只读不 Dispose。
                for (var i = 0; i < 3; i++)
                {
                    var frame = source.ImageFrames[i].Frame;
                    Assert.Equal(16, frame.PixelWidth);
                    Assert.Equal(16, frame.PixelHeight);

                    using var buffer = frame.LockBuffer(Windows.Graphics.Imaging.BitmapBufferAccessMode.Read);
                    var plane = buffer.GetPlaneDescription(0);
                    var raw = new byte[plane.StartIndex + (long)plane.Stride * frame.PixelHeight];
                    var winBuf = new Windows.Storage.Streams.Buffer((uint)raw.Length);
                    frame.CopyToBuffer(winBuf);
                    winBuf.ToArray().AsSpan().CopyTo(raw);

                    var (r, g, b) = Palette[i];
                    // 采样首像素：Bgra8 布局 = B, G, R, A
                    var offset = plane.StartIndex;
                    Assert.Equal(b, raw[offset]);
                    Assert.Equal(g, raw[offset + 1]);
                    Assert.Equal(r, raw[offset + 2]);
                    Assert.Equal(255, raw[offset + 3]);
                }
            }
            finally
            {
                source.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SingleFrameGif_RemainsStaticSource()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wc5gif1-{Guid.NewGuid():N}.gif");
        try
        {
            using (var image = new Image<Bgra32>(8, 8, new Bgra32(10, 20, 30, 255)))
            {
                image.SaveAsGif(path);
            }

            var source = ImageWallpaperLoader.Load(path, 0, 0);
            try
            {
                Assert.NotNull(source.ImageFrame);
                Assert.Null(source.ImageFrames); // 单帧不进动画路径
            }
            finally
            {
                source.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// WP5 .webp 加载测试：ImageSharp 合成静态 WebP → 真实加载器解码，
/// 钉住「选中 webp 文件成功加载」的行为修复。
/// </summary>
public sealed class ImageWallpaperLoaderWebpTests
{
    [Fact]
    public void Load_Webp_DecodesPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wc5webp-{Guid.NewGuid():N}.webp");
        try
        {
            using (var image = new Image<Bgra32>(12, 12, new Bgra32(200, 100, 50, 255)))
            {
                image.SaveAsWebp(path);
            }

            var source = ImageWallpaperLoader.Load(path, 0, 0);
            try
            {
                Assert.NotNull(source.ImageFrame);
                Assert.Null(source.ImageFrames); // 静态 webp 不进动画路径
                Assert.Equal(12, source.ImageFrame!.PixelWidth);
            }
            finally
            {
                source.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
