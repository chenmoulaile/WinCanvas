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

    /// <summary>壁纸画面预览控制器（持有引用防 GC；事件订阅使其实际常驻）。</summary>
    private readonly WallpaperPreviewController _wallpaperPreview;

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

        // 壁纸画面上屏（装配职责）：订阅 WallpaperService.ImageFrameReady（转 WriteableBitmap
        // 显示到预览 Image）与 VideoPlayerReady（挂 MediaPlayerElement 输出）
        _wallpaperPreview = new WallpaperPreviewController(
            WallpaperPreviewImage,
            WallpaperPreviewPlayer,
            services.Wallpaper,
            global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        // 退出前确保配置落盘 + 壁纸宿主全部销毁（WP1-A）+ NumLock 钩子显式卸载（WP2）：
        // 窗口关闭后消息循环即将退出，FlushAsync 的异步续延（含 await File.WriteAllTextAsync 之后的
        // File.Move）可能没有机会执行，必须用同步 Flush() 写完磁盘再返回，防抖窗口内（最后 500ms）
        // 的修改才不会丢。ShutdownAll 同步卸载全部 WinEvent 钩子并销毁全部宿主窗口（经宿主专用消息
        // 线程往返），满足「应用退出时全部宿主销毁」验收（EnumWindows 按类名计数应归零）。
        // KeyboardIndicator.Dispose 同步显式卸载 LL 键盘钩子（有界等待）——与进程退出时对
        // IsBackground 钩子线程的强制回收构成「显式 Unhook + 进程退出自动卸载」双保险（WP2 DoD）。
        Closed += (_, _) =>
        {
            services.Configuration.Flush();
            services.Wallpaper.ShutdownAll();
            services.KeyboardIndicator.Dispose();
        };

        // 启动初始化：加载配置 + 首次枚举窗口（异步，不阻塞界面显示）
        _ = ViewModel.InitializeAsync();
    }
}
