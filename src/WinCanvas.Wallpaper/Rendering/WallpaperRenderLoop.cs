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

    /// <summary>全屏前台窗口检测的最小间隔（ms）：P/Invoke 本身很轻，但不必每帧都查。</summary>
    private static readonly TimeSpan FullscreenCheckInterval = TimeSpan.FromMilliseconds(250);

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
        long lastFullscreenCheckMs = long.MinValue;
        bool lastFullscreen = false;

        while (!_stopEvent.IsSet)
        {
            // ---- 全屏检测（节流至每 250ms 一次）----
            var nowMs = clock.ElapsedMilliseconds;
            if (nowMs - lastFullscreenCheckMs >= (long) FullscreenCheckInterval.TotalMilliseconds)
            {
                lastFullscreenCheckMs = nowMs;

                var fullscreen = WallpaperNativeMethods.IsForegroundWindowFullscreen();
                if (fullscreen != lastFullscreen)
                {
                    lastFullscreen = fullscreen;
                    Controller.SetFullscreenDetected(fullscreen);
                }
            }

            // ---- 暂停态：不渲染，按轮询间隔睡眠等待恢复 ----
            var interval = Controller.FrameInterval;
            if (!Controller.ShouldRender)
            {
                // 状态切换为暂停时立即上报一次，让视频层等资源降载
                StateChanged?.Invoke(Controller.CurrentState);
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
