using WinCanvas.Wallpaper.Loaders;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// WP4 用例表：<see cref="PkgWallpaperLoader"/> 的纯逻辑部分（方法为 internal，经
/// 被测项目 InternalsVisibleTo 访问——解包主体依赖真实 pkg 字节流，不在本类范围；
/// 这里只钉住路径安全与场景判定的三分支 / 封面优先级，全部用临时目录构造，零 pkg 文件）。
/// </summary>
public sealed class PkgWallpaperLoaderPureLogicTests
{
    /// <summary>各测试独立的临时根目录（模拟 pkg 解包缓存目录）。</summary>
    private readonly string _root = Directory.CreateTempSubdirectory("WinCanvasPkgTests").FullName;

    private string MakeDir(params string[] relativeSegments)
    {
        var dir = _root;
        foreach (var segment in relativeSegments)
            dir = Path.Combine(dir, segment);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteFile(string relativePath, string content = "x")
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    // ---------------- SafeJoin：拒绝绝对路径 / ".." 穿越，接受正常相对路径 ----------------

    [Theory]
    [InlineData("C:\\Windows\\System32\\evil.png")]   // 盘符绝对路径
    [InlineData("C:/Windows/System32/evil.png")]      // 正斜杠盘符绝对路径
    public void SafeJoin_盘符绝对路径_拒绝(string entryPath)
    {
        Assert.Null(PkgWallpaperLoader.SafeJoin(_root, entryPath));
    }

    [Fact]
    public void SafeJoin_UNC式双斜杠路径_按相对路径收编进根目录()
    {
        // 实现口径（钉住现状，非缺陷）：Normalize 把开头的 \\ 或 // TrimStart 掉，
        // UNC 式条目被当作普通相对段收编到根目录内——结果仍安全（不会逃出根目录），
        // 但语义是「收编」而非「拒绝」。
        var joined = PkgWallpaperLoader.SafeJoin(_root, "\\\\evil\\share\\payload.png");

        Assert.NotNull(joined);
        Assert.StartsWith(_root, joined, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..\\evil.png")]          // 直接上级穿越
    [InlineData("materials\\..\\..\\evil.png")] // 混合正常段与穿越
    [InlineData("a/b/../../../evil.png")] // 多级穿越
    public void SafeJoin_目录穿越_拒绝(string entryPath)
    {
        Assert.Null(PkgWallpaperLoader.SafeJoin(_root, entryPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SafeJoin_空白路径_拒绝(string entryPath)
    {
        Assert.Null(PkgWallpaperLoader.SafeJoin(_root, entryPath));
    }

    [Fact]
    public void SafeJoin_正常相对路径_拼接结果落在根目录内()
    {
        var joined = PkgWallpaperLoader.SafeJoin(_root, "materials\\scene\\texture.png");

        Assert.NotNull(joined);
        // 拼接结果必须是根下的完整路径（分隔符统一为系统格式）
        Assert.StartsWith(_root, joined, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.Combine(_root, "materials", "scene", "texture.png"),
            joined,
            ignoreCase: true,
            ignoreLineEndingDifferences: false,
            ignoreWhiteSpaceDifferences: false,
            ignoreAllWhiteSpace: false);
    }

    [Fact]
    public void SafeJoin_正斜杠相对路径_同样落在根目录内()
    {
        var joined = PkgWallpaperLoader.SafeJoin(_root, "sub/image.png");

        Assert.NotNull(joined);
        Assert.StartsWith(_root, joined, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("sub", "image.png"), joined, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- DetectComplexScene：.lua / scene.json "script" 字段 / 正常包 三分支 ----------------

    [Fact]
    public void DetectComplexScene_包内存在lua脚本_判复杂()
    {
        WriteFile("scripts/user.lua", "-- wallpaper engine user script");

        Assert.True(PkgWallpaperLoader.DetectComplexScene(_root, sceneJsonPath: null));
    }

    [Fact]
    public void DetectComplexScene_sceneJson含script字段_判复杂()
    {
        var sceneJson = WriteFile("scene.json", """{"objects":[{"image":"a.png","script":"rotate.lua"}]}""");

        Assert.True(PkgWallpaperLoader.DetectComplexScene(_root, sceneJson));
    }

    [Fact]
    public void DetectComplexScene_sceneJson含scripts复数字段_同样判复杂()
    {
        var sceneJson = WriteFile("scene.json", """{"scripts":["s1.lua"]}""");

        Assert.True(PkgWallpaperLoader.DetectComplexScene(_root, sceneJson));
    }

    [Fact]
    public void DetectComplexScene_正常包无脚本_判简单()
    {
        // 正常包形态：一张 png + 不含 script 字段的 scene.json + 无 .lua
        WriteFile("images/bg.png", "fake-png");
        var sceneJson = WriteFile("scene.json", """{"objects":[{"image":"images/bg.png"}]}""");

        Assert.False(PkgWallpaperLoader.DetectComplexScene(_root, sceneJson));
    }

    [Fact]
    public void DetectComplexScene_无sceneJson且无lua_判简单()
    {
        // 纯纹理包（连 scene.json 都没有）：不判复杂（走视频/图片提取路径）
        WriteFile("textures/tex.png", "fake-png");

        Assert.False(PkgWallpaperLoader.DetectComplexScene(_root, sceneJsonPath: null));
    }

    [Fact]
    public void DetectComplexScene_sceneJson读取失败_不抛异常判简单()
    {
        // 防御分支：scene.json 路径指向不存在文件（竞态删除等）→ 吞掉 IO 异常判简单，
        // 不让单个损坏条目炸掉整个解包流程
        Assert.False(PkgWallpaperLoader.DetectComplexScene(_root, Path.Combine(_root, "missing.json")));
    }

    // ---------------- FindCoverImage：preview.jpg > preview.png > 任意 PNG ----------------

    [Fact]
    public void FindCoverImage_同时存在jpg与png_优先previewJpg()
    {
        WriteFile("preview.png", "png");
        WriteFile("preview.jpg", "jpg");

        Assert.Equal(Path.Combine(_root, "preview.jpg"), PkgWallpaperLoader.FindCoverImage(_root));
    }

    [Fact]
    public void FindCoverImage_仅有previewPng_取previewPng()
    {
        WriteFile("preview.png", "png");
        WriteFile("assets\\other.png", "png"); // 存在其他 png 也不动摇 preview.png 的优先级

        Assert.Equal(Path.Combine(_root, "preview.png"), PkgWallpaperLoader.FindCoverImage(_root));
    }

    [Fact]
    public void FindCoverImage_无preview命名_回退任意PNG()
    {
        WriteFile("textures\\cover.png", "png");

        Assert.Equal(Path.Combine(_root, "textures", "cover.png"), PkgWallpaperLoader.FindCoverImage(_root));
    }

    [Fact]
    public void FindCoverImage_无任何封面_返回null()
    {
        WriteFile("audio.mp3", "mp3");

        Assert.Null(PkgWallpaperLoader.FindCoverImage(_root));
    }
}
