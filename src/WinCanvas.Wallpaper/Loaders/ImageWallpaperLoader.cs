using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Windows.Graphics.Imaging;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Loaders;

/// <summary>
/// 第一层：静态图片加载器（.jpg/.png/.jpeg/.bmp/.gif）。
/// 性能要点：导入时把原图缩放到目标显示器分辨率，避免 4K/8K 原图直送 GPU；
/// 只缩小、不放大。GIF 动画当前版本取第一帧静态呈现。
/// </summary>
public static class ImageWallpaperLoader
{
    /// <summary>支持的图片扩展名（小写、含点）。</summary>
    public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

    /// <summary>判断路径是否为本层支持的图片。</summary>
    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// 加载图片并缩放到目标分辨率。
    /// </summary>
    /// <param name="path">图片文件路径。</param>
    /// <param name="targetWidth">目标显示器宽度（像素）；&lt;=0 时不缩放。</param>
    /// <param name="targetHeight">目标显示器高度（像素）；&lt;=0 时不缩放。</param>
    /// <returns>已就绪的壁纸资源（ImageFrame 为 Bgra8 SoftwareBitmap）。</returns>
    /// <exception cref="FileNotFoundException">图片文件不存在。</exception>
    /// <exception cref="InvalidDataException">图片解码失败。</exception>
    public static WallpaperSource Load(string path, int targetWidth, int targetHeight)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("壁纸图片文件不存在。", path);

        // ---- 解码（GIF 只解第一帧）----
        using var image = Image.Load<Bgra32>(path);

        // ---- 导入缩放：只缩小不放大，等比适配到屏幕矩形内 ----
        if (targetWidth > 0 && targetHeight > 0
            && (image.Width > targetWidth || image.Height > targetHeight))
        {
            var scale = Math.Min(
                (double) targetWidth / image.Width,
                (double) targetHeight / image.Height);

            var newWidth = Math.Max(1, (int) Math.Round(image.Width * scale));
            var newHeight = Math.Max(1, (int) Math.Round(image.Height * scale));

            // Lanczos：缩小时锐度保留最好的重采样器之一；一次到位避免多级缩放损失
            image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
        }

        // ---- ImageSharp -> SoftwareBitmap ----
        // Bgra32 像素与 Bgra8 SoftwareBitmap 逐字节布局一致，整块拷贝即可，无逐像素转换开销
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);

        var frame = SoftwareBitmap.CreateCopyFromBuffer(
            // CsWinRT（WinRT.Runtime）提供的扩展：byte[] -> WinRT IBuffer，
            // 命名空间 System.Runtime.InteropServices.WindowsRuntime
            bytes.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);

        return new WallpaperSource
        {
            Kind = WallpaperKind.Image,
            ImageFrame = frame,
            OriginalPath = Path.GetFullPath(path),
        };
    }
}
