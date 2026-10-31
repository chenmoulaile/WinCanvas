using Microsoft.UI.Xaml;

namespace WinCanvas.App;

/// <summary>
/// 应用入口（WinUI 3 空壳）：仅负责创建并激活占位主窗口。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 启动完成回调：创建占位主窗口并激活。
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
