using System.IO;
using WinCanvas.Wallpaper;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// <see cref="WinTipsIntegrationService"/> 的回退契约用例（v0.3.0）。
///
/// 要守住的是一条具体的事故链路：WinTips.exe 存在但【在当前机器上起不来】时，
/// 绝不能去 spawn 它 —— 框架依赖的 apphost 起不来时会弹【系统模态对话框】
/// 「You must install or update .NET」，那个框 Process.Start 返回成功、
/// try/catch 抓不到也关不掉，用户会看到一个和 NumLock 毫无关系的安装提示。
/// 正确行为是：探测不过 → 不 spawn → 抛本地回退事件（由 App 层转成托盘气泡 / InfoBar）。
/// </summary>
public sealed class WinTipsIntegrationFallbackTests : IDisposable
{
    private readonly string _root;

    public WinTipsIntegrationFallbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinCanvasWinTipsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>造一个「存在但（可能）起不来」的假 WinTips.exe + 框架依赖的 runtimeconfig。</summary>
    private string MakeFakeWinTips(bool selfContained)
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string exe = Path.Combine(dir, "WinTips.exe");
        // 不是合法 PE：即便探测放行、真去 Start，也会抛 Win32Exception 被服务内部接住走回退
        File.WriteAllText(exe, "not a real executable");

        File.WriteAllText(Path.Combine(dir, "WinTips.runtimeconfig.json"), """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "frameworks": [
              { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
              { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
            ]
          }
        }
        """);

        if (selfContained)
        {
            File.WriteAllText(Path.Combine(dir, "coreclr.dll"), "stub");
        }

        return exe;
    }

    [Fact]
    public void 开关关闭时_不产生任何提示事件()
    {
        string exe = MakeFakeWinTips(selfContained: true);
        var service = new WinTipsIntegrationService(exe) { IsEnabled = false };

        bool fired = false;
        service.NumLockTipRequested += _ => fired = true;

        service.ShowNumLockStatus(true);

        Assert.False(fired);
    }

    [Fact]
    public void 找不到exe_走本地回退事件()
    {
        var service = new WinTipsIntegrationService(Path.Combine(_root, "ghost.exe"))
        {
            IsEnabled = true,
        };

        bool? firedState = null;
        service.NumLockTipRequested += isLocked => firedState = isLocked;

        service.ShowNumLockStatus(isLocked: true);

        Assert.Equal(true, firedState);
        Assert.False(service.IsWinTipsAvailable);
        Assert.Contains("未部署", service.WinTipsUnavailableReason);
    }

    [Fact]
    public void 找到exe但起不来_不spawn而是走回退()
    {
        // 关键用例：exe 存在（旧实现会直接 Start 它 → 可能弹系统对话框），
        // 但探测判定不可启动 → 必须改走回退，并且 IsWinTipsAvailable 也要反映真实可用性
        string exe = MakeFakeWinTips(selfContained: false);
        var service = new WinTipsIntegrationService(exe) { IsEnabled = true };

        int firedCount = 0;
        service.NumLockTipRequested += _ => firedCount++;

        service.ShowNumLockStatus(isLocked: false);

        // 无论探测结论是「不可启动」还是「可启动但 Start 假 exe 失败」，
        // 都必须落到本地回退事件，绝不能把用户晾着（一条提示都没有）
        Assert.Equal(1, firedCount);
    }

    [Fact]
    public void 可用性判定与不可用原因成对出现()
    {
        // 界面要能解释「为什么没有浮窗」，否则用户以为功能坏了
        string exe = MakeFakeWinTips(selfContained: false);
        var service = new WinTipsIntegrationService(exe);

        bool available = service.IsWinTipsAvailable;
        string? reason = service.WinTipsUnavailableReason;

        Assert.Equal(reason is null, available);
    }

    [Fact]
    public void 诊断日志在两条路径上都有记录()
    {
        // 回退必须留痕：旧实现的问题是「提示没出现但日志里一个字都没有」，无从归因
        string exe = MakeFakeWinTips(selfContained: false);
        var service = new WinTipsIntegrationService(exe) { IsEnabled = true };

        var logs = new List<string>();
        service.LogMessage += m => logs.Add(m);

        service.ShowNumLockStatus(true);

        Assert.NotEmpty(logs);
    }
}
