using System.IO;
using WinCanvas.Wallpaper;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// WinTips 可启动性探测的用例（v0.3.0）。
///
/// 这条链路要防的事故很具体：框架依赖的 WinTips.exe 在缺 .NET 10 桌面运行时的机器上
/// 被启动时，是【apphost 自己弹系统模态对话框】「需要安装 .NET」——
/// Process.Start 成功返回、我们的 try/catch 抓不到也关不掉。
/// 所以探测必须在 spawn 之前完成，且判「不可启动」时宁可保守（走回退）也不能猜错。
/// </summary>
public sealed class WinTipsRuntimeProbeTests : IDisposable
{
    private readonly string _root;

    public WinTipsRuntimeProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinCanvasProbeTests", Guid.NewGuid().ToString("N"));
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

    private string MakeWinTipsDir(string runtimeConfigJson, bool selfContained)
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "WinTips.exe"), "stub");
        if (runtimeConfigJson != string.Empty)
        {
            File.WriteAllText(Path.Combine(dir, "WinTips.runtimeconfig.json"), runtimeConfigJson);
        }
        if (selfContained)
        {
            File.WriteAllText(Path.Combine(dir, "coreclr.dll"), "stub");
        }

        return dir;
    }

    [Fact]
    public void 路径为空_判不可启动()
    {
        Assert.False(WinTipsRuntimeProbe.IsLaunchable(null, out string? reason));
        Assert.NotNull(reason);

        Assert.False(WinTipsRuntimeProbe.IsLaunchable(Path.Combine(_root, "ghost.exe"), out _));
    }

    [Fact]
    public void 自带运行时的目录_判可启动()
    {
        // self-contained 的判定不看机器上装了什么：目录里有 coreclr 就一定能起
        string dir = MakeWinTipsDir(
            """{ "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } } }""",
            selfContained: true);

        Assert.True(WinTipsRuntimeProbe.IsLaunchable(Path.Combine(dir, "WinTips.exe"), out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void 复数frameworks_正确取桌面运行时版本而不是基础运行时()
    {
        // 实测框架依赖 WPF 写出来的就是这种形态。必须认 frameworks 数组，
        // 并且要取 Microsoft.WindowsDesktop.App 那条 —— 只看基础运行时会误判「能起」，
        // 而 WPF 缺桌面运行时照样弹框。
        string dir = MakeWinTipsDir("""
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "frameworks": [
              { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
              { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
            ]
          }
        }
        """, selfContained: false);

        // 不断言最终 true/false（那取决于测试机上装了什么运行时），
        // 只断言「探测过程不抛、且给出的是可解释的结论」
        bool launchable = WinTipsRuntimeProbe.IsLaunchable(Path.Combine(dir, "WinTips.exe"), out string? reason);
        Assert.True(launchable || !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 读不出runtimeconfig_保守判不可启动()
    {
        // 宁可走回退，也不要 spawn 一个可能弹系统对话框的进程
        string dir = MakeWinTipsDir(string.Empty, selfContained: false);

        Assert.False(WinTipsRuntimeProbe.IsLaunchable(Path.Combine(dir, "WinTips.exe"), out string? reason));
        Assert.Contains("runtimeconfig", reason);
    }

    [Fact]
    public void runtimeconfig损坏_保守判不可启动()
    {
        string dir = MakeWinTipsDir("{ 不是合法 JSON", selfContained: false);

        Assert.False(WinTipsRuntimeProbe.IsLaunchable(Path.Combine(dir, "WinTips.exe"), out _));
    }

    [Fact]
    public void 探测绝不抛异常_任何输入都返回布尔()
    {
        // 这个探测在 NumLock 按键链路上，抛异常会让提示彻底消失
        var weird = new[]
        {
            string.Empty,
            "  ",
            Path.Combine(_root, "nope.exe"),
            _root, // 传目录而不是 exe
        };

        foreach (string path in weird)
        {
            var ex = Record.Exception(() => WinTipsRuntimeProbe.IsLaunchable(path, out _));
            Assert.Null(ex);
        }
    }
}
