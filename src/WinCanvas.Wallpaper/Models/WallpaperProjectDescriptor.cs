using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinCanvas.Wallpaper.Models;

/// <summary>
/// Wallpaper Engine 项目描述符（<c>project.json</c>）的解析结果。
///
/// 为什么需要单独一层：Wallpaper Engine 的本地项目目录（
/// <c>wallpaper_engine/projects/myprojects/&lt;workshopid&gt;/</c>）不是单个媒体文件，
/// 而是「一个 <c>project.json</c> + 若干素材」的工程包，真正的壁纸内容由
/// <c>type</c> + <c>file</c> 两个字段指向。WinCanvas 原先只认 <c>.pkg</c> 单文件，
/// 于是这些【已解包的工程】一个都打不开 —— 而实测它们恰恰是主体。
///
/// 本类只做「读 + 归一化」，不做加载，便于壁纸库扫描器复用。
/// </summary>
public sealed class WallpaperProjectDescriptor
{
    /// <summary>项目目录（含 project.json 的那一层）绝对路径。</summary>
    public string ProjectDirectory { get; init; } = string.Empty;

    /// <summary>展示标题（project.json 的 title；缺失时回落成目录名）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>归一化后的类型（小写）。</summary>
    public WallpaperProjectKind Kind { get; init; }

    /// <summary><c>file</c> 字段解析出的绝对路径；无法解析为 null。</summary>
    public string? MainFilePath { get; init; }

    /// <summary><c>preview</c> 字段解析出的绝对路径（preview.gif / preview.jpg）；缺失为 null。</summary>
    public string? PreviewPath { get; init; }

    /// <summary>Wallpaper Engine 创意工坊 Id（可能为空）。</summary>
    public string? WorkshopId { get; init; }

    /// <summary>内容分级（Everyone / Teen / …），用于界面提示。</summary>
    public string? ContentRating { get; init; }

    /// <summary>
    /// 该壁纸是否【可能】发声。三个来源任一命中即为 true：
    /// ① <c>general.supportsaudioprocessing</c>（会响应音频的场景）；
    /// ② <c>general.properties</c> 里存在名字含 audio/volume/music/bgm/sfx/voice 的属性
    ///    —— 实测各作者取的名千奇百怪（musicvolume / bgmvolume / rainvolume /
    ///    oceansfxvolume / aronavoicelinevolume …），【无法枚举，只能按关键词命中】；
    /// ③ 项目目录里有 sound/ 或 audio/ 子目录，或主媒体文件是视频（视频普遍自带音轨）。
    /// </summary>
    public bool MightHaveAudio { get; init; }

    /// <summary>命中 ② 的音频类属性名（供界面说明「这个壁纸有哪些音量项」）。</summary>
    public IReadOnlyList<string> AudioPropertyNames { get; init; } = Array.Empty<string>();

    /// <summary>project.json 解析失败时的人读原因；成功为 null。</summary>
    public string? ParseError { get; init; }
}

/// <summary>
/// Wallpaper Engine 项目类型。
/// 注意源数据里 <c>type</c> 字段【大小写不统一】：实测同一目录树里同时存在
/// "scene"(32) / "Scene"(8) / "video"(32) / "Video"(1) / "web"(2) / "Web"(2)，
/// 所以解析一律先 ToLowerInvariant —— 直接比字符串会把这些项目全部误判成未知类型。
/// </summary>
public enum WallpaperProjectKind
{
    /// <summary>未知 / 无法识别。</summary>
    Unknown = 0,

    /// <summary>场景壁纸（同目录需有 <c>scene.pkg</c>）。</summary>
    Scene = 1,

    /// <summary>视频壁纸（<c>file</c> 直接指向 mp4 等）。</summary>
    Video = 2,

    /// <summary>网页壁纸（<c>file</c> 指向 index.html）。本程序不渲染网页，降级用预览图。</summary>
    Web = 3,

    /// <summary>音频响应壁纸（WE 的 audiofilter，本程序不支持）。</summary>
    AudioFilter = 4,
}

/// <summary>
/// <c>project.json</c> 读取器（纯逻辑，无 UI / 无 Win32 依赖，可直接单测）。
/// </summary>
public static class WallpaperProjectReader
{
    /// <summary>
    /// 音频属性关键词。小写比较。
    /// 之所以是「关键词命中」而不是「已知字段表」：这些是【作者自定义】的
    /// general.properties 键，Wallpaper Engine 不约束命名，实测同一功能
    /// 在不同壁纸里叫 musicvolume / bgmvolume / ambiencevolume / rainvolume /
    /// oceansfxvolume / voicevolume / gscvolume / aronavoicelinevolume 等。
    /// </summary>
    private static readonly string[] AudioKeywords =
    {
        "audio", "volume", "music", "bgm", "sfx", "sound", "voice", "ambience", "ambient",
    };

    /// <summary>判断目录下是否有 project.json。</summary>
    public static bool HasProjectFile(string directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && File.Exists(Path.Combine(directory, "project.json"));

    /// <summary>
    /// 读取并归一化一个 WE 项目目录。任何情况都不抛异常：
    /// 扫描器要能容忍个别损坏的项目而不中断整轮检索。
    /// </summary>
    /// <param name="directory">项目目录绝对路径。</param>
    public static WallpaperProjectDescriptor? Read(string directory)
    {
        if (!HasProjectFile(directory))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, "project.json"));
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                // WE 的 project.json 是制表符缩进的宽松 JSON；允许注释以防个别导出带注释
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = doc.RootElement;

            var typeText = GetString(root, "type");
            var kind = ParseKind(typeText);

            // file 字段：相对路径，可能含中文/空格/方括号等（实测有
            // "[SHANA]CH0334_Idle-4x-RIFE-RIFE3.1-120fps_waifu2x_2x_mp4.mp4" 这种名字），
            // 也可能整个缺失或为空串（实测 1 个项目 file 为空）。
            var mainFile = ResolveRelative(directory, GetString(root, "file"));
            var preview = ResolveRelative(directory, GetString(root, "preview"));

            var (mightHaveAudio, audioProps) = AnalyzeAudio(root, directory, kind, mainFile);

            return new WallpaperProjectDescriptor
            {
                ProjectDirectory = Path.GetFullPath(directory),
                Title = NonEmpty(GetString(root, "title")) ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),
                Kind = kind,
                MainFilePath = mainFile,
                PreviewPath = preview ?? FindPreviewFallback(directory),
                WorkshopId = NonEmpty(GetString(root, "workshopid")),
                ContentRating = NonEmpty(GetString(root, "contentrating")),
                MightHaveAudio = mightHaveAudio,
                AudioPropertyNames = audioProps,
            };
        }
        catch (Exception ex)
        {
            // 解析失败也要留一条可显示的记录：扫描器据此在界面上标「无法读取」，
            // 而不是让用户觉得「我的壁纸少了一个」却不知原因
            return new WallpaperProjectDescriptor
            {
                ProjectDirectory = Path.GetFullPath(directory),
                Title = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),
                Kind = WallpaperProjectKind.Unknown,
                ParseError = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>type 字段归一化。大小写不敏感，未知值返回 Unknown 而不是抛。</summary>
    public static WallpaperProjectKind ParseKind(string? type)
    {
        return (type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "scene" => WallpaperProjectKind.Scene,
            "video" => WallpaperProjectKind.Video,
            "web" => WallpaperProjectKind.Web,
            "audiofilter" or "audio" => WallpaperProjectKind.AudioFilter,
            _ => WallpaperProjectKind.Unknown,
        };
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static string? NonEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// 把 project.json 里的相对路径解析成绝对路径，并做【目录穿越防护】：
    /// project.json 是可以来自创意工坊的第三方内容，恶意构造的
    /// <c>"file": "..\\..\\..\\windows\\system32\\evil.exe"</c> 不应被本程序当成素材路径使用。
    /// 越界或含非法字符时返回 null（调用方按「资源缺失」处理）。
    /// </summary>
    public static string? ResolveRelative(string directory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(Path.Combine(directory, relative));
            string rootFull = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return null; // 逃出项目目录，拒绝
            }

            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            // 非法路径字符（含冒号的设备名等）会让 GetFullPath 抛 —— 同样按缺失处理
            return null;
        }
    }

    /// <summary>
    /// 判定该壁纸是否可能发声，并收集命中的音频属性名。
    /// </summary>
    private static (bool MightHaveAudio, IReadOnlyList<string> Properties) AnalyzeAudio(
        JsonElement root, string directory, WallpaperProjectKind kind, string? mainFile)
    {
        var names = new List<string>();

        bool supportsAudioProcessing = false;
        if (root.TryGetProperty("general", out var general)
            && general.ValueKind == JsonValueKind.Object)
        {
            if (general.TryGetProperty("supportsaudioprocessing", out var sap))
            {
                supportsAudioProcessing = sap.ValueKind is JsonValueKind.True
                    || (sap.ValueKind == JsonValueKind.Number && sap.GetInt32() != 0);
            }

            if (general.TryGetProperty("properties", out var props)
                && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    string key = prop.Name.ToLowerInvariant();
                    if (AudioKeywords.Any(k => key.Contains(k, StringComparison.Ordinal)))
                    {
                        names.Add(prop.Name);
                    }
                }
            }
        }

        // ③ 素材层面：项目里有 sound/ 或 audio/ 目录，或主文件是视频（视频普遍自带音轨）
        bool hasAudioFolder = HasSubDirectory(directory, "sound") || HasSubDirectory(directory, "audio");
        bool videoMain = kind == WallpaperProjectKind.Video
            || (mainFile != null && WallpaperFormats.IsVideo(mainFile));

        return (supportsAudioProcessing || names.Count > 0 || hasAudioFolder || videoMain, names);
    }

    private static bool HasSubDirectory(string directory, string name)
    {
        try
        {
            return Directory.Exists(Path.Combine(directory, name));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// preview 字段缺失时的兜底：在项目根找一张 preview.* 或任意缩略图。
    /// 实测 96 个项目里 preview.gif(42) / preview.jpg(36) 覆盖了绝大多数，
    /// 少数只有 scene.pkg 的靠 pkg 解包产物提供封面。
    /// </summary>
    private static string? FindPreviewFallback(string directory)
    {
        try
        {
            foreach (var pattern in new[] { "preview.*", "thumbnail.*", "preview_*.gif" })
            {
                var hit = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(WallpaperFormats.IsImage);
                if (hit != null)
                {
                    return hit;
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读：没有预览就算了，不影响项目本身可被选中
        }

        return null;
    }
}
