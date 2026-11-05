using System.Diagnostics;
using WinCanvas.Core.Models;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// 壁纸库检索 v0.4.0 扩展的用例：多来源贯通（工坊/系统/图片库/自定义）、
/// 按根签名增量刷新、libraryfolders.vdf 解析、系统主题枚举、取消与进度。
///
/// 全部用【合成临时目录】（与 WallpaperProjectCompatTests 同一套约定）：
/// 真实库在开发者个人盘上、内容会变，拿它当测试基线会让 CI 今天绿明天红。
/// 注册表来源（SteamPath）做【契约断言】（返回 null 或存在的路径）——
/// 断言具体值会绑定到测试机装没装 Steam，不可移植。
/// </summary>
public sealed class WallpaperLibraryScanTests : IDisposable
{
    private readonly string _root;

    public WallpaperLibraryScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinCanvasLibTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }

    /// <summary>造一个 WE 项目目录（title 唯一，便于断言定位）。</summary>
    private string MakeProject(string parent, string name, string title = "TestTitle", string type = "video")
    {
        string dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"),
            $$"""{ "title" : "{{title}}", "type" : "{{type}}", "file" : "clip.mp4" }""");
        File.WriteAllBytes(Path.Combine(dir, "clip.mp4"), new byte[] { 1, 2, 3 });
        return dir;
    }

    /// <summary>同步收集进度的 IProgress 实现（Progress<T> 异步投递，测试里不好等）。</summary>
    private sealed class SyncProgress : IProgress<WallpaperLibraryScanProgress>
    {
        public List<WallpaperLibraryScanProgress> Reports { get; } = new();
        public void Report(WallpaperLibraryScanProgress value) => Reports.Add(value);
    }

    // ---------- 来源贯通 ----------

    [Fact]
    public void ScanWithCache_条目携带扫描根的来源()
    {
        string workshop = Path.Combine(_root, "workshop");
        string custom = Path.Combine(_root, "custom");
        Directory.CreateDirectory(workshop);
        Directory.CreateDirectory(custom);
        MakeProject(workshop, "111", title: "工坊项目");
        File.WriteAllBytes(Path.Combine(custom, "photo.jpg"), new byte[] { 1 });

        var result = WallpaperEngineLibrary.ScanWithCache(new[]
        {
            new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop),
            new WallpaperLibraryRoot(custom, WallpaperLibrarySource.Custom),
        });

        WallpaperLibraryEntry workshopEntry = result.Entries.Single(e => e.Title == "工坊项目");
        Assert.Equal(WallpaperLibrarySource.Workshop, workshopEntry.Source);

        WallpaperLibraryEntry photo = result.Entries.Single(e => e.Path.EndsWith("photo.jpg"));
        Assert.Equal(WallpaperLibrarySource.Custom, photo.Source);
        Assert.Equal(WallpaperLibraryEntryKind.MediaFile, photo.EntryKind);
    }

    [Fact]
    public void DetectSystemThemeRoots_目录存在时返回系统来源根()
    {
        IReadOnlyList<WallpaperLibraryRoot> roots = WallpaperEngineLibrary.DetectSystemThemeRoots();

        foreach (WallpaperLibraryRoot root in roots)
        {
            Assert.Equal(WallpaperLibrarySource.SystemTheme, root.Source);
            Assert.True(Directory.Exists(root.Path), "契约：探测返回的根必须真实存在");
        }
    }

    [Fact]
    public void DetectPicturesRoots_返回图片库来源且目录存在()
    {
        IReadOnlyList<WallpaperLibraryRoot> roots = WallpaperEngineLibrary.DetectPicturesRoots();

        // 图片库在标准 Windows 环境必然存在；极端精简环境允许为空（契约：返回即存在）
        foreach (WallpaperLibraryRoot root in roots)
        {
            Assert.Equal(WallpaperLibrarySource.Pictures, root.Source);
            Assert.True(Directory.Exists(root.Path));
        }
    }

    [Fact]
    public void ReadSteamPathFromRegistry_返回null或存在的路径()
    {
        string? steamPath = WallpaperEngineLibrary.ReadSteamPathFromRegistry();

        // 契约断言（不绑定到测试机是否装了 Steam）：
        // 有值时必须非空白且路径存在（Steam 写注册表时它就是安装目录）
        if (steamPath is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(steamPath));
            Assert.True(Directory.Exists(steamPath.Replace('/', '\\')),
                $"SteamPath={steamPath} 应指向存在的目录");
        }
    }

    // ---------- 增量刷新（按根签名缓存） ----------

    [Fact]
    public void ScanWithCache_签名未变时整根复用缓存_不重读磁盘()
    {
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        MakeProject(workshop, "111", title: "Original");

        WallpaperLibraryScanResult first = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop) });

        // 篡改缓存条目标题：若第二次扫描真的复用缓存（不重读磁盘），
        // 篡改值就会透传到结果里——这是「确实没有重扫」的直接证据
        first.RootCaches[0].Entries[0].Title = "MUTATED";

        WallpaperLibraryScanResult second = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop) },
            previousCache: first.RootCaches);

        Assert.Equal("MUTATED", second.Entries.Single().Title);
        Assert.Equal(first.RootCaches[0].Signature, second.RootCaches[0].Signature);
        Assert.Equal(first.RootCaches[0].ScannedAtUtc, second.RootCaches[0].ScannedAtUtc);
    }

    [Fact]
    public void ScanWithCache_新增项目后签名变化_触发重扫并更新缓存()
    {
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        MakeProject(workshop, "111", title: "Old");

        WallpaperLibraryScanResult first = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop) });

        // 根目录顶层新增一个项目：签名（顶层子项名 + mtime）必然变化
        MakeProject(workshop, "222", title: "New");

        WallpaperLibraryScanResult second = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop) },
            previousCache: first.RootCaches);

        Assert.NotEqual(first.RootCaches[0].Signature, second.RootCaches[0].Signature);
        Assert.Equal(2, second.Entries.Count);
        Assert.Contains(second.Entries, e => e.Title == "New");
        // 重扫后缓存条目来自磁盘真实数据（首测的缓存未被带入）
        Assert.All(second.RootCaches[0].Entries, e => Assert.NotEqual("MUTATED", e.Title));
    }

    [Fact]
    public void ScanWithCache_强制全量_忽略缓存重读磁盘()
    {
        string workshop = Path.Combine(_root, "workshop");
        Directory.CreateDirectory(workshop);
        MakeProject(workshop, "111", title: "Original");

        WallpaperLibraryScanResult first = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop) });
        first.RootCaches[0].Entries[0].Title = "MUTATED";

        WallpaperLibraryScanResult second = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(workshop, WallpaperLibrarySource.Workshop) },
            previousCache: first.RootCaches,
            forceFullScan: true);

        // 强制全量：磁盘上的真实标题回来，缓存篡改值被覆盖
        Assert.Equal("Original", second.Entries.Single().Title);
    }

    [Fact]
    public void ScanWithCache_根目录被删除_按缺失处理不报错()
    {
        string gone = Path.Combine(_root, "gone");
        Directory.CreateDirectory(gone);
        MakeProject(gone, "111");

        WallpaperLibraryScanResult first = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(gone, WallpaperLibrarySource.Workshop) });
        Assert.Single(first.Entries);

        Directory.Delete(gone, recursive: true);

        WallpaperLibraryScanResult second = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(gone, WallpaperLibrarySource.Workshop) },
            previousCache: first.RootCaches);

        Assert.Empty(second.Entries);
        Assert.Equal(WallpaperEngineLibrary.MissingRootSignature, second.RootCaches[0].Signature);
    }

    [Fact]
    public void ComputeRootSignature_稳定且对增删改名敏感()
    {
        string dir = Path.Combine(_root, "sig");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");

        string s1 = WallpaperEngineLibrary.ComputeRootSignature(dir);
        Assert.NotEqual(WallpaperEngineLibrary.MissingRootSignature, s1);
        Assert.Equal(s1, WallpaperEngineLibrary.ComputeRootSignature(dir)); // 稳定

        // 新增文件 → 变
        File.WriteAllText(Path.Combine(dir, "b.txt"), "y");
        Assert.NotEqual(s1, WallpaperEngineLibrary.ComputeRootSignature(dir));

        // 修改时间变化 → 变（显式设置 mtime：同一计时器量子内连写两次
        // 拿到相同时间戳，靠 sleep 等时间走是抖动测试，这里直接拨表）
        string bFile = Path.Combine(dir, "b.txt");
        string s2 = WallpaperEngineLibrary.ComputeRootSignature(dir);
        File.SetLastWriteTimeUtc(bFile, File.GetLastWriteTimeUtc(bFile) + TimeSpan.FromMinutes(1));
        Assert.NotEqual(s2, WallpaperEngineLibrary.ComputeRootSignature(dir));

        // 目录不存在 → 哨兵
        Directory.Delete(dir, recursive: true);
        Assert.Equal(WallpaperEngineLibrary.MissingRootSignature, WallpaperEngineLibrary.ComputeRootSignature(dir));
    }

    // ---------- 进度与取消 ----------

    [Fact]
    public void ScanWithCache_按根回报进度()
    {
        string a = Path.Combine(_root, "a");
        string b = Path.Combine(_root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        MakeProject(a, "1");
        MakeProject(b, "2");

        var progress = new SyncProgress();
        WallpaperEngineLibrary.ScanWithCache(
            new[]
            {
                new WallpaperLibraryRoot(a, WallpaperLibrarySource.Custom),
                new WallpaperLibraryRoot(b, WallpaperLibrarySource.Custom),
            },
            progress: progress);

        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal((1, 2), (progress.Reports[0].ScannedRoots, progress.Reports[0].TotalRoots));
        Assert.Equal((2, 2), (progress.Reports[1].ScannedRoots, progress.Reports[1].TotalRoots));
        Assert.Equal(progress.Reports[1].EntryCount, progress.Reports[0].EntryCount + 1);
    }

    [Fact]
    public void ScanWithCache_取消令牌_抛出OperationCanceled()
    {
        string dir = Path.Combine(_root, "cancel");
        Directory.CreateDirectory(dir);
        MakeProject(dir, "1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            WallpaperEngineLibrary.ScanWithCache(
                new[] { new WallpaperLibraryRoot(dir, WallpaperLibrarySource.Custom) },
                cancellationToken: cts.Token));
    }

    // ---------- libraryfolders.vdf 解析 ----------

    [Fact]
    public void ParseLibraryFolders_解析多库路径并处理转义()
    {
        string libA = Path.Combine(_root, "libA");
        string libB = Path.Combine(_root, "libB");
        Directory.CreateDirectory(libA);
        Directory.CreateDirectory(libB);

        string vdf = Path.Combine(_root, "libraryfolders.vdf");
        File.WriteAllLines(vdf, new[]
        {
            "\"libraryfolders\"",
            "{",
            "\t\"0\"",
            "\t{",
            $"\t\t\"path\"\t\t\"{libA.Replace("\\", "\\\\")}\"",
            "\t\t\"apps\"",
            "\t\t{",
            "\t\t\t\"431960\"\t\t\"826275581\"",
            "\t\t}",
            "\t}",
            "\t\"1\"",
            "\t{",
            $"\t\t\"path\"\t\t\"{libB.Replace("\\", "\\\\")}\"",
            "\t}",
            "}",
        });

        string[] libraries = WallpaperEngineLibrary.ParseLibraryFolders(vdf).ToArray();

        Assert.Equal(new[] { libA, libB }, libraries);
    }

    [Fact]
    public void ParseLibraryFolders_文件缺失或路径不存在_返回空()
    {
        Assert.Empty(WallpaperEngineLibrary.ParseLibraryFolders(Path.Combine(_root, "no-such.vdf")));

        // 登记的路径不存在：必须被过滤（探测契约「返回即存在」的前置条件）
        string vdf = Path.Combine(_root, "ghost.vdf");
        File.WriteAllText(vdf, "\"path\"\t\t\"Q:\\\\not-exist-steam\\\\\"");
        Assert.Empty(WallpaperEngineLibrary.ParseLibraryFolders(vdf));
    }

    // ---------- 系统主题媒体枚举 ----------

    [Fact]
    public void ScanSingleRoot_系统主题根_顶层与DesktopBackground都被列出()
    {
        // 复刻 Themes 目录的两种固定落点：
        // ① 顶层壁纸文件（TransferredWallpaper-*.jpg）
        // ② 主题包目录下的 DesktopBackground\*.jpg
        // ③ 干扰项：.theme 描述文件与主题包里的非背景目录——都不该出现
        string themes = Path.Combine(_root, "Themes");
        Directory.CreateDirectory(themes);
        File.WriteAllBytes(Path.Combine(themes, "TransferredWallpaper-2024-01-01.jpg"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(themes, "Custom.theme"), "[Theme]");

        string packDesktop = Path.Combine(themes, "{A1B2C3}", "DesktopBackground");
        Directory.CreateDirectory(packDesktop);
        File.WriteAllBytes(Path.Combine(packDesktop, "img1.jpg"), new byte[] { 2 });

        string unrelated = Path.Combine(themes, "{A1B2C3}", "Roamed");
        Directory.CreateDirectory(unrelated);
        File.WriteAllBytes(Path.Combine(unrelated, "noise.png"), new byte[] { 3 });

        var entries = WallpaperEngineLibrary.ScanWithCache(
            new[] { new WallpaperLibraryRoot(themes, WallpaperLibrarySource.SystemTheme) });

        string[] paths = entries.Entries.Select(e => e.Path).ToArray();
        Assert.Contains(Path.Combine(themes, "TransferredWallpaper-2024-01-01.jpg"), paths);
        Assert.Contains(Path.Combine(packDesktop, "img1.jpg"), paths);
        Assert.DoesNotContain(Path.Combine(unrelated, "noise.png"), paths);
        Assert.All(entries.Entries, e => Assert.Equal(WallpaperLibrarySource.SystemTheme, e.Source));
    }

    // ---------- 缓存映射 ----------

    [Fact]
    public void 缓存映射_往返字段不丢()
    {
        var entry = new WallpaperLibraryEntry(
            Path: @"C:\some\project",
            Title: "标题",
            EntryKind: WallpaperLibraryEntryKind.Project,
            PreviewPath: @"C:\some\preview.jpg",
            MightHaveAudio: true,
            IsUsable: false,
            UnusableReason: "缺少 scene.pkg",
            Source: WallpaperLibrarySource.Workshop);

        WallpaperLibraryCacheEntry cached = WallpaperEngineLibrary.ToCacheEntry(entry);
        WallpaperLibraryEntry restored = WallpaperEngineLibrary.FromCacheEntry(cached)!;

        Assert.Equal(entry, restored); // record 相等：全字段一致
    }

    [Fact]
    public void 缓存映射_空Path的脏数据返回null()
    {
        Assert.Null(WallpaperEngineLibrary.FromCacheEntry(new WallpaperLibraryCacheEntry { Path = "" }));
        Assert.Null(WallpaperEngineLibrary.FromCacheEntry(null!));
    }

    // ---------- 旧入口兼容 ----------

    [Fact]
    public void 字符串Scan重载_行为不变_来源按自定义()
    {
        string dir = MakeProject(_root, "legacy", title: "Legacy"); // 项目 + 顶层 clip.mp4
        string media = Path.Combine(_root, "legacy-media");
        Directory.CreateDirectory(media);
        File.WriteAllBytes(Path.Combine(media, "pic.png"), new byte[] { 1 });

        var entries = WallpaperEngineLibrary.Scan(new[] { dir, media, media }, includeLooseMedia: true);

        // dir 产出 2 条（项目 + 它顶层散落的 clip.mp4，v0.3.0 语义保留），
        // media 传两遍去重只算 1 条 → 共 3 条；来源统一按 Custom
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(WallpaperLibrarySource.Custom, e.Source));
        Assert.Contains(entries, e => e.Title == "Legacy" && e.EntryKind == WallpaperLibraryEntryKind.Project);
        Assert.Contains(entries, e => e.Path.EndsWith("clip.mp4"));
        Assert.Contains(entries, e => e.Path.EndsWith("pic.png"));
    }
}
