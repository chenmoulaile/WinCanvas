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
- **配置持久化**：`%APPDATA%/WinCanvas/config.json`，按「进程名|类名」组合键记忆每个窗口的置顶 / 透明 / 穿透 / 圆角 / 背景设置；内存缓存 + 500ms 防抖批量写盘，退出前强制落盘（`ConfigurationService`）。

### 壁纸背景（src/WinCanvas.Wallpaper）

按扩展名分派的三层加载策略（`WallpaperService`；扩展名口径统一由
[`WallpaperFormats`](docs/api.md) 单一来源定义，图片 `.jpg/.jpeg/.png/.bmp/.gif/.webp`、
视频 `.mp4/.m4v/.mov/.webm/.wmv/.mkv/.avi`、打包资源 `.pkg`）：

1. **图片层**：ImageSharp 解码后用 Lanczos 一次性缩放到承载窗口所在显示器分辨率（只缩小不放大），转 Bgra8 `SoftwareBitmap`。**GIF 动画解码全部帧并按帧延迟连续播放**（宿主按帧推进上屏，全屏 / 用户暂停时定格；帧解码受内存预算上限 256MB 约束，超长 GIF 截断为前若干帧循环）；静态 webp 支持（动画 webp 为 ImageSharp 2.1 解码能力范围外，加载失败按既有错误提示）。
2. **视频层**：`Windows.Media.Playback.MediaPlayer`（Media Foundation 硬件解码），静音循环播放。
3. **pkg 层（Wallpaper Engine）**：`.pkg`，经 git 子模块 [repkg](https://github.com/notscuffed/repkg)（v0.4.0-alpha，源码项目引用）解包到 `%LOCALAPPDATA%/WinCanvas/wallpaper-cache/<文件名-hash>/`，`.tex` 纹理解码为 PNG、内嵌视频纹理还原为 mp4；纯视频场景走视频层、纯图场景走图片层；**含 SceneScript 动态脚本场景（存在 .lua 或 scene.json 引用 script 字段）当前不支持动态渲染，降级为仅显示封面图并给出提示**。
4. **动态帧率**：三档状态机（`DynamicFrameRateController`）—— 窗口激活 60fps / 非激活 15fps / 检测到全屏前台窗口或用户暂停时完全暂停；全屏检测每 250ms 节流执行；暂停时同步暂停视频解码、GIF 定格。

### 背景上屏（宿主窗口，src/WinCanvas.Wallpaper/Hosting）

选中窗口设置背景后，画面**真实呈现在目标窗口内部**（不再是仅应用内预览）。承载者是本进程的
Win32 宿主窗口（类名 `WinCanvasWallpaperHost`），两条挂接路径自动选择与降级：

- **嵌入模式（首选）**：宿主以 `WS_CHILD` 子窗口挂进目标窗口客户区、沉到子窗口 Z 序底部——
  目标自绘背景被壁纸覆盖，目标既有控件（编辑框等）位于其上继续可见可点；目标移动 / 缩放 /
  最小化 / 关闭全部自动跟随与清理。
- **跟随模式（回退）**：跨进程嵌入被系统拒绝时（UIPI / 目标权限更高 / DPI 上下文不兼容），
  自动降级为独立顶层窗口，跟随目标外接矩形、Z 序钉在目标正下方；此时目标需轻度透明才能透出
  壁纸——应用会自动将目标设为轻度透明（alpha=220，可在滑块调整）并给出提示。
- 当前生效模式实时显示在壁纸状态文本（「背景挂接：嵌入模式 / 跟随模式」）。
- **图片跨屏重缩放**：目标窗口移动到不同分辨率的显示器后，图片自动按新显示器分辨率重新加载。
- **视频**：组合呈现（`MediaPlayer.GetSurface` + 系统组合引擎），窗口内等比加边居中、无拉伸；
  全屏前台窗口出现时自动暂停、退出后恢复。

主窗口内的预览区是旁路显示（图片预览为快照副本）；视频的预览区在组合呈现路径下不显示画面（主输出为目标窗口）。

### WinTips 提示与 NumLock 指示（src/WinCanvas.Wallpaper/WinTipsIntegrationService.cs + src/WinCanvas.Core/Services/KeyboardIndicatorService.cs）

- **NumLock 键盘钩子**（`KeyboardIndicatorService`，WP2）：低级键盘钩子（`WH_KEYBOARD_LL`）安装在专用后台消息线程（GetMessage 循环，空闲零 CPU），回调内只做一次 `GetKeyState` 读态与去抖比较（微秒级返回，不拖慢全系统键盘输入）；NumLock 状态**变化**时经线程池派发上报——按住自动重复、同一翻转的 KEYDOWN/KEYUP 双时机都只产生一次提示。主界面「WinTips 提示」开关控制钩子启停（关→显式 `UnhookWindowsHookEx` 卸钩 + 进程退出兜底卸载），关闭后按键零反应。
- **WinTips 集成**（`IStatusNotifier` 的实现）：优先进程调用已部署的 `WinTips.exe`（CLI 格式 `--test num on|off [holdSeconds]`）复用其 Win11 质感浮窗；找不到或调用失败时回退本地事件通道（在主界面 InfoBar 显示）。
- **WinTips.exe 自动部署**：构建 `WinCanvas.sln` 时，`WinCanvas.App.csproj` 的 MSBuild Target `BuildAndDeployWinTips` 会用 .NET 10 SDK 外挂构建 WinTips 子模块并把产物拷到应用输出目录 `WinTips\` 下（增量：源码未变时跳过）；外挂构建失败只产生 warning 不阻断主构建，运行期自动走 InfoBar 回退通道。
- WinTips 源码位于 git 子模块 [ThirdParty/WinTips](https://github.com/DoYouNatureQ/WinTips)（net10.0-windows WPF；不加入 sln，仅以外挂 MSBuild Target 构建部署）。

### 状态重启自动恢复（src/WinCanvas.App/ViewModels/MainViewModel.cs，WP3）

重启应用后**不做任何操作**，已保存档案窗口的置顶 / 透明 / 穿透 / 圆角 / 背景自动恢复：

- **档案恢复管线**：启动初始化与手动「刷新」的窗口枚举完成后，对配置中已有档案的窗口按
  **200ms 间隔逐个排队（线程池执行，并发上限 2）** 后台恢复——列表先可用，恢复全程后台进行，
  不阻塞首帧渲染。属性恢复复用 `WindowItemViewModel.RestoreAsync`（幂等置顶语义），
  背景恢复复用 `WallpaperService.UpdateWallpaperAsync`（内部经每窗口更新闸门串行）。
- **幂等**：同一窗口会话内只恢复一次（句柄判重账本 + 壁纸会话字典 `IsBackgroundActive` 判重），
  手动连续刷新不会重复解码 / 重新挂接；恢复失败自动撤销判重允许下次刷新重试。
- **背景文件缺失**：恢复时若档案里的背景文件不存在，InfoBar 警告「背景文件已不存在：…，档案保留」
  并保留档案（用户可能临时移动文件），属性部分照常恢复；文件放回后重启即可恢复背景。
- **开关**：设置区「启动时自动恢复窗口状态」（默认开启，`AppConfig.AutoRestoreOnStartup`）；
  关闭后启动与手动刷新都不做任何恢复动作，档案仍保留在配置中。
- **诊断**：管线打点写入 `%LOCALAPPDATA%\WinCanvas\logs\restore-diag.log`
  （进程启动 / 列表可用 / 单窗口恢复 / 幂等跳过 / 全部完成，含耗时）。

### 其他

- **FPS 性能监控悬浮显示**（开发阶段诊断用）：`CompositionTarget.Rendering` 帧计数 + 每秒取样，主界面开关控制。

## 前置条件

| 依赖 | 说明 |
| --- | --- |
| Windows 11 x64 | 开发与运行目标环境（圆角功能依赖 Win11 DWM 属性） |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 本仓库在 8.0.426 上构建验证通过 |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 构建 WinTips 集成（NumLock 浮窗提示）所需；本仓库在 10.0.403 上构建验证通过。缺失时主构建仍成功（仅一条 warning），NumLock 提示自动走 InfoBar 回退通道 |
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

> 验证手段：`dotnet build WinCanvas.sln`（0 error 为过）+ `dotnet test`（xUnit，两个测试项目共 108 个用例，见 `tests/`）：
> - `tests/WinCanvas.Core.Tests`：配置持久化（防抖合并 / 原子写 / 无 BOM）、窗口档案键语义、
>   `WindowControlService` 失败语义（无效句柄抛 `Win32Exception`）与真实窗口行为冒烟、NumLock 去抖纯逻辑、
>   UIPI 错误 5 的提权引导文案映射（WP5）；
> - `tests/WinCanvas.Wallpaper.Tests`：动态帧率状态机真值表、pkg 加载器纯逻辑（路径安全 / 场景判定 / 封面优先级）、
>   宿主挂接矩形与 DPI 换算纯函数、扩展名单一来源口径（`WallpaperFormats`，WP5）、
>   合成动画 GIF 的多帧解码与帧延迟往返、静态 WebP 解码（WP5）。
>   需要真实桌面会话的用例打 `[Trait("Category", "RequiresWindowsSession")]`，无头环境用
>   `dotnet test --filter "Category!=RequiresWindowsSession"` 排除（不判失败）。

## 使用说明

1. 启动 `WinCanvas.App.exe`，主界面左侧自动列出所有可见顶层窗口（点「刷新」重新枚举）。
2. 选中一个窗口，右侧详情页可调整：
   - **置顶**开关、**透明度**滑块（0~255）、**鼠标穿透**开关、**圆角**（无 / 大圆角 / 小圆角）；
   - 所有改动立即应用到目标窗口并实时写入配置；**完全退出并重启应用后，这些设置连同背景自动恢复**
     （见「状态重启自动恢复」一节；重启后按「进程名|类名」组合键重新定位窗口，窗口句柄变化不影响恢复）。
3. **背景壁纸**：点「选择背景文件」挑选图片 / 视频 / `.pkg` 文件（GIF 动画按帧延迟连续播放，webp 静态图支持），资源在后台线程加载，进度条与状态提示实时显示；画面直接呈现在目标窗口内部（嵌入 / 跟随双模式自动选择，见「背景上屏」一节），窗口移动 / 缩放实时跟随，全屏前台窗口出现时视频自动暂停、GIF 定格。
4. **设置区**：「启动时自动恢复窗口状态」开关控制启动 / 刷新时的档案恢复管线（默认开启）；「WinTips 提示」开关控制 NumLock 浮窗提示（默认关闭；开启即安装 LL 键盘钩子，按下 NumLock 后 2 秒内弹出 WinTips 浮窗，未部署 WinTips.exe 时在主界面 InfoBar 回退提示；关闭即卸钩，按键零反应）；「FPS 性能监控悬浮显示」开关在界面右上角显示渲染帧率。
5. **权限说明（UIPI）**：目标窗口以更高权限（如管理员）运行时，Windows 的 UIPI 机制会拒绝本程序的跨进程操作（置顶 / 透明 / 穿透 / 背景挂接）。相关操作失败（Win32 错误 5 = 拒绝访问）时 InfoBar 会给出明确引导：「目标窗口以更高权限运行，请以管理员身份重启 WinCanvas 后重试」；设置区实时显示本程序的当前权限状态（管理员 / 标准）。背景挂接在嵌入被拒时自动降级为跟随模式，无需手动干预。

## 已知限制

- **SceneScript 动态场景暂不支持**：Wallpaper Engine 中包含 `.lua` 脚本或 scene.json 引用 script 字段的动态场景不做动态渲染，仅降级显示封面图（`PkgWallpaperLoader.DetectComplexScene`）。
- **超长 GIF 截断**：动画 GIF 解码受内存预算上限（约 256MB 缩放后像素）与 240 帧上限约束，超过者只保留前若干帧循环播放（正常 GIF 不受影响）；**动画 WebP** 在 ImageSharp 2.1 解码能力范围外，加载失败按既有错误提示（静态 WebP 正常）。
- **GIF / 图片的应用内预览只显示首帧**：主输出 = 目标窗口内的宿主画面，主窗口预览区是图片快照旁路（GIF 只显示首帧）。
- **Wallpaper Engine 私有格式兼容性**：repkg 为 v0.4.0-alpha，不保证覆盖所有 WE 版本的 pkg / tex 变体；解析失败时该包降级或跳过。
- **跨进程窗口操作受 UIPI 限制**：对以管理员权限运行的窗口执行置顶 / 透明 / 穿透操作，非管理员身份的本程序会失败——错误 5 会触发专项引导（「请以管理员身份重启 WinCanvas 后重试」，设置区可见当前权限状态）；本程序未做自动提权。背景挂接受同样限制，但嵌入被拒时自动降级跟随模式。
- **视频的应用内预览为空**：视频走组合呈现（主输出 = 目标窗口内的宿主），主窗口预览区的 MediaPlayerElement 不再收到画面；图片预览不受影响（GIF 预览为首帧静态）。
- **置顶一致性**：界面开关用幂等 Set 语义 + 实际状态回读；并发从界面与外部同时切换同一窗口的置顶状态时，结果以实际返回状态为准，界面可能与请求值不一致（已有不一致提示）。
- **恢复按组合键匹配、同键共享档案**：启动恢复按「进程名|类名」组合键重新定位窗口；同一进程 + 同一类名的多个窗口（如多个 Chrome 窗口）共享同一份档案，恢复管线会对其中**每个**已枚举到的窗口应用同一档案值（这是档案键设计的既有取舍，见 `WindowInfo.Key`）。恢复只在启动与手动刷新时执行，运行期间本程序不监视外部对窗口属性的改动。
- **NumLock 提示依赖系统注入语义**：NumLock 提示由低级键盘钩子驱动，远程桌面会话或部分键盘驱动宏对 NumLock 的特殊处理可能不产生标准键事件，此时提示不出现（物理按键与 SendInput 注入均已验证可用）。

## 文档

- [架构设计](docs/architecture.md)
- [API 说明](docs/api.md)
- [风险评估](docs/risk.md)
