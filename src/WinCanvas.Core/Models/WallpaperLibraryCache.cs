namespace WinCanvas.Core.Models;

/// <summary>
/// 壁纸库扫描结果的【单条】持久化缓存（v0.4.0 新增，随 config.json 落盘）。
///
/// 为什么要把扫描结果缓存进配置：一次真实库扫描要枚举上百个项目目录并逐个读
/// project.json（工坊目录在机械盘上冷启动可到秒级），没有缓存的话每次启动都要重付。
/// 缓存命中时界面先展示上次结果（秒开），后台再做增量刷新兜底。
///
/// 注意字段是 POCO：该模型直接经 System.Text.Json 序列化，禁止加可通知属性 /
/// UI 依赖；枚举（<see cref="WallpaperLibrarySource"/>、条目形态）以 int 存储，
/// 反序列化端对未知数值要能容忍（<c>(WallpaperLibrarySource)99</c> 不抛异常，
/// 由消费方按「未知来源」降级显示）。
/// </summary>
public sealed class WallpaperLibraryCacheEntry
{
    /// <summary>挂接路径：WE 项目目录或媒体文件绝对路径（条目唯一键）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>显示标题（项目取 project.json 的 title，散落文件取文件名）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>条目形态（WinCanvas.Wallpaper 的 WallpaperLibraryEntryKind 枚举值）。</summary>
    public int Kind { get; set; }

    /// <summary>来源分类（<see cref="WallpaperLibrarySource"/> 枚举值）。</summary>
    public int Source { get; set; }

    /// <summary>预览图绝对路径；无预览为 null。</summary>
    public string? PreviewPath { get; set; }

    /// <summary>是否可能发声（视频壁纸 / 项目声明音轨）。</summary>
    public bool MightHaveAudio { get; set; }

    /// <summary>是否可挂接（扫描期已判定，含降级可用的情形）。</summary>
    public bool IsUsable { get; set; }

    /// <summary>不可用原因；可用时为 null。</summary>
    public string? UnusableReason { get; set; }
}

/// <summary>
/// 壁纸库扫描结果的【按根目录】持久化缓存（v0.4.0 新增，随 config.json 落盘）。
///
/// 增量刷新的记账单位：每个扫描根记一份「内容签名 + 上次扫出的条目」。
/// 下次刷新先算签名（顶层目录/文件名 + 修改时间，见 WallpaperEngineLibrary），
/// 签名没变就整根复用缓存条目、不再读任何 project.json——这才是「增量」：
/// 只对真正变过的根重付扫描成本。
/// </summary>
public sealed class WallpaperLibraryRootCache
{
    /// <summary>扫描根绝对路径（缓存唯一键，比较忽略大小写）。</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>扫描根的来源（<see cref="WallpaperLibrarySource"/> 枚举值）。</summary>
    public int Source { get; set; }

    /// <summary>
    /// 根目录内容签名；目录不存在时为哨兵值（WallpaperEngineLibrary.MissingRootSignature）。
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>上次扫描该根得到的条目（可挂接与不可用的都留：界面要能标灰说明）。</summary>
    public List<WallpaperLibraryCacheEntry> Entries { get; set; } = new();

    /// <summary>该根上次扫描完成时间（UTC，供界面显示「上次扫描」）。</summary>
    public DateTimeOffset ScannedAtUtc { get; set; }
}
