using System;
using System.Runtime.InteropServices;
using WinCanvas.Wallpaper.Rendering;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 壁纸宿主的 DComp 图像渲染器（规格 WP1-B 主路径，一对一挂在 <see cref="WallpaperHostWindow"/> 上）：
/// D3D11CreateDevice(BGRA_SUPPORT) → QI(IDXGIDevice) → DCompositionCreateDevice
/// → CreateTargetForHwnd(host, TRUE) → CreateVisual → CreateSurface(B8G8R8A8 + ALPHA_MODE_IGNORE，
/// 尺寸 = 宿主客户区) → BeginDraw → IDXGISurface1::GetDC → GDI StretchDIBits 把共享快照像素
/// 缩放贴入 → ReleaseDC → EndDraw → SetContent → Commit 上屏。
///
/// 工作量模型（规格 B.4：静态图无逐帧工作）：
/// - 首次 Render / 客户区尺寸变化：重建表面 + 一次 GDI 缩放拷贝 + Commit（事件驱动、一次性）；
/// - 切换图片：快照不变尺寸则仅重贴一次；
/// - 设备失陷（DXGI_ERROR_DEVICE_REMOVED）：释放整棵树，下一次调用自动重建（壁纸会话不销毁）。
/// 任何路径都不注册 FrameTick 逐帧绘制。
///
/// 视频模式（WP1-C）由独立的 <see cref="CompositionVideoPresenter"/> 承担（MediaPlayer 组合呈现，
/// ADR 见 WORKLOG 阶段 C），与本类共享宿主窗口但各持各的合成目标，互不引用。
///
/// 线程模型：全部方法必须在宿主 pump 线程调用（COM 设备与 DComp target 按线程创建使用；
/// WallpaperHostWindow 负责 pump 封送）。字段无需加锁——pump 串行化。
/// </summary>
internal sealed class WallpaperHostRenderer : IDisposable
{
    /// <summary>StretchDIBits 光栅操作码：SRCCOPY(0x00CC0020)。</summary>
    private const uint RopSrcCopy = 0x00CC0020;

    /// <summary>SetStretchBltMode 的 HALFTONE(4)：高质量缩放（放大 / 缩小均比逐色采样平滑）。</summary>
    private const int StretchBltModeHalftone = 4;

    /// <summary>宿主窗口句柄（CreateTargetForHwnd 用；创建后只读）。</summary>
    private readonly IntPtr _hostHwnd;

    /// <summary>D3D11 设备原始指针（D3D11CreateDevice 输出，AddRef 归本类，用毕 Marshal.Release）。</summary>
    private IntPtr _d3dDevice;

    /// <summary>DXGI 设备原始指针（自 _d3dDevice QI，AddRef 归本类）。</summary>
    private IntPtr _dxgiDevice;

    /// <summary>DComp 设备（RCW；树根工厂 + Commit）。</summary>
    private DcompNativeMethods.IDCompositionDevice? _device;

    /// <summary>DComp 目标（设备与宿主 HWND 的绑定）。</summary>
    private DcompNativeMethods.IDCompositionTarget? _target;

    /// <summary>DComp 视觉（根视觉；内容 = surface）。</summary>
    private DcompNativeMethods.IDCompositionVisual? _visual;

    /// <summary>DComp 表面（内容 = 当前快照缩放后的像素；尺寸 = 宿主客户区）。</summary>
    private DcompNativeMethods.IDCompositionSurface? _surface;

    /// <summary>当前表面宽（像素；与客户区宽一致）。</summary>
    private int _surfaceW;

    /// <summary>当前表面高（像素）。</summary>
    private int _surfaceH;

    /// <summary>最近一次渲染的像素快照（重建表面 / 重贴的唯一来源；随 Render 更新）。</summary>
    private BgraSnapshot? _snapshot;

    /// <summary>是否已因设备失陷整树释放（下次 Render/UpdateClientSize 自动重建）。</summary>
    private bool _deviceLost;

    /// <summary>是否已释放（Dispose 后所有调用直接返回失败）。volatile：pump 线程内读写。</summary>
    private volatile bool _disposed;

    public WallpaperHostRenderer(IntPtr hostHwnd)
    {
        _hostHwnd = hostHwnd;
    }

    /// <summary>
    /// 渲染一张静态图（规格 B.3：像素来自共享快照逻辑的产物）。表面尺寸不符时重建；
    /// 尺寸相符也必然重贴一次像素（切换图片内容必须生效，规格 B DoD「切换不同图片背景即时生效」）。
    /// pump 线程调用。
    /// </summary>
    /// <param name="snapshot">紧密排列 PBgra8 像素（BitmapSnapshot.CopyFrom 产物）。</param>
    /// <param name="clientWidth">宿主客户区宽（表面尺寸依据）。</param>
    /// <param name="clientHeight">宿主客户区高。</param>
    /// <returns>true = 已成功 Commit；false = 失败（原因已写诊断日志，宿主保留纯色占位）。</returns>
    public bool Render(BgraSnapshot snapshot, int clientWidth, int clientHeight)
    {
        if (_disposed)
            return false;

        _snapshot = snapshot;

        if (!EnsureTree())
            return false;

        // 表面确保在位（尺寸不符则重建）；内容每次 Render 都重贴——切图即时生效的语义保证
        if (!EnsureSurface(clientWidth, clientHeight))
            return false;

        if (!UploadPixels(snapshot))
            return false;

        return Commit();
    }

    /// <summary>
    /// 宿主客户区尺寸变化（WinEvent 跟随路径回调，pump 线程）：图像模式表面按新尺寸重建并重贴一次
    /// （规格 B.4「尺寸变化时重建 surface 重绘一次」；事件驱动、无逐帧工作）。
    /// </summary>
    public void UpdateClientSize(int width, int height)
    {
        if (_disposed || _deviceLost || _snapshot == null)
            return; // 树未就绪 / 失陷：快照已在，待下次 Render 重建

        if (width <= 0 || height <= 0)
            return;

        if (width == _surfaceW && height == _surfaceH)
            return; // 尺寸未变（纯移动事件），跳过

        if (EnsureSurface(width, height) && UploadPixels(_snapshot))
            _ = Commit();
    }

    /// <summary>释放整棵 DComp 树与设备（幂等）。pump 线程调用。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TearDownTree();
    }

    /// <summary>确保 DComp 树存在（设备失陷后自动重建）。返回 false = 重建失败（原因已记日志）。</summary>
    private bool EnsureTree()
    {
        if (_device != null && !_deviceLost)
            return true;

        // 失陷/首次：清干净再来（幂等）
        TearDownTree();

        // 1) D3D11 设备（BGRA 支持，规格 B.1；特性级别 11_1 → 10_0 逐级回退）
        var levels = new uint[]
        {
            DcompNativeMethods.D3D_FEATURE_LEVEL_11_1,
            DcompNativeMethods.D3D_FEATURE_LEVEL_11_0,
            DcompNativeMethods.D3D_FEATURE_LEVEL_10_1,
            DcompNativeMethods.D3D_FEATURE_LEVEL_10_0,
        };

        var hr = DcompNativeMethods.D3D11CreateDevice(
            IntPtr.Zero, DcompNativeMethods.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            DcompNativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, (uint) levels.Length, DcompNativeMethods.D3D11_SDK_VERSION,
            out _d3dDevice, IntPtr.Zero, IntPtr.Zero);

        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"D3D11CreateDevice 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            return false;
        }

        // 2) QI 出 IDXGIDevice（DCompositionCreateDevice 的输入）
        hr = Marshal.QueryInterface(_d3dDevice, in DcompNativeMethods.IID_IDXGIDevice, out _dxgiDevice);
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"QueryInterface(IDXGIDevice) 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            ReleaseDeviceRaw();
            return false;
        }

        // 3) DComp 设备（规格 B.1：设备经 DXGI 交给 DCompositionCreateDevice）
        hr = DcompNativeMethods.DCompositionCreateDevice(
            _dxgiDevice, in DcompNativeMethods.IID_IDCompositionDevice, out var dcompDevice);
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"DCompositionCreateDevice 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            ReleaseDeviceRaw();
            return false;
        }

        _device = (DcompNativeMethods.IDCompositionDevice) Marshal.GetObjectForIUnknown(dcompDevice);
        Marshal.Release(dcompDevice); // GetObjectForIUnknown 已 AddRef，原始指针引用释放

        // 4) 目标绑定宿主窗口（topmost=TRUE：视觉树覆盖窗口 GDI 纯色占位）
        hr = _device.CreateTargetForHwnd(_hostHwnd, 1, out _target);
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"CreateTargetForHwnd 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            TearDownTree();
            return false;
        }

        // 5) 根视觉（内容在表面就绪后 SetContent——SetContent 槽位无重载歧义，安全）
        hr = _device.CreateVisual(out _visual);
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"CreateVisual 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            TearDownTree();
            return false;
        }

        hr = _target.SetRoot(_visual);
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"SetRoot 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            TearDownTree();
            return false;
        }

        _deviceLost = false;
        HostDiagnostics.Log($"DComp 树就绪 host=0x{_hostHwnd:X}");
        return true;
    }

    /// <summary>
    /// 表面与客户区尺寸不符时重建表面并贴一次像素。返回 false = 失败（已记日志）。
    /// </summary>
    private bool EnsureSurface(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
            return false; // 无有效尺寸（理论上调用方已保证）

        if (_surface != null && _surfaceW == clientWidth && _surfaceH == clientHeight)
            return true; // 尺寸未变，复用现有表面（内容由 UploadPixels 重贴）

        // 旧表面先从视觉摘除再释放（SetContent(null) 避免 DWM 引用已释放对象）
        if (_surface != null)
        {
            _ = _visual?.SetContent(null);
            _ = Marshal.ReleaseComObject(_surface);
            _surface = null;
        }

        if (_device == null || _visual == null)
            return false;

        _surfaceW = clientWidth;
        _surfaceH = clientHeight;

        // 建表面：B8G8R8A8_UNORM + ALPHA_MODE_IGNORE，尺寸 = 客户区（规格 B.2 的格式要求 +
        // WP1-B 实测的 alpha 模式修正：GDI blit 会把预乘表面的 alpha 字节清零导致内容整体透明，
        // 走 GDI CPU 路径必须配 IGNORE（不透明合成）；PREMULTIPLIED 适用于 D2D/预乘内容路径）。
        // 壁纸为整面不透明内容，IGNORE 不损失任何显示语义。
        var hr = _device.CreateSurface(
            (uint) clientWidth, (uint) clientHeight,
            DcompNativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM,
            DcompNativeMethods.DXGI_ALPHA_MODE_IGNORE,
            out _surface);

        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"CreateSurface 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            HandleDeviceState(hr, "CreateSurface");
            _surface = null;
            return false;
        }

        // 内容关联视觉（SetContent 槽位无重载歧义）
        hr = _visual.SetContent(_surface);
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"SetContent 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            HandleDeviceState(hr, "SetContent");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把快照像素缩放贴入表面（BeginDraw → IDXGISurface1::GetDC → StretchDIBits → ReleaseDC → EndDraw）。
    /// DComp 表面不支持 IDXGISurface::Map 的 CPU 直写（WP1-B 探针实测 E_INVALIDARG），
    /// 官方 CPU 路径即 GDI DC：32bpp BI_RGB 的 DIB 数据与 PBgra8 快照逐字节同布局（B/G/R/A 顺序），
    /// 缩放由 GDI HALFTONE 完成。alpha 字节随 DIB 逐行复制进不透明表面。
    /// 配对纪律：BeginDraw 成功后无论内层成败都必须 EndDraw；updateObject 的原始引用与 RCW 各自释放。
    /// </summary>
    private bool UploadPixels(BgraSnapshot snapshot)
    {
        if (_surface == null)
            return false;

        // 显式传入整面更新区（NULL 更新区实测会返回 (1,1) 的带边距偏移，坐标语义含糊）
        var updateRect = Marshal.AllocHGlobal(Marshal.SizeOf<DcompNativeMethods.RECT>());
        try
        {
            Marshal.StructureToPtr(
                new DcompNativeMethods.RECT { Left = 0, Top = 0, Right = _surfaceW, Bottom = _surfaceH },
                updateRect, false);

            var hr = _surface.BeginDraw(
                updateRect, in DcompNativeMethods.IID_IDXGISurface, out var updateObject, out var offset);
            if (hr != DcompNativeMethods.S_OK)
            {
                HostDiagnostics.Log($"BeginDraw 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
                HandleDeviceState(hr, "BeginDraw");
                return false;
            }

            var ok = false;
            DcompNativeMethods.IDXGISurface1? dxgiSurface = null;
            try
            {
                dxgiSurface = Marshal.GetObjectForIUnknown(updateObject) as DcompNativeMethods.IDXGISurface1;
                if (dxgiSurface == null)
                {
                    HostDiagnostics.Log("BeginDraw 返回对象不支持 IDXGISurface1（GetDC 路径不可用）");
                    return false;
                }

                hr = dxgiSurface.GetDC(0, out var hdc);
                if (hr != DcompNativeMethods.S_OK)
                {
                    HostDiagnostics.Log($"GetDC 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
                    HandleDeviceState(hr, "GetDC");
                    return false;
                }

                try
                {
                    // HALFTONE 缩放模式（质量优先；失败回退默认模式继续贴）
                    _ = SetStretchBltMode(hdc, StretchBltModeHalftone);

                    // 快照（snapshot 尺寸）→ 表面（客户区尺寸）整面缩放贴入，起点 = BeginDraw 返回的更新区偏移
                    var bmi = BuildTopDownBitmapInfo(snapshot.Width, snapshot.Height);
                    _ = StretchDIBits(
                        hdc, offset.X, offset.Y, _surfaceW, _surfaceH,
                        0, 0, snapshot.Width, snapshot.Height, snapshot.Pixels,
                        ref bmi, 0 /* DIB_RGB_COLORS */, RopSrcCopy);
                    ok = true;
                }
                finally
                {
                    hr = dxgiSurface.ReleaseDC(IntPtr.Zero);
                    if (hr != DcompNativeMethods.S_OK)
                        HandleDeviceState(hr, "ReleaseDC");
                }
            }
            finally
            {
                if (dxgiSurface != null)
                    _ = Marshal.ReleaseComObject(dxgiSurface);

                Marshal.Release(updateObject); // BeginDraw 输出的原始接口引用

                // EndDraw 与 BeginDraw 严格配对（内层失败也要调用，否则表面锁死）
                hr = _surface.EndDraw();
                if (hr != DcompNativeMethods.S_OK)
                {
                    HostDiagnostics.Log($"EndDraw 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
                    HandleDeviceState(hr, "EndDraw");
                    ok = false;
                }
            }

            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(updateRect);
        }
    }

    /// <summary>构造 top-down 32bpp BI_RGB 的 BITMAPINFO（负高 = 自上而下，与快照行序一致）。</summary>
    private static DcompNativeMethods.BITMAPINFO BuildTopDownBitmapInfo(int width, int height)
    {
        return new DcompNativeMethods.BITMAPINFO
        {
            Header = new DcompNativeMethods.BITMAPINFOHEADER
            {
                BiSize = (uint) Marshal.SizeOf<DcompNativeMethods.BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -height, // 负高 = top-down：DIB 第一行就是位图顶行
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0, // BI_RGB
            },
        };
    }

    /// <summary>提交合成命令。返回 false = 失败（已记日志 + 失陷处理）。</summary>
    private bool Commit()
    {
        if (_device == null)
            return false;

        var hr = _device.Commit();
        if (hr != DcompNativeMethods.S_OK)
        {
            HostDiagnostics.Log($"Commit 失败 HRESULT=0x{hr:X8} host=0x{_hostHwnd:X}");
            HandleDeviceState(hr, "Commit");
            return false;
        }

        HostDiagnostics.Log($"DComp 图像上屏 host=0x{_hostHwnd:X} surface={_surfaceW}x{_surfaceH}");
        return true;
    }

    /// <summary>设备失陷检测（规格 B.5）：命中 DXGI_ERROR_DEVICE_REMOVED 则整树释放，下次调用自动重建。</summary>
    private void HandleDeviceState(int hr, string op)
    {
        if (hr != DcompNativeMethods.DXGI_ERROR_DEVICE_REMOVED)
            return;

        HostDiagnostics.Log($"DComp 设备失陷（{op} 返回 DXGI_ERROR_DEVICE_REMOVED），整树释放待重建 host=0x{_hostHwnd:X}");
        TearDownTree();
        _deviceLost = true;
    }

    /// <summary>释放整棵合成树（COM 引用全部清空；幂等）。设备原始指针一并释放。</summary>
    private void TearDownTree()
    {
        if (_visual != null)
        {
            _ = _visual.SetContent(null);
            _ = Marshal.ReleaseComObject(_visual);
            _visual = null;
        }

        if (_surface != null)
        {
            _ = Marshal.ReleaseComObject(_surface);
            _surface = null;
        }

        if (_target != null)
        {
            _ = Marshal.ReleaseComObject(_target);
            _target = null;
        }

        if (_device != null)
        {
            _ = Marshal.ReleaseComObject(_device);
            _device = null;
        }

        ReleaseDeviceRaw();
        _surfaceW = 0;
        _surfaceH = 0;
    }

    /// <summary>释放 D3D11 / DXGI 原始设备指针（QI / 创建时取得的引用计数）。</summary>
    private void ReleaseDeviceRaw()
    {
        if (_dxgiDevice != IntPtr.Zero)
        {
            Marshal.Release(_dxgiDevice);
            _dxgiDevice = IntPtr.Zero;
        }

        if (_d3dDevice != IntPtr.Zero)
        {
            Marshal.Release(_d3dDevice);
            _d3dDevice = IntPtr.Zero;
        }
    }

    // ===== GDI P/Invoke（渲染器专用；注释规格同 HostNativeMethods）=====

    /// <summary>
    /// 设置指定 DC 的位图拉伸模式（HALFTONE = 4，缩放质量最好；GDI 对 1bpp DC 默认 BLACKONWHITE）。
    /// </summary>
    /// <param name="hdc">目标 DC。</param>
    /// <param name="mode">拉伸模式。</param>
    /// <returns>旧模式；0 = 失败。</returns>
    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    /// <summary>
    /// 把 DIB 位图缩放复制到 DC（快照 → 表面的缩放落点；32bpp BI_RGB 数据与 PBgra8 逐字节同布局）。
    /// </summary>
    /// <param name="hdc">目标 DC（DComp 表面经 GetDC 取得）。</param>
    /// <param name="xDst">目标左上 x（= BeginDraw 的更新区偏移）。</param>
    /// <param name="yDst">目标左上 y。</param>
    /// <param name="destWidth">目标宽（表面宽）。</param>
    /// <param name="destHeight">目标高（表面高）。</param>
    /// <param name="xSrc">源 x（0）。</param>
    /// <param name="ySrc">源 y（0）。</param>
    /// <param name="srcWidth">源宽（快照宽）。</param>
    /// <param name="srcHeight">源高（快照高）。</param>
    /// <param name="bits">DIB 像素字节（紧密排列 PBgra8）。</param>
    /// <param name="bmi">BITMAPINFO（top-down 32bpp）。</param>
    /// <param name="colorUse">DIB_RGB_COLORS(0)。</param>
    /// <param name="rop">SRCCOPY(0x00CC0020)。</param>
    /// <returns>复制的扫描行数；0 = 失败。</returns>
    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(
        IntPtr hdc, int xDst, int yDst, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight,
        byte[] bits, ref DcompNativeMethods.BITMAPINFO bmi, uint colorUse, uint rop);
}
