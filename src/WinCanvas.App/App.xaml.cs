using Microsoft.UI.Xaml;
using WinCanvas.App.Services;

namespace WinCanvas.App;

/// <summary>
/// 应用入口：创建组合根（各服务实例），再创建并激活主窗口。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 启动完成回调：构造 AppServices（窗口控制 / 配置用 Core 实现，
    /// 壁纸与 WinTips 集成用 WinCanvas.Wallpaper 实现）并注入 MainWindow。
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new AppServices();
        _window = new MainWindow(services);
        _window.Activate();
    }
}
