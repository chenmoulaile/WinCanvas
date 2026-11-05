using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 壁纸库轮换的 round-robin 选条目纯函数用例（<see cref="WallpaperRotationRules.PickNextRotationPath"/>）。
/// 钉住的语义（验收点）：只从可用条目挑选（调用方已过滤）；一轮内不重复；
/// 轮完从头再来；上一张被删除 / 列表重排后游标不悬空。
/// </summary>
public sealed class WallpaperRotationRulesTests
{
    /// <summary>构造以 OrdinalIgnoreCase 比较的「本轮已播」集合（与执行路径同一口径）。</summary>
    private static HashSet<string> Played(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void 空候选列表_返回null()
    {
        Assert.Null(WallpaperRotationRules.PickNextRotationPath([], Played(), null));
    }

    [Fact]
    public void 首次轮换_从第一条开始()
    {
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" };

        Assert.Equal("a.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played(), null));
    }

    [Fact]
    public void 上一张在中间_选下一张()
    {
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" };

        Assert.Equal("b.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played(), "a.jpg"));
        Assert.Equal("c.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played("a", "b"), "b.jpg"));
    }

    [Fact]
    public void 上一张在末尾_回绕到第一条()
    {
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" };

        Assert.Equal("a.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played("b", "c"), "c.jpg"));
    }

    [Fact]
    public void 一轮内不重复_已播过的跳过()
    {
        // 轮换游标被外部重置（如热键与定时器交替触发）时，
        // 已播过的条目也不得重复出现——跳过直到未播条目
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" };

        // 游标回绕到 a.jpg，但 a.jpg 本轮已播 → 顺延到 b.jpg
        Assert.Equal("b.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played("a.jpg"), "c.jpg"));
    }

    [Fact]
    public void 全部已播_返回null表示一轮播完_调用方清空后从头再来()
    {
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" };
        var played = Played("a.jpg", "b.jpg", "c.jpg");

        Assert.Null(WallpaperRotationRules.PickNextRotationPath(paths, played, "c.jpg"));

        // 执行路径的「轮完从头再来」：清空本轮集合再调，回到上一张的下一条（wrap → a.jpg）
        played.Clear();
        Assert.Equal("a.jpg", WallpaperRotationRules.PickNextRotationPath(paths, played, "c.jpg"));
    }

    [Fact]
    public void 上一张不在候选列表_从头开始不悬空()
    {
        // 重扫 / 删除后 lastPlayedPath 消失：游标归零而不是越界或死循环
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" };

        Assert.Equal("a.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played(), "removed.jpg"));
    }

    [Fact]
    public void 候选被删除收窄_仍按剩余列表正确轮转()
    {
        // 一轮中途库被重扫收窄：在剩余条目上继续 round-robin
        var narrowed = new[] { "a.jpg", "c.jpg" };

        Assert.Equal("c.jpg", WallpaperRotationRules.PickNextRotationPath(narrowed, Played("a.jpg"), "a.jpg"));
    }

    [Fact]
    public void 路径大小写不同_视为同一条目()
    {
        // Windows 路径大小写不敏感：执行路径与测试都用 OrdinalIgnoreCase 集合，
        // 这里钉住「重扫后大小写变化的同一路径不会导致重复挂接」
        var paths = new[] { @"C:\Wall\A.jpg", "b.jpg" };

        Assert.Equal("b.jpg", WallpaperRotationRules.PickNextRotationPath(paths, Played(@"c:\wall\a.jpg"), @"c:\wall\a.jpg"));
    }
}
