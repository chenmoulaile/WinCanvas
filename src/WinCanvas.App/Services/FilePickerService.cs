using Windows.Storage.Pickers;
using WinCanvas.Wallpaper;

namespace WinCanvas.App.Services;

/// <summary>
/// 文件选择服务：封装 WinUI 的 FileOpenPicker。
/// unpackaged（非打包）应用必须先用 InitializeWithWindow 把选择器挂到窗口句柄上，
/// 否则 PickSingleFileAsync 会抛 COMException。
/// </summary>
public static class FilePickerService
{
    /// <summary>
    /// 壁纸背景支持的扩展名（图片 / 视频 / .pkg 打包资源）。
    /// WP5 起【不再本类自定义字面列表】，直接由 <see cref="WallpaperFormats"/> 拼装——
    /// 扩展名口径全仓库单一事实来源（选择器能选出的文件必须与加载器能接受的完全一致，
    /// 此前 .webp / .avi 的两侧漂移正是各自维护字面列表造成的事故来源）。
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        WallpaperFormats.ImageExtensions
            .Concat(WallpaperFormats.VideoExtensions)
            .Append(WallpaperFormats.PkgExtension)
            .ToArray();

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

    /// <summary>
    /// 弹出「选择文件夹」对话框，用于添加壁纸库扫描目录；取消时返回 null。
    ///
    /// 为什么必须有：Wallpaper Engine 不一定装在 Steam 的标准位置 ——
    /// 实测就有「绿色版放在自己盘某个目录」的情形，注册表与
    /// libraryfolders.vdf 都探不到。没有手工指路的能力，这类安装就用不上自动检索。
    /// </summary>
    /// <param name="ownerHwnd">属主窗口句柄。</param>
    public static async Task<string?> PickFolderAsync(IntPtr ownerHwnd)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };

        // FolderPicker 要求至少一个文件类型才能正常工作（它选的是目录，
        // 但这个占位类型不能为空，否则直接抛异常）—— 官方文档给出的惯例是 "*"
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
