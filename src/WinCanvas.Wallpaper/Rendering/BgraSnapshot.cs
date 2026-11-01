using System;
using System.Runtime.InteropServices.WindowsRuntime; // WindowsRuntimeBufferExtensions.ToArray（CsWinRT 提供的 IBuffer 扩展）
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinCanvas.Wallpaper.Rendering;

/// <summary>
/// 一份已从 SoftwareBitmap 同步复制出来的像素快照（PBgra8，行紧密排列无对齐填充）。
/// 与 DXGI_FORMAT_B8G8R8A8_UNORM + PREMULTIPLIED 的 DComp surface 逐字节同布局，
/// 宿主渲染层可直接按行拷入 surface；也供 UI 层转 WriteableBitmap（PBgra8）显示。
/// 由「预览与宿主共用同一份拷贝逻辑」的约束产生（原 WallpaperPreviewController.SnapshotBitmap 下沉至此）。
/// </summary>
/// <param name="Width">像素宽。</param>
/// <param name="Height">像素高。</param>
/// <param name="Pixels">PBgra8 像素数据（每像素 4 字节：B/G/R/A，行紧密排列，行宽 = Width * 4）。</param>
internal sealed record BgraSnapshot(int Width, int Height, byte[] Pixels);

/// <summary>
/// SoftwareBitmap → <see cref="BgraSnapshot"/> 的共享拷贝工具（WP1-B 从
/// WallpaperPreviewController.SnapshotBitmap 下沉而来，预览与宿主渲染必须共用本实现，禁止各抄一份）。
/// 线程约定：在调用线程同步执行（预览在渲染线程的会话锁内、宿主在 UpdateCoreAsync 的线程池线程），
/// 绝不进 UI 线程（全局硬约束：像素拷贝不得占 UI 线程）。
/// 实现说明：经 <see cref="SoftwareBitmap.CopyToBuffer"/> 把平面原始字节（含行距填充）复制进自建
/// IBuffer 后按平面描述剥离行距——全部是纯 WinRT 投影 API，不含 COM 强转。
/// （WP1-B 实测：早期采用的 (IMemoryBufferByteAccess)(object)reference 强转只在 WinUI 进程上下文
/// 可行，在控制台宿主（诊断命令）内抛 InvalidCastException——下沉为共享实现后必须两种上下文都成立，
/// 故改为纯投影路径，决策记录在 WORKLOG。）
/// </summary>
internal static class BitmapSnapshot
{
    /// <summary>
    /// 把 Bgra8 + Premultiplied 的 SoftwareBitmap 像素同步复制成紧密排列的 PBgra8 快照。
    /// 行距（stride）取自平面描述：底层缓冲可能带行对齐填充，不能假定紧密排列；
    /// 紧密排列的常见情形走整块内存拷贝快路径，带填充时按行剥离。
    /// </summary>
    /// <param name="frame">源位图（Bgra8 + Premultiplied；与 PBgra8 逐字节一致）。</param>
    /// <returns>紧密排列的像素快照。</returns>
    /// <exception cref="NotSupportedException">像素格式不是 Bgra8。</exception>
    public static BgraSnapshot CopyFrom(SoftwareBitmap frame)
    {
        if (frame.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new NotSupportedException($"像素快照仅支持 Bgra8 格式（Premultiplied 与 PBgra8 逐字节一致），实际：{frame.BitmapPixelFormat}。");
        }

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        var pixels = new byte[width * height * 4];

        // 平面描述给出缓冲布局（StartIndex = 平面起始偏移，Stride = 含填充的行距）
        using var buffer = frame.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);

        // 把平面 0 的原始字节复制进自建 IBuffer（容量覆盖 StartIndex + Stride * Height）
        var rawLength = (uint) (plane.StartIndex + (long) plane.Stride * height);
        var source = new Windows.Storage.Streams.Buffer(rawLength);
        frame.CopyToBuffer(source);
        var raw = source.ToArray(); // 纯托管字节出参，之后不再触碰 WinRT 对象

        if (plane.StartIndex == 0 && plane.Stride == width * 4)
        {
            // 快路径：源本身紧密排列，整块一次拷贝
            System.Buffer.BlockCopy(raw, 0, pixels, 0, pixels.Length);
        }
        else
        {
            // 慢路径：逐行剥离行距填充（每行有效字节 = width * 4）
            for (var y = 0; y < height; y++)
            {
                System.Buffer.BlockCopy(raw, plane.StartIndex + y * plane.Stride, pixels, y * width * 4, width * 4);
            }
        }

        return new BgraSnapshot(width, height, pixels);
    }
}
