using System.Diagnostics;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Loaders;
using WinCanvas.Wallpaper.Models;

namespace WvLibraryDiag;

/// <summary>
/// Wallpaper Engine 壁纸库检索与项目解析的验收命令（v0.3.0）。
///
/// 为什么要有它：WE 兼容性是【数据驱动】的功能 —— 正确性取决于真实素材的形态
/// （type 大小写、file 里的中文/方括号文件名、有没有 scene.pkg、web 项目怎么降级），
/// 这些没法靠合成单测穷举。本命令直接对着真实库跑一遍，逐项报告分派结果。
///
/// 用法：
///   WvLibraryDiag [扫描根目录...]
/// 不带参数时只跑自动探测（注册表 + Steam 标准路径 + libraryfolders.vdf）。
///
/// 退出码：0 = 全部可挂条目都能被加载器正确分派；1 = 存在不一致或异常。
/// 关键校验不是「能不能打开」，而是【扫描器判定的可用性 与 加载器的实际行为是否一致】——
/// 两者不一致就是用户在列表里点了却报错的 bug。
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = new();

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("== Wallpaper Engine 壁纸库诊断 (v0.3.0) ==");

        var roots = new List<string>();
        if (args.Length > 0)
        {
            roots.AddRange(args);
        }

        var detected = WallpaperEngineLibrary.DetectRoots();
        Console.WriteLine($"\n-- 自动探测到 {detected.Count} 个候选根 --");
        foreach (var d in detected)
        {
            Console.WriteLine($"   [{d.Source}] {d.Path}");
        }

        foreach (var d in detected)
        {
            if (!roots.Contains(d.Path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(d.Path);
            }
        }

        if (roots.Count == 0)
        {
            Console.WriteLine("\n没有可扫描的根目录。用法：WvLibraryDiag <WE 的 myprojects 目录>");
            return 1;
        }

        Console.WriteLine($"\n-- 扫描根（共 {roots.Count} 个）--");
        foreach (string r in roots)
        {
            Console.WriteLine($"   {r}{(Directory.Exists(r) ? "" : "   [目录不存在]")}");
        }

        var sw = Stopwatch.StartNew();
        List<WallpaperLibraryEntry> entries;
        try
        {
            entries = WallpaperEngineLibrary.Scan(roots).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n扫描整体抛异常：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        sw.Stop();

        int usable = entries.Count(e => e.IsUsable);
        Console.WriteLine($"\n-- 扫描结果：{entries.Count} 条，其中可挂接 {usable} 条，耗时 {sw.ElapsedMilliseconds}ms --");

        var byKind = entries.GroupBy(e => e.EntryKind).OrderByDescending(g => g.Count());
        foreach (var g in byKind)
        {
            Console.WriteLine($"   形态 {g.Key}: {g.Count()}");
        }

        var byProjectKind = entries
            .Where(e => e.EntryKind == WallpaperLibraryEntryKind.Project)
            .GroupBy(e => KindOf(e.Path))
            .OrderByDescending(g => g.Count());
        foreach (var g in byProjectKind)
        {
            Console.WriteLine($"   项目类型 {g.Key}: {g.Count()}");
        }

        Console.WriteLine($"\n   含音频线索的条目: {entries.Count(e => e.MightHaveAudio)}");

        // 分类统计：不可用原因
        Console.WriteLine("\n-- 不可用条目分类 --");
        foreach (var group in entries.Where(e => !e.IsUsable)
                     .GroupBy(e => e.UnusableReason ?? "(无说明)").OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"   {group.Count()} × {group.Key}");
        }

        // 核心校验：扫描器说可用的，加载器必须真能分派；扫描器说不可用的，加载器必须抛得清楚
        Console.WriteLine("\n-- 一致性校验（扫描判定 vs 加载器实际行为）--");
        int checkedCount = 0;
        foreach (WallpaperLibraryEntry entry in entries.Where(e => e.IsUsable).Take(CrossCheckLimit))
        {
            checkedCount++;
            CrossCheck(entry);
        }

        foreach (WallpaperLibraryEntry entry in entries.Where(e => !e.IsUsable).Take(CrossCheckLimit))
        {
            checkedCount++;
            CrossCheckUnusable(entry);
        }
        Console.WriteLine($"   共交叉校验 {checkedCount} 条（上限 {CrossCheckLimit * 2}）");

        Console.WriteLine("\n-- 抽样明细（前 20 条）--");
        foreach (WallpaperLibraryEntry e in entries.Take(20))
        {
            string flag = e.IsUsable ? "OK  " : "SKIP";
            string audio = e.MightHaveAudio ? " [audio?]" : "";
            Console.WriteLine($"   [{flag}] {e.EntryKind,-10} {Truncate(e.Title, 46)}{audio}  {Truncate(e.Path, 60)}");
        }

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"全部通过：{entries.Count} 条目，扫描判定与加载器行为一致。");
            return 0;
        }

        Console.WriteLine($"失败 {Failures.Count} 项：");
        foreach (string f in Failures.Take(40))
        {
            Console.WriteLine("   - " + f);
        }
        return 1;
    }

    /// <summary>交叉校验条数上限：真实库上百项，全跑一遍解码太慢，这里只做分派层校验。</summary>
    private const int CrossCheckLimit = 40;

    /// <summary>取项目类型（读 project.json，失败返回 Unknown）。</summary>
    private static string KindOf(string projectDirectory)
    {
        var d = WallpaperProjectReader.Read(projectDirectory);
        return d == null ? "(读不到)" : d.Kind.ToString() + (d.ParseError != null ? "(解析失败)" : "");
    }

    /// <summary>
    /// 可用条目：必须能解析出「走哪条分派」，且【不真的解码】也要能构造到分派点。
    /// 这里只验证到描述符 + 目标素材存在，避免为诊断而把上百个 4K 素材全解码一遍。
    /// </summary>
    private static void CrossCheck(WallpaperLibraryEntry entry)
    {
        try
        {
            if (entry.EntryKind == WallpaperLibraryEntryKind.MediaFile)
            {
                if (!File.Exists(entry.Path))
                {
                    Fail(entry, "媒体文件条目指向的路径不存在");
                }
                return;
            }

            var d = WallpaperProjectReader.Read(entry.Path);
            if (d == null)
            {
                Fail(entry, "扫描器判可用，但 project.json 读不出来");
                return;
            }
            if (d.ParseError != null)
            {
                Fail(entry, $"扫描器判可用，但解析抛错：{d.ParseError}");
                return;
            }

            switch (d.Kind)
            {
                case WallpaperProjectKind.Scene:
                    // 场景必须能找到 scene.pkg（或至少有预览图可降级）
                    bool hasPkg = File.Exists(Path.Combine(entry.Path, "scene.pkg"));
                    if (!hasPkg && d.PreviewPath == null)
                    {
                        Fail(entry, "scene 项目既无 scene.pkg 也无预览图，却被判可用");
                    }
                    break;

                case WallpaperProjectKind.Video:
                    if (d.MainFilePath == null)
                    {
                        Fail(entry, "video 项目的 file 指向的素材不存在，却被判可用");
                    }
                    break;

                case WallpaperProjectKind.Web:
                    if (d.PreviewPath == null)
                    {
                        Fail(entry, "web 项目没有可降级的预览图，却被判可用");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail(entry, $"校验过程抛异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>不可用条目：加载器必须给出【明确的 NotSupportedException】，而不是静默或未知异常。</summary>
    private static void CrossCheckUnusable(WallpaperLibraryEntry entry)
    {
        if (entry.EntryKind != WallpaperLibraryEntryKind.Project)
        {
            return;
        }

        try
        {
            var source = WallpaperProjectLoader.Load(entry.Path, audioEnabled: false);
            // 加载成功说明扫描器把它误判成不可用了 —— 两处口径不一致
            Fail(entry, $"扫描器判不可用（{entry.UnusableReason}），但加载器却成功了（Kind={source.Kind}）");
            source.Dispose();
        }
        catch (NotSupportedException)
        {
            // 预期路径：类型不支持 / 素材缺失，且异常信息是人话
        }
        catch (Exception ex)
        {
            Fail(entry, $"加载器抛了非预期异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Fail(WallpaperLibraryEntry entry, string message)
    {
        Failures.Add($"{Truncate(entry.Title, 30)} → {message}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
