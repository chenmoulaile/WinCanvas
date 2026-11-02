using System;
using System.IO;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Loaders;

/// <summary>
/// Wallpaper Engine 本地项目加载器（v0.3.0 新增）。
///
/// 补的兼容缺口：此前本程序只认单个 <c>.pkg</c> 文件，而 Wallpaper Engine 在
/// <c>wallpaper_engine/projects/myprojects/&lt;id&gt;/</c> 下存放的是
/// 【已解包的项目目录】—— 一个 <c>project.json</c> 描述符 + 散落的素材。
/// 实测一个真实库 96 个项目里，只有 40 个能靠「直接选中 scene.pkg」打开，
/// 其余（视频类 33、网页类 4、以及 file 指向各种命名形态的项目）全都进不来。
///
/// 分派规则（按 project.json 的 type + file）：
/// · scene → 找同目录 <c>scene.pkg</c>，交 <see cref="PkgWallpaperLoader"/>；
/// · video → <c>file</c> 指向的媒体文件交 <see cref="VideoWallpaperLoader"/>；
/// · web   → 本程序不渲染网页，降级显示 <c>preview.gif/jpg</c> 并明确说明；
/// · 其它 / 解析失败 → 给出人话原因，不抛。
///
/// 所有分支都把 <see cref="WallpaperSource.OriginalPath"/> 统一钉成
/// 【项目目录绝对路径】：恢复管线与「是否已挂同一壁纸」的判重都以此为准，
/// 用目录而不是内部素材路径才稳定（重新导出后 pkg 会变、素材名也可能变，
/// 但项目目录是用户在库里看到的那个条目）。
/// </summary>
public static class WallpaperProjectLoader
{
    /// <summary>判断路径是否是一个 Wallpaper Engine 项目目录。</summary>
    public static bool IsProjectDirectory(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Directory.Exists(path)
        && WallpaperProjectReader.HasProjectFile(path);

    /// <summary>
    /// 加载一个 WE 项目目录。
    /// </summary>
    /// <param name="projectDirectory">项目目录绝对路径（含 project.json）。</param>
    /// <param name="audioEnabled">是否允许壁纸发声（false = 静音）。仅对视频层有意义。</param>
    /// <returns>已就绪的壁纸资源。</returns>
    /// <exception cref="FileNotFoundException">目录不存在或缺 project.json。</exception>
    /// <exception cref="NotSupportedException">项目类型本程序不支持，或所需素材缺失。</exception>
    public static WallpaperSource Load(string projectDirectory, bool audioEnabled = false)
    {
        string full = Path.GetFullPath(projectDirectory);
        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"壁纸项目目录不存在：{full}", full);
        }

        var descriptor = WallpaperProjectReader.Read(full)
            ?? throw new FileNotFoundException("该目录不是 Wallpaper Engine 项目（缺少 project.json）。", full);

        if (descriptor.ParseError != null)
        {
            throw new NotSupportedException($"project.json 无法解析：{descriptor.ParseError}");
        }

        return descriptor.Kind switch
        {
            WallpaperProjectKind.Scene => LoadScene(descriptor, full),
            WallpaperProjectKind.Video => LoadVideo(descriptor, full, audioEnabled),
            WallpaperProjectKind.Web => LoadWebFallback(descriptor, full),
            WallpaperProjectKind.AudioFilter => throw new NotSupportedException(
                $"「{descriptor.Title}」是 Wallpaper Engine 的音频响应壁纸（audiofilter），" +
                "本程序不渲染该类型。"),
            _ => throw new NotSupportedException(
                $"「{descriptor.Title}」的项目类型无法识别（project.json 的 type 缺失或未知）。"),
        };
    }

    /// <summary>
    /// 场景壁纸：同目录的 <c>scene.pkg</c> 交 pkg 加载器。
    /// 注意 project.json 的 <c>file</c> 指向的是 <c>scene.json</c>——那个文件
    /// 【在 pkg 内部】，磁盘上并不存在（实测 40 个场景项目都这样），
    /// 所以这里不能拿 file 当素材，必须显式找 scene.pkg。
    /// </summary>
    private static WallpaperSource LoadScene(WallpaperProjectDescriptor descriptor, string full)
    {
        var pkg = Path.Combine(full, "scene.pkg");
        if (!File.Exists(pkg))
        {
            // 极少数项目只留了工程文件、没导出 pkg。有预览图就降级显示，没有才报错。
            if (descriptor.PreviewPath != null && ImageWallpaperLoader.IsSupported(descriptor.PreviewPath))
            {
                var previewSource = ImageWallpaperLoader.Load(descriptor.PreviewPath, 0, 0);
                return previewSource with
                {
                    Kind = WallpaperKind.Pkg,
                    OriginalPath = full,
                    CoverImagePath = descriptor.PreviewPath,
                    StatusMessage = $"「{descriptor.Title}」没有导出 scene.pkg，已降级显示项目预览图。",
                };
            }

            throw new NotSupportedException(
                $"「{descriptor.Title}」是场景壁纸，但目录内找不到 scene.pkg（项目可能未导出）。");
        }

        var source = PkgWallpaperLoader.Load(pkg);

        // 身份统一钉成项目目录；同时把预览图带上（pkg 内没有 preview.* 时用作封面兜底）
        return source with
        {
            OriginalPath = full,
            CoverImagePath = source.CoverImagePath ?? descriptor.PreviewPath,
        };
    }

    /// <summary>
    /// 视频壁纸：<c>file</c> 直接指向素材（实测含中文名与方括号、长串下划线的文件名，
    /// 所以路径解析必须原样保留这些字符 —— <see cref="WallpaperProjectReader.ResolveRelative"/> 已做）。
    /// </summary>
    private static WallpaperSource LoadVideo(WallpaperProjectDescriptor descriptor, string full, bool audioEnabled)
    {
        var media = descriptor.MainFilePath;

        // file 缺失或指向的文件不存在时，退而求其次在目录里找一个视频
        media ??= FindVideoInDirectory(full);

        if (media == null)
        {
            throw new NotSupportedException(
                $"「{descriptor.Title}」声明为视频壁纸，但项目目录里找不到可播放的视频文件。");
        }

        var source = VideoWallpaperLoader.Load(media, audioEnabled);

        return source with
        {
            OriginalPath = full,
            CoverImagePath = descriptor.PreviewPath,
        };
    }

    /// <summary>
    /// 网页壁纸：本程序不执行 HTML/JS（Live2D 网页壁纸占实测库里的 4 个），
    /// 降级显示 preview.gif —— 它是动画 GIF，图片层能连续播放，
    /// 所以「至少能看到原壁纸的动效」而不是给一张死图。
    /// </summary>
    private static WallpaperSource LoadWebFallback(WallpaperProjectDescriptor descriptor, string full)
    {
        var preview = descriptor.PreviewPath;

        if (preview != null && ImageWallpaperLoader.IsSupported(preview))
        {
            var source = ImageWallpaperLoader.Load(preview, 0, 0);
            return source with
            {
                Kind = WallpaperKind.Image,
                OriginalPath = full,
                CoverImagePath = preview,
                StatusMessage = $"「{descriptor.Title}」是 Wallpaper Engine 的网页壁纸（index.html），" +
                    "本程序不渲染网页内容，已降级显示项目预览动画。",
            };
        }

        throw new NotSupportedException(
            $"「{descriptor.Title}」是 Wallpaper Engine 的网页壁纸（index.html），" +
            "本程序不渲染网页内容，且该项目没有可用的预览图。");
    }

    /// <summary>在目录顶层与一层子目录里找视频文件（file 字段缺失时的兜底）。</summary>
    private static string? FindVideoInDirectory(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(WallpaperFormats.IsVideo)
                // 取最大的：项目里可能同时有原片与短循环片段，大的通常是主素材
                .OrderByDescending(p => new FileInfo(p).Length)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
