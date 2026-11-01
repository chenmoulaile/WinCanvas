using System;
using System.Runtime.InteropServices;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 壁纸宿主 DComp 渲染专用的原生封装（d3d11.dll / dcomp.dll 的导出函数 + 手写 COM 接口声明）。
/// 管线（规格 WP1-B 主路径）：D3D11CreateDevice(BGRA_SUPPORT) → QueryInterface(IID_IDXGIDevice)
/// → DCompositionCreateDevice → CreateTargetForHwnd(host) → CreateVisual → CreateSurface(B8G8R8A8/PREMULTIPLIED)
/// → BeginDraw 取 IDXGISurface → CPU 按行拷贝像素 → EndDraw → Commit 上屏。
///
/// COM 接口槽位依据：成员顺序逐一对照真实 Windows SDK 头文件核对
/// （winsdk-10 镜像 10.0.16299.0 的 um/dcomp.h 与 shared/dxgi.h，WORKLOG 阶段 B 决策 1），
/// 注意网上流传的 Wine dcomp.idl 顺序与其自有实现一致、与微软头文件相反，不可作为依据。
/// 每个接口只声明到「本程序会调用的最后一个成员」为止——尾部省略不影响已声明成员的槽位。
/// 全部方法用 [PreserveSig] int 返回 HRESULT：按 COM 语义自行判错（含 DXGI_ERROR_DEVICE_REMOVED 检测），
/// 不经 .NET 的异常封送。
/// </summary>
internal static class DcompNativeMethods
{
    // ===== 接口 IID（与 SDK 头文件 DECLARE_INTERFACE_IID_ 逐一对应）=====

    /// <summary>IID_IDXGIDevice：QI 自 ID3D11Device，交给 DCompositionCreateDevice。</summary>
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>IID_IDXGISurface：BeginDraw 请求的 CPU 可 Map 表面（预乘 BGRA 逐字节拷贝落点）。</summary>
    public static readonly Guid IID_IDXGISurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

    /// <summary>IID_IDCompositionDevice（dcomp.h: C37EA93A-E7AA-450D-B16F-9746CB0407F3）。</summary>
    public static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");

    /// <summary>IID_IDCompositionTarget（dcomp.h: eacdd04c-117e-4e17-88f4-d1b12b0e3d89）。</summary>
    public static readonly Guid IID_IDCompositionTarget = new("eacdd04c-117e-4e17-88f4-d1b12b0e3d89");

    /// <summary>IID_IDCompositionVisual（dcomp.h: 4d93059d-097b-4651-9a60-f0f25116e2f3）。</summary>
    public static readonly Guid IID_IDCompositionVisual = new("4d93059d-097b-4651-9a60-f0f25116e2f3");

    /// <summary>IID_IDCompositionSurface（dcomp.h: BB8A4953-2C99-4F5A-96F5-4819027FA3AC）。</summary>
    public static readonly Guid IID_IDCompositionSurface = new("BB8A4953-2C99-4F5A-96F5-4819027FA3AC");
    // ===== 常量（d3d11.h / dxgi.h / d3dcommon.h 原值）=====

    /// <summary>D3D_DRIVER_TYPE_HARDWARE(1)：硬件驱动（配 pAdapter=NULL 时由 D3D 自选默认适配器）。</summary>
    public const uint D3D_DRIVER_TYPE_HARDWARE = 1;

    /// <summary>D3D11_CREATE_DEVICE_BGRA_SUPPORT(0x20)：设备支持 BGRA 格式——DComp surface 用 B8G8R8A8 的前提（规格要求）。</summary>
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    /// <summary>D3D11_SDK_VERSION(7)：D3D11CreateDevice 的 SDKVersion 固定值。</summary>
    public const uint D3D11_SDK_VERSION = 7;

    /// <summary>D3D_FEATURE_LEVEL_11_1(0xb100)：特性级别请求（11_1 → 10_0 逐级回退）。</summary>
    public const uint D3D_FEATURE_LEVEL_11_1 = 0xb100;

    /// <summary>D3D_FEATURE_LEVEL_11_0(0xb000)。</summary>
    public const uint D3D_FEATURE_LEVEL_11_0 = 0xb000;

    /// <summary>D3D_FEATURE_LEVEL_10_1(0xa100)。</summary>
    public const uint D3D_FEATURE_LEVEL_10_1 = 0xa100;

    /// <summary>D3D_FEATURE_LEVEL_10_0(0xa000)。</summary>
    public const uint D3D_FEATURE_LEVEL_10_0 = 0xa000;

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM(87)：预乘 BGRA 8bit 格式，与 BgraSnapshot(PBgra8) 逐字节同布局。</summary>
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    /// <summary>DXGI_ALPHA_MODE_PREMULTIPLIED(1)：预乘 alpha——SoftwareBitmap(Bgra8+Premultiplied) 的直配模式。</summary>
    public const uint DXGI_ALPHA_MODE_PREMULTIPLIED = 1;

    /// <summary>DXGI_ALPHA_MODE_IGNORE(3)：忽略 alpha（表面按不透明合成）。GDI 路径用：
    /// GDI 的 32bpp blit 不感知 alpha（会把第 4 字节清零），预乘表面配 GDI 会整体透明。</summary>
    public const uint DXGI_ALPHA_MODE_IGNORE = 3;
    /// <summary>DXGI_MAP_WRITE(2)：IDXGISurface::Map 的 CPU 写标志（像素拷入用）。</summary>
    public const uint DXGI_MAP_WRITE = 2;

    /// <summary>DXGI_ERROR_DEVICE_REMOVED(0x887A0005)：GPU 设备失陷（驱动重置 / TDR）。
    /// 任何 DComp/DXGI 调用返回此码时须重建整棵 DComp 树（规格 B.5），壁纸会话不销毁。</summary>
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int) 0x887A0005);

    /// <summary>S_OK(0)：COM 成功。</summary>
    public const int S_OK = 0;

    /// <summary>
    /// D2D_MATRIX_3X2_F：2D 仿射变换矩阵（行主序）。visual.SetTransform(matrix&) 用它把
    /// surface（=快照尺寸）按比例缩放到宿主客户区尺寸。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D2D_MATRIX_3X2_F
    {
        public float _11; // m11：x 轴缩放
        public float _12; // m12
        public float _21; // m21
        public float _22; // m22：y 轴缩放
        public float _31; // dx：x 平移
        public float _32; // dy：y 平移
    }

    /// <summary>
    /// RECT（与 HostNativeMethods.RECT 同布局；DComp 独立声明避免跨文件耦合，BeginDraw 的更新区参数）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// POINT（BeginDraw 输出的更新区左上偏移；全表面更新时恒 (0,0)，拷贝地址需按它定位）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// DXGI_MAPPED_RECT：IDXGISurface::Map 的输出（CPU 可见内存与行距）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_MAPPED_RECT
    {
        public int Pitch;     // 行距（字节，可能含对齐填充；拷贝必须按行距逐行进行）
        public IntPtr pBits;  // 表面内存首地址
    }

    // ===== DLL 导出 =====

    /// <summary>
    /// 创建 D3D11 设备（d3d11.dll 导出）。规格 B.1：BGRA_SUPPORT 标志必带，输出设备经 QI 取 IDXGIDevice。
    /// </summary>
    /// <param name="pAdapter">适配器（NULL = 默认适配器，配 D3D_DRIVER_TYPE_HARDWARE）。</param>
    /// <param name="driverType">驱动类型（D3D_DRIVER_TYPE_HARDWARE = 1）。</param>
    /// <param name="software">软件光栅模块句柄（不用，NULL）。</param>
    /// <param name="flags">设备标志（D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20）。</param>
    /// <param name="pFeatureLevels">特性级别数组（11_1 → 10_0 逐级回退；NULL 时系统自选）。</param>
    /// <param name="featureLevels">数组长度。</param>
    /// <param name="sdkVersion">SDK 版本（D3D11_SDK_VERSION = 7）。</param>
    /// <param name="ppDevice">输出：ID3D11Device*（本实现只做 QI，不直接调用其方法）。</param>
    /// <param name="pFeatureLevel">输出：实际生效的特性级别（可 NULL）。</param>
    /// <param name="pImmediateContext">输出：立即上下文（本实现不需要，传 NULL）。</param>
    /// <returns>S_OK 成功；DXGI_ERROR_UNSUPPORTED 等失败（无 GPU / 驱动不支持时调用方走回退路径）。</returns>
    [DllImport("d3d11.dll", SetLastError = false)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint driverType, IntPtr software, uint flags,
        uint[]? pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr pImmediateContext);

    /// <summary>
    /// 创建 DirectComposition 设备（dcomp.dll 导出）。输入 DXGI 设备（自 ID3D11Device QI 而来），
    /// 输出 IDCompositionDevice（vtable 见 <see cref="IDCompositionDevice"/>）。
    /// </summary>
    /// <param name="dxgiDevice">IDXGIDevice*（QI 得到的原始接口指针，所有权仍归调用方，本函数只借用）。</param>
    /// <param name="riid">请求的设备接口 Id（IID_IDCompositionDevice）。</param>
    /// <param name="dcompDevice">输出：IDCompositionDevice*（用毕 Release）。</param>
    /// <returns>S_OK 成功；失败时 dcompDevice 为 NULL（GetLastError 不适用，HRESULT 即错误信息）。</returns>
    [DllImport("dcomp.dll", SetLastError = false)]
    public static extern int DCompositionCreateDevice(IntPtr dxgiDevice, in Guid riid, out IntPtr dcompDevice);
    // ===== COM 接口声明（成员顺序 = 真实 SDK 头文件声明顺序 = vtable 槽位；仅声明到最后一处调用）=====

    /// <summary>
    /// IDXGISurface（dxgi.h）。继承链槽位：IUnknown(3) + IDXGIObject(4) + IDXGIDeviceSubObject(1)
    /// + IDXGISurface{GetDesc, Map, Unmap}。Map 在 vtable 第 10 槽（0 起第 9）、Unmap 第 11 槽。
    /// 未调用的前置成员（SetPrivateData 等）按序声明为占位以保持槽位，绝不调用。
    /// </summary>
    [ComImport]
    [Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISurface
    {
        /// <summary>占位：IDXGIObject.SetPrivateData（不调用）。</summary>
        [PreserveSig]
        int SetPrivateData();

        /// <summary>占位：IDXGIObject.SetPrivateDataInterface（不调用）。</summary>
        [PreserveSig]
        int SetPrivateDataInterface();

        /// <summary>占位：IDXGIObject.GetPrivateData（不调用）。</summary>
        [PreserveSig]
        int GetPrivateData();

        /// <summary>占位：IDXGIObject.GetParent（不调用）。</summary>
        [PreserveSig]
        int GetParent();

        /// <summary>占位：IDXGIDeviceSubObject.GetDevice（不调用）。</summary>
        [PreserveSig]
        int GetDevice();

        /// <summary>占位：IDXGISurface.GetDesc（不调用：表面尺寸由 CreateSurface 参数自记）。</summary>
        [PreserveSig]
        int GetDesc();

        /// <summary>
        /// 映射表面内存到进程地址空间（CPU 写入像素的落点）。Map 后必须配对 Unmap。
        /// </summary>
        /// <param name="mappedRect">输出：内存首址与行距（行距可能含对齐填充，逐行拷贝必须用它）。</param>
        /// <param name="mapFlags">DXGI_MAP_READ|DXGI_MAP_WRITE。</param>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷；E_OUTOFMEMORY 等。</returns>
        [PreserveSig]
        int Map(out DXGI_MAPPED_RECT mappedRect, uint mapFlags);

        /// <summary>解除 Map 的映射（与 Map 严格配对；未配对会泄漏锁定）。</summary>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷。</returns>
        [PreserveSig]
        int Unmap();
    }

    /// <summary>
    /// IDCompositionDevice（dcomp.h，v1，IID C37EA93A-…）。真实成员顺序（注意：Commit 在最前，
    /// CreateTargetForHwnd 无 REFIID 参数——与常见错误记忆相反，已逐行核对 SDK 头文件）：
    /// Commit, WaitForCommitCompletion, GetFrameStatistics, CreateTargetForHwnd, CreateVisual,
    /// CreateSurface, …（其后成员本实现不调用，省略声明不影响前述槽位）。
    /// </summary>
    [ComImport]
    [Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDCompositionDevice
    {
        /// <summary>提交本设备全部待处理的 DirectComposition 命令（原子生效到 DWM）。</summary>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷（需重建整棵树）。</returns>
        [PreserveSig]
        int Commit();

        /// <summary>占位：WaitForCommitCompletion（同步等待上次 Commit 被 DWM 处理；本实现用事件驱动不等待）。</summary>
        [PreserveSig]
        int WaitForCommitCompletion();

        /// <summary>占位：GetFrameStatistics（不调用）。</summary>
        [PreserveSig]
        int GetFrameStatistics();

        /// <summary>
        /// 创建绑定到窗口的合成目标（规格 B.2：DCompositionCreateTargetForHwnd(host, TRUE)）。
        /// </summary>
        /// <param name="hwnd">宿主窗口句柄（跨进程父子关系不影响：宿主属本进程）。</param>
        /// <param name="topmost">TRUE = 视觉树合成在窗口自身内容之上（覆盖 GDI 纯色占位）。</param>
        /// <param name="target">输出：IDCompositionTarget（用毕 Release）。</param>
        /// <returns>S_OK 成功；E_INVALIDARG 参数无效；DCOMPOSITION_ERROR_WINDOW_ALREADY_BOUND 目标已被绑定。</returns>
        [PreserveSig]
        int CreateTargetForHwnd(IntPtr hwnd, int topmost, out IDCompositionTarget target);

        /// <summary>创建视觉对象（树的根；内容与变换见 IDCompositionVisual）。</summary>
        /// <param name="visual">输出：IDCompositionVisual（用毕 Release）。</param>
        /// <returns>S_OK 成功；E_OUTOFMEMORY。</returns>
        [PreserveSig]
        int CreateVisual(out IDCompositionVisual visual);

        /// <summary>
        /// 创建 CPU 可更新的合成表面（规格 B.2：B8G8R8A8_UNORM + ALPHA_MODE_PREMULTIPLIED）。
        /// </summary>
        /// <param name="width">宽（像素，= 快照宽）。</param>
        /// <param name="height">高（像素，= 快照高）。</param>
        /// <param name="pixelFormat">DXGI_FORMAT_B8G8R8A8_UNORM(87)。</param>
        /// <param name="alphaMode">DXGI_ALPHA_MODE_PREMULTIPLIED(1)。</param>
        /// <param name="surface">输出：IDCompositionSurface（用毕 Release）。</param>
        /// <returns>S_OK 成功；E_OUTOFMEMORY；DXGI_ERROR_DEVICE_REMOVED。</returns>
        [PreserveSig]
        int CreateSurface(uint width, uint height, uint pixelFormat, uint alphaMode, out IDCompositionSurface surface);
    }

    /// <summary>
    /// IDCompositionTarget（dcomp.h）：设备与窗口的绑定，唯一成员 SetRoot。
    /// </summary>
    [ComImport]
    [Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDCompositionTarget
    {
        /// <summary>设置合成树的根视觉（传入 null 可清空绑定内容）。</summary>
        /// <param name="visual">根视觉（null = 清空）。</param>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷。</returns>
        [PreserveSig]
        int SetRoot(IDCompositionVisual? visual);
    }

    /// <summary>
    /// IDCompositionVisual（dcomp.h）。前 14 个成员是若干 C++ 重载对（占位，本实现不调用），
    /// SetContent 在第 13 个成员槽（vtable 槽 15）——其槽位在重载对的任意相对顺序下一致，无歧义。
    /// 注意：SetTransform 两个重载在真实 ABI 中的相对顺序与 dcomp.h 的 C++ 声明相反（WP1-B 探针
    /// 实测，见 WORKLOG 决策 2），本渲染器不调用 SetTransform（缩放走 GDI StretchDIBits），
    /// 两个重载仅作槽位占位声明（成员总数决定 SetContent 槽位，与重载内部顺序无关）。
    /// </summary>
    [ComImport]
    [Guid("4d93059d-097b-4651-9a60-f0f25116e2f3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDCompositionVisual
    {
        /// <summary>槽 3：SetOffsetX(float)（占位）。</summary>
        [PreserveSig]
        int SetOffsetX(float offsetX);

        /// <summary>槽 4：SetOffsetX(IDCompositionAnimation*)（占位；C# 重载签名天然区分）。</summary>
        [PreserveSig]
        int SetOffsetX(IntPtr animation);

        /// <summary>槽 5：SetOffsetY(float)（占位）。</summary>
        [PreserveSig]
        int SetOffsetY(float offsetY);

        /// <summary>槽 6：SetOffsetY(IDCompositionAnimation*)（占位）。</summary>
        [PreserveSig]
        int SetOffsetY(IntPtr animation);

        /// <summary>槽 7：SetTransform 重载之一（占位；真实 ABI 相对顺序与头文件相反，本实现不调用）。</summary>
        [PreserveSig]
        int SetTransform(IntPtr transform);

        /// <summary>槽 8：SetTransform 重载之二（占位；本实现不调用）。</summary>
        [PreserveSig]
        int SetTransform(in D2D_MATRIX_3X2_F matrix);

        /// <summary>槽 9：SetTransformParent(IDCompositionVisual*)（占位）。</summary>
        [PreserveSig]
        int SetTransformParent(IntPtr visual);

        /// <summary>槽 10：SetEffect(IDCompositionEffect*)（占位）。</summary>
        [PreserveSig]
        int SetEffect(IntPtr effect);

        /// <summary>槽 11：SetBitmapInterpolationMode（占位；本实现不调用——无 visual 级缩放）。</summary>
        [PreserveSig]
        int SetBitmapInterpolationMode(uint interpolationMode);

        /// <summary>槽 12：SetBorderMode（占位）。</summary>
        [PreserveSig]
        int SetBorderMode(uint borderMode);

        /// <summary>槽 13：SetClip(const D2D_RECT_F&amp;)（占位；RECT 与 D2D_RECT_F 同布局）。</summary>
        [PreserveSig]
        int SetClip(in RECT rect);

        /// <summary>槽 14：SetClip(IDCompositionClip*)（占位）。</summary>
        [PreserveSig]
        int SetClip(IntPtr clip);

        /// <summary>
        /// 槽 15：SetContent——把内容对象关联到视觉（本渲染树的核心关联点）。
        /// 官方接受三类内容（dcomp.h SetContent 文档原文）：IDCompositionSurface 实现、
        /// <b>IDXGISwapChain1 实现（视频帧上屏的承载，阶段 C）</b>、CreateSurfaceFromHandle/Hwnd 包装。
        /// 参数必须显式按 IUnknown 封送：裸 object 在 ComImport 里默认走 VARIANT 封送，
        /// 传 IUnknown 指针预期会 AccessViolation（WP1-C 探针实测）。
        /// </summary>
        /// <param name="content">内容对象（IDCompositionSurface / IDXGISwapChain1 的 RCW；null = 清除）。</param>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷。</returns>
        [PreserveSig]
        int SetContent([MarshalAs(UnmanagedType.IUnknown)] object? content);
    }

    /// <summary>
    /// IDXGISurface1（dxgi.h）：在 IDXGISurface 之上增加 GDI 互操作（GetDC / ReleaseDC）。
    /// DComp surface 的 BeginDraw 更新对象经 QI(IID_IDXGISurface1) 取得后，用 GetDC 拿 GDI DC
    /// 贴 DIB（DComp 表面不支持 IDXGISurface::Map 的 CPU 直写，WP1-B 探针实测 E_INVALIDARG）。
    /// 槽位：IUnknown(3) + IDXGIObject(4) + GetDevice(1) + GetDesc/Map/Unmap(3) + GetDC/ReleaseDC(2)。
    /// </summary>
    [ComImport]
    [Guid("4ae63092-6327-4c1b-80ae-bfe12ea32b86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISurface1
    {
        /// <summary>占位：IDXGIObject.SetPrivateData（不调用）。</summary>
        [PreserveSig]
        int SetPrivateData();

        /// <summary>占位：IDXGIObject.SetPrivateDataInterface（不调用）。</summary>
        [PreserveSig]
        int SetPrivateDataInterface();

        /// <summary>占位：IDXGIObject.GetPrivateData（不调用）。</summary>
        [PreserveSig]
        int GetPrivateData();

        /// <summary>占位：IDXGIObject.GetParent（不调用）。</summary>
        [PreserveSig]
        int GetParent();

        /// <summary>占位：IDXGIDeviceSubObject.GetDevice（不调用）。</summary>
        [PreserveSig]
        int GetDevice();

        /// <summary>占位：IDXGISurface.GetDesc（不调用）。</summary>
        [PreserveSig]
        int GetDesc();

        /// <summary>占位：IDXGISurface.Map（不调用：DComp 表面实测拒绝 CPU Map）。</summary>
        [PreserveSig]
        int Map();

        /// <summary>占位：IDXGISurface.Unmap（不调用）。</summary>
        [PreserveSig]
        int Unmap();

        /// <summary>
        /// 取 GDI 设备上下文（DComp surface 的 GDI 绘制入口）。用毕必须配对 ReleaseDC。
        /// </summary>
        /// <param name="discard">TRUE = 调用方不在乎 DC 现有内容（可破坏），本实现传 FALSE(0)。</param>
        /// <param name="hdc">输出：GDI DC（仅在下一次 ReleaseDC 前有效）。</param>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷；E_FAIL 表面不支持 GDI 访问。</returns>
        [PreserveSig]
        int GetDC(int discard, out IntPtr hdc);

        /// <summary>释放 GetDC 取得的 DC，使改动对后续 EndDraw 生效。</summary>
        /// <param name="dirtyRect">脏区矩形（NULL = 全部）。</param>
        /// <returns>S_OK 成功；DXGI_ERROR_DEVICE_REMOVED 设备失陷。</returns>
        [PreserveSig]
        int ReleaseDC(IntPtr dirtyRect);
    }

    /// <summary>
    /// BITMAPINFOHEADER：GDI 位图信息头（StretchDIBits 输入；top-down 32bpp BI_RGB 与
    /// BgraSnapshot 紧密排列逐字节同布局）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint BiSize;            // 结构体大小（调用方必须先填）
        public int BiWidth;            // 宽（像素）
        public int BiHeight;           // 高（像素；负值 = top-down，第一行是位图顶行）
        public ushort BiPlanes;        // 恒 1
        public ushort BiBitCount;      // 每像素位数（32）
        public uint BiCompression;     // 压缩方式（0 = BI_RGB 不压缩）
        public uint BiSizeImage;       // 图像字节数（BI_RGB 可为 0）
        public int BiXPelsPerMeter;    // 水平分辨率（不用，0）
        public int BiYPelsPerMeter;    // 垂直分辨率（不用，0）
        public uint BiClrUsed;         // 调色板颜色数（不用，0）
        public uint BiClrImportant;    // 重要颜色数（不用，0）
    }

    /// <summary>
    /// BITMAPINFO：StretchDIBits 的位图信息（本实现只用 header；32bpp BI_RGB 无调色板）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header; // 信息头
    }


    /// <summary>
    /// IDCompositionSurface（dcomp.h）：CPU 可更新的合成表面。BeginDraw/EndDraw 是前两个成员（槽 3/4），
    /// 其后成员（SuspendDraw 等）不调用故省略。
    /// </summary>
    // （注：视频帧路径不再使用本接口逐帧 BeginDraw——实测逐帧 BeginDraw/EndDraw 会稳定返回
    // E_OUTOFMEMORY（DComp 表面并非为视频级更新频率设计），视频走 IDXGISwapChain1 组合交换链，
    // 见 WallpaperHostRenderer 的视频段。）
    [ComImport]
    [Guid("BB8A4953-2C99-4F5A-96F5-4819027FA3AC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDCompositionSurface
    {
        /// <summary>
        /// 开始绘制：锁定更新区并取出可绘制对象。iid 请求 IID_IDXGISurface 得到 CPU 可 Map 的表面
        /// （另一官方选项 IID_ID2D1DeviceContext 需 D2D 绘制，本实现走零依赖的 CPU 拷贝路线）。
        /// 必须配对 EndDraw；更新期间表面不可被 Commit 引用渲染。
        /// updateRect 用裸指针传递（可空结构体无法作为 COM 参数封送——WP1-B 实测 MarshalDirectiveException）。
        /// </summary>
        /// <param name="updateRect">RECT* 更新区（NULL = 整面；首绘即整面）。</param>
        /// <param name="iid">请求的接口 Id（IID_IDXGISurface）。</param>
        /// <param name="updateObject">输出：所请求接口的指针（用毕 Release）。</param>
        /// <param name="updateOffset">输出：更新区在表面坐标中的左上偏移（拷贝地址按它定位）。</param>
        /// <returns>S_OK 成功；E_INVALIDARG 表面尺寸超限；DXGI_ERROR_DEVICE_REMOVED 设备失陷。</returns>
        [PreserveSig]
        int BeginDraw(IntPtr updateRect, in Guid iid, out IntPtr updateObject, out POINT updateOffset);

        /// <summary>结束绘制：解锁更新区并使改动对后续 Commit 生效。</summary>
        /// <returns>S_OK 成功；E_INVALIDARG 无在途 BeginDraw；DXGI_ERROR_DEVICE_REMOVED 设备失陷。</returns>
        [PreserveSig]
        int EndDraw();
    }

    /// <summary>
    /// IDXGISwapChain1（dxgi1_2.h）：组合交换链接口（视频帧上屏的内容承载——
    /// IDCompositionVisual::SetContent 官方接受 IDXGISwapChain1 实现）。
    /// 本切片只作为 CreateSwapChainForComposition 的出参类型；Present / GetBuffer 经
    /// <see cref="IDXGISwapChain"/> 基切片调用（槽位与派生接口一致）。
    /// 槽位：IUnknown(3) + IDXGIObject(4) + GetDevice(1) + IDXGISwapChain(10) + SwapChain1 专属 11 个（省略）。
    /// </summary>
    [ComImport]
    [Guid("790a45f7-0d42-4876-983a-0a55cfe6f4aa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISwapChain1
    {
        /// <summary>槽 3：IDXGIObject.SetPrivateData（占位，不调用）。</summary>
        [PreserveSig]
        int SetPrivateData();

        /// <summary>槽 4：IDXGIObject.SetPrivateDataInterface（占位，不调用）。</summary>
        [PreserveSig]
        int SetPrivateDataInterface();

        /// <summary>槽 5：IDXGIObject.GetPrivateData（占位，不调用）。</summary>
        [PreserveSig]
        int GetPrivateData();

        /// <summary>槽 6：IDXGIObject.GetParent（占位，不调用）。</summary>
        [PreserveSig]
        int GetParent();

        /// <summary>槽 7：IDXGIDeviceSubObject.GetDevice（占位，不调用）。</summary>
        [PreserveSig]
        int GetDevice();

        /// <summary>槽 8：IDXGISwapChain.Present（占位——帧呈现经 <see cref="IDXGISwapChain"/> 基切片调用）。</summary>
        [PreserveSig]
        int Present(uint syncInterval, uint flags);

        /// <summary>槽 9：IDXGISwapChain.GetBuffer（占位——经基切片调用）。</summary>
        [PreserveSig]
        int GetBuffer(uint bufferIndex, in Guid riid, out IntPtr surface);

        /// <summary>槽 10：GetDesc（占位，不调用）。</summary>
        [PreserveSig]
        int GetDesc();

        /// <summary>槽 11：GetFrameStatistics（占位，不调用）。</summary>
        [PreserveSig]
        int GetFrameStatistics();

        /// <summary>槽 12：GetLastPresentCount（占位，不调用）。</summary>
        [PreserveSig]
        int GetLastPresentCount();

        /// <summary>槽 13：GetFullscreenState（占位，不调用）。</summary>
        [PreserveSig]
        int GetFullscreenState();

        /// <summary>槽 14：SetFullscreenState（占位，不调用）。</summary>
        [PreserveSig]
        int SetFullscreenState();

        /// <summary>槽 15：ResizeBuffers（占位，不调用——尺寸变化走整链重建）。</summary>
        [PreserveSig]
        int ResizeBuffers();

        /// <summary>槽 16：ResizeTarget（占位，不调用）。</summary>
        [PreserveSig]
        int ResizeTarget();

        /// <summary>槽 17：GetContainingOutput（占位，不调用）。</summary>
        [PreserveSig]
        int GetContainingOutput();

        /// <summary>槽 18：GetFrameStatistics1（占位，不调用）。</summary>
        [PreserveSig]
        int GetDesc1();

        /// <summary>槽 19：GetFullscreenDesc（占位，不调用）。</summary>
        [PreserveSig]
        int GetFullscreenDesc();

        /// <summary>槽 20：GetHwnd（占位，不调用）。</summary>
        [PreserveSig]
        int GetHwnd();

        /// <summary>槽 21：GetCoreWindow（占位，不调用）。</summary>
        [PreserveSig]
        int GetCoreWindow();

        /// <summary>槽 22：Present1（占位，不调用）。</summary>
        [PreserveSig]
        int Present1();

        /// <summary>槽 23：IsTemporaryMonoSupported（占位，不调用）。</summary>
        [PreserveSig]
        int IsTemporaryMonoSupported();

        /// <summary>槽 24：GetRestrictToOutput（占位，不调用）。</summary>
        [PreserveSig]
        int GetRestrictToOutput();

        /// <summary>槽 25：SetBackgroundColor（占位，不调用）。</summary>
        [PreserveSig]
        int SetBackgroundColor();

        /// <summary>槽 26：GetBackgroundColor（占位，不调用）。</summary>
        [PreserveSig]
        int GetBackgroundColor();

        /// <summary>槽 27：SetRotation（占位，不调用）。</summary>
        [PreserveSig]
        int SetRotation();

        /// <summary>槽 28：GetRotation（占位，不调用）。</summary>
        [PreserveSig]
        int GetRotation();
    }
}
