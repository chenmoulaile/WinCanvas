using System.IO;
using System.Threading;
using Microsoft.UI.Xaml;
using WinCanvas.App.Services;

namespace WinCanvas.App;

/// <summary>
/// 应用入口：单实例互斥 → 构造组合根（各服务实例）→ 创建并激活主窗口。
///
/// v0.2.0 补的三项收口：
/// ① 单实例互斥。旧实现没有任何互斥，而 <c>ConfigurationService</c> 写的是同一个
///   <c>%APPDATA%\WinCanvas\config.json</c>：两个实例各自维护内存缓存、各自防抖落盘，
///   后写的整份覆盖前一份，用户看到的现象是「设置莫名其妙回滚了」。
///   现在第二个实例不再重复起界面，而是唤醒第一个实例的主窗口后自己退出。
/// ② 全局异常兜底。旧实现没有任何 UnhandledException 处理，且 OnLaunched /
///   MainWindow 构造里的 <c>_ = InitializeAsync()</c> 一旦抛异常就整条启动链静默失败，
///   用户面对一个永远空白的界面且没有任何提示。
/// ③ 崩溃可归因：异常一律落到 logs\crash.log（带轮转），便于事后定位。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 单实例互斥体名（Global\ 前缀 = 跨整个登录会话可见；同一用户的多桌面会话互不干扰）。
    /// 含版本号无关，只保证「同一份安装只跑一个界面实例」。
    /// </summary>
    private const string SingleInstanceMutexName = @"Global\WinCanvas.App.SingleInstance";

    /// <summary>唤醒已有实例主窗口的命名事件（第二实例发，第一实例收）。</summary>
    private const string ShowMainWindowEventName = @"Global\WinCanvas.App.ShowMainWindow";

    private Window? _window;

    /// <summary>互斥体持有句柄；进程退出由 OS 回收，但显式释放更规矩。</summary>
    private Mutex? _singleInstanceMutex;

    /// <summary>已有实例用来接收「唤醒」信号的等待句柄。</summary>
    private RegisteredWaitHandle? _showWindowWait;

    public App()
    {
        InitializeComponent();

        // WinUI 3 的 Application.UnhandledException 是【事件】（不同于 UWP 的可重写
        // OnUnhandledException），必须在构造期挂上，早于任何后续初始化，
        // 才能兜住 OnLaunched 里就发生的异常。
        UnhandledException += OnAppUnhandledException;
    }

    /// <summary>当前主窗口（供托盘菜单、全局热键等外部触发方回到主界面）。</summary>
    public MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// 启动完成回调：先抢单实例锁，再构造 AppServices（窗口控制 / 配置用 Core 实现，
    /// 壁纸与 WinTips 集成用 WinCanvas.Wallpaper 实现）并注入 MainWindow。
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!TryAcquireSingleInstance())
        {
            // 已有实例在跑：把它的主窗口叫到前台，本实例干净退出，
            // 绝不留下第二个界面去争同一份 config.json。
            SignalExistingInstance();
            Exit();
            return;
        }

        StartShowWindowListener();

        try
        {
            var services = new AppServices();
            _window = new MainWindow(services);
            MainWindow = (MainWindow)_window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            // 装配期崩溃（XAML 资源缺失 / 服务构造失败等）：留痕 + 尽力告知，不让它裸抛出去
            LogFatal(ex, "OnLaunched 装配主窗口失败");
            throw;
        }
    }

    /// <summary>
    /// 抢单实例互斥体。
    /// </summary>
    /// <returns>true = 本实例是唯一实例，继续启动；false = 已有实例在跑。</returns>
    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                return true;
            }

            // 拿不到所有权：说明别人持有。保留字段以便进程退出时释放，但不再等待。
            return false;
        }
        catch (Exception ex)
        {
            // 互斥体创建失败（极端权限 / 名称冲突）：退化成「照常启动」，
            // 宁可失去单实例保证，也不要因为一把锁把用户挡在程序外面。
            LogFatal(ex, "单实例互斥体创建失败，已退化为允许多实例");
            return true;
        }
    }

    /// <summary>通知已有实例把主窗口带到前台。</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowMainWindowEventName);
            ev.Set();
        }
        catch (Exception)
        {
            // 对方刚好在退出 / 事件尚未注册：静默——本实例反正要退出
        }
    }

    /// <summary>
    /// 注册「唤醒」等待：后台线程池等命名事件，命中后封送 UI 线程显示主窗口。
    /// 用 RegisterWaitForSingleObject 而不是自己起线程，避免多一条常驻线程。
    /// </summary>
    private void StartShowWindowListener()
    {
        try
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainWindowEventName);
            _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
                ev,
                (_, _) =>
                {
                    // 事件在池线程触发，界面操作必须回 UI 线程
                    _window?.DispatcherQueue.TryEnqueue(() => MainWindow?.ShowFromSecondInstance());
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            LogFatal(ex, "唤醒事件注册失败（第二实例将无法唤起本实例窗口）");
        }
    }

    /// <summary>
    /// WinUI 3 未处理异常兜底：记日志并吞掉（Handled = true），避免一个后台
    /// 任务里的偶发异常直接把整个程序炸掉——本程序长期驻留、还挂着别人的窗口，
    /// 「崩了」的代价远大于「这一动作没做成」。
    /// 装配期与退出路径的异常仍然外抛（那些是真没法继续）。
    /// </summary>
    private void OnAppUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        LogFatal(args.Exception, "UI 未处理异常");

        // 有异常对象就吞下继续跑；没有异常对象属于运行时内部故障，交回默认策略
        args.Handled = args.Exception is not null;
    }

    /// <summary>
    /// 崩溃 / 严重异常落盘：%LOCALAPPDATA%\WinCanvas\logs\crash.log。
    /// 写日志本身再失败也不能抛出去（否则兜底机制自己变成崩溃源）。
    /// </summary>
    internal static void LogFatal(Exception? exception, string context)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinCanvas", "logs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "crash.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 4 * 1024 * 1024)
            {
                string rolled = path + ".old";
                if (File.Exists(rolled))
                {
                    File.Delete(rolled);
                }

                File.Move(path, rolled);
            }

            string text =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {context}" +
                $"{Environment.NewLine}{exception?.GetType().FullName}: {exception?.Message}" +
                $"{exception?.StackTrace}{Environment.NewLine}{Environment.NewLine}";

            File.AppendAllText(path, text);
        }
        catch
        {
            // 兜底日志写不动就算了，绝不在异常处理路径里再抛
        }
    }
}
