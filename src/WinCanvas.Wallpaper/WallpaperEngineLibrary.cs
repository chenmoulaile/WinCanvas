using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using WinCanvas.Wallpaper.Loaders;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper;

/// <summary>
/// 壁纸库里的一条可选项（v0.3.0 新增，供「自动检索」列表面板使用）。
/// </summary>
/// <param name="Path">
/// 传给 <see cref="IWallpaperService.UpdateWallpaperAsync"/> 的路径 ——
/// 可能是 WE 项目目录，也可能是散落的图片/视频/pkg 文件，取决于 <see cref="EntryKind"/>。
/// </param>
public sealed record WallpaperLibraryEntry(
    string Path,
    string Title,
    WallpaperLibraryEntryKind EntryKind,
    string? PreviewPath,
    bool MightHaveAudio,
    bool IsUsable,
    string? UnusableReason);

/// <summary>库条目的形态。</summary>
public enum WallpaperLibraryEntryKind
{
    /// <summary>Wallpaper Engine 项目目录（含 project.json）。</summary>
    Project,

    /// <summary>单个媒体文件（图片 / 视频 / .pkg）。</summary>
    MediaFile,
}

/// <summary>
/// Wallpaper Engine 壁纸库检索器（v0.3.0 新增）。
///
/// 为什么需要：此前挂壁纸只能「点按钮 → 在文件对话框里一层层翻到那个目录」，
/// 而 Wallpaper Engine 的库动辄上百个项目、分散在 Steam 库与创意工坊缓存里。
/// 本类负责【自动发现安装位置 + 列出可用壁纸】，界面只需要渲染这份目录。
///
/// 发现策略（按可靠性排序，全部失败则返回空列表，绝不猜一个不存在的路径）：
/// ① 用户显式配置的扫描根（设置面板可增删）—— 这是唯一能覆盖
///    「非 Steam 安装 / 绿色版 / 放在自己盘上」情形的手段，实测本机就是这种；
/// ② 注册表卸载信息里的 Steam App 431960 安装位置；
/// ③ 标准 Steam 路径 + libraryfolders.vdf 里登记的其它 Steam 库；
/// ④ 上述每个库下的 workshop/content/431960（创意工坊已订阅壁纸）。
///
/// 扫描深度刻意限制在【根目录的直接子项】：不做递归全盘遍历 ——
/// 一个真实库里 96 个项目就是 96 个子目录，递归下去会撞上素材文件夹
/// （shaders / materials / sound）并产生大量无意义条目。
/// </summary>
public static class WallpaperEngineLibrary
{
    /// <summary>Wallpaper Engine 在 Steam 上的 AppId。</summary>
    private const string SteamAppId = "431960";

    /// <summary>WE 项目相对 Steam 库的安装子路径。</summary>
    private static readonly string ProjectsRelative =
        Path.Combine("steamapps", "common", "wallpaper_engine", "projects", "myprojects");

    /// <summary>创意工坊内容相对 Steam 库的子路径。</summary>
    private static readonly string WorkshopRelative =
        Path.Combine("steamapps", "workshop", "content", SteamAppId);

    /// <summary>
    /// 推断候选扫描根（不含用户自定义项）。返回的每一项都【已确认存在】。
    /// </summary>
    public static IReadOnlyList<string> DetectRoots()
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path)))
                {
                    found.Add(Path.GetFullPath(path));
                }
            }
            catch (Exception)
            {
                // 非法路径字符 / 权限问题：跳过这个候选，不影响其它来源
            }
        }

        // ② 注册表：Steam 卸载信息里记了安装目录（HKLM 与 HKCU 都试，32/64 位视图都试）
        Add(ReadSteamInstallLocation(RegistryHive.LocalMachine, RegistryView.Registry64));
        Add(ReadSteamInstallLocation(RegistryHive.LocalMachine, RegistryView.Registry32));
        Add(ReadSteamInstallLocation(RegistryHive.CurrentUser, RegistryView.Registry64));

        // ③ 标准路径
        foreach (string steamRoot in StandardSteamRoots())
        {
            AddProjectAndWorkshop(steamRoot, Add);
        }

        // ③' libraryfolders.vdf 里登记的其它 Steam 库（多盘安装很常见）
        foreach (string library in SteamLibraryRoots())
        {
            AddProjectAndWorkshop(library, Add);
        }

        return found;
    }

    private static void AddProjectAndWorkshop(string steamRoot, Action<string?> add)
    {
        if (string.IsNullOrEmpty(steamRoot))
        {
            return;
        }

        add(Path.Combine(steamRoot, ProjectsRelative));
        add(Path.Combine(steamRoot, WorkshopRelative));
    }

    private static IEnumerable<string> StandardSteamRoots()
    {
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"C:\Program Files\Steam";
    }

    /// <summary>从注册表读 Steam 版 WE 的安装位置（未通过 Steam 安装时为 null）。</summary>
    private static string? ReadSteamInstallLocation(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SteamAppId}");

            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception)
        {
            // 键不存在 / 无权限：正常情况（绿色版、非 Steam 安装），静默返回 null
            return null;
        }
    }

    /// <summary>
    /// 解析 Steam 的 libraryfolders.vdf，取出所有已登记的库根目录。
    /// 只读文本、按行匹配 "path"，不实现完整 VDF 语法 —— 我们只要路径。
    /// </summary>
    private static IEnumerable<string> SteamLibraryRoots()
    {
        foreach (string steamRoot in StandardSteamRoots())
        {
            string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(vdf);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string line in lines)
            {
                // 形如：			"path"		"C:\\Program Files (x86)\\Steam"
                int firstQuote = line.IndexOf('"');
                if (firstQuote < 0 || !line.TrimStart().StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int secondQuote = line.IndexOf('"', firstQuote + 1);
                int thirdQuote = line.IndexOf('"', secondQuote + 1);
                if (secondQuote < 0 || thirdQuote <= secondQuote)
                {
                    continue;
                }

                string raw = line[(secondQuote + 1)..thirdQuote].Replace(@"\\", @"\");
                if (Directory.Exists(raw))
                {
                    yield return raw;
                }
            }
        }
    }

    /// <summary>
    /// 扫描若干根目录，产出可挂接的壁纸条目。
    /// </summary>
    /// <param name="roots">扫描根（WE 的 myprojects 目录、workshop 目录，或任意含素材的文件夹）。</param>
    /// <param name="includeLooseMedia">
    /// 是否把根目录里的【散落的图片/视频/pkg】也列为条目。
    /// 实测 myprojects 下就有直接把 mp4 放在项目目录里的情况，关掉会漏。
    /// </param>
    /// <param name="cancellationToken">取消令牌（大库扫描可中断）。</param>
    public static IReadOnlyList<WallpaperLibraryEntry> Scan(
        IEnumerable<string> roots, bool includeLooseMedia = true, CancellationToken cancellationToken = default)
    {
        var entries = new List<WallpaperLibraryEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots ?? Array.Empty<string>())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            // 根目录本身就是一个 WE 项目（用户直接指到某个项目目录）
            if (WallpaperProjectReader.HasProjectFile(root))
            {
                TryAdd(entries, seenPaths, BuildProjectEntry(root));
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                continue; // 根不可读：跳过
            }

            foreach (string sub in subDirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // 只认「含 project.json 的子目录」：素材目录（shaders/materials/sound）没有描述符，
                // 自然被排除，不需要维护一份黑名单
                if (WallpaperProjectReader.HasProjectFile(sub))
                {
                    TryAdd(entries, seenPaths, BuildProjectEntry(sub));
                }
            }

            if (includeLooseMedia)
            {
                foreach (WallpaperLibraryEntry media in EnumerateLooseMedia(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryAdd(entries, seenPaths, media);
                }
            }
        }

        // 排序：可用的排前面，同类里按标题 —— 让用户最先看到能挂的
        return entries
            .OrderByDescending(e => e.IsUsable)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>为一个 WE 项目目录构造条目。</summary>
    private static WallpaperLibraryEntry? BuildProjectEntry(string directory)
    {
        var descriptor = WallpaperProjectReader.Read(directory);
        if (descriptor == null)
        {
            return null;
        }

        // 明确判定「能不能挂」，并把不能的原因算在扫描阶段：
        // 界面上就能直接标灰 + 说明，而不是等用户点了才报错
        (bool usable, string? reason) = Classify(descriptor);

        return new WallpaperLibraryEntry(
            Path: descriptor.ProjectDirectory,
            Title: descriptor.Title,
            EntryKind: WallpaperLibraryEntryKind.Project,
            PreviewPath: descriptor.PreviewPath,
            MightHaveAudio: descriptor.MightHaveAudio,
            IsUsable: usable,
            UnusableReason: reason);
    }

    /// <summary>
    /// 判定项目可挂接性。与 <see cref="WallpaperProjectLoader"/> 的分派规则严格对齐，
    /// 避免「列表里看着可选、点了却报错」。
    /// </summary>
    private static (bool Usable, string? Reason) Classify(WallpaperProjectDescriptor descriptor)
    {
        if (descriptor.ParseError != null)
        {
            return (false, $"project.json 无法解析（{descriptor.ParseError}）");
        }

        switch (descriptor.Kind)
        {
            case WallpaperProjectKind.Scene:
                // scene 依赖同目录的 scene.pkg；没有 pkg 但有预览图时仍可降级显示
                if (File.Exists(Path.Combine(descriptor.ProjectDirectory, "scene.pkg")))
                {
                    return (true, null);
                }

                return descriptor.PreviewPath != null
                    ? (true, "未导出 scene.pkg，将降级显示预览图")
                    : (false, "场景壁纸缺少 scene.pkg（项目未导出）");

            case WallpaperProjectKind.Video:
                return descriptor.MainFilePath != null
                    ? (true, null)
                    : (false, "视频壁纸的 file 指向的文件不存在");

            case WallpaperProjectKind.Web:
                // 本程序不渲染网页，但有预览动画可降级
                return descriptor.PreviewPath != null
                    ? (true, "网页壁纸不渲染动态内容，将显示预览动画")
                    : (false, "网页壁纸（index.html）不受支持，且无预览图");

            case WallpaperProjectKind.AudioFilter:
                return (false, "音频响应壁纸（audiofilter）不受支持");

            default:
                return (false, "无法识别的项目类型");
        }
    }

    /// <summary>列出根目录里的散落媒体文件（图片 / 视频 / pkg），只扫顶层不递归。</summary>
    private static IEnumerable<WallpaperLibraryEntry> EnumerateLooseMedia(string root)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(root);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string file in files)
        {
            if (!WallpaperFormats.IsImage(file) && !WallpaperFormats.IsVideo(file) && !WallpaperFormats.IsPkg(file))
            {
                continue;
            }

            // 项目目录里的 preview.* 不重复列：它已经作为该项目的缩略图出现过了
            string name = Path.GetFileName(file);
            if (name.StartsWith("preview", StringComparison.OrdinalIgnoreCase)
                && WallpaperProjectReader.HasProjectFile(root))
            {
                continue;
            }

            yield return new WallpaperLibraryEntry(
                Path: Path.GetFullPath(file),
                Title: Path.GetFileNameWithoutExtension(file),
                EntryKind: WallpaperLibraryEntryKind.MediaFile,
                PreviewPath: WallpaperFormats.IsImage(file) ? Path.GetFullPath(file) : null,
                MightHaveAudio: WallpaperFormats.IsVideo(file),
                IsUsable: true,
                UnusableReason: null);
        }
    }

    private static void TryAdd(List<WallpaperLibraryEntry> entries, HashSet<string> seen, WallpaperLibraryEntry? entry)
    {
        if (entry == null)
        {
            return;
        }

        if (seen.Add(entry.Path))
        {
            entries.Add(entry);
        }
    }
}
