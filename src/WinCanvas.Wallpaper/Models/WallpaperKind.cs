namespace WinCanvas.Wallpaper.Models;

/// <summary>
/// 背景资源的实际类型（三层策略的判定结果）。
/// </summary>
public enum WallpaperKind
{
    /// <summary>静态图片背景（.jpg/.png/.jpeg/.bmp/.gif，第一层）。</summary>
    Image = 1,

    /// <summary>视频背景（.mp4 或 pkg 内嵌视频纹理，第二层）。</summary>
    Video = 2,

    /// <summary>pkg 打包资源背景（第三层；复杂 SceneScript 场景会降级为封面图）。</summary>
    Pkg = 3,
}
