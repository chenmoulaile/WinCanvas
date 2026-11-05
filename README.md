# WinCanvas

高性能 Windows 窗口管理工具：对任意窗口实现置顶 / 透明 / 鼠标穿透 / 圆角，并支持独立壁纸背景（图片、视频、Wallpaper Engine 的 `.pkg` 与**本地项目目录**），内置壁纸库自动检索、系统托盘、17 条可自定义全局快捷键与 WinTips 操作提示。

- 平台：Windows 11 x64（最低目标 Windows 10 1809 / 10.0.17763，圆角功能需 Windows 11）
- 技术栈：C# / .NET 10 / WinUI 3 / Win32

> v0.2.0 起从 .NET 8 迁到 **.NET 10**：.NET 8 与 9 于 2026-11-10 终止支持，
> 而本项目的发布产物把运行时打进包内（self-contained），继续钉在 EOL 运行时上
> 意味着分发出去的是一份不再收安全更新的应用。

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
4. **动态帧率**：三档状态机（`DynamicFrameRateController`）—— 窗口激活 60fps / 非激活 15fps /
   检测到全屏前台窗口或用户暂停时完全暂停；全屏与激活态检测每 250ms 节流执行；暂停时同步暂停视频解码、GIF 定格。
   v0.2.0 接线修正：「非激活 15fps」档此前在产品代码里**永不生效**（`SetWindowActive` 全仓只有测试调用），
   现由渲染循环的 `ActiveProbe` 每 250ms 探测目标窗口是否持有前台焦点来驱动。

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
- **开关**：设置页「启动时自动恢复窗口状态」（默认开启，`AppConfig.AutoRestoreOnStartup`）；
  关闭后启动与手动刷新都不做任何恢复动作，档案仍保留在配置中。
  **关闭时会在界面上说明**（v0.3.2）：档案里存着背景壁纸而开关关着时，InfoBar 每次会话提示一次
  「已关闭…配置里存有 N 份档案…需要在设置页打开」。此前这条分支只写日志、界面一字不提，
  而日志在 `%LOCALAPPDATA%` 里没人会去翻 —— 用户看到的就是「挂好的壁纸重启后全没了，
  什么功能都像失效」，沉默会被读成故障。
- **诊断**：管线打点写入 `%LOCALAPPDATA%\WinCanvas\logs\restore-diag.log`
  （进程启动 / 列表可用 / 单窗口恢复 / 幂等跳过 / 全部完成，含耗时）。

### 系统托盘与全局快捷键（src/WinCanvas.App/Services，v0.2.0 新增）

- **系统托盘图标**（`TrayIconService`）：`Shell_NotifyIcon` + 一个隐藏消息窗口
  （`MessagePumpWindow`，与全局热键共用同一条泵线程，进程只多一条线程、多一个 HWND）。
  左键单击切换主界面显示/隐藏；右键（或中键）弹菜单：显示/隐藏主窗口、刷新窗口列表、
  暂停/恢复壁纸渲染、开/关 NumLock 提示、退出。
- **关闭语义**：点窗口 X 在有托盘时**不再结束程序**，而是收进托盘（进程继续看着已设属性
  与壁纸宿主）；真退出走托盘菜单「退出」。
- **全局快捷键**（`GlobalHotkeyService` + `HotkeyTable`）：17 条可自定义组合键，分两组作用对象。
  程序在后台或收进托盘、**焦点在任意窗口（含非本应用窗口）时同样生效**——注册在专用消息线程的
  隐藏窗口上（`MessagePumpWindow`，GetMessage 循环），与应用是否聚焦无关。默认值：

  作用于「主界面列表选中的窗口」（`Ctrl+Alt+` 系列）：

  | 功能 | 默认组合 |
  | --- | --- |
  | 置顶切换 | `Ctrl+Alt+T` |
  | 提高 / 降低透明度 | `Ctrl+Alt+↑` / `Ctrl+Alt+↓` |
  | 鼠标穿透切换 | `Ctrl+Alt+K` |
  | 圆角循环（无→大→小→无） | `Ctrl+Alt+R` |
  | 刷新窗口列表 | `Ctrl+Alt+F` |
  | 壁纸渲染暂停/恢复 | `Ctrl+Alt+W` |
  | 清除所选窗口背景 | `Ctrl+Alt+X` |
  | 显示 / 隐藏主界面 | `Ctrl+Alt+C` |
  | NumLock 提示开关 | `Ctrl+Alt+N` |
  | 壁纸声音开/关（v0.3.0） | `Ctrl+Alt+M` |

  作用于「当前焦点窗口」（`Win+Alt+` 系列，v0.3.3 新增，目标 = `GetForegroundWindow()`，
  对本应用自身窗口不生效）：

  | 功能 | 默认组合 |
  | --- | --- |
  | 置顶切换 | `Win+Alt+T` |
  | 鼠标穿透切换 | `Win+Alt+P` |
  | 提高 / 降低透明度（步进 ±10） | `Win+Alt+↑` / `Win+Alt+↓` |
  | 恢复不透明 | `Win+Alt+O` |
  | 圆角循环（无→大→小→无） | `Win+Alt+U` |

  改键方式：主界面「全局快捷键」区点对应行的组合键按钮，然后直接按下新组合
  （`Esc` 取消）。校验规则：**必须至少含 Ctrl 或 Alt**——全局热键抢在焦点窗口之前，
  允许单键等于让用户在别的程序里突然按不动那个键。被其它程序占用（Win32 错误 1409）时
  只让那一条失败并在界面上标出「请换一个组合或关闭该条」，不影响其余热键。
- **单实例互斥**：第二个实例不再重复起界面，而是唤醒第一个实例的主窗口后自己退出
  （迁移前两实例同写一份 `config.json`，互相覆盖，表现为「设置莫名回滚」）。
- **窗口搜索**：左侧列表上方可按标题 / 进程名 / 类名子串过滤。
- **列表项的可访问名字**（v0.3.2 修）：窗口行与壁纸库条目各覆写 `ToString()`
  （`标题 — 进程 · 类名` / `标题 — 类型`）。WinUI 在 `DataTemplate` 没给容器显式 Name 时
  回落到 `ToString()`，不覆写就是每一行都念成
  `WinCanvas.App.ViewModels.WindowItemViewModel` —— 屏幕阅读器与任何按名字定位行的自动化
  都用不了这两个列表（实测 UIA 树里确实全是类型名）。

### Wallpaper Engine 项目目录与壁纸库（src/WinCanvas.Wallpaper，v0.3.0 新增）

此前只吃单个 `.pkg` 文件，而 Wallpaper Engine 电脑端的**本地/创意工坊壁纸是一个目录**
（`project.json` + `scene.pkg` / `video.mp4` / `web.json` + `preview.gif`），
用户要么在文件选择器里找不到入口，要么选中的是预览图而不是壁纸本体。

- **项目目录识别**（`WallpaperProjectReader` + `WallpaperProjectLoader`）：
  读 `project.json` 判定形态并分派 —— `scene` → 解包同目录 `scene.pkg` 走 pkg 层；
  `video` → 直接走视频层；`web` → **不渲染网页**，退化为预览图（明确提示原因，
  因为网页壁纸需要浏览器/JS 运行时，不在本项目射程内）；`audiofilter` 与无法识别的类型
  给出人话拒绝理由。`filetype` 大小写不敏感（真实库里同时存在 `scene`/`Scene`、`video`/`Video`）。
  解析器**永不抛异常**：非法 JSON 返回 `ParseError` 描述，条目在库里标为不可用而不是让面板崩掉。
- **安全**：`project.json` 是第三方文件，其中 `general.image` / `videoaudiopath` 之类
  的相对路径在拼接前做目录穿越检查（`ResolveRelative`），`..\..\..\Windows\x` 一律拒绝。
- **原路径归一**：项目条目的 `OriginalPath` 永远指向**项目目录**，因此配置里记的、
  启动时恢复的也是目录 —— 修掉「目录型壁纸重启后被判成『背景文件已不存在』而不恢复」
  （`WallpaperFormats.IsAvailableBackgroundPath` = 文件存在 **或** 是可用项目目录）。
- **壁纸库自动检索**（`WallpaperEngineLibrary`）：`DetectRoots()` 依次试
  注册表 `Steam App 431960` 的 InstallLocation（HKLM 64/32 + HKCU）、常见 Steam 安装路径、
  `libraryfolders.vdf` 里的全部库、`workshop/content/431960`；探测不到就在界面上手填目录
  （`WallpaperLibraryRoots` 存进配置，用户根优先、自动探测兜底、去重）。
  扫描只下探一层子目录（WE 的库本身就是 `<根>/<id>/project.json`），
  实测**真实库 96 条耗时 27ms**（78 个项目目录 + 18 个散装媒体文件；
  scene 40 / video 33 / web 4 / 无法识别 1，其中 95 条可挂接）。
  「扫描判定可用」与「加载器真的能用」由 `tools/WvLibraryDiag` 逐条交叉校验，防止列表里
  出现点下去才报错的条目。
- **缩略图**：库里条目上百个，构造时全解码会在打开面板的一瞬间把几十张 GIF 推进内存
  （实测私有内存 48MB → **247MB**）。改由 View 的 `Loaded` 事件按需解码，
  并用 `SemaphoreSlim(1,1)` 串行化 + 解码后立即缩到 120px 存 PNG 再回 UI 线程，
  回落到 102MB。
- **壁纸声音开关（默认关闭）**：迁移前 `VideoWallpaperLoader` 把 `IsMuted` 硬编码为 `true`，
  带配乐的壁纸**没有任何办法出声**。现在有 `WallpaperAudioEnabled`（配置字段
  `Version 2` 起持久化 + `Ctrl+Alt+M` 全局切换），开启后按 60% 音量播放
  （`VideoWallpaperLoader.DefaultPlaybackVolume`，可在系统音量合成器里单独调），
  关闭时 `IsMuted` 即时作用到**正在播放**的会话，不需要重新挂一次壁纸。
  真实库里 77/96 条带音频线索（`project.json` 里出现 audio / volume / music / bgm 等关键字），
  这些条目在面板上标 ♪，静音时在说明行里解释「为什么这个壁纸没声音」而不是让人以为是 bug。

### 其他

- **FPS 性能监控悬浮显示**（开发阶段诊断用）：`CompositionTarget.Rendering` 帧计数 + 每秒取样，主界面开关控制。开关关闭时会**退订**每帧回调（v0.2.0 修正：旧实现用 lambda 订阅静态事件且永不退订，关掉后每帧回调仍终身存在，并经静态事件根钉住整个 UI 对象图）。

## 前置条件

| 依赖 | 说明 |
| --- | --- |
| Windows 11 x64 | 开发与运行目标环境（圆角功能依赖 Win11 DWM 属性；Win10 1809+ 可运行，Mica 自动降级为亚克力/纯色） |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | **必需**。本仓库在 10.0.403 上构建验证通过；`global.json` 钉住该版本（`rollForward: latestFeature`）。同时用于构建 WinTips 子模块（net10.0-windows） |
| Windows App SDK | 通过 NuGet 包 `Microsoft.WindowsAppSDK 1.8.260921001` 自动还原；配置 `WindowsAppSDKSelfContained=true` + `SelfContained=true` + `WindowsPackageType=None`（非打包部署），目标机器**无需**安装 .NET 运行时或 Windows App Runtime |
| git | 用于拉取两个子模块（repkg、WinTips） |

> 版本单一来源：全仓库版本号只写在 `Directory.Build.props` 的 `<WinCanvasVersion>`。
> exe 的 `AssemblyVersion` / `FileVersion` / `ProductVersion` 与安装包的
> `AppVersion` / `VersionInfoVersion` 都由它派生（CI 从同一处读取后传给 ISCC）。

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
# 产物位于 src/WinCanvas.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/WinCanvas.App.exe
```

> 验证手段：`dotnet build WinCanvas.sln`（0 error 为过）+ `dotnet test`（xUnit，两个测试项目共 186 个用例，见 `tests/`）：
> - `tests/WinCanvas.Core.Tests`（63）：配置持久化（防抖合并 / 原子写 / 无 BOM / **活对象撕裂** /
>   **CTS 世代竞态** / `.bak` 恢复 / 坏文件隔离 / Dispose 兜底落盘）、窗口档案键语义、
>   `WindowControlService` 失败语义与真实窗口行为冒烟、NumLock 去抖纯逻辑、UIPI 错误 5 的提权引导文案映射、
>   **全局热键表**（默认补齐 / 用户值不被覆盖 / 组合键文本化 / 三类校验 / 默认表自身无冲突 / 目录覆盖全部动作）；
> - `tests/WinCanvas.Wallpaper.Tests`（123）：动态帧率状态机真值表、pkg 加载器纯逻辑（路径安全 / 场景判定 / 封面优先级）、
>   宿主挂接矩形与 DPI 换算纯函数、扩展名单一来源口径（`WallpaperFormats`）、合成动画 GIF 的多帧解码与帧延迟往返、
>   静态 WebP 解码、**壁纸呈现路径分派**（`Kind` 与 `HasVideoPlayer` 互不蕴含 —— 钉住 pkg 内嵌视频不再被漏判）、
>   **WE 项目兼容**（`project.json` 大小写 / 中文与方括号文件名 / 目录穿越拒绝 / 非法 JSON 不抛 /
>   四种形态的加载器分派与降级理由 / 扫描分类与去重 / `IsProjectDirectory` /
>   音频线索关键字判定 / `IsMuted` 与音量随开关变化、默认音量 < 1.0）、
>   **WinTips 外挂可启动性探测**（`runtimeconfig.json` 单数 `framework` 与复数 `frameworks[]`
>   两种写法、必须挑 `Microsoft.WindowsDesktop.App` 而非基础运行时、self-contained 同级 `coreclr.dll`、
>   探测失败绝不启动进程而是给出原因）。
>   需要真实桌面会话的用例打 `[Trait("Category", "RequiresWindowsSession")]`，无头环境用
>   `dotnet test --filter "Category!=RequiresWindowsSession"` 排除（不判失败）。
>   **注意**：这批用例在无会话环境下是【构造失败】而非跳过（有意设计，见该测试类注释），
>   所以 CI 的默认测试步骤必须带 `--filter`。
>
> CI：`.github/workflows/build-and-test.yml`（`windows-latest`，构建 + headless 测试 +
> 诊断工具编译检查 + 发布产物齐备校验 + 安装包编译）。`tools/` 下 6 个诊断工程不在
> `WinCanvas.sln` 里，由 CI 单独编译以防无声腐烂。

## 界面结构（v0.3.1）

主窗口左侧是导航栏，三页共享同一个 `MainViewModel`（页面经依赖属性注入，**不存在第二份状态**）：

| 页面 | 内容 |
| --- | --- |
| **窗口与壁纸** | 左列窗口列表（搜索 + 刷新）→ 选中后右侧显示窗口属性（置顶 / 透明 / 穿透 / 圆角）与壁纸挂接区，下方是壁纸库面板 |
| **设置** | 全局 / 软件级开关：当前权限、启动自动恢复、WinTips 提示、**壁纸声音**、FPS 监控，以及完整的全局快捷键表（改键 / 停用 / 恢复默认 / 应用） |
| **关于** | 版本与提交哈希、.NET / Windows App SDK / 系统 build、分发形态（是否 self-contained）、配置与日志的绝对路径 + 一键打开目录、第三方组件与仓库链接 |

三页用**可见性切换**而不是 `Frame.Navigate`：换页会把窗口列表的选中态、壁纸预览控件、
库面板的滚动位置全部重建，而那些正是用户期望「切回来还在」的状态。

## 使用说明

1. 启动 `WinCanvas.App.exe`，「窗口与壁纸」页左侧自动列出所有可见顶层窗口（点「刷新」重新枚举）。
2. 选中一个窗口，右侧详情页可调整：
   - **置顶**开关、**透明度**滑块（0~255）、**鼠标穿透**开关、**圆角**（无 / 大圆角 / 小圆角）；
   - 所有改动立即应用到目标窗口并实时写入配置；**完全退出并重启应用后，这些设置连同背景自动恢复**
     （见「状态重启自动恢复」一节；重启后按「进程名|类名」组合键重新定位窗口，窗口句柄变化不影响恢复）。
3. **背景壁纸**：点「选择背景文件」挑选图片 / 视频 / `.pkg` 文件，或直接在下方**壁纸库**里点条目
   （GIF 动画按帧延迟连续播放，webp 静态图支持），资源在后台线程加载，进度条与状态提示实时显示；
   画面直接呈现在目标窗口内部（嵌入 / 跟随双模式自动选择，见「背景上屏」一节），
   窗口移动 / 缩放实时跟随，全屏前台窗口出现时视频自动暂停、GIF 定格。
4. **设置页**：「启动时自动恢复窗口状态」控制启动 / 刷新时的档案恢复管线（默认开启）；
   「WinTips 提示」控制 NumLock 浮窗（默认关闭；开启即安装 LL 键盘钩子，按下 NumLock 后 2 秒内弹出
   WinTips 浮窗，**WinTips 不可用时回退为托盘气泡**，没有托盘才退回 InfoBar；关闭即卸钩）；
   「壁纸声音」默认关闭；「FPS 性能监控悬浮显示」在界面右上角显示渲染帧率；
   全局快捷键表在这里改键（点组合键按钮后直接按下新键，Esc 取消）。
5. **权限说明（UIPI）**：目标窗口以更高权限（如管理员）运行时，Windows 的 UIPI 机制会拒绝本程序的跨进程操作（置顶 / 透明 / 穿透 / 背景挂接）。相关操作失败（Win32 错误 5 = 拒绝访问）时 InfoBar 会给出明确引导：「目标窗口以更高权限运行，请以管理员身份重启 WinCanvas 后重试」；设置页顶部显示本程序的当前权限状态（管理员 / 标准），关于页也有一行。背景挂接在嵌入被拒时自动降级为跟随模式，无需手动干预。

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
- [第三方组件与许可证声明](THIRD-PARTY-NOTICES.md)

## v0.2.0 / v0.3.0 新增内容的已知限制

### 托盘与全局快捷键（v0.2.0）

- **全局快捷键可能被其它程序占用**：注册失败（Win32 错误 1409）时只有那一条不生效，
  界面会标出「已被系统或其它程序占用，请换一个组合」，其余热键不受影响。
- **默认组合键要求至少含 Ctrl 或 Alt**：这是刻意的校验，不是限制 ——
  全局热键抢在焦点窗口之前，允许单键或仅 Shift 做全局热键会让用户在其它程序里按不动那个键。
- **托盘图标默认在通知区域折叠面板里**：Windows 对新注册图标的默认行为，
  需要常驻可见请在系统设置「其它系统托盘图标」里把它拖出。
- **托盘与热键共用一条消息线程**：该线程若被模态菜单长时间占用，热键注册会延后；
  注册调用带 5 秒超时并在超时后如实报告，不会静默假装成功。
- **注册结果只落到诊断日志**：`%LOCALAPPDATA%\WinCanvas\logs\crash.log` 里每次应用绑定表
  会写一行「成功 N 条，失败 M 条（原因）」。用户收进托盘后看不到界面红字时，这是唯一入口。

### Wallpaper Engine 兼容与壁纸库（v0.3.0）

- **网页型（`web`）壁纸不渲染**：这类壁纸靠浏览器 + JS 运行时（常带 Steam Overlay
  与 CEF 自定义 API），本项目不内嵌浏览器内核，选中后**只显示 preview 图并说明原因**。
  真实库里 4/96 条属于这种。
- **`audiofilter` 与无法识别的项目类型直接拒绝**并给出理由，不做「猜一个能播的文件」——
  猜错时用户看到的是莫名其妙的静态图，比一条明确失败更难归因。真实库里 1/96 条属于这种。
- **声音开关只覆盖视频层音轨**：`video` 型项目与散装视频文件的音轨受
  `WallpaperAudioEnabled` 控制；`scene`（pkg）里打包的音频资源当前不播放
  （repkg 解出的是纹理与视频，音频播放在本项目射程外）。「含音频线索」标记
  来自 `project.json` 的关键字判断，代表**该壁纸在 WE 里可能发声**，不代表本程序能播。
- **自动探测只覆盖 Steam 安装**：`DetectRoots()` 认注册表 `Steam App 431960`、
  标准 Steam 路径与 `libraryfolders.vdf`。非 Steam 部署（自行移动到别的盘、
  或用 `projects/myprojects` 直接开发的工作副本）**探测结果为 0 个根** ——
  实测本机即如此（96 条的库、自动探测 0 根），界面会明确提示手填目录，
  填进去之后与自动探测到的根完全等价。
- **扫描只下探一层**：WE 的库布局是 `<根>/<id>/project.json`，因此不递归；
  把项目再套一层子目录的自制结构不会被发现（也不会误报，属于「看不到」而非「看错」）。
- **缩略图串行解码**：库里条目上百个时缩略图逐个出现（一次只解一张），
  这是刻意的内存取舍 —— 并行解码实测把私有内存从 48MB 顶到 247MB。
- **仓库根尚无自身 LICENSE 文件**：两个子模块均为 MIT（不含传染性许可证），
  但本项目自身的授权由维护者决定，见 `THIRD-PARTY-NOTICES.md`。
