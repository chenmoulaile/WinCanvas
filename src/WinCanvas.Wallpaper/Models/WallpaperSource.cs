using System;
using System.Collections.Generic;
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
    /// 动画（GIF 多帧，WP5）时为首帧（与 <see cref="ImageFrames"/>[0].Frame 同一实例），
    /// 保证「ImageFrame 非空即可渲染首帧」的既有判定对动画同样成立。
    /// </summary>
    public SoftwareBitmap? ImageFrame { get; init; }

    /// <summary>
    /// 动画帧集合（WP5 新增，GIF 多帧支持）：非空表示多帧动画——
    /// 每帧含画面与展示时长，渲染侧按帧延迟推进（壁纸会话在渲染线程驱动，暂停态自然定格）。
    /// null = 单帧静态（jpg / png / webp / 单帧 GIF / pkg 封面）。
    /// 首帧与 <see cref="ImageFrame"/> 同一实例（见其注释）；Dispose 由本类统一释放。
    /// </summary>
    public IReadOnlyList<WallpaperAnimationFrame>? ImageFrames { get; init; }

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

    /// <summary>
    /// 本资源是否【可能发声】（v0.3.0 新增，供界面提示与声音开关判断）。
    /// 视频层一律 true（视频素材普遍自带音轨）；pkg / 项目加载器按
    /// project.json 的音频线索（supportsaudioprocessing、音量类属性、sound/ 目录）置位。
    /// 与 <see cref="WinCanvas.Wallpaper.WallpaperService.AudioEnabled"/> 一起决定实际是否出声。
    /// </summary>
    public bool HasAudioTrack { get; init; }

    /// <summary>视频资源的磁盘路径（第二层直接文件，或 pkg 内嵌视频解包产物）。</summary>
    public string? VideoPath { get; init; }

    /// <summary>
    /// 本资源是否携带需要组合呈现的视频播放器。
    ///
    /// 为什么要有这个属性（v0.2.0）：服务层原先用 <c>Kind == WallpaperKind.Video</c>
    /// 判断「有没有视频要上屏」，而 pkg 内嵌视频场景的 Kind 是 <c>Pkg</c>
    /// （PkgWallpaperLoader 必须保持 Pkg：它驱动 UI 的背景类型标签与 pkg 元数据语义），
    /// 于是那条判定对 pkg 视频永假 → BeginVideo / Play 从不执行，目标窗口里只剩纯色占位。
    /// Kind 回答的是「资源从哪来」，播放器回答的是「要不要走视频呈现」——
    /// 两者不是一回事，这里把后者显式表达出来，避免再被混用。
    /// </summary>
    public bool HasVideoPlayer => Player is not null;

    /// <summary>原始资源路径。</summary>
    public string OriginalPath { get; init; } = string.Empty;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 图像资源用毕即释放（非托管备份缓冲随对象释放）。
        // 动画帧（WP5）：首帧与 ImageFrame 是同一实例，只随 ImageFrames 释放一遍，避免双重 Dispose；
        // SoftwareBitmap.Dispose 重复调用虽多数情况无害，但契约上不保证，这里显式区分两条释放路径。
        if (ImageFrames is { } frames)
        {
            foreach (var animationFrame in frames)
                animationFrame.Frame.Dispose();
        }
        else
        {
            ImageFrame?.Dispose();
        }

        // 释放 MF 解码器与音频图（VideoPath 只是磁盘文件，交由缓存策略管理，不在此删除）
        Player?.Dispose();
    }
}
