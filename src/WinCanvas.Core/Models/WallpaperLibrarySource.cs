namespace WinCanvas.Core.Models;

/// <summary>
/// 壁纸库条目的来源分类（v0.4.0 壁纸库自动检索扩展）。
///
/// 为什么是枚举而不是只有「路径」：同一批素材文件（图片/视频/pkg/project.json 目录）
/// 可能来自完全不同的地方——创意工坊缓存、系统主题、「图片」库或用户随手指的文件夹。
/// 界面必须把来源标出来（上百条壁纸里，工坊条目带 scene.pkg 缓存、系统条目是
/// Windows 自己的主题背景、自定义条目用户自己最清楚），否则用户没法判断
/// 「删了这个目录会不会把壁纸弄丢」。
///
/// 数值已钉死：该枚举会以 int 形式持久化进 config.json 的壁纸库缓存
/// （<see cref="WallpaperLibraryCacheEntry.Source"/>），新增成员只能追加、不能改值。
/// </summary>
public enum WallpaperLibrarySource
{
    /// <summary>Wallpaper Engine（Steam 创意工坊 431960 或本地 myprojects / 用户配置的 WE 项目目录）。</summary>
    Workshop = 0,

    /// <summary>系统主题壁纸（%LOCALAPPDATA%\Microsoft\Windows\Themes 下的主题背景文件）。</summary>
    SystemTheme = 1,

    /// <summary>用户图片库（KnownFolder Pictures，即「此电脑 → 图片」）。</summary>
    Pictures = 2,

    /// <summary>用户自定义目录（config 持久化，设置区 / 库面板可增删）。</summary>
    Custom = 3,
}
