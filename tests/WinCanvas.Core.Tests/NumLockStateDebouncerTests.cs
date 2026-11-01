using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP4 用例表：<see cref="NumLockStateDebouncer"/>（WP4 自 KeyboardIndicatorService 抽离的
/// 去抖纯逻辑）。钩子线程与 GetKeyState 全部抽离在外——本类只测「采样 → 是否上报」的
/// 纯判定语义，无需真实键盘或钩子。语义红线：同状态连续事件只上报一次
/// （抑制按住自动重复的 KEYDOWN 风暴），这是 NumLock 提示不刷屏的唯一防线。
/// </summary>
public sealed class NumLockStateDebouncerTests
{
    [Fact]
    public void 同状态连续事件_只上报一次_其余被抑制()
    {
        var debouncer = new NumLockStateDebouncer();
        debouncer.Seed(locked: false); // 钩子装好前的基线：当前未锁定

        // 按住不放的自动重复 / 同一翻转的两个读取时机：连续喂入相同状态，均不得上报
        Assert.False(debouncer.OnSample(locked: false));
        Assert.False(debouncer.OnSample(locked: false));
        Assert.False(debouncer.OnSample(locked: false));
    }

    [Fact]
    public void 状态真实翻转_恰好上报一次_来回翻转各上报一次()
    {
        var debouncer = new NumLockStateDebouncer();
        debouncer.Seed(locked: false);

        Assert.True(debouncer.OnSample(locked: true));  // false → true：上报
        Assert.False(debouncer.OnSample(locked: true)); // 重复 true：吞掉
        Assert.True(debouncer.OnSample(locked: false)); // true → false：再次上报
        Assert.False(debouncer.OnSample(locked: false));
    }

    [Fact]
    public void 未播种时第一次采样视为翻转_上报()
    {
        // 基线 -1（未知）：任何第一次采样都与 -1 不同 → 上报。
        // 该口径对应「Seed 前回调不可能到达」（播种先于装钩，见 KeyboardIndicatorService），
        // 防御性钉住：即使不 Seed 也不会把首事件静默吞掉。
        var debouncer = new NumLockStateDebouncer();
        Assert.True(debouncer.OnSample(locked: false));
    }

    [Fact]
    public void Seed_使开启前已有的旧状态不被误报为变化()
    {
        // 开启监听时开关本来就处于锁定位：播种 true 后，第一个 true 采样不得触发提示
        //（否则用户开机即收到一条莫名的「已锁定」）。
        var debouncer = new NumLockStateDebouncer();
        debouncer.Seed(locked: true);

        Assert.False(debouncer.OnSample(locked: true));
        Assert.True(debouncer.OnSample(locked: false)); // 真实翻转仍上报
    }

    [Fact]
    public void LastReportedState_始终反映最近一次采样值()
    {
        var debouncer = new NumLockStateDebouncer();
        Assert.Equal(-1, debouncer.LastReportedState); // -1 = 未播种

        debouncer.Seed(locked: false);
        Assert.Equal(0, debouncer.LastReportedState);

        debouncer.OnSample(locked: true);
        Assert.Equal(1, debouncer.LastReportedState);

        debouncer.OnSample(locked: true); // 被抑制的重复采样也推进基线（与原 Interlocked 语义一致）
        Assert.Equal(1, debouncer.LastReportedState);
    }
}
