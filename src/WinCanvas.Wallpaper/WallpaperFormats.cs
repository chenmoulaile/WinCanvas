using System;
using System.IO;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper;

/// <summary>
/// 壁纸支持的扩展名与类型判定（WP5 起【全仓库唯一事实来源】）。
///
/// 收敛背景（总提示词 WP5 任务 2）：此前扩展名列表分散在四处各自维护——
/// <c>ImageWallpaperLoader.SupportedExtensions</c>、<c>VideoWallpaperLoader.SupportedExtensions</c>、
/// <c>MainViewModel.ImageExtensions/VideoExtensions</c>、<c>FilePickerService.SupportedExtensions</c>——
/// 已经产生过一次真实缺陷（.webp 只在 ViewModel 与选择器里、不在图片加载器里，
/// 用户选中后运行时报「不支持的壁纸类型」）。本类把「哪些扩展名算图片 / 视频 / pkg」
/// 收敛为一份定义，全部消费方（三层加载器、服务分派、ViewModel 类型推断、文件选择器过滤）
/// 一律引用本类，禁止再各自复制字面列表。
///
/// 注意：这里只描述「按扩展名的分派口径」；解码器对具体编码变体的支持范围（如
/// ImageSharp 2.1 不支持动画 WebP）是另一层约束，由加载器在解码失败时按既有错误通道上报。
/// </summary>
public static class WallpaperFormats
{
    /// <summary>图片背景支持的扩展名（小写、含点）。.webp 为 WP5 新增（ImageSharp 2.1 支持静态 WebP 解码）。</summary>
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// 视频背景支持的扩展名（小写、含点）。为原四份字面列表的并集收敛：
    /// .m4v（原加载器有、选择器无）与 .avi（原选择器有、加载器无）不再互相缺失——
    /// 两者容器均由 Media Foundation 解封装，编解码能力属运行期系统组件范畴（与 .mkv/.webm 同一约束）。
    /// </summary>
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".wmv", ".mkv", ".avi" };

    /// <summary>pkg 打包资源扩展名（Wallpaper Engine 格式）。</summary>
    public const string PkgExtension = ".pkg";

    /// <summary>路径是否为受支持的图片背景（按扩展名判定，大小写不敏感）。</summary>
    public static bool IsImage(string path) =>
        Array.IndexOf(ImageExtensions, GetExtension(path)) >= 0;

    /// <summary>路径是否为受支持的视频背景（按扩展名判定，大小写不敏感）。</summary>
    public static bool IsVideo(string path) =>
        Array.IndexOf(VideoExtensions, GetExtension(path)) >= 0;

    /// <summary>路径是否为 pkg 打包资源（按扩展名判定，大小写不敏感）。</summary>
    public static bool IsPkg(string path) =>
        string.Equals(GetExtension(path), PkgExtension, StringComparison.Ordinal);

    /// <summary>
    /// 按扩展名推断背景类型（三层策略的分派依据）。
    /// </summary>
    /// <returns>对应类型；不受支持的扩展名返回 null（调用方按「不支持的壁纸类型」处理）。</returns>
    public static WallpaperKind? DetectKind(string path)
    {
        if (IsImage(path))
            return WallpaperKind.Image;
        if (IsVideo(path))
            return WallpaperKind.Video;
        if (IsPkg(path))
            return WallpaperKind.Pkg;

        return null;
    }

    /// <summary>取小写、含点的扩展名（无扩展名返回空串；统一口径供各判定方法使用）。</summary>
    private static string GetExtension(string path) =>
        Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
}
