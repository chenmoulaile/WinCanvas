using System.IO;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Loaders;
using WinCanvas.Wallpaper.Models;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// Wallpaper Engine 项目兼容（project.json 解析、类型分派、可用性判定）的用例（v0.3.0）。
///
/// 用【合成临时目录】而不是真实库路径：真实库在开发者个人盘上、内容会变，
/// 拿它当测试基线会让 CI 今天绿明天红。这里刻意复刻在真实库里实测到的形态：
/// type 大小写混用、file 含中文与方括号、scene 的 file 指向 pkg 内部的 scene.json、
/// web 项目只有预览图可降级、file 为空串。
/// </summary>
public sealed class WallpaperProjectCompatTests : IDisposable
{
    private readonly string _root;

    public WallpaperProjectCompatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinCanvasWeTests", Guid.NewGuid().ToString("N"));
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

    /// <summary>造一个 WE 项目目录。</summary>
    private string MakeProject(string name, string projectJson, params string[] files)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), projectJson);
        foreach (string file in files)
        {
            string target = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, new byte[] { 1, 2, 3, 4 });
        }

        return dir;
    }

    // ---------- type 大小写归一化 ----------

    [Theory]
    [InlineData("scene", WallpaperProjectKind.Scene)]
    [InlineData("Scene", WallpaperProjectKind.Scene)]   // 实测同一库里 8 个项目是大写 S
    [InlineData("video", WallpaperProjectKind.Video)]
    [InlineData("Video", WallpaperProjectKind.Video)]
    [InlineData("web", WallpaperProjectKind.Web)]
    [InlineData("Web", WallpaperProjectKind.Web)]
    [InlineData("audiofilter", WallpaperProjectKind.AudioFilter)]
    [InlineData("", WallpaperProjectKind.Unknown)]
    [InlineData("nonsense", WallpaperProjectKind.Unknown)]
    public void ParseKind_大小写与未知值都归一化(string type, WallpaperProjectKind expected)
    {
        Assert.Equal(expected, WallpaperProjectReader.ParseKind(type));
    }

    // ---------- 描述符解析 ----------

    [Fact]
    public void Read_Scene项目_识别类型并保留预览图()
    {
        string dir = MakeProject("2386275267", """
        {
            "title" : "Blue Archive",
            "type" : "scene",
            "file" : "scene.json",
            "preview" : "preview.gif",
            "workshopid" : "2386275267",
            "contentrating" : "Everyone"
        }
        """, "preview.gif");

        var d = WallpaperProjectReader.Read(dir);

        Assert.NotNull(d);
        Assert.Equal(WallpaperProjectKind.Scene, d!.Kind);
        Assert.Equal("Blue Archive", d.Title);
        Assert.Equal("2386275267", d.WorkshopId);
        // file 指向的 scene.json 在 pkg 内部、磁盘上不存在 —— 必须解析为 null 而不是硬凑一个路径
        Assert.Null(d.MainFilePath);
        Assert.NotNull(d.PreviewPath);
    }

    [Fact]
    public void Read_视频项目_file含中文与方括号也能解析()
    {
        // 实测真实库里的文件名形态，例如：
        //   "file" : "发送.mp4"
        //   "file" : "[SHANA]CH0334_Idle-4x-RIFE-RIFE3.1-120fps_waifu2x_2x_mp4.mp4"
        string name = "[SHANA]CH0334_Idle-4x-中文_waifu2x_2x_mp4.mp4";
        string dir = MakeProject("3650786658", $$"""
        {
            "title" : "天童爱丽丝",
            "type" : "video",
            "file" : "{{name}}",
            "preview" : "preview.jpg"
        }
        """, name, "preview.jpg");

        var d = WallpaperProjectReader.Read(dir);

        Assert.NotNull(d);
        Assert.Equal(WallpaperProjectKind.Video, d!.Kind);
        Assert.NotNull(d.MainFilePath);
        Assert.Equal(name, Path.GetFileName(d.MainFilePath));
        // 视频一律标为「可能发声」：这是声音开关需要作用的对象
        Assert.True(d.MightHaveAudio);
    }

    [Fact]
    public void Read_file穿越项目目录_被拒绝()
    {
        // project.json 来自创意工坊，属第三方内容：
        // "file": "../../../Windows/System32/notepad.exe" 不能被当成素材路径交给播放器
        string dir = MakeProject("evil", """
        {
            "title" : "evil",
            "type" : "video",
            "file" : "../../../Windows/System32/notepad.exe"
        }
        """);

        var d = WallpaperProjectReader.Read(dir);

        Assert.NotNull(d);
        Assert.Null(d!.MainFilePath);
    }

    [Fact]
    public void Read_音频属性按关键词命中_不依赖具体字段名()
    {
        // 实测各作者给音量项起的名字互不相同，无法枚举，只能关键词匹配
        string dir = MakeProject("withaudio", """
        {
            "title" : "audio scene",
            "type" : "scene",
            "file" : "scene.json",
            "general" : {
                "supportsaudioprocessing" : true,
                "properties" : {
                    "schemecolor" : { "type" : "color", "value" : "0 0 0" },
                    "musicvolume" : { "type" : "slider", "value" : 0.5 },
                    "oceansfxvolume" : { "type" : "slider", "value" : 0.4 },
                    "aronavoicelinevolume" : { "type" : "slider", "value" : 0.7 }
                }
            }
        }
        """);

        var d = WallpaperProjectReader.Read(dir);

        Assert.NotNull(d);
        Assert.True(d!.MightHaveAudio);
        Assert.Contains("musicvolume", d.AudioPropertyNames);
        Assert.Contains("oceansfxvolume", d.AudioPropertyNames);
        Assert.Contains("aronavoicelinevolume", d.AudioPropertyNames);
        // 非音频属性不得混进来：schemecolor 是配色
        Assert.DoesNotContain("schemecolor", d.AudioPropertyNames);
    }

    [Fact]
    public void Read_纯静态场景_不标记为可发声()
    {
        string dir = MakeProject("silent", """
        {
            "title" : "silent",
            "type" : "scene",
            "file" : "scene.json",
            "general" : { "properties" : { "schemecolor" : { "type" : "color", "value" : "0 0 0" } } }
        }
        """);

        var d = WallpaperProjectReader.Read(dir);

        Assert.NotNull(d);
        Assert.False(d!.MightHaveAudio);
        Assert.Empty(d.AudioPropertyNames);
    }

    [Fact]
    public void Read_非法JSON_返回错误描述而不抛()
    {
        // 扫描器要能容忍个别损坏项目：一个坏 project.json 不能让整轮检索中断
        string dir = MakeProject("broken", "{ 这不是合法的 JSON ");

        var d = WallpaperProjectReader.Read(dir);

        Assert.NotNull(d);
        Assert.NotNull(d!.ParseError);
        Assert.Equal(WallpaperProjectKind.Unknown, d.Kind);
    }

    [Fact]
    public void Read_目录不存在_返回null()
    {
        Assert.Null(WallpaperProjectReader.Read(Path.Combine(_root, "nonexistent")));
    }

    // ---------- 加载器分派 ----------

    [Fact]
    public void Load_Scene无pkg但有预览_降级而不是抛()
    {
        string dir = MakeProject("nopkg", """
        {
            "title" : "no pkg",
            "type" : "scene",
            "file" : "scene.json",
            "preview" : "preview.jpg"
        }
        """, "preview.jpg");

        // 预览图必须是真能解码的图片，否则 ImageWallpaperLoader 会抛 —— 这里用最小合法 PNG
        WriteMinimalPng(Path.Combine(dir, "preview.jpg"));

        var source = WallpaperProjectLoader.Load(dir);

        Assert.NotEqual(WallpaperProjectKind.Unknown, WallpaperProjectReader.Read(dir)!.Kind);
        Assert.NotNull(source.ImageFrame);
        Assert.Equal(Path.GetFullPath(dir), source.OriginalPath);
        Assert.Contains("降级", source.StatusMessage);
        source.Dispose();
    }

    [Fact]
    public void Load_Scene无pkg且无预览_抛人话异常()
    {
        string dir = MakeProject("bare", """
        { "title" : "bare", "type" : "scene", "file" : "scene.json" }
        """);

        var ex = Assert.Throws<NotSupportedException>(() => WallpaperProjectLoader.Load(dir));
        Assert.Contains("scene.pkg", ex.Message);
    }

    [Fact]
    public void Load_音频项目声明为video但素材丢失_抛人话异常()
    {
        string dir = MakeProject("missingmedia", """
        { "title" : "missing", "type" : "video", "file" : "不存在.mp4" }
        """);

        var ex = Assert.Throws<NotSupportedException>(() => WallpaperProjectLoader.Load(dir));
        Assert.Contains("找不到可播放的视频", ex.Message);
    }

    [Fact]
    public void Load_网页项目_抛出不支持并说明原因()
    {
        string dir = MakeProject("webproj", """
        { "title" : "l2d web", "type" : "web", "file" : "index.html" }
        """, "index.html");

        var ex = Assert.Throws<NotSupportedException>(() => WallpaperProjectLoader.Load(dir));
        Assert.Contains("网页壁纸", ex.Message);
    }

    [Fact]
    public void Load_音频响应壁纸_明确拒绝而不是静默空白()
    {
        string dir = MakeProject("audiofilter", """
        { "title" : "af", "type" : "audiofilter", "file" : "scene.json" }
        """);

        var ex = Assert.Throws<NotSupportedException>(() => WallpaperProjectLoader.Load(dir));
        Assert.Contains("audiofilter", ex.Message);
    }

    [Fact]
    public void IsProjectDirectory_只认含projectJson的目录()
    {
        string project = MakeProject("real", """{ "title" : "r", "type" : "video" }""");
        string plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.True(WallpaperProjectLoader.IsProjectDirectory(project));
        Assert.False(WallpaperProjectLoader.IsProjectDirectory(plain));
        Assert.False(WallpaperProjectLoader.IsProjectDirectory(Path.Combine(_root, "ghost")));
    }

    // ---------- 检索器：扫描判定与加载器行为必须一致 ----------

    [Fact]
    public void Scan_混合库_分类正确且可用性与加载器一致()
    {
        MakeProject("scene_ok", """{ "title" : "S", "type" : "scene", "file" : "scene.json" }""", "scene.pkg");
        MakeProject("scene_broken", """{ "title" : "SB", "type" : "scene", "file" : "scene.json" }""");
        MakeProject("web_broken", """{ "title" : "WB", "type" : "web", "file" : "index.html" }""", "index.html");

        var entries = WallpaperEngineLibrary.Scan(new[] { _root }, includeLooseMedia: false);

        var sceneOk = Assert.Single(entries, e => e.Title == "S");
        Assert.True(sceneOk.IsUsable);

        // 无 pkg 且无预览的场景项目必须被判不可用 —— 且加载器也确实会抛
        var sceneBroken = Assert.Single(entries, e => e.Title == "SB");
        Assert.False(sceneBroken.IsUsable);
        Assert.Throws<NotSupportedException>(() => WallpaperProjectLoader.Load(sceneBroken.Path));

        var webBroken = Assert.Single(entries, e => e.Title == "WB");
        Assert.False(webBroken.IsUsable);
    }

    [Fact]
    public void Scan_素材子目录不被当成项目()
    {
        // 真实项目里有 shaders/ materials/ sound/ 等子目录，它们没有 project.json，
        // 若被当成条目，用户会在列表里看到一堆无意义的重复项
        string project = MakeProject("proj", """{ "title" : "P", "type" : "scene", "file" : "scene.json" }""", "scene.pkg");
        Directory.CreateDirectory(Path.Combine(project, "shaders"));
        Directory.CreateDirectory(Path.Combine(project, "materials"));

        var entries = WallpaperEngineLibrary.Scan(new[] { project }, includeLooseMedia: false);

        // project 自身被判为一个项目目录 → 1 条；其子目录不产生条目
        Assert.Single(entries);
        Assert.Equal(WallpaperLibraryEntryKind.Project, entries[0].EntryKind);
    }

    [Fact]
    public void Scan_去重_同一路径不重复出现()
    {
        MakeProject("dup", """{ "title" : "D", "type" : "scene", "file" : "scene.json" }""", "scene.pkg");

        // 同一个根传两次（用户配置 + 自动探测重叠是常见情形）
        var entries = WallpaperEngineLibrary.Scan(new[] { _root, _root, Path.GetFullPath(_root) },
            includeLooseMedia: false);

        Assert.Single(entries);
    }

    [Fact]
    public void Scan_不存在的根_被跳过而不抛()
    {
        var entries = WallpaperEngineLibrary.Scan(new[] { Path.Combine(_root, "ghost"), _root },
            includeLooseMedia: false);

        Assert.Empty(entries);
    }

    /// <summary>写一个 1x1 的最小合法 PNG（够 ImageWallpaperLoader 解码）。</summary>
    private static void WriteMinimalPng(string path)
    {
        // PNG: signature + IHDR(1x1, 8bit RGBA) + IDAT + IEND，zlib 存储块为固定 0 头
        byte[] png =
        {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,
            0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52, 0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,
            0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,0x89,
            0x00,0x00,0x00,0x0A,0x49,0x44,0x41,0x54, 0x78,0x9C,0x63,0x00,0x01,0x00,0x00,0x05,0x00,0x01,
            0x0D,0x0A,0x2D,0xB4,
            0x00,0x00,0x00,0x00,0x49,0x45,0x4E,0x44, 0xAE,0x42,0x60,0x82,
        };
        File.WriteAllBytes(path, png);
    }
}
