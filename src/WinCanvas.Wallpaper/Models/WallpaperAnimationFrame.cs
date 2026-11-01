using System;
using Windows.Graphics.Imaging;

namespace WinCanvas.Wallpaper.Models;

/// <summary>
/// 动画壁纸的单帧（WP5，GIF 多帧支持）：解码后的整幅画面 + 该帧应展示的时长。
/// 为 WallpaperSource.ImageFrames 的元素类型——用 record 结构体保持不可变与值语义，
/// 像素本体（<see cref="Frame"/>）为 Bgra8 Premultiplied SoftwareBitmap，随宿主 <see cref="WallpaperSource"/>
/// 的 Dispose 链统一释放（见 WallpaperSource.Dispose 的帧释放路径）。
/// </summary>
/// <param name="Frame">该帧画面（Bgra8 Premultiplied；与宿主 DComp 表面同布局，可直接快照上屏）。</param>
/// <param name="Delay">该帧展示时长（GIF 帧延迟，单位 1/100 秒；缺失或 0 由加载器取默认 100ms）。</param>
public sealed record WallpaperAnimationFrame(SoftwareBitmap Frame, TimeSpan Delay);
