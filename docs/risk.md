# 风险评估与缓解

每个风险条目均注明：风险描述 → 缓解措施 → **当前实现实际覆盖到的层次**（基于本仓库真实代码，未实现的部分如实标注"未覆盖"）。

## 1. Wallpaper Engine 私有格式版本变化

**风险**：WE 的 `.pkg` / `.tex` 是私有格式，官方可能随版本更新改变魔数、条目表结构、纹理压缩方式；repkg（子模块，v0.4.0-alpha，见 `git submodule status`：`8005eb2 ThirdParty/repkg (v0.4.0-alpha)`）对新版本的跟进有滞后。

**缓解措施（已在实现中）**：

- 解析入口校验：包过早结束抛 `InvalidDataException`（src/WinCanvas.Wallpaper/Loaders/PkgWallpaperLoader.cs:52），repkg 内部对未知魔数抛 `UnknownMagicException`（ThirdParty/repkg/RePKG.Application/Exceptions/UnknownMagicException.cs）。
- 单条目隔离：解包循环内 `try/catch` 逐条目吞异常，单个损坏纹理留空不拖垮整包（PkgWallpaperLoader.cs:178-188）。
- 降级链完整：复杂 SceneScript 场景 → 封面图；无视频无可用图 → 空源 + 明确 `StatusMessage`；错误经 `WallpaperService.StatusReported` 上报 InfoBar。
- 缓存复用：解包产物按「文件名 + 头部 1MB SHA256 前 12 位」落盘到 `%LOCALAPPDATA%/WinCanvas/wallpaper-cache/`，同路径不同内容不互相覆盖。
- 解包路径安全：`SafeJoin` 拒绝绝对路径与 `..` 穿越（PkgWallpaperLoader.cs:233）。

**当前覆盖层次**：条目表解析、`.tex`→PNG 解码、视频纹理还原 mp4、scene.json 存在性判定 —— 已覆盖；**未覆盖**：SceneScript 动态场景的动态渲染（.lua / scene.json script 字段场景仅降级封面图，见 README「已知限制」）、scene.json 的结构化解析（目前只做前 2MB 的关键字段文本扫描）。

## 2. Win32 API 兼容性

**风险**：

- `GetWindowLongPtrW/SetWindowLongPtrW` 在 32 位 user32 中不存在，直接 P/Invoke 会在 x86 崩溃。
- `DWMWA_WINDOW_CORNER_PREFERENCE(33)` 是 Windows 11 新属性，旧系统返回 `E_INVALIDARG`。
- 依赖 Win32 返回值约定不一致的 API（如 `SetWindowLongPtr` 失败可能返回 0，而旧值恰好为 0 是合法情况）易误判。

**缓解措施（已在实现中）**：

- 位数兼容封装：`GetWindowLongPtrCompat / SetWindowLongPtrCompat` 按 `IntPtr.Size` 自动选择 `*PtrW` / `*W` 入口（src/WinCanvas.Core/Native/NativeMethods.cs:191-207）。
- 圆角失败显式判定：`DwmSetWindowAttribute` HRESULT 负值即抛 `Win32Exception`（含 HRESULT 值），不会静默失败（src/WinCanvas.Core/Services/WindowControlService.cs:174-179）。
- 旧值 0 歧义处理：写扩展样式前 `Marshal.SetLastSystemError(0)` 清零，仅在「返回 0 且 GetLastError 非 0」时判失败（WindowControlService.cs:199-212）。
- 全部窗口操作失败都以 `Win32Exception` 上抛 → ViewModel 捕获 → InfoBar 提示，UI 不崩溃。

**当前覆盖层次**：user32/dwmapi 调用的返回值检查与错误上抛 —— 已覆盖；**未覆盖**：系统版本探测与功能降级开关（在 Win10 上圆角功能只会报错提示，不会自动隐藏）；未对 `SetWindowPos` 失败做重试或 UIPI 专项处理（见第 4 节）。

## 3. 稳定性：跨进程窗口操作与运行期竞态

**风险**：目标窗口可能在操作过程中被销毁；枚举到的进程可能恰好退出；渲染线程与 UI/线程池并发操作同一会话可能触碰已释放资源；防抖写盘与退出落盘并发写同一文件。

**缓解措施（已在实现中）**：

- 枚举健壮性：`Process.GetProcessById` 对已退出进程抛 `ArgumentException/InvalidOperationException`，捕获后跳过该窗口（src/WinCanvas.Core/Services/WindowControlService.cs:63-71）；排除应用自身句柄防自锁（MainViewModel.cs:199）。
- 渲染会话生命周期：会话字典由 `_gate` 锁保护、临界区只做替换；切换资源时**先摘旧回调再 Dispose**，防止在途帧回调摸到已释放资源（src/WinCanvas.Wallpaper/WallpaperService.cs:87-89）；`WallpaperSession.Dispose` 幂等。
- 渲染线程抗崩：单帧 `FrameTick` 异常被吞掉不杀死线程（WallpaperRenderLoop.cs:105-112）；落后帧直接重新对齐，避免追帧风暴；`IsBackground` 后台线程不阻塞进程退出。
- 配置写入串行化：`SemaphoreSlim(1,1)` 保证防抖写盘与 `FlushAsync` 不并发写同一文件；损坏 JSON 回退默认值。
- 视频状态同步失败安全忽略（播放器可能正在重建 Source，下个状态变化再试，WallpaperService.cs:226-230）。
- 启动恢复风暴（WP3）：档案恢复管线按 200ms 间隔逐个排队、并发上限 2、任务链串行；单窗口判重（会话账本 + 壁纸会话字典）防重复加载；管线异常只记录不反噬 UI；恢复失败撤销判重允许重试（src/WinCanvas.App/ViewModels/MainViewModel.cs，`QueueRestorePipeline` / `RestoreOneAsync`）。
- 启动恢复与 UI 解耦（WP3）：列表先可用、恢复后台进行；属性应用封送 UI 线程（可绑定属性通知的线程约束），Win32 与壁纸加载在线程池。

**当前覆盖层次**：枚举容错、会话生命周期、写盘并发、宿主窗口双路径挂接（A1 嵌入失败自动降级 A2、目标销毁双信号去重清理）与视频组合呈现的异常吞噬、启动恢复管线防风暴 —— 已覆盖；自动化测试骨架已建立（xUnit，档案键语义等核心模型，WP0 起）；**未覆盖**：对已销毁 hwnd 的操作仍会走 Win32 调用并失败报错（没有 hwnd 存活预检查）；宿主挂接的端到端行为由诊断命令（tools/Wp1AHostDiag）实测覆盖，未进 xUnit 套件；恢复管线的端到端行为由诊断命令（tools/Wp3RestoreDiag）+ restore-diag.log 实测覆盖，未进 xUnit 套件。

## 4. 跨进程窗口操作权限

**风险**：

- 对以**管理员权限**运行的窗口，非管理员进程调用 `SetWindowPos` / `SetWindowLongPtr` 会因 UIPI（用户界面特权隔离）失败。
- 直接修改第三方窗口样式（尤其游戏、带反作弊的进程）可能触发误报或被还原。
- 操作其他进程窗口属于灰色地带，需仅用于用户自己的桌面管理。

**缓解措施（已在实现中）**：

- 全部操作失败都会抛 `Win32Exception` 并以 InfoBar 呈现给用户，不会静默半成功。
- 置顶恢复用「读真实状态 + 按需幂等 Set」，界面开关用幂等 Set + 实际状态回读，不一致时有明确提示（src/WinCanvas.App/ViewModels/WindowItemViewModel.cs）。
- 不做代码注入、不挂钩目标进程、不写目标进程内存 —— 只通过 user32 公开 API 操作窗口属性，接触面最小。
- 背景挂接双路径（WP1）：跨进程 SetParent 会把宿主窗口隐式附加进目标的输入队列，个别应用可能出现焦点 / 快捷键异常 —— 已知风险缓解：宿主带 `WS_EX_TRANSPARENT`（不参与命中测试）、异常检测（焦点异常 / 父子断裂自动降级跟随模式）、诊断日志（tools/Wp1AHostDiag）。
- 组合呈现的视频（WP1-C）不经目标进程：`MediaPlayer.GetSurface` 的组合表面由 DWM 直接合成进宿主窗口，与目标进程零交互，规避了帧数据跨进程传递面。

**当前覆盖层次**：错误可见化、背景挂接的 UIPI 失败专项处理（嵌入被拒识别 Win32 错误 5 → 自动降级跟随模式并提示，WP1-D）—— 已覆盖；**未覆盖**：程序自身未做管理员提权（manifest 未声明 requireAdministrator），对提权窗口的操作会失败；未针对反作弊进程做白名单/黑名单。

## 5. 第三方依赖安全与内存问题

**风险**：

- repkg / ImageSharp 解析**不可信文件**（用户从网上下载的 pkg / 图片），历史上有解码器内存安全漏洞；超大图 / 超大包可能造成内存峰值。
- 非托管资源（SoftwareBitmap、MediaPlayer 解码器、渲染线程句柄）泄漏会随时间累积。
- NuGet 依赖版本冲突（构建警告）。

**缓解措施（已在实现中）**：

- 图片导入即缩放：解码后立刻等比 Lanczos 缩放到显示器分辨率（只缩小不放大），4K/8K 原图不直送 GPU，帧缓冲按缩放后尺寸分配（src/WinCanvas.Wallpaper/Loaders/ImageWallpaperLoader.cs:44-56）。
- 资源确定性释放：`WallpaperSource.Dispose` 释放 `SoftwareBitmap` 与 `MediaPlayer`；会话切换即释放旧资源；`WallpaperRenderLoop.Dispose` 只发停止信号，规避 `ObjectDisposedException`（src/WinCanvas.Wallpaper/Rendering/WallpaperRenderLoop.cs:121-127）。
- 视频懒加载：`MediaSource.CreateFromUri` 按需流式读取，不整读视频进内存（VideoWallpaperLoader.cs:47）；pkg hash 只读头部 1MB（PkgWallpaperLoader.cs:141）。
- 依赖收敛：ImageSharp 与 repkg 锁同一版本 2.1.9，避免同依赖双版本。
- 解包目录名含内容 hash、条目路径过 `SafeJoin` 防穿越写入。

**当前覆盖层次**：确定性 Dispose、缩放降内存、路径安全 —— 已覆盖；**未覆盖**：ImageSharp / repkg 无已知的 CVE 专项审查；**实测构建存在 2 个 MSB3277 警告**（`dotnet build WinCanvas.sln -c Debug`，2026-11-01）：`System.Drawing.Common 9.0.0` 传递依赖与 `System.Reflection.Metadata 8.0/9.0` 版本冲突，当前运行未受影响，但升级依赖时需留意。

## 6. 风险矩阵汇总

| 风险域 | 缓解覆盖 | 未覆盖（当前缺口） |
| --- | --- | --- |
| WE 私有格式变化 | 条目级容错、降级链、缓存复用 | SceneScript 动态渲染、scene.json 结构化解析 |
| Win32 兼容性 | 位数兼容、HRESULT/GetLastError 判定 | Win10 降级开关、版本探测 |
| 稳定性/竞态 | 枚举容错、会话锁、防抖串行写盘、宿主挂接双路径降级 | hwnd 存活预检查 |
| 跨进程权限 | 错误可见化、纯公开 API、无注入、背景挂接 UIPI 降级 | 提权、反作弊规避 |
| 背景上屏（WP1 新增） | 宿主双路径（嵌入/跟随自动降级）、DComp/组合呈现、设备失陷重建、跨屏重缩放 | 跨进程 SetParent 输入队列附加的个别应用兼容性（缓解=自动降级跟随模式） |
| 全系统键盘输入（WP2 新增） | LL 钩子装在专用消息线程（绝不占 UI 线程）、回调只做微秒级判定（读一次 GetKeyState + 去抖比较）后立即 `CallNextHookEx` 放行、绝不吞键；重活（进程启动）全部派发线程池；状态去抖防提示风暴；显式 Unhook + 进程退出双保险 | 回调内异常已被捕获但系统侧超时摘钩（LowLevelHooksTimeout）无法从托管侧探测；远程桌面 / 非标准键盘驱动可能不产生标准 NumLock 键事件（提示不出现，无副作用） |
| 启动自动恢复（WP3 新增） | 200ms 间隔排队 + 并发上限 2 + 任务链串行（防启动风暴）；会话判重账本 + 壁纸会话字典判重（幂等，不新增锁体系）；列表先可用、恢复后台化（不阻塞首帧）；属性应用封送 UI 线程；背景文件缺失只警告并保留档案；管线异常只记录不反噬 UI；开关关闭整条管线跳过 | 启动瞬间的窗口枚举快照与恢复之间目标可能销毁（Win32 调用失败走既有错误上报路径，可重试）；同 Key 多窗口共享档案的既有取舍（见 README「已知限制」） |
| 第三方/内存 | 确定性 Dispose、导入缩放、路径安全 | CVE 专项审查、MSB3277 警告收敛 |
| 功能完整性 | 图片 / 视频 / pkg 封面上屏到目标窗口（宿主双路径，见 README「背景上屏」）；NumLock 键盘钩子与 WinTips 提示链路（WP2，见 architecture.md §4）；状态重启自动恢复管线（WP3，见 architecture.md §5） | SceneScript 动态渲染（见 README「已知限制」） |
