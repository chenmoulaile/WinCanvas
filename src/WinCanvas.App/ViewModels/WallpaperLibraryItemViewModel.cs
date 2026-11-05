using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using WinCanvas.Core.Models;
using WinCanvas.Wallpaper;
using WinCanvas.Wallpaper.Models;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// 壁纸库面板里的一项（v0.3.0 新增）。
///
/// 缩略图的取舍：一个真实库有上百个条目，每个 preview 可能是几 MB 的 GIF。
/// 全部按原始尺寸解码会把工作集推到几百 MB，所以这里统一
/// <c>DecodePixelWidth = 120</c> 让 WinUI 在解码阶段就降采样 ——
/// 面板里本来也只有 120px 宽的格子。
/// 另外缩略图【解码失败不影响条目本身可选】：预览只是辅助信息，
/// 不能因为一张坏图让用户挂不了这张壁纸。
/// </summary>
public sealed partial class WallpaperLibraryItemViewModel : ObservableObject
{
    private readonly WallpaperLibraryEntry _entry;

    /// <summary>UI 调度队列：缩略图在线程池解码完成后封送回这里赋值。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public WallpaperLibraryItemViewModel(WallpaperLibraryEntry entry, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
    {
        _entry = entry;
        _dispatcher = dispatcher;
    }

    /// <summary>传给壁纸服务的挂接路径（项目目录或媒体文件）。</summary>
    public string Path => _entry.Path;

    /// <summary>显示标题。</summary>
    public string Title => _entry.Title;

    /// <summary>条目来源（工坊 / 系统主题 / 图片库 / 自定义，v0.4.0 新增）。</summary>
    public WallpaperLibrarySource Source => _entry.Source;

    /// <summary>来源的中文短标签（来源标签格子用）。</summary>
    public string SourceLabel => Source switch
    {
        WallpaperLibrarySource.Workshop => "工坊",
        WallpaperLibrarySource.SystemTheme => "系统",
        WallpaperLibrarySource.Pictures => "图片库",
        WallpaperLibrarySource.Custom => "自定义",
        _ => "未知", // config 缓存里出现未知数值（旧版本新枚举）：降级显示而不是崩溃
    };

    /// <summary>类型短标签（场景 / 视频 / 网页 / 图片 / pkg）。</summary>
    public string KindLabel => _entry.EntryKind switch
    {
        WallpaperLibraryEntryKind.Project => KindOfProject(_entry.Path),
        _ => KindOfFile(_entry.Path),
    };

    /// <summary>
    /// UIA 里这个条目读出来的名字（同 <see cref="WindowItemViewModel"/>：
    /// 不覆写就念成类型名，上百条壁纸在无障碍工具与自动化脚本里全都无法区分）。
    /// </summary>
    public override string ToString() => _entry.IsUsable
        ? $"{Title} — {KindLabel}"
        : $"{Title} — {KindLabel}（不可用：{_entry.UnusableReason}）";

    /// <summary>是否可能发声。</summary>
    public bool MightHaveAudio => _entry.MightHaveAudio;

    /// <summary>音频标记的可见性。</summary>
    public Microsoft.UI.Xaml.Visibility AudioVisibility =>
        _entry.MightHaveAudio ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>能否挂接。假 = 面板里标灰并显示原因。</summary>
    public bool IsUsable => _entry.IsUsable;

    /// <summary>不可用的原因（可用时为空串，避免绑定到 null 显示空白）。</summary>
    public string UnusableReason => _entry.UnusableReason ?? string.Empty;

    /// <summary>不可用说明的可见性。</summary>
    public Microsoft.UI.Xaml.Visibility UnusableVisibility =>
        _entry.IsUsable ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>副标题一行：类型 + 路径末段，帮用户认出到底是哪一个。</summary>
    public string Subtitle
    {
        get
        {
            string tail = System.IO.Path.GetFileName(_entry.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            return $"{KindLabel} · {tail}";
        }
    }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Visibility PreviewVisibility { get; set; } = Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// 缩略图。由 <see cref="EnsurePreview"/> 延迟生成（线程池解码 + 封送回 UI）。
    /// </summary>
    public BitmapImage? Preview { get; private set; }

    /// <summary>
    /// 按需生成缩略图（由 View 的 Loaded 事件驱动，只处理已滚进视口的条目）。幂等。
    ///
    /// 为什么不直接把 <c>BitmapImage.UriSource</c> 指到 preview.gif：
    /// Wallpaper Engine 的预览图【绝大多数是动画 GIF，而且是源分辨率】
    /// （实测单个 preview.gif 常见 0.5~3MB）。WinUI 的 BitmapImage 会把 GIF
    /// 动画解码并驻留全部帧，<c>DecodePixelWidth</c> 只缩最终显示尺寸、
    /// 不缩解码峰值 —— 实机测得一屏十几条缩略图把私有内存从 48MB 顶到 247MB，
    /// 等于用一个面板把程序基础内存翻了五倍。
    ///
    /// 现在的做法：交给 ImageSharp 解码成首帧 → 立刻降到 120px → 编码 PNG →
    /// 原图马上 Dispose；并且用 <see cref="_thumbnailGate"/> 【串行化】，
    /// 同一时刻只有一张预览处于解码峰值。
    /// （ImageSharp 2.1 的 GifDecoder.MaxFrames 没有公开的 Load 入口可用，
    ///   所以这里靠「串行 + 尽早降采样」控峰值，而不是靠限制帧数。）
    /// </summary>
    public void EnsurePreview()
    {
        if (_previewStarted || _entry.PreviewPath == null || !File.Exists(_entry.PreviewPath))
        {
            return;
        }

        _previewStarted = true;
        string path = _entry.PreviewPath;
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = _dispatcher;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            byte[]? png;
            await _thumbnailGate.WaitAsync().ConfigureAwait(false);
            try
            {
                png = TryBuildThumbnail(path);
            }
            finally
            {
                _thumbnailGate.Release();
            }

            if (png == null)
            {
                return; // 解码失败：保持占位，条目本身仍然可选
            }

            dispatcher?.TryEnqueue(async () =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await stream.WriteAsync(png.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);

                    Preview = bitmap;
                    PreviewVisibility = Microsoft.UI.Xaml.Visibility.Visible;
                    OnPropertyChanged(nameof(Preview));
                }
                catch (Exception)
                {
                    // 缩略图赋值失败不影响条目可选
                }
            });
        });
    }

    /// <summary>
    /// 生成 120px 宽的静态首帧 PNG。任何异常都返回 null ——
    /// 缩略图是辅助信息，不能反噬「这张壁纸能不能挂」。
    /// </summary>
    private static byte[]? TryBuildThumbnail(string path)
    {
        try
        {
            using Image image = Image.Load(path);
            return EncodeThumbnail(image);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? EncodeThumbnail(Image image)
    {
        using (image)
        {
            try
            {
                if (image.Width > ThumbnailWidth)
                {
                    // 先缩放再编码：让驻留时间极短，也保证产出的 PNG 只有几 KB
                    image.Mutate(x => x.Resize(ThumbnailWidth, 0));
                }

                using var ms = new MemoryStream();
                image.SaveAsPng(ms);
                return ms.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>缩略图宽度（面板格子 150px，120 足够）。</summary>
    private const int ThumbnailWidth = 120;

    /// <summary>
    /// 缩略图解码串行闸：把「同时处于解码峰值的预览数」压到 1。
    /// static：所有条目共享，因为峰值是进程级的内存问题，不是单条目问题。
    /// </summary>
    private static readonly SemaphoreSlim _thumbnailGate = new(1, 1);

    private bool _previewStarted;

    private static string KindOfProject(string directory)
    {
        var descriptor = WallpaperProjectReader.Read(directory);
        return descriptor?.Kind switch
        {
            WallpaperProjectKind.Scene => "场景壁纸",
            WallpaperProjectKind.Video => "视频壁纸",
            WallpaperProjectKind.Web => "网页壁纸",
            WallpaperProjectKind.AudioFilter => "音频响应壁纸",
            _ => "未知类型",
        };
    }

    private static string KindOfFile(string file)
    {
        WallpaperKind? kind = WallpaperFormats.DetectKind(file);
        return kind switch
        {
            WallpaperKind.Image => "图片",
            WallpaperKind.Video => "视频",
            WallpaperKind.Pkg => "pkg 包",
            _ => "文件",
        };
    }
}
