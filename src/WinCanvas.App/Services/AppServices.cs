using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Services;
using WinCanvas.Wallpaper;

namespace WinCanvas.App.Services;

/// <summary>
/// 应用级组合根（简易构造注入容器）：
/// 集中创建各服务实例，由 App.OnLaunched 构造一次并注入 MainWindow / ViewModel。
/// - 窗口控制 / 配置：Core 的实现（通过 Core 服务接口暴露）；
/// - 壁纸：<see cref="WallpaperService"/>（图片 / 视频 / pkg 三层加载策略 + 后台渲染循环，
///   pkg 解析由 ThirdParty/repkg 子模块的源码项目引用提供）；
/// - 状态提示：<see cref="WinTipsIntegrationService"/>（IStatusNotifier 的 WinTips 集成实现，
///   优先进程调用 WinTips.exe 的 CLI 浮窗，回退本地事件通道）。
/// </summary>
public sealed class AppServices
{
    /// <summary>配置服务（Core 实现）：config.json 的读写与防抖落盘。</summary>
    public IConfigurationService Configuration { get; }

    /// <summary>窗口控制服务（Core 实现）：枚举窗口 / 置顶 / 透明 / 穿透 / 圆角。</summary>
    public IWindowControlService WindowControl { get; }

    /// <summary>壁纸服务（WinCanvas.Wallpaper 实现，实现 Core 的 IWallpaperService）。
    /// <see cref="WallpaperService.StatusReported"/> 由 ViewModel 订阅展示加载提示。</summary>
    public WallpaperService Wallpaper { get; }

    /// <summary>WinTips 集成服务（WinCanvas.Wallpaper 实现，实现 Core 的 IStatusNotifier）。
    /// 「WinTips 提示」开关通过其 <see cref="WinTipsIntegrationService.IsEnabled"/> 控制。</summary>
    public WinTipsIntegrationService WinTips { get; }

    /// <summary>NumLock 键盘指示器服务（Core 实现，WP2）：LL 键盘钩子监听 NumLock 状态变化，
    /// 状态变化时调用 <see cref="WinTips"/> 的 ShowNumLockStatus（浮窗或本地回退）。
    /// 「WinTips 提示」开关同时控制它的 <see cref="KeyboardIndicatorService.Start"/> / Stop（钩子启停）。</summary>
    public KeyboardIndicatorService KeyboardIndicator { get; }

    public AppServices()
    {
        Configuration = new ConfigurationService();
        WindowControl = new WindowControlService();
        // 壁纸服务注入窗口控制服务：A2 跟随模式下目标不透明时自动应用轻度透明（WP1-A）
        Wallpaper = new WallpaperService(WindowControl);
        WinTips = new WinTipsIntegrationService();
        // NumLock 钩子注入 WinTips 集成服务作上报目标（WP2）：
        // 钩子只负责「状态变化」事件源，是否对外呈现仍由 WinTips.IsEnabled 兜底把关
        KeyboardIndicator = new KeyboardIndicatorService(WinTips);
    }
}
