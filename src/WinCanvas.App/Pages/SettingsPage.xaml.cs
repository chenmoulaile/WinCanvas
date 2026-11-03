using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.ViewModels;

namespace WinCanvas.App.Pages;

/// <summary>
/// 设置页（全局 / 软件级开关 + 快捷键表）。
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
}
