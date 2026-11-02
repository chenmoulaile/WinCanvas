using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace WinCanvas.App.Services;

/// <summary>
/// 开发阶段的 FPS 性能监控器：
/// 通过 CompositionTarget.Rendering（每个渲染帧回调一次，UI 线程）累计帧数，
/// 每秒用 DispatcherQueueTimer 取样一次，得到每秒渲染帧率，
/// 供主界面右上角的悬浮 FPS 指示观察 WinUI 渲染性能。
///
/// v0.2.0 修正的资源泄漏：旧实现在构造函数里用 **lambda** 订阅
/// <see cref="CompositionTarget.Rendering"/>，而该事件是【静态事件】——
/// lambda 无法退订、类也没实现 IDisposable，于是：
/// ① 关掉界面上的「FPS 性能监控悬浮显示」开关后，每帧回调仍终身执行
///   （旧注释称「帧计数本身开销可忽略」，但真正的问题不是这点开销）；
/// ② 静态事件委托链持有这个 lambda → 持有 this → 持有 FpsMonitor，
///   而 FpsMonitor 又经订阅链牵住 MainViewModel 与整棵 XAML 树，
///   等于把整个 UI 对象图钉在 GC 根上，永不回收。
/// 现在改为「具名处理器 + 随开关挂/摘 + Dispose 兜底摘」。
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    /// <summary>UI 线程调度队列（Tick 与事件回调都切回 UI 线程）。</summary>
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>每秒取样一次的计时器。</summary>
    private readonly DispatcherQueueTimer _timer;

    /// <summary>
    /// 帧计数处理器（具名、可退订）。订阅/退订必须用同一个委托实例，
    /// 这正是旧实现用 lambda 后再也摘不掉的原因。
    /// </summary>
    private readonly EventHandler<object> _renderHandler;

    /// <summary>当前秒内累计的渲染帧数。</summary>
    private int _frameCount;

    /// <summary>帧处理器当前是否挂在静态事件上。</summary>
    private bool _subscribed;

    private bool _enabled;
    private bool _disposed;

    /// <summary>每秒帧率取样结果（UI 线程触发）。</summary>
    public event EventHandler<int>? FpsChanged;

    public FpsMonitor(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTimerTick;

        // 只准备处理器，【不在构造时订阅】：静态事件的订阅权交给 Enabled，
        // 保证「关了就真的不再每帧回调」。
        _renderHandler = (_, _) => Interlocked.Increment(ref _frameCount);
    }

    /// <summary>是否启用 FPS 监控（对应主界面「FPS 性能监控悬浮显示」开关）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            _frameCount = 0;

            if (value)
            {
                if (!_subscribed)
                {
                    CompositionTarget.Rendering += _renderHandler;
                    _subscribed = true;
                }

                _timer.Start();
            }
            else
            {
                _timer.Stop();

                // 关闭即摘掉每帧回调：这是本次修正的核心——旧实现只停计时器，
                // 每帧回调一直挂到进程结束。
                if (_subscribed)
                {
                    CompositionTarget.Rendering -= _renderHandler;
                    _subscribed = false;
                }
            }
        }
    }

    /// <summary>每秒取样：取走累计帧数并抛事件。</summary>
    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        var fps = Interlocked.Exchange(ref _frameCount, 0);
        _dispatcherQueue.TryEnqueue(() => FpsChanged?.Invoke(this, fps));
    }

    /// <summary>释放：停计时器、摘掉静态帧回调、断开外部订阅。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer.Stop();
        _timer.Tick -= OnTimerTick;

        if (_subscribed)
        {
            CompositionTarget.Rendering -= _renderHandler;
            _subscribed = false;
        }

        _enabled = false;
        FpsChanged = null; // 断开订阅链，避免退出路径上再回调到已释放的界面
    }
}
