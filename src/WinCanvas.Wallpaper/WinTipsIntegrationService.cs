using System;
using System.Diagnostics;
using System.IO;
using WinCanvas.Core.Interfaces;

namespace WinCanvas.Wallpaper;

/// <summary>
/// IStatusNotifier 的 WinTips 集成实现。
/// WinTips（ThirdParty/WinTips 子模块）目标框架为 net10.0-windows 的 WPF WinExe，
/// 本环境仅有 .NET 8 SDK，既无法项目引用也无法构建它 —— 故采用"本地适配实现"：
///   1. 优先：进程调用已部署的 WinTips.exe 的 CLI 测试命令
///      <c>--test num on|off [holdSeconds]</c>（源码 App/App.xaml.cs 的 RunTest 与
///      Core/TipInfo.cs 的 TryParseKind 验证了该参数格式），复用其 Win11 质感浮窗 UI；
///   2. 回退：找不到 WinTips.exe 时，抛出本地事件 <see cref="NumLockTipRequested"/>，
///      由 App 层自行呈现（例如托盘气泡 / Toast）。
/// </summary>
public sealed class WinTipsIntegrationService : IStatusNotifier
{
    /// <summary>可选：显式指定 WinTips.exe 路径（配置服务就绪后由配置注入）。</summary>
    private readonly string? _explicitExePath;

    /// <summary>WinTips.exe 提示默认停留时长（秒）；2 秒为 README 示例量级，够看清状态。</summary>
    private const double TipHoldSeconds = 2;

    /// <summary>
    /// 回退通道：WinTips.exe 不可用时触发，参数 = NumLock 是否锁定。
    /// 触发线程 = 调用 ShowNumLockStatus 的线程。
    /// </summary>
    public event Action<bool>? NumLockTipRequested;

    /// <summary>诊断信息（WinTips 调用结果），供日志/UI 层订阅。</summary>
    public event Action<string>? LogMessage;

    /// <summary>
    /// 是否有【可用】的 WinTips 浮窗通道 —— 不只是「文件存在」，
    /// 还必须通过运行时可启动性探测（否则启动会弹系统对话框）。
    /// </summary>
    public bool IsWinTipsAvailable
    {
        get
        {
            var exe = TryLocateWinTipsExe();
            return exe != null && WinTipsRuntimeProbe.IsLaunchable(exe, out _);
        }
    }

    /// <summary>
    /// WinTips 不可用的原因（可用时为 null）。供设置区把「为什么没浮窗」讲清楚，
    /// 而不是让用户以为功能坏了。
    /// </summary>
    public string? WinTipsUnavailableReason
    {
        get
        {
            var exe = TryLocateWinTipsExe();
            if (exe == null)
            {
                return "未部署 WinTips.exe";
            }

            return WinTipsRuntimeProbe.IsLaunchable(exe, out string? reason) ? null : reason;
        }
    }

    /// <summary>
    /// 是否允许对外弹出提示。对应主界面「WinTips 提示」开关：
    /// 开关关闭时 <see cref="ShowNumLockStatus"/> 直接忽略，不启动 WinTips.exe、不触发回退事件。
    /// </summary>
    public bool IsEnabled { get; set; }

    public WinTipsIntegrationService(string? explicitExePath = null)
    {
        _explicitExePath = explicitExePath;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 本方法不阻塞调用方（键盘钩子回调可能是高频路径）：
    /// WinTips.exe 启动开销在几十毫秒量级，用一次性进程启动且不等待退出。
    /// </remarks>
    public void ShowNumLockStatus(bool isLocked)
    {
        // 「WinTips 提示」开关关闭时静默忽略，不产生任何浮窗或回退事件
        if (!IsEnabled)
            return;

        var exe = TryLocateWinTipsExe();

        // 找到 exe ≠ 能安全启动它：框架依赖的 WinTips 在缺对应 .NET 运行时的机器上，
        // 启动时由 apphost 弹【系统模态对话框】「需要安装 .NET」——
        // 那个框 Process.Start 成功返回、try/catch 抓不到也关不掉。
        // 所以必须先探测，探测不过直接走本地回退，绝不 spawn。
        if (exe != null && !WinTipsRuntimeProbe.IsLaunchable(exe, out string? launchBlockReason))
        {
            LogMessage?.Invoke($"WinTips.exe 存在但不可安全启动：{launchBlockReason}。NumLock 提示走本地事件通道。");
            NumLockTipRequested?.Invoke(isLocked);
            return;
        }

        if (exe != null)
        {
            try
            {
                // WinTips CLI：--test num on|off [hold]
                // （App.xaml.cs RunTest：args[0]=kind, args[1]=on/off, args[2]=hold 秒）
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--test num {(isLocked ? "on" : "off")} {TipHoldSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                LogMessage?.Invoke($"WinTips NumLock 提示已请求（isLocked={isLocked}）: {exe}");
            }
            catch (Exception ex)
            {
                // 启动失败（权限/文件失效等）：降级到本地事件通道
                LogMessage?.Invoke($"WinTips.exe 调用失败，回退本地提示通道：{ex.Message}");
                NumLockTipRequested?.Invoke(isLocked);
            }

            return;
        }

        // 未找到 WinTips.exe：本地适配回退
        LogMessage?.Invoke("未找到 WinTips.exe，NumLock 提示走本地事件通道。");
        NumLockTipRequested?.Invoke(isLocked);
    }

    /// <summary>
    /// 定位 WinTips.exe：显式路径 -> 本程序目录旁的 WinTips\WinTips.exe
    /// -> %LOCALAPPDATA%\Programs\WinTips\WinTips.exe。
    /// </summary>
    private string? TryLocateWinTipsExe()
    {
        if (_explicitExePath != null && File.Exists(_explicitExePath))
            return _explicitExePath;

        var candidates = new[]
        {
            // 与本类库所在程序同目录（部署时 WinTips 可随包发布）
            Path.Combine(AppContext.BaseDirectory, "WinTips", "WinTips.exe"),
            Path.Combine(AppContext.BaseDirectory, "WinTips.exe"),
            // WinTips 常见用户级安装位置
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "WinTips", "WinTips.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinTips", "WinTips.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
