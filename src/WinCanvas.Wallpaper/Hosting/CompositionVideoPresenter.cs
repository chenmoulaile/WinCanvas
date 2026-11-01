using System;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Threading;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Composition;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 视频呈现器（规格 WP1-C 备选路径，ADR 见 WORKLOG 阶段 C）：
/// <see cref="MediaPlayer.GetSurface(Compositor)"/> 拿到播放器的组合表面 → SurfaceBrush 挂到
/// SpriteVisual → <c>ICompositorDesktopInterop.CreateDesktopWindowTarget</c> 把 WinRT 组合树
/// 绑定到宿主 HWND。视频画面由 Media Foundation + 系统组合引擎直接呈现：
/// <b>零逐帧拷贝、零轮询</b>——帧率联动完全复用既有状态机（暂停态 → player.Pause() → 画面定格）。
///
/// 线程模型（WP1-C 实测驱动）：Windows.UI.Composition 的对象（Compositor/Target/Visual）
/// 要求创建线程持有 DispatcherQueue（无 DQ 时 new Compositor() 抛 UnauthorizedAccess，见
/// WORKLOG），而 pump 线程无法补建 DQ。因此本类自持一条专用 DQ 线程
/// （<see cref="DispatcherQueueController.CreateOnDedicatedThread"/>），全部组合对象在该线程
/// 创建与操作，外部方法经 <see cref="RunOnCompositionThread"/> 同步封送（返回时操作已完成）。
///
/// 与 DComp 图像渲染器（<see cref="WallpaperHostRenderer"/>）的关系：
/// 同一宿主 HWND 上各持一个合成目标（WinRT DesktopWindowTarget + DComp 目标），二者按
/// 「当前壁纸类型」互斥生效——视频时 Root = 视频视觉，图片时 DComp 树重贴像素。
///
/// 比例正确（规格 DoD）：SpriteVisual 的尺寸/偏移按视频原始宽高（NaturalVideoWidth/Height）
/// 在客户区内做「等比缩放 + 居中」的加边计算——禁止拉伸变形。
/// </summary>
internal sealed class CompositionVideoPresenter : IDisposable
{
    /// <summary>ICompositorDesktopInterop 的接口 IID（DirectN 生成代码同值互证：29e691fa-4567-4dca-b319-d0f207eb6807）。</summary>
    private static readonly Guid IID_ICompositorDesktopInterop = new("29e691fa-4567-4dca-b319-d0f207eb6807");

    /// <summary>组合对象专用 DQ 线程的控制器（构造时创建，进程内保留）。</summary>
    private readonly DispatcherQueueController _dispatcherQueueController;

    /// <summary>组合对象所在的 DispatcherQueue（封送目标）。</summary>
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>组合器（DQ 线程上创建一次）。</summary>
    private Compositor? _compositor;

    /// <summary>桌面窗口目标（Compositor 与宿主 HWND 的绑定）。</summary>
    private CompositionTarget? _target;

    /// <summary>视频视觉（内容 = 播放器组合表面的画刷）。</summary>
    private SpriteVisual? _videoVisual;

    /// <summary>播放器组合表面（GetSurface 产物；随播放器生命周期释放）。</summary>
    private MediaPlayerSurface? _playerSurface;

    /// <summary>画刷（持有 CompositionSurface 引用）。</summary>
    private CompositionSurfaceBrush? _brush;

    /// <summary>宿主窗口句柄。</summary>
    private readonly IntPtr _hostHwnd;

    /// <summary>是否已释放。volatile：pump 线程内读写。</summary>
    private volatile bool _disposed;

    public CompositionVideoPresenter(IntPtr hostHwnd)
    {
        _hostHwnd = hostHwnd;
        _dispatcherQueueController = DispatcherQueueController.CreateOnDedicatedThread();
        _dispatcherQueue = _dispatcherQueueController.DispatcherQueue;
    }

    /// <summary>
    /// 在组合线程上同步执行委托（TryEnqueue + 信号等待）。组合对象有 DQ 线程亲和，
    /// 本类的全部组合操作都经此封送。
    /// </summary>
    /// <param name="action">待执行委托（在 DQ 线程上运行，内部不得再同步等待组合线程）。</param>
    /// <returns>true = 已执行完成；false = 入队失败或等待超时。</returns>
    private bool RunOnCompositionThread(Action action, int timeoutMs = 10000)
    {
        var done = new ManualResetEventSlim(false);
        var ok = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 组合线程任务异常不杀 DQ；记日志后继续
                HostDiagnostics.Log($"视频组合任务异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                done.Set();
            }
        });

        if (!ok)
        {
            done.Dispose();
            return false;
        }

        var completed = done.Wait(timeoutMs);
        done.Dispose();
        return completed;
    }

    /// <summary>
    /// 附着视频：建组合树并把播放器组合表面画刷挂上（幂等；尺寸/位置按视频比例重算）。
    /// pump 线程调用（内部封送到组合线程）。
    /// </summary>
    /// <param name="player">视频会话的播放器（组合呈现路径，帧服务器标志须为关）。</param>
    /// <param name="clientWidth">宿主客户区宽。</param>
    /// <param name="clientHeight">宿主客户区高。</param>
    /// <returns>true = 已附着；false = 失败（已记日志）。</returns>
    public bool Attach(MediaPlayer player, int clientWidth, int clientHeight)
    {
        if (_disposed)
            return false;

        var ok = false;
        RunOnCompositionThread(() =>
        {
            try
            {
                _compositor ??= new Compositor();

                if (_target == null)
                {
                    // Compositor（WinRT 投影对象）→ ABI 指针 → QI(ICompositorDesktopInterop)
                    var compositorAbi = ((WinRT.IWinRTObject) _compositor).NativeObject.ThisPtr;
                    var hr = Marshal.QueryInterface(compositorAbi, in IID_ICompositorDesktopInterop, out var interopPtr);
                    if (hr != DcompNativeMethods.S_OK)
                    {
                        HostDiagnostics.Log($"QI(ICompositorDesktopInterop) 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
                        return;
                    }

                    try
                    {
                        var interop = (ICompositorDesktopInterop) Marshal.GetObjectForIUnknown(interopPtr);
                        hr = interop.CreateDesktopWindowTarget(_hostHwnd, 1 /* topmost */, out var targetAbi);
                        if (hr != DcompNativeMethods.S_OK)
                        {
                            HostDiagnostics.Log($"CreateDesktopWindowTarget 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
                            return;
                        }

                        try
                        {
                            // IInspectable ABI → 托管 CompositionTarget（CsWinRT 官方通道）
                            _target = WinRT.MarshalInterface<CompositionTarget>.FromAbi(targetAbi);
                        }
                        finally
                        {
                            Marshal.Release(targetAbi);
                        }

                        HostDiagnostics.Log($"视频组合目标就绪 host=0x{_hostHwnd:X}");
                    }
                    finally
                    {
                        Marshal.Release(interopPtr);
                    }
                }

                // 播放器组合表面（幂等：同一播放器复用；换播放器则先释放旧的）
                if (_playerSurface == null || !ReferenceEquals(_playerSurface.MediaPlayer, player))
                {
                    _playerSurface?.Dispose();
                    _playerSurface = player.GetSurface(_compositor);
                    _brush?.Dispose();
                    _brush = _compositor.CreateSurfaceBrush(_playerSurface.CompositionSurface);
                }

                _videoVisual ??= _compositor.CreateSpriteVisual();
                _videoVisual.Brush = _brush;
                LayoutVideoCore(clientWidth, clientHeight);

                if (_target.Root != _videoVisual)
                    _target.Root = _videoVisual;

                HostDiagnostics.Log($"视频组合视觉就绪 host=0x{_hostHwnd:X} client={clientWidth}x{clientHeight}");
                ok = true;
            }
            catch (Exception ex)
            {
                // WinRT 调用失败（如目标已被绑定 / 播放器已释放）：记日志，保留纯色占位
                HostDiagnostics.Log($"视频组合附着失败 host=0x{_hostHwnd:X}：{ex.GetType().Name}: {ex.Message}");
                ok = false;
            }
        });

        return ok;
    }

    /// <summary>
    /// 按视频原始宽高在客户区内做「等比缩放 + 居中」加边布局（规格 DoD：比例正确、禁止拉伸）。
    /// 视频尺寸未知（NaturalVideo* = 0）时先铺满客户区，MediaOpened 后随尺寸变化重算。
    /// pump 线程调用（内部封送到组合线程）。
    /// </summary>
    public void LayoutVideo(int clientWidth, int clientHeight)
    {
        if (_disposed || clientWidth <= 0 || clientHeight <= 0)
            return;

        RunOnCompositionThread(() => LayoutVideoCore(clientWidth, clientHeight));
    }

    /// <summary>布局核心（组合线程上执行；调用方必须经 RunOnCompositionThread 封送）。</summary>
    private void LayoutVideoCore(int clientWidth, int clientHeight)
    {
        if (_videoVisual == null)
            return;

        var videoW = _playerSurface?.MediaPlayer.PlaybackSession.NaturalVideoWidth ?? 0;
        var videoH = _playerSurface?.MediaPlayer.PlaybackSession.NaturalVideoHeight ?? 0;

        float visualW;
        float visualH;
        float offsetX;
        float offsetY;
        if (videoW <= 0 || videoH <= 0)
        {
            visualW = clientWidth;
            visualH = clientHeight;
            offsetX = 0;
            offsetY = 0;
        }
        else
        {
            // 等比缩放：取 client/video 缩放因子的较小值（contain 语义），余量做居中加边
            var scale = Math.Min((double) clientWidth / videoW, (double) clientHeight / videoH);
            visualW = (float) (videoW * scale);
            visualH = (float) (videoH * scale);
            offsetX = (clientWidth - visualW) / 2f;
            offsetY = (clientHeight - visualH) / 2f;
        }

        _videoVisual.Size = new Vector2(visualW, visualH);
        _videoVisual.Offset = new Vector3(offsetX, offsetY, 0);
    }

    /// <summary>
    /// 解除附着（切出视频 / 会话销毁）：组合树 Root 清空、释放播放器表面与画刷（幂等）。
    /// 组合目标保留（复用；进程退出随 Compositor 回收）。pump 线程调用（内部封送）。
    /// </summary>
    public void Detach()
    {
        if (_disposed)
            return;

        RunOnCompositionThread(() =>
        {
            try
            {
                if (_target != null)
                    _target.Root = null;

                if (_videoVisual != null)
                    _videoVisual.Brush = null;

                _brush?.Dispose();
                _brush = null;
                _playerSurface?.Dispose();
                _playerSurface = null;
                _videoVisual = null;
            }
            catch (Exception ex)
            {
                HostDiagnostics.Log($"视频组合解除失败 host=0x{_hostHwnd:X}：{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>释放全部资源（应用退出）。pump 线程调用（内部封送）。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Detach();
    }

    /// <summary>
    /// ICompositorDesktopInterop（windows.ui.composition.interop.h；仅声明本实现调用的首个方法）。
    /// GUID 取自 DirectN 生成代码（源自真实 Windows 元数据，29e691fa-4567-4dca-b319-d0f207eb6807）。
    /// </summary>
    [ComImport]
    [Guid("29e691fa-4567-4dca-b319-d0f207eb6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        /// <summary>
        /// 创建绑定到桌面窗口的合成目标（视频组合树的挂接点）。
        /// </summary>
        /// <param name="hwndTarget">宿主 HWND。</param>
        /// <param name="isTopmost">TRUE = 组合内容在窗口自身内容之上。</param>
        /// <param name="result">输出：IDesktopWindowTarget 的 IInspectable（用 MarshalInterface 转
        /// 托管 CompositionTarget；用毕 Release）。</param>
        /// <returns>S_OK 成功；E_INVALIDARG 参数无效。</returns>
        [PreserveSig]
        int CreateDesktopWindowTarget(IntPtr hwndTarget, int isTopmost, out IntPtr result);
    }
}
