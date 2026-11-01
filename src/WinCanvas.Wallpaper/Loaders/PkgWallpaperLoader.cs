using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RePKG.Application.Package;
using RePKG.Application.Texture;
using RePKG.Core.Package;
using RePKG.Core.Texture;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Loaders;

/// <summary>
/// 第三层：Wallpaper Engine .pkg 加载器。
/// 直接项目引用 repkg 子模块源码（RePKG.Core 数据模型 + RePKG.Application 解析器）：
/// 递归提取 scene.json / project.json / 纹理，.tex 解码为 PNG（内嵌视频纹理还原为 .mp4）。
/// 复杂场景判定：包内存在 .lua 脚本，或 scene.json 引用了 "script" 字段 ——
/// 此类 SceneScript 动态场景当前版本不支持，降级为仅显示封面图并上报明确提示。
/// </summary>
public static class PkgWallpaperLoader
{
    /// <summary>pkg 解包缓存根目录。</summary>
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinCanvas", "wallpaper-cache");

    /// <summary>
    /// 解包并分析 pkg。
    /// </summary>
    /// <param name="pkgPath">.pkg 文件路径。</param>
    /// <returns>已就绪的壁纸资源（视频纹理 -> Video；纯图场景 -> Image；复杂场景 -> 封面图 + 提示）。</returns>
    /// <exception cref="FileNotFoundException">pkg 文件不存在。</exception>
    /// <exception cref="InvalidDataException">pkg 头部魔数不合法（不是 Wallpaper Engine 包）。</exception>
    public static WallpaperSource Load(string pkgPath)
    {
        if (!File.Exists(pkgPath))
            throw new FileNotFoundException("壁纸 pkg 文件不存在。", pkgPath);

        // ---- 打开 pkg，用 repkg 的 PackageReader 读取条目表 + 数据 ----
        using var fileStream = File.OpenRead(pkgPath);
        using var reader = new BinaryReader(fileStream);

        var packageReader = new PackageReader { ReadEntryBytes = true };
        Package package;
        try
        {
            package = packageReader.ReadFrom(reader);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"pkg 文件过早结束，不是合法的 Wallpaper Engine 包：{pkgPath}", ex);
        }

        fileStream.Close();

        // ---- 解包到缓存目录（已解包过则复用缓存）----
        var extractRoot = GetCacheDirectory(pkgPath);
        var extractedNow = ExtractPackage(package, extractRoot);

        // ---- 分析场景 ----
        var sceneJson = FindFile(extractRoot, "scene.json");
        var isComplexScene = DetectComplexScene(extractRoot, sceneJson);

        // 复杂 SceneScript 场景：当前版本不支持动态脚本，只显示封面图
        if (isComplexScene)
        {
            var cover = FindCoverImage(extractRoot);
            WallpaperSource coverSource;
            if (cover != null && ImageWallpaperLoader.IsSupported(cover))
                coverSource = ImageWallpaperLoader.Load(cover, 0, 0); // 封面图不缩放，交 UI 层铺排
            else
                coverSource = new WallpaperSource { Kind = WallpaperKind.Image, OriginalPath = pkgPath };

            return new WallpaperSource
            {
                Kind = WallpaperKind.Pkg,
                ImageFrame = coverSource.ImageFrame,
                ExtractedRoot = extractRoot,
                SceneJsonPath = sceneJson,
                CoverImagePath = cover,
                OriginalPath = Path.GetFullPath(pkgPath),
                StatusMessage = "该 pkg 包含 SceneScript 动态脚本场景，当前版本暂不支持动态渲染，已降级为仅显示封面图。",
            };
        }

        // ---- 纯视频场景：包内有内嵌 mp4（含视频纹理 .tex 还原出的 mp4）----
        var video = FindEmbeddedVideo(extractRoot);
        if (video != null)
        {
            var source = VideoWallpaperLoader.Load(video);
            return source with
            {
                Kind = WallpaperKind.Pkg,
                ExtractedRoot = extractRoot,
                SceneJsonPath = sceneJson,
            };
        }

        // ---- 纯图片场景：取面积最大的已解码 PNG 作为壁纸底图 ----
        var image = FindMainImage(extractRoot);
        if (image != null)
        {
            var source = ImageWallpaperLoader.Load(image, 0, 0);
            return source with
            {
                Kind = WallpaperKind.Pkg,
                ExtractedRoot = extractRoot,
                SceneJsonPath = sceneJson,
            };
        }

        // 既没有视频也没有可用图片：返回空源，由服务层上报
        return new WallpaperSource
        {
            Kind = WallpaperKind.Pkg,
            ExtractedRoot = extractRoot,
            SceneJsonPath = sceneJson,
            OriginalPath = Path.GetFullPath(pkgPath),
            StatusMessage = "pkg 解包完成，但未找到可渲染的媒体资源（无视频、无可用图片纹理）。",
        };
    }

    #region 解包

    /// <summary>pkg 对应的缓存目录：%LOCALAPPDATA%/WinCanvas/wallpaper-cache/&lt;文件名-hash&gt;/。</summary>
    private static string GetCacheDirectory(string pkgPath)
    {
        // 目录名 = 文件名 + 内容前 1MB 的 hash：同一路径不同内容的包不会互相覆盖
        var fileName = Path.GetFileNameWithoutExtension(pkgPath);
        var hash = ComputePkgHash(pkgPath);
        return Path.Combine(CacheRoot, $"{fileName}-{hash}");
    }

    private static string ComputePkgHash(string pkgPath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(pkgPath);

        // 只取文件头 1MB 计算 hash：pkg 头部含条目表，足以区分不同包，且大文件不整读
        var header = new byte[1024 * 1024];
        var read = stream.Read(header, 0, header.Length);

        var digest = sha.ComputeHash(header, 0, read);
        var builder = new StringBuilder(digest.Length * 2);
        foreach (var b in digest)
            builder.Append(b.ToString("x2"));

        return builder.ToString()[..12];
    }

    /// <summary>
    /// 把 package 的所有条目写到磁盘。.tex 条目用 repkg 解码为 PNG / 还原 mp4，其余原样写出。
    /// 已存在 marker（与 pkg hash 对应的目录 + extracted.flag）则跳过解包，直接复用缓存。
    /// </summary>
    /// <returns>true = 本次实际执行了解包；false = 命中缓存。</returns>
    private static bool ExtractPackage(Package package, string extractRoot)
    {
        var marker = Path.Combine(extractRoot, "extracted.flag");
        if (File.Exists(marker))
            return false;

        Directory.CreateDirectory(extractRoot);

        // repkg 的 TexReader.Default 装配好完整的 .tex 读取管线（头部 + mip 解压 + GIF 帧信息）
        var texReader = TexReader.Default;
        var texConverter = new TexToImageConverter();

        foreach (var entry in package.Entries)
        {
            var targetPath = SafeJoin(extractRoot, entry.FullPath);
            if (targetPath == null || entry.Bytes == null)
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            try
            {
                if (string.Equals(entry.Extension, ".tex", StringComparison.OrdinalIgnoreCase))
                    WriteTexDecoded(entry, targetPath, texReader, texConverter);
                else
                    File.WriteAllBytes(targetPath, entry.Bytes);
            }
            catch
            {
                // 单个条目损坏不应导致整个包失败：损坏纹理留空，渲染层会跳过
            }
        }

        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        return true;
    }

    /// <summary>
    /// 解码单个 .tex：视频纹理还原为 .mp4，普通纹理解码为 PNG。
    /// </summary>
    private static void WriteTexDecoded(
        PackageEntry entry, string targetPath, TexReader texReader, TexToImageConverter converter)
    {
        using var memory = new MemoryStream(entry.Bytes);
        using var texBinary = new BinaryReader(memory);

        var tex = texReader.ReadFrom(texBinary);
        var result = converter.ConvertToImage(tex);

        memory.Position = 0;

        switch (result.Format)
        {
            case MipmapFormat.VideoMp4:
                // 视频纹理：mipmap 数据就是完整 mp4 字节流（repkg 已校验 ftyp 魔数）
                File.WriteAllBytes(Path.ChangeExtension(targetPath, ".mp4"), entry.Bytes);
                break;
            case MipmapFormat.ImagePNG:
                File.WriteAllBytes(Path.ChangeExtension(targetPath, ".png"), result.Bytes);
                break;
            default:
                if (result.Format.IsImage())
                {
                    // 其他图片容器格式（jpg/gif 等）：按 repkg 给出的扩展名落盘
                    var ext = result.Format.GetFileExtension();
                    File.WriteAllBytes(Path.ChangeExtension(targetPath, ext), result.Bytes);
                }
                // 其余（压缩纹理未解出等）：跳过
                break;
        }
    }

    /// <summary>
    /// 把 pkg 条目相对路径安全拼到缓存目录下：拒绝绝对路径与 ".." 穿越。
    /// （WP4：由 private 放宽为 internal——纯路径逻辑按总提示词用例表要求可单测，
    /// 经 InternalsVisibleTo 对 WinCanvas.Wallpaper.Tests 开放，不进公共 API。）
    /// </summary>
    internal static string? SafeJoin(string root, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            return null;

        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, normalized));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    #endregion

    #region 场景分析

    /// <summary>
    /// 递归查找目录下指定文件名（不区分大小写）。
    /// </summary>
    private static string? FindFile(string root, string fileName)
    {
        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
            .OrderBy(p => p.Length) // 多个同名时取路径最短的（最靠近根）
            .FirstOrDefault();
    }

    /// <summary>
    /// 判定是否为复杂 SceneScript 场景：
    /// 包内存在 .lua 脚本，或 scene.json 中引用了 "script" / "scripts" 字段。
    /// （WP4：由 private 放宽为 internal——三分支判定为纯目录/文本逻辑，按总提示词
    /// 用例表要求以临时目录构造直接单测，经 InternalsVisibleTo 对测试程序集开放。）
    /// </summary>
    internal static bool DetectComplexScene(string extractRoot, string? sceneJsonPath)
    {
        // 条件一：存在 .lua 用户脚本
        if (Directory.EnumerateFiles(extractRoot, "*.lua", SearchOption.AllDirectories).Any())
            return true;

        // 条件二：scene.json 引用了脚本字段
        if (sceneJsonPath != null)
        {
            try
            {
                // scene.json 可能很大，只读前 2MB 判定关键字段
                using var stream = File.OpenRead(sceneJsonPath);
                var buffer = new byte[2 * 1024 * 1024];
                var len = stream.Read(buffer, 0, buffer.Length);
                var text = Encoding.UTF8.GetString(buffer, 0, len);

                return text.Contains("\"script\"", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("\"scripts\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 找封面图：优先包根的 preview.jpg / preview.png / preview_gif*，其次任一张已解码 PNG。
    /// （WP4：由 private 放宽为 internal——候选优先级为纯文件枚举逻辑，按总提示词
    /// 用例表要求以临时目录构造直接单测，经 InternalsVisibleTo 对测试程序集开放。）
    /// </summary>
    internal static string? FindCoverImage(string extractRoot)
    {
        var candidates = new[]
        {
            "preview.jpg", "preview.png", "preview.jpeg", "preview_gif.png", "preview.gif",
        };

        foreach (var name in candidates)
        {
            var path = Path.Combine(extractRoot, name);
            if (File.Exists(path))
                return path;
        }

        return Directory.EnumerateFiles(extractRoot, "preview.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(ImageWallpaperLoader.IsSupported)
            ?? Directory.EnumerateFiles(extractRoot, "*.png", SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
    }

    /// <summary>pkg 内嵌视频：优先独立 .mp4 条目，其次视频纹理还原出的 .mp4（materials 下）。</summary>
    private static string? FindEmbeddedVideo(string extractRoot)
    {
        return Directory.EnumerateFiles(extractRoot, "*.mp4", SearchOption.AllDirectories)
            .OrderByDescending(p => new FileInfo(p).Length) // 取最大的视频：小 mp4 常是道具素材
            .FirstOrDefault();
    }

    /// <summary>纯图场景主图：取面积最大的 PNG（scene 的 material 通常纹理最大）。</summary>
    private static string? FindMainImage(string extractRoot)
    {
        return Directory.EnumerateFiles(extractRoot, "*.png", SearchOption.AllDirectories)
            .Select(p => (Path: p, Size: new FileInfo(p).Length))
            .OrderByDescending(x => x.Size)
            .Where(x => x.Size > 1024) // 过滤图标类小纹理
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    #endregion
}
