using System.Text.Json;
using WinCanvas.Core.Models;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 壁纸库缓存模型（v0.4.0 新增，随 config.json 落盘）的序列化往返用例。
/// 缓存破损 / 字段缺失不得让启动链失败：反序列化端靠「空集合 / 默认值」兜底，
/// 这里验证默认形状与字段保真。
/// </summary>
public sealed class WallpaperLibraryCacheModelTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void AppConfig_含壁纸库缓存_序列化往返字段不丢()
    {
        var config = new AppConfig();
        config.WallpaperLibraryRoots.Add(@"D:\Steam\steamapps\workshop\content\431960");
        config.WallpaperLibraryCache.Add(new WallpaperLibraryRootCache
        {
            Root = @"D:\Steam\steamapps\workshop\content\431960",
            Source = (int)WallpaperLibrarySource.Workshop,
            Signature = "ABC123",
            ScannedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Entries =
            {
                new WallpaperLibraryCacheEntry
                {
                    Path = @"D:\Steam\steamapps\workshop\content\431960\1375313043",
                    Title = "标题",
                    Kind = 0, // WallpaperLibraryEntryKind.Project
                    Source = (int)WallpaperLibrarySource.Workshop,
                    PreviewPath = @"D:\...\preview.jpg",
                    MightHaveAudio = true,
                    IsUsable = false,
                    UnusableReason = "缺少 scene.pkg（项目未导出）",
                },
                new WallpaperLibraryCacheEntry
                {
                    Path = @"C:\Users\chenk\Pictures\shot.png",
                    Title = "shot",
                    Kind = 1, // MediaFile
                    Source = (int)WallpaperLibrarySource.Pictures,
                    IsUsable = true,
                },
            },
        });

        string json = JsonSerializer.Serialize(config, Options);
        AppConfig? roundTrip = JsonSerializer.Deserialize<AppConfig>(json, Options);

        Assert.NotNull(roundTrip);
        WallpaperLibraryRootCache root = Assert.Single(roundTrip!.WallpaperLibraryCache);
        Assert.Equal(@"D:\Steam\steamapps\workshop\content\431960", root.Root);
        Assert.Equal((int)WallpaperLibrarySource.Workshop, root.Source);
        Assert.Equal("ABC123", root.Signature);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), root.ScannedAtUtc);
        Assert.Equal(2, root.Entries.Count);

        WallpaperLibraryCacheEntry project = root.Entries[0];
        Assert.Equal(@"D:\Steam\steamapps\workshop\content\431960\1375313043", project.Path);
        Assert.Equal("标题", project.Title);
        Assert.Equal(0, project.Kind);
        Assert.True(project.MightHaveAudio);
        Assert.False(project.IsUsable);
        Assert.Equal("缺少 scene.pkg（项目未导出）", project.UnusableReason);

        WallpaperLibraryCacheEntry media = root.Entries[1];
        Assert.Equal((int)WallpaperLibrarySource.Pictures, media.Source);
        Assert.Null(media.PreviewPath); // WhenWritingNull：空值不落盘，读回为 null
        Assert.Null(media.UnusableReason);
        Assert.True(media.IsUsable);
    }

    [Fact]
    public void AppConfig_无壁纸库缓存字段_反序列化为空集合()
    {
        // 旧版本 config.json（v0.3.x）没有 WallpaperLibraryCache 字段：
        // 必须反序列化成空集合而不是 null（与 Windows / Hotkeys 的向后兼容语义一致）
        const string legacyJson = """
        {
          "Version": 2,
          "Windows": [],
          "WallpaperLibraryRoots": []
        }
        """;

        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(legacyJson, Options);

        Assert.NotNull(config);
        Assert.Empty(config!.WallpaperLibraryCache);
        Assert.Empty(config.WallpaperLibraryRoots);
    }

    [Fact]
    public void 缓存条目_未知来源数值_反序列化不抛异常()
    {
        // 手改 / 半截 config 的容忍契约：未知枚举数值按 int 透传，消费方降级显示
        const string json = """
        {
          "Root": "C:\\x",
          "Source": 99,
          "Signature": "",
          "Entries": [ { "Path": "C:\\x\\p", "Title": "t", "Kind": 7, "Source": 98 } ]
        }
        """;

        WallpaperLibraryRootCache? cache = JsonSerializer.Deserialize<WallpaperLibraryRootCache>(json, Options);

        Assert.NotNull(cache);
        Assert.Equal((WallpaperLibrarySource)99, (WallpaperLibrarySource)cache!.Source);
        Assert.Equal(7, cache.Entries[0].Kind);
    }
}
