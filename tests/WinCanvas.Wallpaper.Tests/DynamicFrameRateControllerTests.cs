using WinCanvas.Wallpaper.Models;
using WinCanvas.Wallpaper.Rendering;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// WP4 用例表：<see cref="DynamicFrameRateController"/> 的状态真值表与事件语义。
/// 被测对象是「纯状态机，不持有线程资源」（类注释原文），全部用例无需真实窗口或渲染线程。
/// 优先级语义：user 暂停 > 全屏 > 激活/非激活。
/// </summary>
public sealed class DynamicFrameRateControllerTests
{
    /// <summary>
    /// 订阅 StateChanged 并返回事件参数快照列表（List.Count 即触发次数）。
    /// 注意：事件在本测试线程同步触发（类注释「触发线程 = 调用 Set* 的线程」），
    /// 无跨线程访问，List 无需加锁；也不用局部计数变量——返回值会值拷贝导致闭包计数丢失。
    /// </summary>
    private static List<WallpaperRenderState> Observe(DynamicFrameRateController controller)
    {
        var states = new List<WallpaperRenderState>();
        controller.StateChanged += states.Add;
        return states;
    }

    /// <summary>真值表理论数据：(用户暂停, 全屏, 激活) → 期望状态。覆盖 2×2×2 全组合 + 优先级断言。</summary>
    public static TheoryData<bool, bool, bool, WallpaperRenderState> TruthTable => new()
    {
        // user 暂停最高优先：无论全屏/激活与否 → PausedByUser
        { true,  false, true,  WallpaperRenderState.PausedByUser },
        { true,  false, false, WallpaperRenderState.PausedByUser },
        { true,  true,  true,  WallpaperRenderState.PausedByUser },
        { true,  true,  false, WallpaperRenderState.PausedByUser },
        // 全屏次之：非暂停 + 全屏 → PausedFullscreen
        { false, true,  true,  WallpaperRenderState.PausedFullscreen },
        { false, true,  false, WallpaperRenderState.PausedFullscreen },
        // 正常档：非暂停 + 非全屏 → 激活 60fps / 非激活 15fps
        { false, false, true,  WallpaperRenderState.Active60Fps },
        { false, false, false, WallpaperRenderState.Inactive15Fps },
    };

    [Theory]
    [MemberData(nameof(TruthTable))]
    public void CurrentState_真值表_优先级为用户暂停大于全屏大于激活(
        bool paused, bool fullscreen, bool active, WallpaperRenderState expected)
    {
        var controller = new DynamicFrameRateController
        {
            // 三个开关一次到位（Set 顺序无关，CurrentState 按优先级即时求值）
        };
        controller.SetWindowActive(active);
        controller.SetFullscreenDetected(fullscreen);
        controller.SetPausedByUser(paused);

        Assert.Equal(expected, controller.CurrentState);
    }

    [Theory]
    [InlineData(true, false)]  // 用户暂停 → 不渲染
    [InlineData(false, true)]  // 全屏暂停 → 不渲染
    public void ShouldRender_两种暂停态均为false(bool paused, bool fullscreen)
    {
        var controller = new DynamicFrameRateController();
        controller.SetPausedByUser(paused);
        controller.SetFullscreenDetected(fullscreen);

        Assert.False(controller.ShouldRender);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRender_非暂停两档均为true(bool active)
    {
        var controller = new DynamicFrameRateController();
        controller.SetWindowActive(active); // Active60Fps / Inactive15Fps 都要渲染

        Assert.True(controller.ShouldRender);
    }

    [Fact]
    public void Set同值重复_不触发StateChanged()
    {
        var controller = new DynamicFrameRateController();
        var states = Observe(controller);

        // 各开关重复喂同值（全屏监视器 250ms 节流回填同值的真实场景）：
        // 状态机必须按「值变化」而非「调用发生」触发事件，否则渲染循环被无谓唤醒。
        controller.SetWindowActive(true);
        controller.SetWindowActive(true);
        controller.SetFullscreenDetected(false);
        controller.SetFullscreenDetected(false);
        controller.SetPausedByUser(false);
        controller.SetPausedByUser(false);

        Assert.Empty(states);
    }

    [Fact]
    public void Set新值_触发StateChanged且事件携带正确新态()
    {
        var controller = new DynamicFrameRateController();
        var states = Observe(controller);

        controller.SetWindowActive(false); // true → false：Active60Fps → Inactive15Fps
        Assert.Single(states);
        Assert.Equal(WallpaperRenderState.Inactive15Fps, states[0]);

        controller.SetFullscreenDetected(true); // 非激活 → 全屏暂停
        Assert.Equal(2, states.Count);
        Assert.Equal(WallpaperRenderState.PausedFullscreen, states[1]);

        controller.SetPausedByUser(true); // 全屏 → 用户暂停（更高优先级压制）
        Assert.Equal(3, states.Count);
        Assert.Equal(WallpaperRenderState.PausedByUser, states[2]);

        controller.SetPausedByUser(false); // 仍全屏：回到 PausedFullscreen
        Assert.Equal(4, states.Count);
        Assert.Equal(WallpaperRenderState.PausedFullscreen, states[3]);

        controller.SetFullscreenDetected(false); // 非激活（windowActive 仍是 false）
        Assert.Equal(5, states.Count);
        Assert.Equal(WallpaperRenderState.Inactive15Fps, states[4]);
    }

    [Fact]
    public void FrameInterval_激活档60fps_非激活档15fps_暂停态按8fps轮询()
    {
        var controller = new DynamicFrameRateController();

        // 激活：1000/60 ≈ 16.7ms（UI 线程 16ms 预算的来源口径）
        Assert.Equal(TimeSpan.FromMilliseconds(1000.0 / 60.0), controller.FrameInterval);

        controller.SetWindowActive(false);
        Assert.Equal(TimeSpan.FromMilliseconds(1000.0 / 15.0), controller.FrameInterval);

        controller.SetPausedByUser(true);
        // 暂停态不渲染，按 ~8fps 轮询恢复条件（实现内写定的兜底频率）
        Assert.Equal(TimeSpan.FromMilliseconds(1000.0 / 8.0), controller.FrameInterval);
    }
}
