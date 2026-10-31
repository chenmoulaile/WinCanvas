using Windows.Storage.Pickers;

namespace WinCanvas.App.Services;

/// <summary>
/// 文件选择服务：封装 WinUI 的 FileOpenPicker。
/// unpackaged（非打包）应用必须先用 InitializeWithWindow 把选择器挂到窗口句柄上，
/// 否则 PickSingleFileAsync 会抛 COMException。
/// </summary>
public static class FilePickerService
{
    /// <summary>壁纸背景支持的扩展名（图片 / 视频 / .pkg 打包资源）。</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = new[]
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", // 图片
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv",  // 视频
        ".pkg",                                            // Wallpaper Engine 风格打包资源
    };

    /// <summary>
    /// 弹出「选择背景文件」对话框，返回所选文件的完整路径；用户取消时返回 null。
    /// </summary>
    /// <param name="ownerHwnd">属主窗口句柄（用于把选择器挂到应用窗口上）。</param>
    public static async Task<string?> PickWallpaperFileAsync(IntPtr ownerHwnd)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };

        // unpackaged 应用必须显式关联属主窗口
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);

        foreach (var ext in SupportedExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
