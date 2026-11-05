using System.Windows.Input;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// 设置区「壁纸库目录」列表里的一行（v0.4.0 新增）：一个用户自定义扫描根 + 它的移除命令。
///
/// 为什么单独一行对象而不直接绑字符串：移除按钮需要拿到「删哪一条」，
/// 数据模板里经 <c>x:Bind</c> 只能绑到行对象自身的成员，把命令放进行对象
/// 与 HotkeyRowViewModel（快捷键行）是同一套模式，界面与行为都归一行所有。
/// 行对象不可变（Path 只读），列表变化走整体重建，不需要可通知属性。
/// </summary>
public sealed class WallpaperLibraryRootRow
{
    /// <summary>该行对应的自定义扫描根（config 里持久化的原始字符串）。</summary>
    public string Path { get; }

    /// <summary>移除该行对应的目录（由 <see cref="MainViewModel"/> 提供，含落盘与重扫）。</summary>
    public ICommand RemoveCommand { get; }

    public WallpaperLibraryRootRow(string path, ICommand removeCommand)
    {
        Path = path;
        RemoveCommand = removeCommand;
    }
}
