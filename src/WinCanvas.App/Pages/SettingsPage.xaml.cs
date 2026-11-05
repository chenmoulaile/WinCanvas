using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.Services;
using WinCanvas.App.ViewModels;

namespace WinCanvas.App.Pages;

/// <summary>
/// 设置页（全局 / 软件级开关 + 快捷键表 + 壁纸库目录）。
///
/// 为什么用依赖属性注入 <see cref="MainViewModel"/> 而不是自己 new 一个：
/// 快捷键的注册、托盘动作、配置持久化全都挂在主窗口那一份 VM 上。
/// 页面再持一份就等于两个实例抢同一批全局资源（同名热键第二个必注册失败），
/// 配置也会互相覆盖。所以这里只做呈现，状态仍然只有一处真源。
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    /// <summary><see cref="ViewModel"/> 的依赖属性（x:Bind 需要属主实例上的属性）。</summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(MainViewModel),
        typeof(SettingsPage),
        new PropertyMetadata(null));

    /// <summary>主视图模型（由主窗口注入）。</summary>
    public MainViewModel ViewModel
    {
        get => (MainViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 「添加壁纸库目录」按钮：与主界面库面板同一条路径（View 弹选择器 →
    /// 回灌 ViewModel 的 AddLibraryRoot 命令）。文件夹选择器需要属主窗口句柄，
    /// 从 App 上登记的主窗口取；拿不到（装配异常等极端情形）静默返回——
    /// 主界面的同名按钮仍可用。
    /// </summary>
    private async void OnAddLibraryRootClick(object sender, RoutedEventArgs e)
    {
        try
        {
            App? app = App.Current as App;
            if (app?.MainWindow is null)
            {
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
            string? picked = await FilePickerService.PickFolderAsync(hwnd);
            await ViewModel.AddLibraryRootCommand.ExecuteAsync(picked);
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "打开文件夹选择器失败（设置页）");
        }
    }
}
