using System;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Playback;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.Wallpaper.Models;

/// <summary>
/// 一份已准备就绪的壁纸资源。三种类型互斥填充：
/// 图片层填 <see cref="ImageFrame"/>，视频层填 <see cref="Player"/>，
/// pkg 层填 <see cref="ExtractedRoot"/> 及其降级后的展示资源。
/// 用毕必须 Dispose，及时释放图像与 GPU/解码器资源。
/// 用 record 以支持各加载器完成基础构造后用 with 表达式补充元数据。
/// </summary>
public sealed record WallpaperSource : IDisposable
{
    private bool _disposed;

    /// <summary>实际加载的资源类型。</summary>
    public WallpaperKind Kind { get; init; }

    /// <summary>
    /// 图片层（第一层）：导入时已缩放到目标显示器分辨率的帧，
    /// 可由 UI 层直接转成 WriteableBitmap / CanvasBitmap 上传 GPU。
    /// 仅 <see cref="WallpaperKind.Image"/> 或 pkg 降级为封面图时非空。
    /// </summary>
    public SoftwareBitmap? ImageFrame { get; init; }

    /// <summary>
    /// 视频层（第二层 / pkg 内嵌视频）：Media Foundation 硬件解码播放器。
    /// App 端的 MediaPlayerElement 可通过 SetMediaPlayer 绑定该实例。
    /// </summary>
    public MediaPlayer? Player { get; init; }

    /// <summary>pkg 解包缓存目录（第三层）。非 pkg 类型为 null。</summary>
    public string? ExtractedRoot { get; init; }

    /// <summary>pkg 内解析出的场景描述文件（scene.json）完整路径；无则为 null。</summary>
    public string? SceneJsonPath { get; init; }

    /// <summary>复杂 SceneScript 场景降级时用于展示的封面图路径；非降级场景为 null。</summary>
    public string? CoverImagePath { get; init; }

    /// <summary>需要上报给用户的明确提示（例如"复杂场景仅显示封面"）；无提示为 null。</summary>
    public string? StatusMessage { get; init; }

    /// <summary>视频资源的磁盘路径（第二层直接文件，或 pkg 内嵌视频解包产物）。</summary>
    public string? VideoPath { get; init; }

    /// <summary>原始资源路径。</summary>
    public string OriginalPath { get; init; } = string.Empty;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 图像资源用毕即释放（非托管备份缓冲随对象释放）
        ImageFrame?.Dispose();

        // 释放 MF 解码器与音频图（VideoPath 只是磁盘文件，交由缓存策略管理，不在此删除）
        Player?.Dispose();
    }
}
