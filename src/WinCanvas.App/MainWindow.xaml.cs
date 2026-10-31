using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinCanvas.App.Services;
using WinCanvas.App.ViewModels;

namespace WinCanvas.App;

/// <summary>
/// 主窗口（Fluent Design，Mica 背景 + 标题栏融入内容区）。
/// code-behind 只负责：视觉装饰（Mica / 标题栏）、服务装配（构造 ViewModel）、
/// 退出前 Flush 配置；所有业务逻辑都在 <see cref="MainViewModel"/> 中。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>主 ViewModel（XAML 通过 x:Bind 绑定）。</summary>
    public MainViewModel ViewModel { get; }

    public MainWindow(AppServices services)
    {
        InitializeComponent();

        // Fluent：Mica 背景 + 内容延伸进标题栏（AppTitleBar 为拖拽区）
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "WinCanvas";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));

        // 服务装配：文件选择器需要属主窗口句柄；FPS 监控需要 UI 线程调度队列
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ViewModel = new MainViewModel(
            services,
            global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            () => FilePickerService.PickWallpaperFileAsync(hwnd),
            () => hwnd);

        // 退出前确保配置落盘（防抖写盘可能还有未刷的数据）
        Closed += async (_, _) => await services.Configuration.FlushAsync();

        // 启动初始化：加载配置 + 首次枚举窗口（异步，不阻塞界面显示）
        _ = ViewModel.InitializeAsync();
    }
}
