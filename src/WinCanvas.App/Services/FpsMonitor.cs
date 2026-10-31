using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace WinCanvas.App.Services;

/// <summary>
/// 开发阶段的 FPS 性能监控器：
/// 通过 CompositionTarget.Rendering（每个渲染帧回调一次，UI 线程）累计帧数，
/// 每秒用 DispatcherQueueTimer 取样一次，得到每秒渲染帧率，
/// 供主界面右上角的悬浮 FPS 指示观察 WinUI 渲染性能。
/// 未启用（Enabled = false）时只停止取样计时器；帧计数本身开销可忽略。
/// </summary>
public sealed class FpsMonitor
{
    /// <summary>UI 线程调度队列（Tick 与事件回调都切回 UI 线程）。</summary>
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>每秒取样一次的计时器。</summary>
    private readonly DispatcherQueueTimer _timer;

    /// <summary>当前秒内累计的渲染帧数。</summary>
    private int _frameCount;

    private bool _enabled;

    /// <summary>每秒帧率取样结果（UI 线程触发）。</summary>
    public event EventHandler<int>? FpsChanged;

    public FpsMonitor(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTimerTick;

        // CompositionTarget.Rendering：每个渲染帧触发一次（UI 线程），仅做计数
        CompositionTarget.Rendering += (_, _) => Interlocked.Increment(ref _frameCount);
    }

    /// <summary>是否启用 FPS 监控（对应主界面「FPS 性能监控悬浮显示」开关）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            _frameCount = 0;
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    /// <summary>每秒取样：取走累计帧数并抛事件。</summary>
    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        var fps = Interlocked.Exchange(ref _frameCount, 0);
        _dispatcherQueue.TryEnqueue(() => FpsChanged?.Invoke(this, fps));
    }
}
