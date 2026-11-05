using System.ComponentModel;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 隐私窗口工作包的【真实窗口】验证（RequiresWindowsSession：需要真实桌面会话）。
/// 覆盖方案 (a) 的机制层证据与前台监听（EVENT_SYSTEM_FOREGROUND）的端到端事件：
/// ① SetLayeredWindowAttributes 写入遮蔽 alpha → GetLayeredWindowAttributes 回读一致；
/// ② 恢复按档案值写入 → 回读等于档案值（证明「恢复用档案值」路径在 Win32 层成立）；
/// ③ 前台监听在「最小化→还原」真实前台切换下收到目标窗口事件。
/// 无头环境用 --filter "Category!=RequiresWindowsSession" 排除（与既有口径一致）。
/// </summary>
[Trait("Category", "RequiresWindowsSession")]
public sealed class PrivacyRealWindowTests : IDisposable
{
    private readonly WindowControlService _service = new();
    private readonly TestWindowHarness.TestWindow? _window;

    public PrivacyRealWindowTests()
    {
        // 桌面会话不可用（无头 CI / 会话受限）时以明确中文原因失败——本类用例在无头环境
        // 应被 Trait 过滤排除，而不是带病运行（与 WindowControlServiceRealWindowTests 同约定）。
        _window = TestWindowHarness.Create() ?? throw new InvalidOperationException(
            $"真实窗口创建失败，RequiresWindowsSession 用例无法运行：{TestWindowHarness.LastCreateError}");
    }

    public void Dispose() => _window?.Dispose();

    [Fact]
    public async Task 遮蔽_SetTransparency写入遮蔽值_回读一致()
    {
        byte mask = PrivacyMaskRules.ComputeMaskAlpha(255); // 30

        await _service.SetTransparencyAsync(_window!.Hwnd, mask);

        // SetLayeredWindowAttributes 写入 30 → GetLayeredWindowAttributes 回读必须一致：
        // 这是方案 (a)「降 alpha 遮蔽」在 Win32 层可验证的机制证据（A2 宿主与 Cloak 无此回读面）
        Assert.True(TestWindowHarness.TryGetLayeredAlpha(_window.Hwnd, out byte alpha, out uint flags),
            "窗口未进入分层状态或分层属性读取失败（SetLayeredWindowAttributes 生效前提被破坏）");
        Assert.NotEqual(0u, flags & TestWindowHarness.LWA_ALPHA);
        Assert.Equal(mask, alpha);
    }

    [Fact]
    public async Task 恢复_按档案值写入_回读等于档案值而非默认255()
    {
        // 模拟「用户手动设置过 alpha=128（档案值）→ 失焦遮蔽 → 聚焦恢复」的完整 Win32 序列：
        // 恢复写入档案值 128，回读必须是 128——钉住「恢复不得硬编码 255」的服务层行为
        const byte profileAlpha = 128;

        await _service.SetTransparencyAsync(_window!.Hwnd, profileAlpha); // 用户档案
        await _service.SetTransparencyAsync(_window.Hwnd, PrivacyMaskRules.ComputeMaskAlpha(profileAlpha)); // 失焦遮蔽
        Assert.True(TestWindowHarness.TryGetLayeredAlpha(_window.Hwnd, out byte maskedAlpha, out _));
        Assert.Equal(PrivacyMaskRules.MaskAlphaDefault, maskedAlpha);

        await _service.SetTransparencyAsync(_window.Hwnd, profileAlpha); // 聚焦恢复（按档案值）
        Assert.True(TestWindowHarness.TryGetLayeredAlpha(_window.Hwnd, out byte restoredAlpha, out _));
        Assert.Equal(profileAlpha, restoredAlpha);
    }

    [Fact]
    public async Task 遮蔽_未分层窗口_自动补LAYERED样式()
    {
        // SetTransparencyAsync 内部 EnsureLayeredStyle 会给非分层窗口补 WS_EX_LAYERED
        // （全程持 _exStyleGate 锁，与置顶/穿透路径互斥）——隐私遮蔽对普通窗口开箱即用
        var styleBefore = TestWindowHarness.GetExStyle(_window!.Hwnd);
        Assert.Equal(0, styleBefore & TestWindowHarness.WS_EX_LAYERED);

        await _service.SetTransparencyAsync(_window.Hwnd, PrivacyMaskRules.MaskAlphaDefault);

        var styleAfter = TestWindowHarness.GetExStyle(_window.Hwnd);
        Assert.NotEqual(0, styleAfter & TestWindowHarness.WS_EX_LAYERED);
    }

    [Fact]
    public async Task ForegroundWatcher_最小化还原_收到目标窗口前台事件()
    {
        var watcher = new ForegroundWatcherService();

        var received = new List<IntPtr>();
        var targetSeen = new ManualResetEventSlim(false);
        watcher.ForegroundChanged += hwnd =>
        {
            lock (received)
            {
                received.Add(hwnd);
            }

            if (hwnd == _window!.Hwnd)
            {
                targetSeen.Set();
            }
        };

        watcher.Start();

        try
        {
            // 钩子在专用线程上异步安装：轮询等真实安装完成，再触发前台切换
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!watcher.IsHookInstalled && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(watcher.IsHookInstalled, "前台监听钩子 3 秒内未安装成功（IsHookInstalled 仍为 false）");

            // 最小化（前台移交其它窗口）→ 还原（还原动作会激活窗口，前台授给本测试窗口）。
            // 不用 SetForegroundWindow：后台进程受前台锁定限制，还原是窗口管理动作不受限。
            TestWindowHarness.ShowWindowCmd(_window!.Hwnd, TestWindowHarness.SW_MINIMIZE);
            await Task.Delay(150); // 两次切换之间留间隔，避免事件在极端调度下合并观察
            TestWindowHarness.ShowWindowCmd(_window.Hwnd, TestWindowHarness.SW_RESTORE);

            Assert.True(targetSeen.Wait(TimeSpan.FromSeconds(5)),
                $"还原窗口后 5 秒内未收到该窗口（0x{_window.Hwnd:X}）的 EVENT_SYSTEM_FOREGROUND 事件。" +
                $"期间共收到 {CountEvents(received)} 个前台事件。");
        }
        finally
        {
            watcher.Dispose(); // 幂等：卸钩 + 停线程
        }
    }

    private static int CountEvents(List<IntPtr> events)
    {
        lock (events)
        {
            return events.Count;
        }
    }
}
