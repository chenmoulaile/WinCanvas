using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
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

    public WallpaperLibraryItemViewModel(WallpaperLibraryEntry entry)
    {
        _entry = entry;
    }

    /// <summary>传给壁纸服务的挂接路径（项目目录或媒体文件）。</summary>
    public string Path => _entry.Path;

    /// <summary>显示标题。</summary>
    public string Title => _entry.Title;

    /// <summary>类型短标签（场景 / 视频 / 网页 / 图片 / pkg）。</summary>
    public string KindLabel => _entry.EntryKind switch
    {
        WallpaperLibraryEntryKind.Project => KindOfProject(_entry.Path),
        _ => KindOfFile(_entry.Path),
    };

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
    private Microsoft.UI.Xaml.Visibility _previewVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// 缩略图。延迟创建：面板里条目上百个，构造时全解码会在打开界面的一瞬间
    /// 把几十上百张图同时推进内存；改由 View 在元素加载时调 <see cref="EnsurePreview"/>。
    /// </summary>
    public BitmapImage? Preview { get; private set; }

    /// <summary>
    /// 按需加载缩略图（由 View 的 Loaded 事件驱动，只处理已滚进视口的条目）。
    /// 幂等。
    /// </summary>
    public void EnsurePreview()
    {
        if (Preview != null || _entry.PreviewPath == null || !File.Exists(_entry.PreviewPath))
        {
            return;
        }

        try
        {
            Preview = new BitmapImage
            {
                DecodePixelWidth = 120,
                UriSource = new Uri(_entry.PreviewPath),
            };
            PreviewVisibility = Microsoft.UI.Xaml.Visibility.Visible;
            OnPropertyChanged(nameof(Preview));
        }
        catch (Exception)
        {
            // 预览图解不开：保持占位可见性为 Collapsed，条目本身仍然可选
        }
    }

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
