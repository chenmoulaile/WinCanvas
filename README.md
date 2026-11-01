# WinCanvas

高性能 Windows 窗口管理工具：对任意窗口实现置顶 / 透明 / 鼠标穿透 / 圆角，并支持独立壁纸背景（图片、视频、Wallpaper Engine 格式），内置 WinTips 操作提示。

- 平台：Windows 11 x64（最低目标 Windows 10 1809 / 10.0.17763，圆角功能需 Windows 11）
- 技术栈：C# / .NET 8 / WinUI 3 / Win32

## 功能

### 窗口管理（src/WinCanvas.Core）

- **窗口枚举**：后台线程枚举所有可见顶层窗口（标题 / 类名 / 进程名 / 进程 Id），自动排除无标题窗口与应用自身，不阻塞 UI（`WindowControlService.EnumerateTopLevelWindowsAsync`）。
- **置顶切换**：`SetWindowPos` + `HWND_TOPMOST` / `HWND_NOTOPMOST`，只改 Z 序、不动位置尺寸、不抢焦点（提供 Toggle 与幂等 Set 两种语义；档案恢复用「读真实状态 + 按需 Set」避免翻转误伤）。
- **窗口透明**：`WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`，透明度 0（全透明）~ 255（不透明）。
- **鼠标穿透**：`WS_EX_TRANSPARENT`（启用时一并置位 `WS_EX_LAYERED`；关闭时只清穿透位、保留分层样式以保住已设置的透明度）。
- **窗口圆角**：`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)`，支持无 / 大圆角 / 小圆角三档（大圆角需 Windows 11）。
- **配置持久化**：`%APPDATA%/WinCanvas/config.json`，按「进程名|标题」组合键记忆每个窗口的置顶 / 透明 / 穿透 / 圆角 / 背景设置；内存缓存 + 500ms 防抖批量写盘，退出前强制落盘（`ConfigurationService`）。

### 壁纸背景（src/WinCanvas.Wallpaper）

按扩展名分派的三层加载策略（`WallpaperService`）：

1. **图片层**：`.jpg/.jpeg/.png/.bmp/.gif`，ImageSharp 解码后用 Lanczos 一次性缩放到承载窗口所在显示器分辨率（只缩小不放大），转 Bgra8 `SoftwareBitmap`；GIF 当前取第一帧静态呈现。
2. **视频层**：`.mp4/.m4v/.mov/.webm/.wmv/.mkv`，`Windows.Media.Playback.MediaPlayer`（Media Foundation 硬件解码），静音循环播放。
3. **pkg 层（Wallpaper Engine）**：`.pkg`，经 git 子模块 [repkg](https://github.com/notscuffed/repkg)（v0.4.0-alpha，源码项目引用）解包到 `%LOCALAPPDATA%/WinCanvas/wallpaper-cache/<文件名-hash>/`，`.tex` 纹理解码为 PNG、内嵌视频纹理还原为 mp4；纯视频场景走视频层、纯图场景走图片层；**含 SceneScript 动态脚本场景（存在 .lua 或 scene.json 引用 script 字段）当前不支持动态渲染，降级为仅显示封面图并给出提示**。
4. **动态帧率**：三档状态机（`DynamicFrameRateController`）—— 窗口激活 60fps / 非激活 15fps / 检测到全屏前台窗口或用户暂停时完全暂停；全屏检测每 250ms 节流执行；暂停时同步暂停视频解码。

### WinTips 提示（src/WinCanvas.Wallpaper/WinTipsIntegrationService.cs）

- `IStatusNotifier` 的 WinTips 集成实现：优先进程调用已部署的 `WinTips.exe`（CLI 格式 `--test num on|off [holdSeconds]`）复用其 Win11 质感浮窗；找不到或调用失败时回退本地事件通道（当前在主界面 InfoBar 显示）。
- 主界面提供「WinTips 提示」开关，关闭时静默忽略所有提示。
- WinTips 源码位于 git 子模块 [ThirdParty/WinTips](https://github.com/DoYouNatureQ/WinTips)（net10.0-windows WPF，.NET 8 SDK 无法构建，因此采用进程调用而非项目引用）。

### 其他

- **FPS 性能监控悬浮显示**（开发阶段诊断用）：`CompositionTarget.Rendering` 帧计数 + 每秒取样，主界面开关控制。

## 前置条件

| 依赖 | 说明 |
| --- | --- |
| Windows 11 x64 | 开发与运行目标环境（圆角功能依赖 Win11 DWM 属性） |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 本仓库在 8.0.426 上构建验证通过 |
| Windows App SDK | 通过 NuGet 包 `Microsoft.WindowsAppSDK 1.8.260921001` 自动还原；项目配置 `WindowsAppSDKSelfContained=true` + `WindowsPackageType=None`（非打包部署），目标机器**无需**单独安装 Windows App Runtime |
| git | 用于拉取两个子模块（repkg、WinTips） |

## 构建

```bash
# 1. 克隆仓库（含子模块）
git clone --recursive <本仓库地址>
cd WinCanvas

# 若已克隆过，补拉子模块：
git submodule update --init --recursive

# 2. 构建（Debug 或 Release）
dotnet build WinCanvas.sln -c Debug

# 3. 运行
# 产物位于 src/WinCanvas.App/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/WinCanvas.App.exe
```

> 验证手段：`dotnet build WinCanvas.sln`（0 error 为过）+ `dotnet test`（xUnit 测试，当前覆盖窗口档案键语义等核心模型，见 `tests/`）。

## 使用说明

1. 启动 `WinCanvas.App.exe`，主界面左侧自动列出所有可见顶层窗口（点「刷新」重新枚举）。
2. 选中一个窗口，右侧详情页可调整：
   - **置顶**开关、**透明度**滑块（0~255）、**鼠标穿透**开关、**圆角**（无 / 大圆角 / 小圆角）；
   - 所有改动立即应用到目标窗口并实时写入配置，下次选中该窗口（或重启应用后）自动恢复。
3. **背景壁纸**：点「选择背景文件」挑选图片 / 视频 / `.pkg` 文件，资源在后台线程加载，进度条与状态提示实时显示。
4. **设置区**：「WinTips 提示」开关控制 NumLock 浮窗提示；「FPS 性能监控悬浮显示」开关在界面右上角显示渲染帧率。

## 已知限制

- **SceneScript 动态场景暂不支持**：Wallpaper Engine 中包含 `.lua` 脚本或 scene.json 引用 script 字段的动态场景不做动态渲染，仅降级显示封面图（`PkgWallpaperLoader.DetectComplexScene`）。
- **壁纸画面为应用内预览**：`WallpaperPreviewController` 已把 `ImageFrameReady` 接到主窗口预览 Image（WriteableBitmap 上屏）、视频 `MediaPlayer` 挂到 `MediaPlayerElement` 输出；把画面直接绘制到外部目标窗口内部（SetParent 子窗口宿主）尚未实现，属于进行中的工作。
- **NumLock 键盘钩子未实现**：`WinTipsIntegrationService.ShowNumLockStatus` 目前没有调用方，键盘指示器（NumLock 状态监听）尚未接入。
- **GIF 动图只取第一帧**，不播放动画。
- **Wallpaper Engine 私有格式兼容性**：repkg 为 v0.4.0-alpha，不保证覆盖所有 WE 版本的 pkg / tex 变体；解析失败时该包降级或跳过。
- **跨进程窗口操作受 UIPI 限制**：对以管理员权限运行的窗口执行置顶 / 透明 / 穿透操作，非管理员身份的本程序会失败（错误以 InfoBar 提示呈现）；本程序未做提权。
- **置顶一致性**：界面开关用幂等 Set 语义 + 实际状态回读；并发从界面与外部同时切换同一窗口的置顶状态时，结果以实际返回状态为准，界面可能与请求值不一致（已有不一致提示）。
- WinTips 子模块目标框架为 net10.0-windows（WPF），本环境 .NET 8 SDK 无法构建，只能进程调用已部署的 `WinTips.exe`，未部署时走本地回退通道。

## 文档

- [架构设计](docs/architecture.md)
- [API 说明](docs/api.md)
- [风险评估](docs/risk.md)
