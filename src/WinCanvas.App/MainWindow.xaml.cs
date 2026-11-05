using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
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

    /// <summary>
    /// 用户经托盘菜单 / 退出动作发起的「真退出」标志。
    /// 关闭按钮在托盘可用时会被拦下改成「收进托盘」，只有走退出动作才放行。
    /// </summary>
    private bool _isExiting;

    /// <summary>按键捕获是否已挂上窗口（只挂一次）。</summary>
    private bool _keyCaptureHooked;

    public MainWindow(AppServices services)
    {
        InitializeComponent();

        // Fluent：Mica 背景 + 内容延伸进标题栏（AppTitleBar 为拖拽区）。
        //
        // v0.2.0 加版本判定与降级：csproj 的 TargetPlatformMinVersion 是 10.0.17763
        // （Win10 1809），而 Mica 需要 Windows 11 22H2+。旧代码无条件赋值，
        // 在 Win10 上要么静默无背景、要么直接抛 —— 而这一句在构造函数里，
        // 抛出来就是「双击图标没反应」。现在失败退回纯色底，界面照常可用。
        TryApplyBackdrop();

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

        // ===== 应用外壳：系统托盘 + 全局热键（v0.2.0 新增）=====
        // 由 View 提供「显示 / 隐藏 / 是否可见 / 退出」四个动作，ViewModel 不直接持有 Window：
        // 保持 VM 可测试，也让窗口生命周期只有一处真源。
        bool shellOk = ViewModel.AttachShell(
            showMainWindow: () => DispatcherQueue.TryEnqueue(ShowMainWindow),
            hideMainWindow: () => DispatcherQueue.TryEnqueue(HideToTray),
            isMainWindowVisible: () => IsVisibleToUser,
            out string? shellError);

        if (!shellOk)
        {
            // 外壳起不来不影响主功能，但必须让用户知道为什么没有托盘图标和快捷键
            App.LogFatal(null, $"应用外壳建立失败：{shellError}");
        }

        ViewModel.SetExitHandler(() => DispatcherQueue.TryEnqueue(ExitApplication));

        // 窗口图标：任务栏 / alt-tab 用 WinCanvas.ico。取不到就保持系统默认图标。
        TrySetWindowIcon(hwnd);

        // ===== 隐私窗口（隐私窗口工作包）：失焦遮蔽的事件装配 =====
        // code-behind 只把 WinUI 的激活状态事件转发给 ViewModel（判定与开关逻辑在 VM 的
        // SetMainWindowFocusState），遮罩层可见性由 ViewModel.MainWindowPrivacyActive 驱动。
        // 注意：WinUI 3 的 Window 没有 Deactivated 事件（与 UWP CoreWindow 不同），
        // 失焦以 Activated 事件携带的 WindowActivationState.Deactivated 判定——
        // 这也是官方文档给出的失焦口径。失焦后 XAML 遮罩随窗口合成进入 DWM 窗口采样
        // → 任务栏 / Alt-Tab 预览同步呈现遮罩。
        Activated += (_, args) => ViewModel.SetMainWindowFocusState(
            isActivated: args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated);

        // ===== 关闭语义改造：有托盘时「关闭」= 收进托盘，「退出」= 真退出 =====
        // 迁移前只有 Closed：点 X 直接结束进程，壁纸宿主与已设属性全靠「下次启动自动恢复」兜，
        // 用户想「让工具在后台继续看着这些窗口」根本做不到 —— 这正是托盘要解决的问题。
        AppWindow.Closing += (Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs args) =>
        {
            if (_isExiting || !ViewModel.IsTrayActive)
            {
                return; // 真退出，或没有托盘可收留：照常关闭
            }

            args.Cancel = true;
            HideToTray();
        };

        // 退出前确保配置落盘 + 壁纸宿主全部销毁（WP1-A）+ NumLock 钩子显式卸载（WP2）：
        // 窗口关闭后消息循环即将退出，FlushAsync 的异步续延（含 await File.WriteAllTextAsync 之后的
        // File.Move）可能没有机会执行，必须用同步 Flush() 写完磁盘再返回，防抖窗口内（最后 500ms）
        // 的修改才不会丢。ShutdownAll 同步卸载全部 WinEvent 钩子并销毁全部宿主窗口（经宿主专用消息
        // 线程往返），满足「应用退出时全部宿主销毁」验收（EnumWindows 按类名计数应归零）。
        // KeyboardIndicator.Dispose 同步显式卸载 LL 键盘钩子（有界等待）——与进程退出时对
        // IsBackground 钩子线程的强制回收构成「显式 Unhook + 进程退出自动卸载」双保险（WP2 DoD）。
        Closed += (_, _) =>
        {
            // ViewModel 里持有 FpsMonitor（订阅了静态 CompositionTarget.Rendering）与
            // 渲染循环相关引用；不 Dispose 就整棵对象图被静态事件钉住不回收。
            ViewModel.Dispose();
            _wallpaperPreview.Dispose();
            services.Configuration.Flush();
            services.Wallpaper.ShutdownAll();
            services.KeyboardIndicator.Dispose();
        };

        // 启动初始化：加载配置 + 首次枚举窗口（异步，不阻塞界面显示）。
        // 必须观察异常：旧实现 `_ = InitializeAsync()` 即发即忘，加载配置时文件被占用 /
        // 无权限会让整条启动链静默失败，用户面对一个永远空白且没有任何提示的界面。
        _ = InitializeSafelyAsync();
    }

    /// <summary>
    /// 背景材质：优先 Mica（Win11 22H2+），不可用或抛异常时退回桌面亚克力，再不行留纯色。
    /// 任何失败都不允许从构造函数里抛出去。
    /// </summary>
    private void TryApplyBackdrop()
    {
        // Mica 需要 Windows 11 22H2（10.0.22621）
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            try
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                return;
            }
            catch (Exception ex)
            {
                App.LogFatal(ex, "Mica 背景设置失败，尝试降级");
            }
        }

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            // 两种材质都不可用：不设 SystemBackdrop，用默认纯色底，界面功能不受影响
            App.LogFatal(ex, "背景材质全部不可用，已退回纯色底");
        }
    }

    /// <summary>
    /// 第二个实例启动时被唤醒：把主窗口带回前台并恢复（配合单实例互斥体）。
    /// </summary>
    public void ShowFromSecondInstance()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = true;
            }

            // 统一走 ShowMainWindow（Show + 还原最小化 + Activate 是一整套）。
            // 唤醒路径自己只调 Activate() 的话，窗口从隐藏态回到可见态这件事
            // 就不会被任何一处状态记录到，显示/隐藏开关因此单向卡死。
            ShowMainWindow();
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "唤醒主窗口失败");
        }
    }

    /// <summary>启动初始化的异常兜底：失败要在界面上说清楚，不能留一个静默空白的窗口。</summary>
    private async Task InitializeSafelyAsync()
    {
        try
        {
            await ViewModel.InitializeAsync();

            // 配置加载完才有热键行可绑，捕获处理器在这里挂才拿得到 ViewModel 的状态
            HookHotkeyCapture();
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "启动初始化失败");
            ViewModel.ReportStartupFailure(ex);
        }
    }

    // ===== 主导航（窗口与壁纸 / 设置 / 关于） =====

    /// <summary>
    /// 切换页面。三块内容都在同一棵视觉树里，只改可见性 ——
    /// 用 Frame.Navigate 换页会把首页的 ListView 选中态、壁纸预览控件、
    /// 库面板的滚动位置全部重建，而那些状态正是用户期望「切回来还在」的。
    /// </summary>
    private void OnNavSelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender,
        Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        // 首帧解析到 NavigationViewItem 的 IsSelected=True 时就会触发本事件，
        // 那时 XAML 后半页的字段还没被 InitializeComponent 赋值 —— 忽略这一次，
        // 默认可见性（首页显示、另两页 Collapsed）本就是我们要的初始态。
        if (HomePage is null || SettingsPanel is null || AboutPanel is null)
        {
            return;
        }

        string tag = (args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string ?? "home";

        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 托盘 / 窗口可见性 =====

    /// <summary>
    /// 主窗口当前对用户是否可见。
    ///
    /// 【必须】每次现问 <c>AppWindow.IsVisible</c>，不要镜像成布尔字段：
    /// v0.3.0 就是用一个手工同步的 <c>_isVisibleToUser</c> 判断的，而第二实例唤醒走
    /// <see cref="ShowFromSecondInstance"/>，它只 Activate() 不碰这个字段 → 字段与真实
    /// 状态永久相反。后果是「显示/隐藏」这个动作单向卡死：
    /// 字段卡在 false 时按 <c>Ctrl+Alt+C</c> 只会重复调 Show()（空操作，窗口关不掉）；
    /// 卡在 true 时按它只会重复调 Hide()（窗口再也叫不回来，看起来就是「程序没界面」）。
    /// 托盘菜单文案与左键单击共用同一个判断，所以三处一起坏。
    /// </summary>
    private bool IsVisibleToUser => AppWindow.IsVisible;

    /// <summary>把主窗口带回前台。</summary>
    private void ShowMainWindow()
    {
        try
        {
            AppWindow.Show();

            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            Activate();
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "显示主窗口失败");
        }
    }

    /// <summary>收进托盘：只隐藏窗口，进程与全部热键/钩子继续工作。</summary>
    private void HideToTray()
    {
        try
        {
            AppWindow.Hide();
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "隐藏主窗口失败");
        }
    }

    /// <summary>真退出：放行 Closing 拦截，然后关窗口（Closed 里做 Flush + ShutdownAll 收口）。</summary>
    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    /// <summary>
    /// 用 WM_SETICON 给窗口挂上应用图标（任务栏与 alt-tab 生效）。
    /// WinUI 3 的 Window 没有托管的 Icon 属性，只能走 Win32；
    /// HICON 由系统持有，进程退出时随句柄表回收，这里不 DestroyIcon。
    /// </summary>
    private void TrySetWindowIcon(IntPtr hwnd)
    {
        try
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "WinCanvas.ico");
            if (!System.IO.File.Exists(iconPath))
            {
                return;
            }

            int cx = Native.ShellNativeMethods.GetSystemMetrics(Native.ShellNativeMethods.SM_CXSMICON);
            IntPtr small = Native.ShellNativeMethods.LoadImageW(IntPtr.Zero, iconPath,
                Native.ShellNativeMethods.IMAGE_ICON, cx > 0 ? cx : 16, cx > 0 ? cx : 16,
                Native.ShellNativeMethods.LR_LOADFROMFILE);
            IntPtr big = Native.ShellNativeMethods.LoadImageW(IntPtr.Zero, iconPath,
                Native.ShellNativeMethods.IMAGE_ICON, 32, 32,
                Native.ShellNativeMethods.LR_LOADFROMFILE);

            if (small != IntPtr.Zero)
            {
                Native.ShellNativeMethods.SendMessageW(hwnd, Native.ShellNativeMethods.WM_SETICON,
                    new IntPtr(Native.ShellNativeMethods.ICON_SMALL), small);
            }

            if (big != IntPtr.Zero)
            {
                Native.ShellNativeMethods.SendMessageW(hwnd, Native.ShellNativeMethods.WM_SETICON,
                    new IntPtr(Native.ShellNativeMethods.ICON_BIG), big);
            }
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "窗口图标设置失败（不影响功能）");
        }
    }

    // ===== 快捷键按键捕获 =====

    /// <summary>
    /// 挂上按键捕获。用根元素的 <c>AddHandler(..., handledEventsToo: true)</c>
    /// 而不是 <c>Window.KeyDown +=</c>：
    /// ① Window 不是 UIElement，没有 AddHandler，也没有 KeyDown——只能挂到内容根元素上；
    /// ② 捕获态下焦点常落在 ToggleSwitch / Button 上，它们会把 Ctrl+字母这类按键
    ///   【当作已处理】吃掉，普通 KeyDown 订阅根本收不到。
    /// </summary>
    private void HookHotkeyCapture()
    {
        if (_keyCaptureHooked)
        {
            return;
        }

        if (Content is not UIElement root)
        {
            App.LogFatal(null, "主窗口内容根元素不可用，快捷键录入无法启用");
            return;
        }

        _keyCaptureHooked = true;
        root.AddHandler(UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnCaptureKeyDown),
            handledEventsToo: true);
    }

    /// <summary>
    /// 捕获态下的按键处理：把 WinUI 的 <see cref="Windows.System.VirtualKey"/> 与
    /// 实时修饰键状态，翻译成 Win32 的 vk + MOD_* 交给 ViewModel。
    /// </summary>
    private void OnCaptureKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (!ViewModel.IsCapturingHotkey)
        {
            return;
        }

        var vk = args.Key;

        // 只按修饰键本身不算「主键」，继续等真正的键
        if (vk is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftWindows
            or Windows.System.VirtualKey.RightWindows or Windows.System.VirtualKey.LeftShift
            or Windows.System.VirtualKey.RightShift or Windows.System.VirtualKey.LeftMenu
            or Windows.System.VirtualKey.RightMenu)
        {
            args.Handled = true;
            return;
        }

        var mods = Core.Models.HotkeyModifiers.None;

        if (IsDown(Native.ShellNativeMethods.VK_CONTROL))
        {
            mods |= Core.Models.HotkeyModifiers.Control;
        }

        if (IsDown(Native.ShellNativeMethods.VK_MENU))
        {
            mods |= Core.Models.HotkeyModifiers.Alt;
        }

        if (IsDown(Native.ShellNativeMethods.VK_SHIFT))
        {
            mods |= Core.Models.HotkeyModifiers.Shift;
        }

        if (IsDown(Native.ShellNativeMethods.VK_LWIN) || IsDown(Native.ShellNativeMethods.VK_RWIN))
        {
            mods |= Core.Models.HotkeyModifiers.Win;
        }

        // Windows.System.VirtualKey 的枚举值与 Win32 VK_* 码同构，可直接强转
        args.Handled = ViewModel.HandleCapturedKey((int)vk, mods);
    }

    /// <summary>
    /// 「添加扫描目录」按钮：ViewModel 不直接碰 WinUI 对话框（保持可测试），
    /// 由 View 负责弹选择器并把结果回灌给命令。
    /// </summary>
    private async void OnAddLibraryRootClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            string? picked = await FilePickerService.PickFolderAsync(hwnd);
            await ViewModel.AddLibraryRootCommand.ExecuteAsync(picked);
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "打开文件夹选择器失败");
        }
    }

    /// <summary>
    /// 壁纸库条目点击：把该条目挂到当前选中的窗口。
    /// GridView 开了 IsItemClickEnabled，条目对象在 e.ClickedItem 上。
    /// </summary>
    private void OnLibraryItemClick(object sender, Microsoft.UI.Xaml.Controls.ItemClickEventArgs e)
    {
        if (e.ClickedItem is ViewModels.WallpaperLibraryItemViewModel item)
        {
            // 走生成的命令而不是直接调私有方法：命令自带 CanExecute（需要已选中窗口），
            // 未选中时它自己就会拒绝，界面反馈与快捷键路径完全一致
            ViewModel.ApplyLibraryItemCommand.Execute(item);
        }
    }

    /// <summary>
    /// 库面板条目滚进视口时才解码缩略图。
    /// 一个真实库有上百个条目，构造时全解码会在打开界面的一瞬间把几十上百张图
    /// 同时推进内存；交给 ListView 的虚拟化节奏，只处理实际可见的那些。
    /// </summary>
    private void OnLibraryItemLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.WallpaperLibraryItemViewModel item })
        {
            item.EnsurePreview();
        }
    }

    /// <summary>读某个虚拟键的实时按下状态（最高位为 1 = 按下）。</summary>
    private static bool IsDown(int virtualKey)
    {
        try
        {
            return (Native.ShellNativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, $"读取修饰键状态失败（0x{virtualKey:X2}）");
            return false;
        }
    }
}
