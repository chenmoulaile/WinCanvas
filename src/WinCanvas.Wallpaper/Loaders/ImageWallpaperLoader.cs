using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Windows.Graphics.Imaging;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Loaders;

/// <summary>
/// 第一层：静态图片加载器（扩展名口径见 <see cref="WallpaperFormats.ImageExtensions"/>：.jpg/.jpeg/.png/.bmp/.gif/.webp）。
/// 性能要点：导入时把原图缩放到目标显示器分辨率，避免 4K/8K 原图直送 GPU；
/// 只缩小、不放大。GIF 动画自 WP5 起解码全部帧（见 <see cref="Load"/> 的动画路径），
/// 其余格式仍为单帧静态呈现。
/// </summary>
public static class ImageWallpaperLoader
{
    /// <summary>
    /// 支持的图片扩展名（小写、含点）。
    /// WP5 起【不再本类自定义】，直接别名 <see cref="WallpaperFormats.ImageExtensions"/>——
    /// 扩展名口径全仓库单一事实来源，此处仅为既有公共 API 兼容保留。
    /// </summary>
    public static readonly string[] SupportedExtensions = WallpaperFormats.ImageExtensions;

    /// <summary>判断路径是否为本层支持的图片（口径同 <see cref="WallpaperFormats.IsImage"/>）。</summary>
    public static bool IsSupported(string path) => WallpaperFormats.IsImage(path);

    /// <summary>
    /// 加载图片并缩放到目标分辨率。
    /// 动画（多帧 GIF，WP5）：解码全部帧到 <see cref="WallpaperSource.ImageFrames"/>
    /// （每帧含 Bgra8 像素与展示时长），首帧同时填入 <see cref="WallpaperSource.ImageFrame"/>
    /// 兼容「取 ImageFrame 判定可渲染」的既有消费方；帧推进由壁纸会话在渲染线程按帧延迟驱动。
    /// 动画帧数 / 内存设上限（见 <see cref="MaxAnimatedFrameBytes"/>）：超限截断只保留前若干帧
    /// （ pathological 级超长 GIF 防内存失控，正常 GIF 不受影响）。
    /// </summary>
    /// <param name="path">图片文件路径。</param>
    /// <param name="targetWidth">目标显示器宽度（像素）；&lt;=0 时不缩放。</param>
    /// <param name="targetHeight">目标显示器高度（像素）；&lt;=0 时不缩放。</param>
    /// <returns>已就绪的壁纸资源（ImageFrame 为 Bgra8 SoftwareBitmap；动画另含 ImageFrames）。</returns>
    /// <exception cref="FileNotFoundException">图片文件不存在。</exception>
    /// <exception cref="InvalidDataException">图片解码失败。</exception>
    public static WallpaperSource Load(string path, int targetWidth, int targetHeight)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("壁纸图片文件不存在。", path);

        // ---- 解码：先按普通图片读入（GIF 动画会带出全部帧到 image.Frames）----
        using var image = Image.Load<Bgra32>(path);

        // 多帧即动画（GIF 特性；jpg/png/webp 恒为单帧）。动画帧的解码 / 缩放走逐帧路径。
        if (image.Frames.Count > 1)
            return LoadAnimated(path, image, targetWidth, targetHeight);

        ResizeToFit(image, targetWidth, targetHeight);

        // ---- ImageSharp -> SoftwareBitmap ----
        // Bgra32 像素与 Bgra8 SoftwareBitmap 逐字节布局一致，整块拷贝即可，无逐像素转换开销
        var frame = ToSoftwareBitmap(image);

        return new WallpaperSource
        {
            Kind = WallpaperKind.Image,
            ImageFrame = frame,
            OriginalPath = Path.GetFullPath(path),
        };
    }

    /// <summary>动画帧总像素字节预算（256MB）：超过即停止追加帧，防 pathological 超长 GIF 内存失控。</summary>
    private const long MaxAnimatedFrameBytes = 256L * 1024 * 1024;

    /// <summary>动画帧数硬上限（与字节预算取更严者）：240 帧 @10fps = 24 秒循环，覆盖绝大多数 GIF。</summary>
    private const int MaxAnimatedFrameCount = 240;

    /// <summary>GIF 帧延迟缺失 / 为 0（部分工具写 0 表示「尽快」）时的兜底展示时长（100ms ≈ 10fps 常规值）。</summary>
    private static readonly TimeSpan DefaultFrameDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 动画路径：逐帧克隆 → 按统一比例缩放 → 转 Bgra8 SoftwareBitmap，附 GIF 帧延迟。
    /// ImageSharp 对 GIF 的解码器把每一帧合成为「截至该帧的完整画布」（disposal 语义在解码期处理），
    /// CloneFrame 取出的即是可直接显示的整幅画面，无需在渲染侧再做帧叠加。
    /// </summary>
    private static WallpaperSource LoadAnimated(
        string path, Image<Bgra32> image, int targetWidth, int targetHeight)
    {
        // 缩放比例对全部帧统一计算一次（各帧同画布尺寸，逐帧分别缩放保证位图完全对齐）
        var scale = 1.0;
        if (targetWidth > 0 && targetHeight > 0
            && (image.Width > targetWidth || image.Height > targetHeight))
        {
            scale = Math.Min(
                (double) targetWidth / image.Width,
                (double) targetHeight / image.Height);
        }

        // 帧数上限：字节预算（缩放后单帧字节 × 帧数）与帧数硬上限取更严者，至少保 2 帧
        var scaledBytesPerFrame = (long) Math.Round(image.Width * scale) * (long) Math.Round(image.Height * scale) * 4;
        var maxFrames = (int) Math.Clamp(MaxAnimatedFrameBytes / Math.Max(1, scaledBytesPerFrame), 2, MaxAnimatedFrameCount);
        var frameCount = Math.Min(image.Frames.Count, maxFrames);

        var frames = new List<WallpaperAnimationFrame>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            // CloneFrame：克隆为独立 Image（各自持像素缓冲），缩放不影响相邻帧
            using var cloned = image.Frames.CloneFrame(i);
            ResizeToFit(cloned, targetWidth, targetHeight, scale);

            // GIF 帧延迟单位 = 1/100 秒；多帧非 GIF 容器（理论不会走到）的空 Gif 元数据 FrameDelay=0，取默认值
            var gifMetadata = image.Frames[i].Metadata.GetGifMetadata();
            var delay = gifMetadata.FrameDelay > 0
                ? TimeSpan.FromMilliseconds(gifMetadata.FrameDelay * 10)
                : DefaultFrameDelay;

            frames.Add(new WallpaperAnimationFrame(ToSoftwareBitmap(cloned), delay));
        }

        return new WallpaperSource
        {
            Kind = WallpaperKind.Image,
            // 首帧同时作为 ImageFrame：服务层「ImageFrame 非空即渲染首帧」的判定与预览旁路零改动
            ImageFrame = frames[0].Frame,
            ImageFrames = frames,
            OriginalPath = Path.GetFullPath(path),
        };
    }

    /// <summary>
    /// 把导入期缩放收敛为一个入口（只缩小不放大，Lanczos3；静态路径直接改原图）。
    /// <paramref name="forcedScale"/> 为 null 时按目标分辨率现算比例（静态路径）；
    /// 动画路径传入按首帧（=全部帧同画布）统一算好的比例，保证各帧缩放结果严格对齐。
    /// </summary>
    private static void ResizeToFit(Image<Bgra32> image, int targetWidth, int targetHeight, double? forcedScale = null)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
            return;

        var scale = forcedScale
                    ?? Math.Min(
                        (double) targetWidth / image.Width,
                        (double) targetHeight / image.Height);
        if (scale >= 1.0)
            return; // 只缩小、不放大

        var newWidth = Math.Max(1, (int) Math.Round(image.Width * scale));
        var newHeight = Math.Max(1, (int) Math.Round(image.Height * scale));

        // Lanczos：缩小时锐度保留最好的重采样器之一；一次到位避免多级缩放损失
        image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
    }

    /// <summary>ImageSharp Bgra32 帧 → Bgra8 Premultiplied SoftwareBitmap（整块拷贝，无逐像素转换）。</summary>
    private static SoftwareBitmap ToSoftwareBitmap(Image<Bgra32> image)
    {
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);

        return SoftwareBitmap.CreateCopyFromBuffer(
            // CsWinRT（WinRT.Runtime）提供的扩展：byte[] -> WinRT IBuffer，
            // 命名空间 System.Runtime.InteropServices.WindowsRuntime
            bytes.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);
    }
}
