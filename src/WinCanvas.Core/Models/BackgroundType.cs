namespace WinCanvas.Core.Models;

/// <summary>
/// 窗口背景的类型。
/// </summary>
public enum BackgroundType
{
    /// <summary>无背景（保持默认窗口外观）。</summary>
    None = 0,

    /// <summary>静态图片背景。</summary>
    Image = 1,

    /// <summary>视频背景。</summary>
    Video = 2,

    /// <summary>pkg 打包资源背景（本程序自定义的打包格式，尚未实现）。</summary>
    Pkg = 3,
}
