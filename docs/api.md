﻿# API 说明

本文档全部接口与模型**从真实代码抄录**（来源文件在每节标注），不包含尚未实现的虚构成员。

## 1. Core 服务接口（src/WinCanvas.Core/Interfaces/）

### 1.1 IWindowControlService

窗口控制服务：枚举顶层窗口并对其执行置顶 / 透明 / 鼠标穿透 / 圆角等 Win32 操作。

```csharp
public interface IWindowControlService
{
    /// <summary>在后台线程枚举所有可见的顶层窗口。</summary>
    /// <returns>可见顶层窗口列表（含 hwnd / 标题 / 类名 / 进程名 / 进程 Id）。</returns>
    Task<IReadOnlyList<WindowInfo>> EnumerateTopLevelWindowsAsync(CancellationToken cancellationToken = default);

    /// <summary>切换指定窗口的置顶状态：已置顶则取消置顶，未置顶则置顶。</summary>
    /// <returns>切换后窗口是否处于置顶状态。</returns>
    Task<bool> ToggleTopmostAsync(IntPtr hwnd);

    /// <summary>读取窗口当前的实际置顶状态（WS_EX_TOPMOST 样式位）。</summary>
    Task<bool> GetIsTopmostAsync(IntPtr hwnd);

    /// <summary>将窗口置顶 / 取消置顶（幂等语义：按目标状态直接设置）。</summary>
    Task SetTopmostAsync(IntPtr hwnd, bool topmost);

    /// <summary>设置窗口透明度（通过 WS_EX_LAYERED + SetLayeredWindowAttributes）。</summary>
    /// <param name="alpha">透明度 0（全透明）~ 255（不透明）。</param>
    Task SetTransparencyAsync(IntPtr hwnd, byte alpha);

    /// <summary>启用 / 禁用鼠标穿透。启用时一并置位 WS_EX_LAYERED；关闭时只清 WS_EX_TRANSPARENT、保留 WS_EX_LAYERED（透明度仍依赖分层样式）。</summary>
    Task EnableClickThroughAsync(IntPtr hwnd, bool enable);

    /// <summary>
    /// 设置窗口圆角（DWMWA_WINDOW_CORNER_PREFERENCE）。
    /// true 使用小圆角（DWMWCP_ROUNDSMALL），false 使用标准大圆角（DWMWCP_ROUND）；
    /// 关闭圆角（DWMWCP_DONOTROUND）请使用 SetCornerPreferenceAsync。
    /// </summary>
    Task SetRoundedCornersAsync(IntPtr hwnd, bool enableSmallRadius = false);

    /// <summary>设置任意圆角偏好（含关闭圆角 DWMWCP_DONOTROUND）。</summary>
    Task SetCornerPreferenceAsync(IntPtr hwnd, CornerPreference preference);
}
```

### 1.2 IConfigurationService

配置服务：负责 `%APPDATA%/WinCanvas/config.json` 的读写，内存缓存 + 延迟防抖（500ms）批量写盘。

```csharp
public interface IConfigurationService
{
    /// <summary>从磁盘加载配置（若文件不存在返回默认配置）并缓存到内存。</summary>
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>用新配置覆盖内存缓存，并安排一次延迟写盘（多次调用自动合并，防抖批量落盘）。</summary>
    void Update(AppConfig config);

    /// <summary>立即将内存缓存写盘（应用退出前调用，确保不丢数据）。</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
```

### 1.3 IWallpaperService

壁纸 / 动态背景渲染服务。实现：`WinCanvas.Wallpaper.WallpaperService`（三层加载策略）。

```csharp
public interface IWallpaperService
{
    /// <summary>为指定窗口初始化壁纸渲染并加载背景资源。</summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    /// <param name="path">背景资源路径（图片 / 视频文件或 pkg 包路径）。</param>
    Task InitializeWallpaperAsync(IntPtr hwnd, string path);

    /// <summary>运行期切换指定窗口的背景资源。</summary>
    Task UpdateWallpaperAsync(IntPtr hwnd, string path);

    /// <summary>
    /// 查询指定窗口当前是否已挂着「同一背景文件」的活动壁纸会话（WP3）。
    /// 供启动 / 刷新时的档案恢复管线判重：会话已存在则跳过重复加载。
    /// </summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    /// <param name="path">背景资源路径（与档案里保存的一致）。</param>
    /// <returns>true = 该窗口已有同一背景文件的活动会话（恢复可跳过）。</returns>
    bool IsBackgroundActive(IntPtr hwnd, string path);

    /// <summary>暂停渲染（例如系统进入省电模式或窗口不可见时）。</summary>
    void PauseRendering();

    /// <summary>恢复渲染。</summary>
    void ResumeRendering();
}
```

### 1.4 IStatusNotifier

状态提示服务。实现：`WinCanvas.Wallpaper.WinTipsIntegrationService`。

```csharp
public interface IStatusNotifier
{
    /// <summary>提示 NumLock 键的锁定状态（供键盘指示器功能使用）。</summary>
    /// <param name="isLocked">true = NumLock 已锁定，false = 未锁定。</param>
    void ShowNumLockStatus(bool isLocked);
}
```

## 2. 数据模型（src/WinCanvas.Core/Models/）

### 2.1 WindowState（持久化模型，src/WinCanvas.Core/Models/WindowState.cs）

单个被管理窗口的持久化状态。窗口唯一标识 = 「进程名|类名」组合键（成员转义防碰撞），辅以 hwnd 值哈希做运行期匹配（hwnd 重启后可能变化，恢复时以 `Key` 为主键；WP3 恢复管线亦按 `Key` 重新定位窗口）。

```csharp
public sealed class WindowState
{
    /// <summary>窗口唯一组合键："进程名|类名"（成员转义后拼接，见 WindowInfo.Key）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>hwnd 句柄值的哈希（当前实现取句柄的整数值），仅作运行期辅助匹配。</summary>
    public long HwndHash { get; set; }

    /// <summary>窗口所属进程名（如 "notepad"）。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>窗口所属进程 Id。</summary>
    public int ProcessId { get; set; }

    /// <summary>当前是否置顶（WS_EX_TOPMOST）。</summary>
    public bool IsTopmost { get; set; }

    /// <summary>窗口透明度，0（全透明）~ 255（不透明），对应 SetLayeredWindowAttributes 的 bAlpha。</summary>
    public byte Alpha { get; set; } = 255;

    /// <summary>当前是否鼠标穿透（WS_EX_TRANSPARENT）。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>圆角偏好（对应 DWMWA_WINDOW_CORNER_PREFERENCE）。</summary>
    public CornerPreference Corner { get; set; } = CornerPreference.Default;

    /// <summary>背景类型：图片 / 视频 / pkg 打包资源。</summary>
    public BackgroundType Background { get; set; } = BackgroundType.None;

    /// <summary>背景资源路径（图片/视频文件路径或 pkg 包路径），无背景时为 null。</summary>
    public string? BackgroundPath { get; set; }
}
```

### 2.2 AppConfig

```csharp
/// <summary>%APPDATA%/WinCanvas/config.json 的根配置模型。</summary>
public sealed class AppConfig
{
    /// <summary>配置文件格式版本，便于将来迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 启动时是否自动恢复已保存档案的窗口（置顶 / 透明 / 穿透 / 圆角 / 背景）（WP3）。
    /// 默认 true；关闭后启动与手动刷新都跳过整个恢复管线，档案仍保留。
    /// </summary>
    public bool AutoRestoreOnStartup { get; set; } = true;

    /// <summary>各窗口的持久化状态（按 WindowState.Key 唯一）。</summary>
    public List<WindowState> Windows { get; set; } = new();
}
```

### 2.3 WindowInfo（运行期快照 record，不持久化）

```csharp
public sealed record WindowInfo(IntPtr Hwnd, string Title, string ClassName, string ProcessName, int ProcessId)
{
    /// <summary>「进程名|类名」组合键，成员转义（\ 与 | 转义）防碰撞，与 WindowState.Key 同一规则。</summary>
    public string Key => $"{Encode(ProcessName)}|{Encode(ClassName)}";

    /// <summary>hwnd 句柄值哈希，与 WindowState.HwndHash 同一规则。</summary>
    public long HwndHash => Hwnd.ToInt64();
}
```

### 2.4 枚举

```csharp
/// <summary>窗口圆角偏好，取值与 Win32 DWMWCP_* 常量一一对应。</summary>
public enum CornerPreference
{
    Default = 0,    // DWMWCP_DEFAULT：由系统默认行为决定
    DoNotRound = 1, // DWMWCP_DONOTROUND：不使用圆角
    Round = 2,      // DWMWCP_ROUND：标准大圆角
    RoundSmall = 3, // DWMWCP_ROUNDSMALL：小圆角
}

/// <summary>窗口背景的类型。</summary>
public enum BackgroundType
{
    None = 0,  // 无背景
    Image = 1, // 静态图片背景
    Video = 2, // 视频背景
    Pkg = 3,   // pkg 打包资源背景
}
```

## 3. Wallpaper 模块公开类型（src/WinCanvas.Wallpaper/）

### 3.1 WallpaperService（实现 IWallpaperService，src/WinCanvas.Wallpaper/WallpaperService.cs）

```csharp
public sealed class WallpaperService : IWallpaperService
{
    /// <summary>状态上报（pkg 降级提示、加载错误等），供 UI 层订阅展示。触发线程可能是工作线程。</summary>
    public event Action<string>? StatusReported;

    /// <summary>图片层新帧就绪：参数为窗口句柄与导入时已缩放好的 SoftwareBitmap。
    /// 在渲染线程触发；订阅方需在回调线程内同步复制像素，再封送到 Dispatcher 转 WriteableBitmap 上屏。
    /// 【App 层订阅者】WallpaperPreviewController（src/WinCanvas.App/Services/）转 WriteableBitmap 显示到主窗口预览区。</summary>
    public event Action<IntPtr, SoftwareBitmap>? ImageFrameReady;

    /// <summary>视频层播放器就绪（线程池线程触发）：订阅方把播放器挂到 MediaPlayerElement（SetMediaPlayer）输出。</summary>
    public event Action<IntPtr, MediaPlayer>? VideoPlayerReady;

    // 接口成员：InitializeWallpaperAsync / UpdateWallpaperAsync / PauseRendering / ResumeRendering
}
```

### 3.2 WallpaperService 宿主/事件扩展（WP1，src/WinCanvas.Wallpaper/）

WP1 在 `WallpaperService` 上新增的公共成员与事件（均由服务内部接线，App 层只订阅）：

```csharp
public sealed class WallpaperService : IWallpaperService
{
    /// <summary>会话宿主挂接模式确定 / 变化（创建成功或 A1 降级 A2）。
    /// pump 线程或线程池触发；App 层据此在状态文本显示「嵌入模式 / 跟随模式」。</summary>
    public event Action<IntPtr, HostAttachMode>? SessionHostModeChanged;

    /// <summary>销毁全部会话与宿主（IWallpaperService 新增；应用退出时调用）。</summary>
    public void ShutdownAll();
}
```

### 3.3 宿主窗口类型（WP1，src/WinCanvas.Wallpaper/Hosting/，internal）

| 类型 | 职责 |
| --- | --- |
| `WallpaperHostWindow`（internal sealed） | 宿主窗口封装：窗口类注册、A1 跨进程 `WS_CHILD` 嵌入 / A2 顶层跟随、WinEvent 跟随、A1→A2 自动降级、目标销毁上报。公共成员：`TryCreate(target, [preferred], out error)`、`RenderImage(BgraSnapshot)`、`BeginVideo(MediaPlayer)`、`UpdateVideoLayout(w, h)`、`EndVideo()`、`CountAliveHosts(processIdFilter)`、事件 `AttachModeChanged` / `TargetLost` / `ClientSizeChanged(target, w, h)` |
| `HostAttachMode`（internal enum） | `Embed`（A1 跨进程子窗口嵌入）/ `Follow`（A2 顶层跟随） |
| `WallpaperHostRenderer`（internal sealed） | DComp 图像渲染器：设备树 + `BeginDraw → GetDC → StretchDIBits → EndDraw → Commit`；表面按客户区尺寸重建；设备失陷整树重建 |
| `CompositionVideoPresenter`（internal sealed） | 视频组合呈现（ADR：GetSurface + DesktopWindowTarget + SpriteVisual 等比加边）；自持专用 DispatcherQueue 线程 |
| `WallpaperHostPump`（internal sealed） | 宿主专用消息线程：跨线程 DestroyWindow 合法性与 WINEVENT_OUTOFCONTEXT 回调的统一载体；`Execute(action)` 同步封送 |
| `HostNativeMethods` / `DcompNativeMethods`（internal static） | 宿主 / DComp 专用 P/Invoke（全部带规格级中文注释） |
| `BitmapSnapshot` / `BgraSnapshot`（internal，Rendering/） | SoftwareBitmap → PBgra8 紧密快照的共享拷贝工具（预览与宿主共用一份实现） |

### 3.4 WinTipsIntegrationService（实现 IStatusNotifier，src/WinCanvas.Wallpaper/WinTipsIntegrationService.cs）

```csharp
public sealed class WinTipsIntegrationService : IStatusNotifier
{
    /// <summary>回退通道：WinTips.exe 不可用时触发，参数 = NumLock 是否锁定。</summary>
    public event Action<bool>? NumLockTipRequested;

    /// <summary>诊断信息（WinTips 调用结果），供日志/UI 层订阅。</summary>
    public event Action<string>? LogMessage;

    /// <summary>是否已找到可调用的 WinTips.exe。</summary>
    public bool IsWinTipsAvailable { get; }

    /// <summary>是否允许对外弹出提示（主界面「WinTips 提示」开关）。</summary>
    public bool IsEnabled { get; set; }

    public WinTipsIntegrationService(string? explicitExePath = null);

    // 接口成员：ShowNumLockStatus(bool isLocked) —— 调用方 = Core 的 KeyboardIndicatorService（WP2）
}
```

### 3.5 KeyboardIndicatorService（src/WinCanvas.Core/Services/KeyboardIndicatorService.cs，WP2 新增）

NumLock LL 键盘钩子服务：专用消息线程安装/回调/卸载，状态变化去抖后经线程池上报 `IStatusNotifier`。

```csharp
public sealed class KeyboardIndicatorService : IDisposable
{
    /// <summary>诊断打点（安装/状态变化/去抖抑制/Unhook 完成），同时落
    /// %LOCALAPPDATA%/WinCanvas/logs/keyboard-diag.log。触发线程 = 钩子线程 / 线程池。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>钩子当前是否已安装（跨周期真实口径；Stop 后钩子线程收尾完成前仍可为 true）。</summary>
    public bool IsHookInstalled { get; }

    /// <param name="notifier">上报目标（组合根注入 WinTipsIntegrationService）。</param>
    public KeyboardIndicatorService(IStatusNotifier notifier);

    /// <summary>启动监听（幂等）。只做托管线程创建，钩子安装发生在专用线程内部——UI 线程零 P/Invoke。</summary>
    public void Start();

    /// <summary>停止监听（幂等）。投递 WM_QUIT 与 Join 派发线程池，调用方立即返回。</summary>
    public void Stop();

    /// <summary>显式同步卸钩（有界等待），应用退出时由 MainWindow.Closed 调用。</summary>
    public void Dispose();
}
```

配套 P/Invoke（`src/WinCanvas.Core/Native/NativeMethods.cs`，注释规格同既有条目）：`SetWindowsHookExW` / `UnhookWindowsHookEx` / `CallNextHookEx` / `GetKeyState` / `PostThreadMessageW` / `GetMessageW` / `PeekMessageW` / `GetModuleHandleW` / `GetCurrentThreadId`，常量 `WH_KEYBOARD_LL(13)` / `VK_NUMLOCK(0x90)` / `WM_KEYDOWN(0x0100)` / `WM_KEYUP(0x0101)` / `WM_QUIT(0x0012)` / `HC_ACTION(0)` / `LLKHF_INJECTED(0x10)`，类型 `LowLevelKeyboardProc` / `KBDLLHOOKSTRUCT` / `MSG` / `POINT`。

### 3.6 DynamicFrameRateController（src/WinCanvas.Wallpaper/Rendering/DynamicFrameRateController.cs）

纯状态机，不持有线程资源。三档策略：窗口激活 60fps / 非激活 15fps / 全屏前台或用户暂停完全暂停。

```csharp
public sealed class DynamicFrameRateController
{
    public const double ActiveFps = 60.0;   // 窗口激活时的目标帧率
    public const double InactiveFps = 15.0; // 窗口非激活时的目标帧率（省电档）

    /// <summary>状态变化事件：参数为新的渲染状态。触发线程 = 调用 Set* 方法的线程。</summary>
    public event Action<WallpaperRenderState>? StateChanged;

    public WallpaperRenderState CurrentState { get; }
    public bool ShouldRender { get; }          // 两种暂停态均返回 false
    public TimeSpan FrameInterval { get; }     // 暂停态返回 ~8fps 的轮询间隔

    public void SetWindowActive(bool active);        // 激活 → 60fps / 非激活 → 15fps
    public void SetFullscreenDetected(bool fullscreen); // 存在全屏前台窗口 → 暂停
    public void SetPausedByUser(bool paused);           // 用户/系统显式暂停
}
```

### 3.7 WallpaperRenderLoop（src/WinCanvas.Wallpaper/Rendering/WallpaperRenderLoop.cs）

壁纸专用后台渲染线程（线程名 `WinCanvas.Wallpaper.Render`，IsBackground、BelowNormal 优先级）。

```csharp
public sealed class WallpaperRenderLoop : IDisposable
{
    /// <summary>帧回调：在每个应渲染的帧上于渲染线程触发。</summary>
    public event Action? FrameTick;

    /// <summary>渲染状态变化回调（渲染线程触发），用于同步视频播放器的播放/暂停。</summary>
    public event Action<WallpaperRenderState>? StateChanged;

    /// <summary>帧率控制器（外部通过 Set* 方法驱动激活态/用户暂停）。</summary>
    public DynamicFrameRateController Controller { get; }

    public bool IsRunning { get; }  // 渲染线程是否仍在运行
    public void Start();            // 启动渲染线程，重复调用无副作用
    public void Dispose();          // 只发停止信号，不等待线程退出（后台线程随进程回收）
}
```

### 3.8 加载器（src/WinCanvas.Wallpaper/Loaders/）

```csharp
// ImageWallpaperLoader：第一层静态图片（.jpg/.jpeg/.png/.bmp/.gif），GIF 取第一帧
public static readonly string[] SupportedExtensions;                 // { ".jpg", ".jpeg", ".png", ".bmp", ".gif" }
public static bool IsSupported(string path);
// 加载并缩放到目标分辨率（<=0 不缩放）；返回 ImageFrame 为 Bgra8 SoftwareBitmap
public static WallpaperSource Load(string path, int targetWidth, int targetHeight);

// VideoWallpaperLoader：第二层视频（.mp4/.m4v/.mov/.webm/.wmv/.mkv）
public static readonly string[] SupportedExtensions;
public static bool IsSupported(string path);
// 创建 MediaPlayer（静音、循环、不自动播放），Player 非空
public static WallpaperSource Load(string path);

// PkgWallpaperLoader：第三层 Wallpaper Engine .pkg（repkg 解包 + 场景分析）
public static WallpaperSource Load(string pkgPath);
// 抛 FileNotFoundException（文件不存在）/ InvalidDataException（pkg 头魔数不合法）
```

### 3.9 模型

```csharp
/// <summary>背景资源的实际类型（三层策略的判定结果）。</summary>
public enum WallpaperKind { Image = 1, Video = 2, Pkg = 3 }

/// <summary>壁纸渲染状态（动态帧率控制器的输出）。</summary>
public enum WallpaperRenderState
{
    Active60Fps = 0,      // 前台激活：目标 60fps
    Inactive15Fps = 1,    // 非激活：目标 15fps 省电
    PausedFullscreen = 2, // 检测到全屏前台窗口：暂停
    PausedByUser = 3,     // 用户/系统显式暂停
}

/// <summary>一份已准备就绪的壁纸资源（record，用毕必须 Dispose）。</summary>
public sealed record WallpaperSource : IDisposable
{
    public WallpaperKind Kind { get; init; }          // 实际加载的资源类型
    public SoftwareBitmap? ImageFrame { get; init; }  // 图片层帧（pkg 降级封面图时也非空）
    public MediaPlayer? Player { get; init; }         // 视频层播放器（App 端 SetMediaPlayer 绑定）
    public string? ExtractedRoot { get; init; }       // pkg 解包缓存目录
    public string? SceneJsonPath { get; init; }       // pkg 内 scene.json 完整路径
    public string? CoverImagePath { get; init; }      // 复杂场景降级展示的封面图路径
    public string? StatusMessage { get; init; }       // 需上报给用户的明确提示
    public string? VideoPath { get; init; }           // 视频资源的磁盘路径
    public string OriginalPath { get; init; }         // 原始资源路径
    public void Dispose();                            // 释放 ImageFrame 与 Player
}
```

## 4. Win32 P/Invoke 接口清单

完整签名与逐条中文注释见源码：`WinCanvas.Core/Native/NativeMethods.cs`（内部类）与 `WinCanvas.Wallpaper/Native/WallpaperNativeMethods.cs`（内部类）。清单如下：

### NativeMethods（user32 / dwmapi / kernel32）

| API | 用途 | 关键返回值 |
| --- | --- | --- |
| `EnumWindows(EnumWindowsProc, IntPtr)` | 枚举顶层窗口 | false = 回调终止或失败（GetLastError） |
| `IsWindowVisible(IntPtr)` | 判断窗口可见性 | false = 不可见或句柄无效 |
| `GetWindowTextLength(IntPtr)` | 标题字符长度 | 无标题返回 0 |
| `GetWindowText(IntPtr, StringBuilder, int)` | 读取标题 | 实际复制字符数 |
| `GetClassName(IntPtr, StringBuilder, int)` | 读取类名 | 实际复制字符数 |
| `GetWindowThreadProcessId(IntPtr, out uint)` | 取进程 Id | 返回创建窗口的线程 Id |
| `SetWindowPos(...)` | 置顶切换（HWND_TOPMOST/-NOTOPMOST + SWP_NOMOVE\|NOSIZE\|NOACTIVATE） | false = 失败 |
| `SetLayeredWindowAttributes(..., LWA_ALPHA)` | 整体 alpha 透明 | false = 失败 |
| `GetWindowLongPtrCompat / SetWindowLongPtrCompat` | 32/64 位兼容的 GWL_EXSTYLE 读写 | 旧值；0 需结合 GetLastError 判断 |
| `DwmGetWindowAttribute / DwmSetWindowAttribute` | DWM 属性读写（圆角 = 33） | HRESULT，负值为错误 |
| `SetWindowsHookExW(WH_KEYBOARD_LL, proc, hMod, 0)` | 安装 LL 键盘钩子（WP2，须由带消息循环的专用线程调用） | IntPtr.Zero = 失败（GetLastError） |
| `UnhookWindowsHookEx(IntPtr)` | 卸载钩子（WP2，钩子线程收尾时调用） | false = 失败 |
| `CallNextHookEx(...)` | 钩子链放行（WP2，回调无条件调用，绝不吞键） | 下一钩子的返回值 |
| `GetKeyState(int)` | 读虚拟键同步状态（WP2，切换位 bit0 为全局会话状态） | bit0=1 表示 NumLock 锁定 |
| `PostThreadMessageW(uint, uint, IntPtr, IntPtr)` | 向钩子线程投递 WM_QUIT（WP2 停止流程） | false = 失败 |
| `GetMessageW / PeekMessageW` | 钩子线程消息循环 / 建队（WP2） | 0 = WM_QUIT 退出循环 |
| `GetModuleHandleW / GetCurrentThreadId` | 取主模块句柄（hMod）/ 钩子线程 Id（WP2） | 句柄 / 线程 Id |

### WallpaperNativeMethods（user32）

| API | 用途 |
| --- | --- |
| `GetForegroundWindow()` | 取前台窗口（全屏检测第一步） |
| `GetWindowLongW(hwnd, GWL_EXSTYLE)` | 读扩展样式（WS_EX_TOPMOST 判定） |
| `GetWindowRect(IntPtr, out RECT)` | 窗口矩形 |
| `MonitorFromWindow(IntPtr, MONITOR_DEFAULTTONEAREST)` | 取窗口所在显示器 |
| `GetMonitorInfoW(HMONITOR, ref MONITORINFO)` | 显示器矩形（全屏判定 + 图片缩放目标分辨率） |

对外封装的静态方法：`IsForegroundWindowFullscreen(bool borderlessOnly = true)`（无边框置顶且完整覆盖显示器矩形才判定全屏）与 `TryGetMonitorResolutionForWindow(IntPtr hwnd, out int width, out int height)`。

## 5. 配置文件格式

位置：`%APPDATA%/WinCanvas/config.json`（UTF-8 **无 BOM**，RFC 8259；缩进、中文原样不转 \uXXXX）。示例：

```json
{
  "Version": 1,
  "Windows": [
    {
      "Key": "notepad|无标题 - 记事本",
      "HwndHash": 197128,
      "ProcessName": "notepad",
      "Title": "无标题 - 记事本",
      "ProcessId": 12345,
      "IsTopmost": true,
      "Alpha": 200,
      "ClickThrough": false,
      "Corner": 2,
      "Background": 1,
      "BackgroundPath": "D:/wallpapers/aurora.png"
    }
  ]
}
```

行为要点（src/WinCanvas.Core/Services/ConfigurationService.cs）：

- 首次运行文件不存在 → 返回默认配置；JSON 损坏（`JsonException`）→ 回退默认配置，不崩溃。
- `Update` 后 500ms 防抖合并写盘；应用退出时 `MainWindow.Closed` 调同步的 `Flush()` 强制落盘（消息循环即将退出，异步续延可能没有机会执行）。
- `ConfigurationService(string configFilePath)` 构造可注入路径（测试用）；`ConfigFilePath` 属性供诊断。

## 6. 命令行 / 自动化入口

- 本应用当前**未提供** CLI 或 IPC 入口，只有 WinUI 3 图形界面。
- 间接依赖的外部 CLI：`WinTips.exe --test num on|off [holdSeconds]`（本程序以进程调用方式触发，参数格式来自 WinTips 子模块源码 `App/App.xaml.cs` 的 `RunTest` 与 `Core/TipInfo.cs` 的 `TryParseKind`）。
