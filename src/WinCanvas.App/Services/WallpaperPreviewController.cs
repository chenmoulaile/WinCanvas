using System;
using System.Runtime.InteropServices.WindowsRuntime; // IBuffer.AsStream（CsWinRT 提供的 WinRT 互操作扩展）
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Rendering;

namespace WinCanvas.App.Services;

/// <summary>
/// 壁纸画面预览控制器：把 <see cref="WallpaperService"/> 的输出接到主窗口的预览承载区，
/// 是 ImageFrameReady / VideoPlayerReady 的 App 层订阅者。
/// - 图片（含 pkg 降级封面）：订阅 <see cref="WallpaperService.ImageFrameReady"/>，
///   在回调线程（渲染线程）内同步把 SoftwareBitmap 像素复制成 <see cref="BgraSnapshot"/>
///  （复制用下沉到 WinCanvas.Wallpaper 的共享工具 <see cref="BitmapSnapshot"/>——
///   预览与宿主渲染共用同一份实现，规格 WP1-B.3 要求勿复制两份；
///   订阅调用发生在会话内部锁中、先于 Source.Dispose，规避在途帧与资源释放的竞争），
///   再封送到 DispatcherQueue 转 WriteableBitmap（PBgra8）显示；
/// - 视频：订阅 <see cref="WallpaperService.VideoPlayerReady"/>，把播放器挂到
///   MediaPlayerElement（SetMediaPlayer）作为输出面，画面由 Media Foundation 自主呈现。
/// 注意：宿主窗口（WP1-A 引入）才是壁纸的主输出；本控制器是旁路预览（规格 WP1-D 预览旁路化）。
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
    /// 图片帧就绪（渲染线程回调）：先同步复制像素（共享工具），再封送 UI 线程上屏。
    /// </summary>
    private void OnImageFrameReady(IntPtr hwnd, SoftwareBitmap frame)
    {
        BgraSnapshot snapshot;
        try
        {
            snapshot = BitmapSnapshot.CopyFrom(frame); // 预览与宿主共用的下沉实现（WP1-B）
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

    /// <summary>把像素快照转成 WriteableBitmap 供 Image 控件显示（必须在 UI 线程调用）。
    /// （原 SnapshotBitmap 本地实现已下沉为 WinCanvas.Wallpaper 的共享工具 <see cref="BitmapSnapshot"/>，
    /// 预览与宿主渲染共用一份，规格 WP1-B.3；此处不再保留副本。）</summary>
    private static WriteableBitmap ToWriteableBitmap(BgraSnapshot snapshot)
    {
        var bitmap = new WriteableBitmap(snapshot.Width, snapshot.Height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(snapshot.Pixels, 0, snapshot.Pixels.Length);
        bitmap.Invalidate(); // 通知 XAML 位图内容已更新，需要重新呈现
        return bitmap;
    }
}
