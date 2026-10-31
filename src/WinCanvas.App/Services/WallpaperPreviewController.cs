using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime; // IBuffer.AsStream（CsWinRT 提供的 WinRT 互操作扩展）
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using WinCanvas.Wallpaper;

namespace WinCanvas.App.Services;

/// <summary>
/// 一份已从 SoftwareBitmap 同步复制出来的像素快照（PBgra8，行紧密排列无对齐填充）。
/// 复制发生在渲染线程的会话锁内，之后 UI 线程可以安全使用，不再受源资源释放影响。
/// </summary>
/// <param name="Width">像素宽。</param>
/// <param name="Height">像素高。</param>
/// <param name="Pixels">PBgra8 像素数据（每像素 4 字节：B/G/R/A，行紧密排列）。</param>
internal sealed record BgraSnapshot(int Width, int Height, byte[] Pixels);

/// <summary>
/// WinRT IMemoryBuffer 的官方 COM 访问接口：直接拿到底层内存指针，
/// 用于在不经编码器的情况下读取 SoftwareBitmap 的像素数据。
/// GUID 固定为 5B0D3235-4DBA-4D44-865E-8F1D0E4FD0AD（WinRT ABI 规定）。
/// </summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD0AD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMemoryBufferByteAccess
{
    /// <summary>获取底层缓冲区：buffer 为内存指针，capacity 为字节容量（指针仅在持有引用期间有效）。</summary>
    unsafe void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>
/// 壁纸画面预览控制器：把 <see cref="WallpaperService"/> 的输出接到主窗口的预览承载区，
/// 是 ImageFrameReady / VideoPlayerReady 的 App 层订阅者（此前全仓库无订阅者、画面上屏未接线）。
/// - 图片（含 pkg 降级封面）：订阅 <see cref="WallpaperService.ImageFrameReady"/>，
///   在回调线程（渲染线程）内同步把 SoftwareBitmap 像素复制成 <see cref="BgraSnapshot"/>
///  （订阅调用发生在会话内部锁中、先于 Source.Dispose，规避在途帧与资源释放的竞争），
///   再封送到 DispatcherQueue 转 WriteableBitmap（PBgra8）显示；
/// - 视频：订阅 <see cref="WallpaperService.VideoPlayerReady"/>，把播放器挂到
///   MediaPlayerElement（SetMediaPlayer）作为输出面，画面由 Media Foundation 自主呈现。
/// 注意：本控制器呈现的是「当前壁纸的画面预览」；把画面直接绘制到外部目标窗口内部
/// （SetParent 子窗口宿主）是后续工作包的范围。
/// </summary>
public sealed class WallpaperPreviewController
{
    /// <summary>图片画面承载控件（预览区内的 Image）。</summary>
    private readonly Image _imagePresenter;

    /// <summary>视频画面承载控件（预览区内的 MediaPlayerElement）。</summary>
    private readonly MediaPlayerElement _playerPresenter;

    /// <summary>UI 线程调度队列：渲染线程 / 线程池事件封送回 UI 的通道。</summary>
    private readonly DispatcherQueue _dispatcherQueue;

    public WallpaperPreviewController(
        Image imagePresenter,
        MediaPlayerElement playerPresenter,
        WallpaperService wallpaper,
        DispatcherQueue dispatcherQueue)
    {
        _imagePresenter = imagePresenter;
        _playerPresenter = playerPresenter;
        _dispatcherQueue = dispatcherQueue;

        // 事件在服务生命周期内持续有效，控制器由 MainWindow 持有，无需退订
        wallpaper.ImageFrameReady += OnImageFrameReady;
        wallpaper.VideoPlayerReady += OnVideoPlayerReady;
    }

    /// <summary>
    /// 图片帧就绪（渲染线程回调）：先同步复制像素，再封送 UI 线程上屏。
    /// </summary>
    private void OnImageFrameReady(IntPtr hwnd, SoftwareBitmap frame)
    {
        BgraSnapshot snapshot;
        try
        {
            snapshot = SnapshotBitmap(frame);
        }
        catch
        {
            // 像素读取失败不致命：预览缺失不影响加载 / 配置等其余链路
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            // 切到图片模式：暂停并隐藏视频输出，显示图片帧
            _playerPresenter.MediaPlayer?.Pause();
            _playerPresenter.Visibility = Visibility.Collapsed;
            _imagePresenter.Visibility = Visibility.Visible;
            _imagePresenter.Source = ToWriteableBitmap(snapshot);
        });
    }

    /// <summary>
    /// 视频播放器就绪（线程池线程回调）：封送 UI 线程，把播放器挂到输出面。
    /// </summary>
    private void OnVideoPlayerReady(IntPtr hwnd, Windows.Media.Playback.MediaPlayer player)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // 切到视频模式：清掉图片帧，把播放器挂到 MediaPlayerElement（接管其画面输出）；
            // 播放 / 暂停仍由 WallpaperService 的渲染状态机驱动（loader 已配置循环与静音）
            _imagePresenter.Source = null;
            _imagePresenter.Visibility = Visibility.Collapsed;
            _playerPresenter.Visibility = Visibility.Visible;
            _playerPresenter.SetMediaPlayer(player);
        });
    }

    /// <summary>
    /// 把 SoftwareBitmap 的像素同步复制成紧密排列的 PBgra8 快照（在调用线程执行）。
    /// 加载器产出的 Bgra8 + Premultiplied 位图与 WriteableBitmap 的 PBgra8 布局一致，逐字节直拷即可。
    /// </summary>
    private static BgraSnapshot SnapshotBitmap(SoftwareBitmap frame)
    {
        if (frame.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new NotSupportedException($"预览仅支持 Bgra8 像素格式，实际：{frame.BitmapPixelFormat}。");
        }

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        var pixels = new byte[width * height * 4];

        using var buffer = frame.LockBuffer(BitmapBufferAccessMode.Read);
        // 行距（stride）取自平面描述：底层缓冲可能带行对齐填充，不能假定紧密排列
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();

        unsafe
        {
            // IBufferReference 的底层原生对象实现 IMemoryBufferByteAccess，
            // 经 COM 转型拿到原始指针后逐行复制（把行填充剥离成紧密排列）
            var byteAccess = (IMemoryBufferByteAccess)(object)reference;
            byteAccess.GetBuffer(out byte* source, out _);

            for (var y = 0; y < height; y++)
            {
                int sourceRow = plane.StartIndex + y * plane.Stride;
                int targetRow = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    int s = sourceRow + x * 4;
                    int d = targetRow + x * 4;
                    pixels[d] = source[s];
                    pixels[d + 1] = source[s + 1];
                    pixels[d + 2] = source[s + 2];
                    pixels[d + 3] = source[s + 3];
                }
            }
        }

        return new BgraSnapshot(width, height, pixels);
    }

    /// <summary>把像素快照转成 WriteableBitmap 供 Image 控件显示（必须在 UI 线程调用）。</summary>
    private static WriteableBitmap ToWriteableBitmap(BgraSnapshot snapshot)
    {
        var bitmap = new WriteableBitmap(snapshot.Width, snapshot.Height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(snapshot.Pixels, 0, snapshot.Pixels.Length);
        bitmap.Invalidate(); // 通知 XAML 位图内容已更新，需要重新呈现
        return bitmap;
    }
}
