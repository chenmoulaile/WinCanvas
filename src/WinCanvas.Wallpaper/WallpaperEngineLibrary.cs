using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using WinCanvas.Core.Models;
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
/// <param name="Source">条目来源（v0.4.0 新增：工坊 / 系统 / 图片库 / 自定义，界面打标签用）。</param>
public sealed record WallpaperLibraryEntry(
    string Path,
    string Title,
    WallpaperLibraryEntryKind EntryKind,
    string? PreviewPath,
    bool MightHaveAudio,
    bool IsUsable,
    string? UnusableReason,
    WallpaperLibrarySource Source = WallpaperLibrarySource.Custom);

/// <summary>库条目的形态。</summary>
public enum WallpaperLibraryEntryKind
{
    /// <summary>Wallpaper Engine 项目目录（含 project.json）。</summary>
    Project,

    /// <summary>单个媒体文件（图片 / 视频 / .pkg）。</summary>
    MediaFile,
}

/// <summary>
/// 一个候选扫描根（v0.4.0 新增）：路径 + 它属于哪类来源。
/// 来源随条目一路带到界面（「工坊 / 系统 / 图片库 / 自定义」标签），
/// 也决定扫描细则（如系统主题目录要多探一层 DesktopBackground）。
/// </summary>
/// <param name="Path">绝对路径；构造方保证已确认存在或允许缺席（扫描时容错）。</param>
/// <param name="Source">来源分类。</param>
public sealed record WallpaperLibraryRoot(string Path, WallpaperLibrarySource Source);

/// <summary>一次增量扫描的进度快照（界面显示「第 N / M 个目录」用）。</summary>
/// <param name="ScannedRoots">已完成的根数。</param>
/// <param name="TotalRoots">总根数。</param>
/// <param name="EntryCount">截至当前的累计条目数。</param>
public sealed record WallpaperLibraryScanProgress(int ScannedRoots, int TotalRoots, int EntryCount);

/// <summary>一次增量扫描的结果：合并后的条目（按可用性 + 标题排序）+ 供持久化的按根缓存。</summary>
/// <param name="Entries">合并去重后的全部条目。</param>
/// <param name="RootCaches">按根的缓存（签名 + 条目 + 扫描时间），整包写进 config。</param>
public sealed record WallpaperLibraryScanResult(
    IReadOnlyList<WallpaperLibraryEntry> Entries,
    IReadOnlyList<WallpaperLibraryRootCache> RootCaches);

/// <summary>
/// Wallpaper Engine 壁纸库检索器（v0.3.0 新增；v0.4.0 扩展为多来源检索 + 增量刷新）。
///
/// 为什么需要：此前挂壁纸只能「点按钮 → 在文件对话框里一层层翻到那个目录」，
/// 而 Wallpaper Engine 的库动辄上百个项目、分散在 Steam 库与创意工坊缓存里。
/// 本类负责【自动发现安装位置 + 列出可用壁纸】，界面只需要渲染这份目录。
///
/// 工坊发现策略（按可靠性排序，全部失败则返回空列表，绝不猜一个不存在的路径）：
/// ① 用户显式配置的扫描根（设置面板可增删）—— 这是唯一能覆盖
///    「非 Steam 安装 / 绿色版 / 放在自己盘上」情形的手段，实测本机就是这种；
/// ② 注册表卸载信息里的 Steam App 431960 安装位置；
/// ③ HKCU\Software\Valve\Steam 的 SteamPath（v0.4.0 新增）——Steam 装在非标准盘时
///    （实测本机 D:\Steam）这是【唯一】能探到库位置的注册表来源，此前漏掉它导致
///    本机 120 个工坊项目全部探不到；
/// ④ 标准 Steam 路径 + 各 Steam 根下 libraryfolders.vdf 里登记的其它库；
/// ⑤ 上述每个库下的 workshop/content/431960（创意工坊已订阅壁纸）
///    与 steamapps/common/wallpaper_engine/projects/myprojects（本地项目）。
///
/// v0.4.0 新增的两类候选根（与工坊并列，来源标签区分）：
/// · 系统主题壁纸 %LOCALAPPDATA%\Microsoft\Windows\Themes（顶层壁纸文件 + 各主题包的
///   DesktopBackground 目录）——目录不存在或里面没有图片属正常，按空处理绝不报错；
/// · 用户图片库（KnownFolder Pictures）。
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

    /// <summary>系统主题壁纸目录（v0.4.0）：TransferredWallpaper 与主题包的桌面背景都落在这。</summary>
    private static readonly string SystemThemesDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Themes");

    /// <summary>主题包内桌面背景的固定子目录名（Themes\{主题包}\DesktopBackground）。</summary>
    private const string DesktopBackgroundFolderName = "DesktopBackground";

    /// <summary>
    /// 根目录内容签名的哨兵值：目录不存在 / 不可读时的签名。
    /// 缓存里同样是该值就整根复用（跳过重扫），否则触发重扫并写回新签名。
    /// </summary>
    internal const string MissingRootSignature = "missing";

    /// <summary>
    /// 推断【工坊】候选扫描根。返回的每一项都【已确认存在】。
    /// v0.4.0 起返回带来源的根记录（此前只返回路径字符串）。
    /// </summary>
    public static IReadOnlyList<WallpaperLibraryRoot> DetectRoots()
    {
        var found = new List<WallpaperLibraryRoot>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(path);
                if (Directory.Exists(full)
                    && found.All(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(new WallpaperLibraryRoot(full, WallpaperLibrarySource.Workshop));
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

        // ②' 非 Steam 渠道的 WE（v0.4.0 新增）：按 DisplayName 枚举卸载表找「Wallpaper Engine」
        //    条目（绿色版 / 独立安装也会有卸载登记），其 projects 目录直接就是扫描根
        foreach (string installDir in ReadWallpaperEngineInstallLocations())
        {
            Add(Path.Combine(installDir, "projects"));
        }

        // ②'' WE 独立安装的常见路径（ask 要求「注册表/常见路径」；都做存在性检查，不存在即跳过）
        Add(@"C:\Program Files\Wallpaper Engine\projects");
        Add(@"C:\Program Files (x86)\Wallpaper Engine\projects");

        // ③ + ③'：Steam 根候选 = 标准路径 ∪ HKCU SteamPath（v0.4.0 新增）；
        //    每个 Steam 根下既找 WE 安装目录，也解析 libraryfolders.vdf 里的多库登记
        foreach (string steamRoot in SteamRootCandidates())
        {
            Add(Path.Combine(steamRoot, ProjectsRelative));
            Add(Path.Combine(steamRoot, WorkshopRelative));

            foreach (string library in ParseLibraryFolders(
                         Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf")))
            {
                Add(Path.Combine(library, ProjectsRelative));
                Add(Path.Combine(library, WorkshopRelative));
            }
        }

        return found;
    }

    /// <summary>
    /// 系统主题壁纸候选根（v0.4.0 新增）。目录不存在（从未换过主题的精简系统）返回空列表。
    /// </summary>
    public static IReadOnlyList<WallpaperLibraryRoot> DetectSystemThemeRoots()
    {
        return Directory.Exists(SystemThemesDirectory)
            ? new[] { new WallpaperLibraryRoot(SystemThemesDirectory, WallpaperLibrarySource.SystemTheme) }
            : Array.Empty<WallpaperLibraryRoot>();
    }

    /// <summary>
    /// 用户图片库候选根（v0.4.0 新增）。走 KnownFolder Pictures 的托管封装
    /// （Environment.SpecialFolder.MyPictures），用户重定向过「图片」位置时返回重定向后的路径；
    /// 系统取不到该 KnownFolder（极端精简环境）返回空列表。
    /// </summary>
    public static IReadOnlyList<WallpaperLibraryRoot> DetectPicturesRoots()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return !string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures)
            ? new[] { new WallpaperLibraryRoot(pictures, WallpaperLibrarySource.Pictures) }
            : Array.Empty<WallpaperLibraryRoot>();
    }

    /// <summary>
    /// 全部自动探测候选根（v0.4.0 入口）：工坊 + 系统主题 + 图片库，按路径去重（先到先得）。
    /// </summary>
    public static IReadOnlyList<WallpaperLibraryRoot> DetectCandidateRoots()
    {
        var result = new List<WallpaperLibraryRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (WallpaperLibraryRoot root in DetectRoots()
                     .Concat(DetectSystemThemeRoots())
                     .Concat(DetectPicturesRoots()))
        {
            if (seen.Add(root.Path))
            {
                result.Add(root);
            }
        }

        return result;
    }

    /// <summary>
    /// Steam 安装根候选：标准路径 + 注册表 HKCU\Software\Valve\Steam 的 SteamPath。
    /// SteamPath 通常是正斜杠小写（实测本机为 "d:/steam"），统一归一化成反斜杠绝对路径；
    /// 该路径不必真的存在（后面每个候选都会自行做存在性检查）。
    /// </summary>
    private static IEnumerable<string> SteamRootCandidates()
    {
        foreach (string standard in StandardSteamRoots())
        {
            yield return standard;
        }

        string? steamPath = ReadSteamPathFromRegistry();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            yield return steamPath.Replace('/', '\\');
        }
    }

    /// <summary>
    /// 读 HKCU\Software\Valve\Steam 下的 SteamPath 注册表值（v0.4.0 新增）。
    /// 键不存在 / 无权限 / 值非字符串（正常情况：没装 Steam）返回 null，绝不抛异常。
    /// </summary>
    internal static string? ReadSteamPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception)
        {
            // 注册表访问失败（权限 / 配额）：静默降级，Steam 根退回标准路径候选
            return null;
        }
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
    /// 枚举卸载注册表里 DisplayName 为「Wallpaper Engine」的条目，取其安装目录
    /// （v0.4.0 新增，覆盖非 Steam 渠道的独立安装）。HKLM 64/32 与 HKCU 三个视图都查。
    /// 找不到 / 无权限返回空序列，绝不抛。
    /// </summary>
    private static IEnumerable<string> ReadWallpaperEngineInstallLocations()
    {
        foreach ((RegistryHive hive, RegistryView view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            // 迭代器方法不能在带 catch 的 try 块体内 yield（CS1626）：
            // 先在 try/catch 里收集成列表，再在块外产出
            var locations = new List<string>();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("DisplayName") is not string displayName
                        || !displayName.Contains("Wallpaper Engine", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (subKey.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                    {
                        locations.Add(location);
                    }
                }
            }
            catch (Exception)
            {
                // 该视图不可读：跳过，其它视图继续
            }

            foreach (string location in locations)
            {
                yield return location;
            }
        }
    }

    /// <summary>
    /// 解析 Steam 的 libraryfolders.vdf，取出所有已登记的库根目录（v0.4.0 改为按传入路径解析，
    /// 便于从注册表 SteamPath 指到的 Steam 根里读，也便于单测合成文件）。
    /// 只读文本、按行匹配 "path"，不实现完整 VDF 语法 —— 我们只要路径。
    /// 文件不存在 / 读不动 / 无匹配返回空序列，绝不抛。
    /// </summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        if (!File.Exists(vdfPath))
        {
            yield break;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(vdfPath);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string line in lines)
        {
            // 形如：			"path"		"C:\\Program Files (x86)\\Steam"
            // v0.4.0 修复的继承缺陷：旧实现取 line[(path收口引号+1)..值开引号]，
            // 截出来的是两个引号之间的【制表符分隔符】而不是路径，
            // Directory.Exists 恒为假 → 多库解析静默返回空（工坊多库从未被探到过）。
            // 现在在去空白后的行内定位：跳过 "path" 字面量（6 字符），取
            // 【值的开引号 + 1 .. 值的收口引号】之间的内容。
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int valueOpenQuote = trimmed.IndexOf('"', "\"path\"".Length);
            if (valueOpenQuote < 0)
            {
                continue;
            }

            int valueCloseQuote = trimmed.IndexOf('"', valueOpenQuote + 1);
            if (valueCloseQuote <= valueOpenQuote)
            {
                continue;
            }

            string raw = trimmed[(valueOpenQuote + 1)..valueCloseQuote].Replace(@"\\", @"\");
            if (Directory.Exists(raw))
            {
                yield return raw;
            }
        }
    }

    /// <summary>
    /// 扫描若干根目录，产出可挂接的壁纸条目（v0.3.0 旧入口，来源按「自定义」记）。
    /// 新代码请改用 <see cref="ScanWithCache"/>：它带来源贯通与增量缓存。
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
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (WallpaperLibraryEntry entry in ScanSingleRoot(root, WallpaperLibrarySource.Custom, includeLooseMedia, cancellationToken))
            {
                TryAdd(entries, seenPaths, entry);
            }
        }

        return SortEntries(entries);
    }

    /// <summary>
    /// 增量扫描（v0.4.0 新入口）：带来源贯通 + 按根签名缓存。
    ///
    /// 增量语义：每个根先算内容签名（顶层目录/文件名 + 修改时间），
    /// 与上次缓存（<paramref name="previousCache"/>）一致就【整根复用缓存条目】，
    /// 不再读任何 project.json / 探测 scene.pkg；签名变了（或强制全量）才重扫该根。
    /// 重扫后条目按可用性 + 标题排序整体返回，缓存按根整包替换写回 config。
    ///
    /// 已知边界（接受并在此声明）：「自定义根下某项目的 project.json 原地改内容」
    /// 不会改变其父目录的修改时间，签名不变则该根被复用——此时点一次
    /// 「检索壁纸库」（forceFullScan）即可强制重扫全部根。
    /// </summary>
    /// <param name="roots">候选扫描根（带来源）。</param>
    /// <param name="previousCache">上次扫描落盘的按根缓存；null / 空表 = 首次扫描（全量）。</param>
    /// <param name="forceFullScan">true = 忽略缓存全部重扫（手动「检索壁纸库」按钮走这个）。</param>
    /// <param name="progress">进度回报（每完成一个根报一次）；null 表示不关心。</param>
    /// <param name="cancellationToken">取消令牌（收到取消抛 OperationCanceledException）。</param>
    public static WallpaperLibraryScanResult ScanWithCache(
        IReadOnlyList<WallpaperLibraryRoot> roots,
        IReadOnlyList<WallpaperLibraryRootCache>? previousCache = null,
        bool forceFullScan = false,
        IProgress<WallpaperLibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<WallpaperLibraryEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootCaches = new List<WallpaperLibraryRootCache>(roots.Count);
        int totalRoots = roots.Count;

        for (int i = 0; i < totalRoots; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WallpaperLibraryRoot root = roots[i];
            WallpaperLibraryRootCache? previous = previousCache?.FirstOrDefault(c =>
                string.Equals(c.Root, root.Path, StringComparison.OrdinalIgnoreCase)
                && c.Source == (int)root.Source);

            string signature = ComputeRootSignature(root.Path);

            List<WallpaperLibraryEntry> rootEntries;
            if (!forceFullScan
                && previous is not null
                && string.Equals(previous.Signature, signature, StringComparison.Ordinal))
            {
                // 签名未变：整根复用缓存条目，不碰磁盘上的 project.json（增量省的就是这部分 IO）
                rootEntries = previous.Entries
                    .Select(FromCacheEntry)
                    .Where(e => e is not null)
                    .Select(e => e!)
                    .ToList();
                rootCaches.Add(previous);
            }
            else
            {
                rootEntries = ScanSingleRoot(root.Path, root.Source, includeLooseMedia: true, cancellationToken).ToList();
                rootCaches.Add(new WallpaperLibraryRootCache
                {
                    Root = root.Path,
                    Source = (int)root.Source,
                    Signature = signature,
                    Entries = rootEntries.Select(ToCacheEntry).ToList(),
                    ScannedAtUtc = DateTimeOffset.UtcNow,
                });
            }

            foreach (WallpaperLibraryEntry entry in rootEntries)
            {
                TryAdd(entries, seenPaths, entry);
            }

            progress?.Report(new WallpaperLibraryScanProgress(i + 1, totalRoots, entries.Count));
        }

        return new WallpaperLibraryScanResult(SortEntries(entries), rootCaches);
    }

    /// <summary>
    /// 扫描单个根目录（工坊 / 主题 / 图片库 / 自定义通用）：
    /// ① 根本身是 WE 项目 → 一条；② 直接子目录含 project.json → 各一条；
    /// ③ 散落媒体 → 系统主题根走专用枚举（多探一层 DesktopBackground），其余扫顶层。
    /// 根不存在 / 不可读返回空序列，绝不抛（候选目录缺席是正常形态，不是错误）。
    /// </summary>
    private static IEnumerable<WallpaperLibraryEntry> ScanSingleRoot(
        string root, WallpaperLibrarySource source, bool includeLooseMedia, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        // 根目录本身就是一个 WE 项目（用户直接指到某个项目目录）
        if (WallpaperProjectReader.HasProjectFile(root))
        {
            WallpaperLibraryEntry? self = BuildProjectEntry(root, source);
            if (self is not null)
            {
                yield return self;
            }
        }

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(root);
        }
        catch (Exception)
        {
            yield break; // 根不可读：跳过
        }

        foreach (string sub in subDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 只认「含 project.json 的子目录」：素材目录（shaders/materials/sound）没有描述符，
            // 自然被排除，不需要维护一份黑名单
            if (WallpaperProjectReader.HasProjectFile(sub))
            {
                WallpaperLibraryEntry? entry = BuildProjectEntry(sub, source);
                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }

        if (includeLooseMedia)
        {
            IEnumerable<WallpaperLibraryEntry> media = source == WallpaperLibrarySource.SystemTheme
                ? EnumerateSystemThemeMedia(root)
                : EnumerateLooseMedia(root);

            foreach (WallpaperLibraryEntry entry in media)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
    }

    /// <summary>按「可用优先、同类按标题」排序（让用户最先看到能挂的）。</summary>
    private static IReadOnlyList<WallpaperLibraryEntry> SortEntries(List<WallpaperLibraryEntry> entries)
    {
        return entries
            .OrderByDescending(e => e.IsUsable)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>为一个 WE 项目目录构造条目。</summary>
    private static WallpaperLibraryEntry? BuildProjectEntry(string directory, WallpaperLibrarySource source)
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
            UnusableReason: reason,
            Source: source);
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
        // 项目目录里的 preview.* 不重复列：它已经作为该项目的缩略图出现过了
        bool suppressPreviews = WallpaperProjectReader.HasProjectFile(root);

        foreach (WallpaperLibraryEntry entry in EnumerateMediaFiles(root, source: WallpaperLibrarySource.Custom))
        {
            string name = Path.GetFileName(entry.Path);
            if (suppressPreviews && name.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return entry;
        }
    }

    /// <summary>
    /// 系统主题根的媒体枚举（v0.4.0）：Themes 目录的壁纸有两处固定落点——
    /// ① 顶层文件（如 TransferredWallpaper-*.jpg，换机迁移的桌面背景）；
    /// ② 每个主题包目录下的 DesktopBackground 子目录（Themes\{主题包}\DesktopBackground\*.jpg）。
    /// 只探这两处固定形态，不递归全树——主题包里的资源文件（*.theme 描述文件等）没有壁纸价值。
    /// </summary>
    private static IEnumerable<WallpaperLibraryEntry> EnumerateSystemThemeMedia(string root)
    {
        foreach (WallpaperLibraryEntry entry in EnumerateMediaFiles(root, source: WallpaperLibrarySource.SystemTheme))
        {
            yield return entry;
        }

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(root);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string sub in subDirectories)
        {
            string desktopBackground = Path.Combine(sub, DesktopBackgroundFolderName);
            if (!Directory.Exists(desktopBackground))
            {
                continue;
            }

            foreach (WallpaperLibraryEntry entry in EnumerateMediaFiles(desktopBackground, source: WallpaperLibrarySource.SystemTheme))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// 枚举一个目录【顶层】的受支持媒体文件（扩展名口径统一走 <see cref="WallpaperFormats"/>
    /// 单一事实来源）。目录不可读返回空序列。
    /// </summary>
    private static IEnumerable<WallpaperLibraryEntry> EnumerateMediaFiles(string directory, WallpaperLibrarySource source)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
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

            yield return new WallpaperLibraryEntry(
                Path: Path.GetFullPath(file),
                Title: Path.GetFileNameWithoutExtension(file),
                EntryKind: WallpaperLibraryEntryKind.MediaFile,
                PreviewPath: WallpaperFormats.IsImage(file) ? Path.GetFullPath(file) : null,
                MightHaveAudio: WallpaperFormats.IsVideo(file),
                IsUsable: true,
                UnusableReason: null,
                Source: source);
        }
    }

    private static void TryAdd(List<WallpaperLibraryEntry> entries, HashSet<string> seen, WallpaperLibraryEntry entry)
    {
        if (seen.Add(entry.Path))
        {
            entries.Add(entry);
        }
    }

    // ---------- 按根内容签名与缓存映射（v0.4.0 增量刷新的基础设施） ----------

    /// <summary>
    /// 计算扫描根的内容签名：顶层目录名 + 文件名 + 各自的修改时间（UTC Tick），
    /// 排序后做 SHA-256。只取【直接子项】的元数据——与扫描深度（顶层 + 直接子项目录）
    /// 同一口径：子项增删 / 改名 / 改内容都会改变签名，深层素材变化不该触发重扫。
    /// 目录不存在 / 不可读返回 <see cref="MissingRootSignature"/> 哨兵。
    /// </summary>
    internal static string ComputeRootSignature(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return MissingRootSignature;
        }

        var lines = new List<string>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                lines.Add($"d|{Path.GetFileName(dir)}|{Directory.GetLastWriteTimeUtc(dir).Ticks}");
            }

            foreach (string file in Directory.EnumerateFiles(root))
            {
                lines.Add($"f|{Path.GetFileName(file)}|{File.GetLastWriteTimeUtc(file).Ticks}");
            }
        }
        catch (Exception)
        {
            // 扫到一半不可读（权限 / 盘拔了）：按缺失处理，宁可触发一次重扫也不复用旧缓存
            return MissingRootSignature;
        }

        lines.Sort(StringComparer.Ordinal);

        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(hash);
    }

    /// <summary>条目 → 缓存条目（写 config 用）。字段一一对应，枚举按数值落盘。</summary>
    internal static WallpaperLibraryCacheEntry ToCacheEntry(WallpaperLibraryEntry entry)
    {
        return new WallpaperLibraryCacheEntry
        {
            Path = entry.Path,
            Title = entry.Title,
            Kind = (int)entry.EntryKind,
            Source = (int)entry.Source,
            PreviewPath = entry.PreviewPath,
            MightHaveAudio = entry.MightHaveAudio,
            IsUsable = entry.IsUsable,
            UnusableReason = entry.UnusableReason,
        };
    }

    /// <summary>
    /// 缓存条目 → 条目（重启秒开的回填路径）。Path 为空的脏数据返回 null（反序列化端
    /// 对手改 / 半截 config 要能容忍）；未知枚举数值按原值透传（界面按「未知来源」降级显示）。
    /// </summary>
    internal static WallpaperLibraryEntry? FromCacheEntry(WallpaperLibraryCacheEntry cache)
    {
        if (cache is null || string.IsNullOrWhiteSpace(cache.Path))
        {
            return null;
        }

        return new WallpaperLibraryEntry(
            Path: cache.Path,
            Title: cache.Title,
            EntryKind: (WallpaperLibraryEntryKind)cache.Kind,
            PreviewPath: cache.PreviewPath,
            MightHaveAudio: cache.MightHaveAudio,
            IsUsable: cache.IsUsable,
            UnusableReason: cache.UnusableReason,
            Source: (WallpaperLibrarySource)cache.Source);
    }
}
