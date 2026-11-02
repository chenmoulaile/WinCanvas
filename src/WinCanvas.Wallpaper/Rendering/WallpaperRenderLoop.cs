using System;
using System.Diagnostics;
using System.Threading;
using WinCanvas.Wallpaper.Models;
using WinCanvas.Wallpaper.Native;

namespace WinCanvas.Wallpaper.Rendering;

/// <summary>
/// 壁纸专用后台渲染线程。
/// 生命周期：Start 后在独立线程循环，直至 Dispose。
/// 每帧通过 <see cref="FrameTick"/> 回调（在渲染线程上触发），帧率由
/// <see cref="DynamicFrameRateController"/> 决定；全屏检测也在此线程节流执行。
/// 整个循环绝不触碰 UI 线程，也不做任何 UI 调度——消费方自行负责封送。
/// </summary>
public sealed class WallpaperRenderLoop : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopEvent = new(false);

    /// <summary>
    /// 状态探测（全屏前台窗口 / 承载窗口激活态）的最小间隔：
    /// 两类 P/Invoke 本身很轻，但不必每帧都查。
    /// </summary>
    private static readonly TimeSpan StateCheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 「承载壁纸的窗口是否前台激活」探针（渲染线程调用，须轻量、绝不阻塞）。
    /// 由消费方（WallpaperService）按会话注入；null = 不探测，保持恒 60fps 的既有行为。
    /// 接进 <see cref="DynamicFrameRateController.SetWindowActive"/>，让「非激活 15fps」档真正生效。
    /// </summary>
    public Func<bool>? ActiveProbe { get; set; }

    /// <summary>
    /// 帧回调：在每个应渲染的帧上于渲染线程触发。
    /// 订阅方（WallpaperService）在此驱动各层的逐帧工作并释放过期资源。
    /// </summary>
    public event Action? FrameTick;

    /// <summary>渲染状态变化回调（渲染线程触发），用于同步视频播放器的播放/暂停。</summary>
    public event Action<WallpaperRenderState>? StateChanged;

    /// <summary>帧率控制器（外部通过 Set* 方法驱动激活态/用户暂停）。</summary>
    public DynamicFrameRateController Controller { get; } = new();

    /// <summary>渲染线程是否仍在运行。</summary>
    public bool IsRunning => _thread.IsAlive;

    public WallpaperRenderLoop()
    {
        _thread = new Thread(RenderLoop)
        {
            Name = "WinCanvas.Wallpaper.Render",
            // 后台线程：进程退出时不被本线程阻塞
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
    }

    /// <summary>启动渲染线程。重复调用无副作用。</summary>
    public void Start()
    {
        if (_thread.IsAlive)
            return;

        _stopEvent.Reset();
        _thread.Start();
    }

    private void RenderLoop()
    {
        // Stopwatch 单调时钟：避免系统时间跳变影响帧间隔
        var clock = Stopwatch.StartNew();
        long nextFrameMs = 0;
        long lastStateCheckMs = long.MinValue;
        bool lastFullscreen = false;
        bool? lastActive = null;

        // 上一次已对外通报的渲染状态。null = 尚未通报过，首轮必发一次让订阅方对齐。
        WallpaperRenderState? lastEmittedState = null;

        while (!_stopEvent.IsSet)
        {
            // ---- 状态探测（节流至每 250ms 一次）：全屏前台窗口 + 承载窗口激活态 ----
            var nowMs = clock.ElapsedMilliseconds;
            if (nowMs - lastStateCheckMs >= (long) StateCheckInterval.TotalMilliseconds)
            {
                lastStateCheckMs = nowMs;

                var fullscreen = WallpaperNativeMethods.IsForegroundWindowFullscreen();
                if (fullscreen != lastFullscreen)
                {
                    lastFullscreen = fullscreen;
                    Controller.SetFullscreenDetected(fullscreen);
                }

                // 激活态探针（v0.2.0 接线）：旧实现 DynamicFrameRateController.SetWindowActive
                // 全仓库【只有测试调用】，产品代码一处没有 → 三档帧率里的「非激活 15fps」
                // 档在生产环境永不生效，壁纸永远按 60fps 跑，README 承诺的省电档形同虚设。
                // 探针异常一律按「维持当前档」处理：绝不让渲染线程因探测崩掉。
                var probe = ActiveProbe;
                if (probe != null)
                {
                    try
                    {
                        bool active = probe();
                        if (active != lastActive)
                        {
                            lastActive = active;
                            Controller.SetWindowActive(active);
                        }
                    }
                    catch
                    {
                        // 吞掉：探测失败保持现状，下一轮再试
                    }
                }
            }

            // ---- 状态同步：仅在状态真正变化时通报一次（边沿触发）----
            //
            // v0.2.0 修正的两个连带缺陷：
            // ① 旧代码把 StateChanged?.Invoke 写在「暂停分支」内部，于是暂停期间每轮
            //   （FrameInterval 在暂停档是 ~125ms）都无条件通报一次 → 订阅方每秒被叫 8 次
            //   去 player.Pause() + 跨线程读 PlaybackSession，纯属空转。
            // ② 更要命的是：因为通报点只在暂停分支，【活跃态永远不会被通报】，
            //   订阅方 OnStateChanged 里「Active60Fps/Inactive15Fps → player.Play()」
            //   那条恢复分支成了不可达的死代码 —— 全屏暂停结束后视频不会恢复播放，
            //   而 README「全屏前台窗口出现时自动暂停、退出后恢复」承诺的是这个行为。
            // 现在把通报移到循环顶部按状态变化边沿触发，两个问题一起解决。
            var state = Controller.CurrentState;
            if (state != lastEmittedState)
            {
                lastEmittedState = state;
                StateChanged?.Invoke(state);
            }

            // ---- 暂停态：不渲染，按轮询间隔睡眠等待恢复 ----
            var interval = Controller.FrameInterval;
            if (!Controller.ShouldRender)
            {
                _stopEvent.Wait(interval);
                continue;
            }

            // ---- 帧等待：距离下一帧未到则小睡 ----
            nowMs = clock.ElapsedMilliseconds;
            if (nowMs < nextFrameMs)
            {
                var sleepMs = Math.Min(nextFrameMs - nowMs, 15);
                if (sleepMs > 0)
                    Thread.Sleep((int) sleepMs);
                continue;
            }

            // ---- 出帧：在渲染线程上回调 ----
            try
            {
                FrameTick?.Invoke();
            }
            catch
            {
                // 单帧回调异常不允许杀死渲染线程；异常内容由订阅方自行记录
            }

            // 计算下一帧时间点；若已落后（长卡顿）则直接从当前时间重新对齐，避免追帧风暴
            nextFrameMs += (long) interval.TotalMilliseconds;
            if (nextFrameMs <= clock.ElapsedMilliseconds)
                nextFrameMs = clock.ElapsedMilliseconds + (long) interval.TotalMilliseconds;
        }
    }

    public void Dispose()
    {
        // 只发停止信号，不 Dispose 等待句柄本身：
        // 渲染线程可能正阻塞在 _stopEvent.Wait() 上，先 Dispose 会抛 ObjectDisposedException。
        // 线程是 IsBackground 后台线程，进程退出时自动回收；句柄交由进程生命周期回收。
        _stopEvent.Set();
    }
}
