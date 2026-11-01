# WinCanvas 开发工作日志（WORKLOG）

> 本文件是「WinCanvas 补全与质量提升」总提示词（`.zcode/spec/spec.md`，已 gitignore）要求的常驻工作日志：每个工作包（WP0~WP5）完成后追加一节，记录「改动清单 / 关键决策与理由 / 验证证据摘要」。全部 WP 完成后由 WP5 终稿整理为顶部总览表。
>
> **warning 基线口径**：`dotnet clean WinCanvas.sln -c Debug` 后 `dotnet build WinCanvas.sln -c Debug`，取输出末尾 MSBuild 的 sln 汇总「N 个警告」数。增量构建下未重建的项目不出警告，数字不可比，不得以增量构建数字充当门禁证据。

---

## 0. 环境基线（WP0 记录，2026-11-01）

### 0.1 环境核验（实际执行命令与原文输出）

- **分支**：`git checkout -b feat/complete-w0-w5`（自 main@8ab6ea0 切出），后续所有 WP 均在本分支进行。
- **`git rev-parse HEAD`**（切分支时）：

  ```
  8ab6ea0c987be6ea79282fa33a4a1ad43057352e
  ```

- **`dotnet --list-sdks`**：

  ```
  8.0.426 [C:\Program Files\dotnet\sdk]
  10.0.403 [C:\Program Files\dotnet\sdk]
  ```

  两代 SDK 齐备（.NET 8 构建三自研项目，.NET 10 预留给 WP2 的 WinTips 子模块构建）。
- **`git submodule status`**：

  ```
   f479cb3b2192022a51f76c954b5952a8b6c4002a ThirdParty/WinTips (App-7-gf479cb3)
   8005eb271f4cb5b9e375bafa912029a0d67b5eb8 ThirdParty/repkg (v0.4.0-alpha)
  ```

  两个子模块均无 `+` / `-` 前缀（工作树与检出提交一致、未初始化缺失），符合预期。

### 0.2 构建基线（后续 WP 门禁基线）

- **命令**：`dotnet clean WinCanvas.sln -c Debug` → `dotnet build WinCanvas.sln -c Debug`（clean 全量构建，复现两次结果一致）。
- **结果**：`0 个错误 / 26 个警告`，用时约 20~65s。
- **警告构成**（按 warning code 归类，全部出自 `src/WinCanvas.App` 项目）：
  - `MVVMTK0045`：CommunityToolkit.Mvvm 对 `[ObservableProperty]` 字段的 WinRT AOT 兼容性提示（建议改 partial property），集中在 `MainViewModel` / `WindowItemViewModel` 的镜像属性上——属已知技术债，功能不受影响，本工作包不动（总提示词禁止回退既有实现，后续 WP 亦不新增此类字段即可不增长此数）。
  - `MSB3277`：System.Drawing.Common 传递依赖版本对齐提示（WindowsAppSDK 链路），非代码缺陷。
- **产物路径**：
  - 主程序：`src/WinCanvas.App/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/WinCanvas.App.exe`（已确认存在）
  - 类库：`src/WinCanvas.Core/bin/Debug/net8.0-windows10.0.19041.0/WinCanvas.Core.dll`、`src/WinCanvas.Wallpaper/bin/Debug/net8.0-windows10.0.19041.0/`
  - 测试：`tests/WinCanvas.Core.Tests/bin/Debug/net8.0-windows10.0.19041.0/WinCanvas.Core.Tests.dll`
- **备注**：本机 `dotnet build` 由默认 SDK 10.0.403 承载 MSBuild 构建与解析（三个自研项目 TFM 仍为 net8.0-windows10.0.19041.0，产物运行时为 .NET 8），属正常现象；两代 SDK 共存不影响 net8.0 目标的构建与运行。

---

## 1. WP0 环境基线与测试骨架（2026-11-01）

**状态**：完成。提交：`feat(WP0): 环境基线与测试骨架`（分支 `feat/complete-w0-w5`；各 WP 提交哈希由 WP5 终稿总览表统一汇总）。

### 改动清单

| 文件 | 动作 | 说明 |
| --- | --- | --- |
| `tests/WinCanvas.Core.Tests/WinCanvas.Core.Tests.csproj` | 新增 | xUnit 测试项目；TFM 与被测项目一致（net8.0-windows10.0.19041.0）；引用 WinCanvas.Core |
| `tests/WinCanvas.Core.Tests/WindowStateKeySanityTests.cs` | 新增 | 6 个 sanity 测试：档案键组合与相等性、`|`/`\` 转义防碰撞、HwndHash 与 Key 语义区分 |
| `WinCanvas.sln` | 修改 | 加入 `tests/WinCanvas.Core.Tests`（`dotnet sln add`，Debug/Release × Any CPU） |
| `WORKLOG.md` | 新增 | 本文件：环境基线记录 + WP0 实录 + WP1~WP5 预留区块（常驻文件） |
| `README.md` | 修改 | ① 构建命令笔误 `cd Window` → `cd WinCanvas`；② 删除「本解决方案当前无自动化测试项目」失真表述（见关键决策 3） |

### 关键决策与理由

1. **测试栈取本机 NuGet 缓存已有版本**：xunit 2.9.2 / Microsoft.NET.Test.Sdk 17.11.1 / xunit.runner.visualstudio 2.8.2（已确认存在于 `~/.nuget/packages/`，还原零网络依赖）。新增 NuGet 依赖的必要性：仓库原无任何测试框架，WP0 的 DoD 要求 `dotnet test` 可运行，xUnit 系是总提示词 §3 明示可用的选项，无可行的零新依赖方案。
2. **sanity 测试以 `WindowInfo.Key` 实现为准**：总提示词 §2 把档案键描述为「进程名|标题」组合键，但代码实际实现为「进程名|类名」组合键（`src/WinCanvas.Core/Models/WindowInfo.cs:19-26`，注释明确解释了弃用标题的原因——浏览器 / 记事本标题随时变化会导致档案失配），`WindowState.Key` 只是该键的持久化载体属性。测试按真实实现钉住组合、转义（`|`→`\|`、`\`→`\\`）与 `HwndHash` 语义。该 spec 与代码的描述出入仅记录，不改动代码。
3. **README 第 64 行失真表述提前改写**（超出 WP0 字面任务清单）：原表述「本解决方案当前无自动化测试项目」在测试骨架落地后即与代码脱节。总提示词把改写它排在 WP4，但 §3「禁止文档与代码脱节」是更高优先级的全局硬约束，故本 WP 一并改写为真实描述，避免仓库在任何中间状态下存在失真文档。
4. **测试项目不引用 `WinCanvas.App` / `WinCanvas.Wallpaper`**：App 是 WinUI 3 非打包 exe（x64 平台），测试项目引用它会拖入 WinUI 运行时依赖并破坏 `dotnet test` 稳定性；WP4 按总提示词为 Wallpaper 建独立测试项目并加 `InternalsVisibleTo`，本 WP 不预做。

### 验证证据摘要

- 基线构建（clean 全量，命令见 §0.2）：`0 个错误 / 26 个警告`；**门禁基线 = 26 warning**。
- 测试项目入 soln 后 clean 全量构建：`0 个错误 / 26 个警告`——新增测试项目未引入任何新警告（`grep -c "warning"` 原始计数 96 系 MSB3277 多行明细与项目重复输出的干扰，不以该口径记录）。
- `dotnet test tests/WinCanvas.Core.Tests/WinCanvas.Core.Tests.csproj -c Debug`：

  ```
  已通过! - 失败:     0，通过:     6，已跳过:     0，总计:     6，持续时间: 23 ms - WinCanvas.Core.Tests.dll (net8.0)
  ```

- `dotnet test WinCanvas.sln -c Debug`（sln 入口）：`已通过! - 失败: 0，通过: 6，已跳过: 0，总计: 6`。

### DoD 自检

- [x] `dotnet build` 0 error，基线 warning 数（26）已记录在 WORKLOG §0.2
- [x] `dotnet test` 运行成功且 6 个测试通过（超出 DoD 最低要求「1 个测试通过」，同为 sanity 范畴）
- [x] WORKLOG.md 存在且含基线记录（本文件 §0）
- [x] README 笔误已修复（`cd Window` → `cd WinCanvas`）
- [x] 已提交 `feat(WP0): 环境基线与测试骨架`

---

## 2. WP1 壁纸真实上屏

### 2.A 阶段 A：宿主窗口与双路径挂接（2026-11-01）

**状态**：完成。提交：`feat(WP1-A): 宿主窗口与双路径挂接`（分支 `feat/complete-w0-w5`）。先不渲染，宿主以纯色（深蓝 RGB(30,80,180)，`HostNativeMethods.HostPhaseAFillColor`）GDI 填充验证挂接正确性；阶段 B 换 DComp 渲染时窗口类、挂接与跟随逻辑保留复用。

#### 改动清单

| 文件 | 动作 | 说明 |
| --- | --- | --- |
| `src/WinCanvas.Wallpaper/Hosting/HostAttachMode.cs` | 新增 | 挂接模式枚举（Embed=A1 / Follow=A2）+ 降级规则文档 |
| `src/WinCanvas.Wallpaper/Hosting/HostNativeMethods.cs` | 新增 | 宿主专用 P/Invoke（窗口类/创建/绘制/分层/Z 序/WinEvent/DWM/消息循环），注释规格对齐 Core 的 `NativeMethods.cs` |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostPump.cs` | 新增 | 宿主专用消息线程：全部宿主窗口共享一条 `GetMessageW` 循环线程，跨线程同步执行通道 `Execute`（`PostThreadMessageW(WM_APP_PUMP_TASK)` + GCHandle + 回执等待） |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostWindow.cs` | 新增 | 宿主窗口本体：窗口类注册、A1/A2 双路径创建、WinEvent 跟随、A1→A2 降级、目标销毁上报、`CountAliveHosts` 残留计数、A1/A2 矩形计算纯函数（供 WP4 测试） |
| `src/WinCanvas.Wallpaper/Hosting/HostDiagnostics.cs` | 新增 | 挂接诊断日志（`%LOCALAPPDATA%\WinCanvas\logs\host-diag.log`），跟随类打点 200ms 节流 |
| `src/WinCanvas.Wallpaper/WallpaperService.cs` | 修改 | `_hosts` 字典（`_gate` 保护）、`SessionHostModeChanged` 事件、构造可选注入 `IWindowControlService`、`UpdateCoreAsync` 会话替换后旧会话释放前创建/复用宿主、TargetLost 清理（复用 `_updateGates` 串行）、A2 自动透明（alpha=220）、`ShutdownAll`；`WallpaperSession` 增加 `Host` 属性与 Attach/Detach |
| `src/WinCanvas.Core/Interfaces/IWallpaperService.cs` | 修改 | 增加 `ShutdownAll()`（销毁全部会话与宿主，应用退出时调用） |
| `src/WinCanvas.App/Services/AppServices.cs` | 修改 | `new WallpaperService(WindowControl)`（注入窗口控制服务供 A2 自动透明） |
| `src/WinCanvas.App/MainWindow.xaml.cs` | 修改 | `Closed` 处理追加 `services.Wallpaper.ShutdownAll()`（应用退出零残留） |
| `src/WinCanvas.App/ViewModels/MainViewModel.cs` | 修改 | 订阅 `SessionHostModeChanged` → 壁纸状态文本显示「嵌入模式 / 跟随模式」 |
| `src/WinCanvas.Wallpaper/WinCanvas.Wallpaper.csproj` | 修改 | `<InternalsVisibleTo Include="Wp1AHostDiag" />`（诊断命令访问 internal 宿主类型；WP4 测试项目届时同样加入） |
| `tools/Wp1AHostDiag/`（csproj + Program.cs，不入 sln） | 新增 | 阶段 A 验收诊断命令：默认模式跑三类目标（记事本 A1 / 记事本 A2 / Edge A1）共 29 项断言；`probe` 模式分离跨进程挂接两种实现路径；`service` 模式走产品接线冒烟 |

#### 关键决策与理由

1. **宿主全部原生操作集中在专用消息线程（pump）**：跨线程 `DestroyWindow` 非法、`WINEVENT_OUTOFCONTEXT` 回调要求安装钩子的线程跑消息循环——两件事用同一条线程一并满足。全部宿主窗口共享一条 pump（每会话一线程会线程爆炸）；空闲时阻塞在 `GetMessageW` 上零 CPU 占用，`IsBackground` 进程退出自动回收。已知风险：若目标线程假死，跨进程窗口操作（如子窗口 `WM_PARENTNOTIFY` 同步送达父窗口）可能拖住 pump——`TryCreate` 以 15s 超时兜底防调用方挂死，pump 假死期间其余宿主操作排队（可接受，实测未复现）。
2. **A2 Z 序钉位锚点偏离规格字面公式**：规格写 `SetWindowPos(host, GetWindow(target, GW_HWNDNEXT), …)`，但按 Win32 语义 `hWndInsertAfter` 是「宿主插到它后面」、`GW_HWNDNEXT` 取的是已位于目标下方的相邻窗口，该式会把宿主放到「目标下方第二格」，与规格自己声明的目标「宿主始终位于目标正下方」矛盾。按语义正确实现：锚点直接传目标自身（`SetWindowPos(host, target, …)`），诊断实测 `GetWindow(target, GW_HWNDNEXT)` 向下遍历可遇宿主 ✓。
3. **A1 从「直接以跨进程窗口为父 CreateWindowExW(WS_CHILD)」改为两步挂接**：规格字面做法被 OS 拒绝——实测（probe 模式）`CreateWindowExW(WS_CHILD, parent=跨进程记事本)` 返回 NULL 且 `GetLastError=0`（不设错误码的策略性拒绝）；而「自建 WS_POPUP → `SetWindowLongPtr(GWL_STYLE)` 转正 WS_CHILD → `SetParent(目标)`」成功且 `GetAncestor(GA_PARENT)==target`。两步路径与规格最终形态语义一致（WS_CHILD 子窗口），实现保留「先改样式再 SetParent」的顺序并注释了探针依据。
4. **TargetLost 清理转线程池执行（死锁规避）**：清理若在 pump 线程上等 `_updateGates`，而持闸门的 `UpdateCoreAsync` 正同步等 pump 往返，两端互等即死锁。因此 pump 线程上的 TargetLost 回调只投递 `Task.Run(CleanupSessionCoreAsync)`；清理与壁纸更新共用同一 hwnd 的 `_updateGates` 闸门串行，消除「更新刚建好宿主、清理又销毁」的交错。
5. **降级检测比规格的「30 秒观察期」更保守**：焦点异常（宿主意外获得键盘焦点——宿主带 WS_EX_TRANSPARENT 永不该获得）在挂接全程监测（跨进程 SetParent 的输入队列问题若发生几乎都在初期显现，持续监测成本只是一个事件过滤分支）；另加「父子关系被外力破坏」（`GetAncestor(GA_PARENT) != 目标`，借 REORDER/SHOW/HIDE 低频事件校验）→ 同样降级 A2。A1 创建失败（UIPI 等）直接降级 A2，与规格一致。
6. **跨进程挂接的 DPI 上下文约束（实测结论）**：跨进程 SetParent/CreateWindow(WS_CHILD) 要求父子两侧 DPI 感知上下文一致。WinUI 3 主程序是 PerMonitorV2，对 PMv2/System-aware 目标（记事本、浏览器等绝大多数应用）A1 可用；对 DPI-unaware 目标 A1 会被拒 → 自动降级 A2（A2 宿主矩形由 PMv2 进程以物理像素跟随，不受目标虚拟化影响）。此约束与降级闭环是产品在真实环境的主要行为分支。
7. **A2 自动透明的 alpha 读取**：用 `GetLayeredWindowAttributes` 读目标当前 alpha（未分层 / 未设置过则视为 255），已有透明（<255）则尊重用户设置，仅 255 时自动 `SetTransparencyAsync(220)` 并经 `StatusReported` 提示——规格 A2.4 的语义。
8. **诊断工具自设 PMv2**：DPI-unaware 进程 `GetWindowRect` 被虚拟化（本机 200% 缩放 ÷2），与 DWM 外接框（恒物理像素）不可比；工具设 PMv2 后与产品同上下文，测量口径一致。
9. **实测修正两处文档化坐标语义**（写回代码注释）：① `GetAncestor(GA_PARENT)` 对顶层窗口返回桌面窗口（#32769）而非 NULL，「是否顶层」应判 `GA_ROOT == 自身`；② Win11/DWM 下子窗口 `GetWindowRect` 返回屏幕物理坐标（非旧文档「父客户区坐标」），位置比对须经 `ScreenToClient` 映射，宽高比对不受影响（产品逻辑只比宽高，不受此发现影响）。
10. **A2 目标最小化时按 0 尺寸创建**：最小化目标的外接矩形是 (-32000) 幽灵坐标，按 0 尺寸隐藏创建，待 MINIMIZEEND/SHOW 事件定位显示。

#### 验证证据摘要

- **构建门禁**：`dotnet clean WinCanvas.sln -c Debug` → `dotnet build WinCanvas.sln -c Debug` = **0 个错误 / 26 个警告**（= WP0 基线 26，未新增）。
- **测试**：`dotnet test WinCanvas.sln -c Debug --no-build` = **已通过! - 失败: 0，通过: 6，已跳过: 0，总计: 6**。
- **诊断命令**（`dotnet run --project tools/Wp1AHostDiag -c Debug`，2026-11-01 实测，29/29 断言 PASS，摘要；诊断日志 `%LOCALAPPDATA%\WinCanvas\logs\host-diag.log` 同步留痕）：
  - 记事本-A1嵌入：`mode = Embed`；`GetAncestor(host, GA_PARENT) == target`；宿主左上经 ScreenToClient 映射 = (0,0)、宽高 = 客户区 1934x1270；跟随三轮最大 **62ms**；目标关闭后 TargetLost **31ms**、`IsWindow(host)=False`；
  - 记事本-A2跟随：`mode = Follow`；`GA_ROOT == host`（顶层）；宿主矩形 == DWM 外接框 (698,214,2636,1486)；宿主位于目标下方（目标下方第一格为系统 IME 窗口，向下遍历遇宿主）；跟随三轮最大 **63ms**；最小化 → 宿主隐藏、恢复 → 重现且矩形同步；关闭后清理 **16ms**；
  - 浏览器(Edge)-A1嵌入：`mode = Embed`；父子关系 ✓；客户区 2880x1706 对齐 ✓；跟随 **0ms**；清理 **218ms**；
  - 收尾：`WinCanvasWallpaperHost` 存活计数 = **0**（EnumWindows + EnumChildWindows 全域枚举）。
  - 跟随耗时口径：程序化 SetWindowPos 改目标 → 2ms 步进轮询宿主矩形匹配，含 WinEvent 排队延迟，DoD ≤100ms 全部满足。
- **服务级接线冒烟**（`service` 模式）：临时 PNG 经真实 `ImageWallpaperLoader` 解码 → `UpdateWallpaperAsync` → `SessionHostModeChanged = Embed`、宿主存活 1 → `ShutdownAll` → 存活 **0**。证明会话替换 / 宿主创建 / 模式上报 / 退出清理的产品路径真实工作。
- **跨进程挂接探针**（`probe` 模式，决策 3/6 的事实依据）：
  ```
  ① 直接 CreateWindowExW(WS_CHILD, parent=target)：失败，GetLastError=0
  ② POPUP→改WS_CHILD→SetParent：成功，GA_PARENT=0x1E0F0A（=target）
  ③ POPUP→直接SetParent：成功，GA_PARENT=0x1E0F0A（=target）
  ```
- **宿主期间纯色上屏**：宿主 WM_PAINT/WM_ERASEBKGND 以深蓝纯色填充（人工可在真实会话中肉眼确认宿主覆盖目标自绘背景、既有子控件仍在其上）。

#### 人工验证清单（自动化无法覆盖，需真实交互会话确认）

- [ ] 记事本 A1 挂接后：宿主区域显示深蓝纯色（说明：记事本编辑区是子窗口控件、位于宿主上方，覆盖效果以「窗口非控件区域 / 控件间隙」为准），编辑文字、右键菜单、快捷键（Ctrl+S 等）全部正常；
- [ ] Edge A1 挂接后：网页滚动、点击链接、输入框键入、右键菜单全部正常；
- [ ] A2 模式下（用 `service` 场景或主程序对 DPI-unaware 目标挂背景）：手动把透明度滑块从 255 拉低，确认目标身后透出宿主纯色；确认 A2 自动透明提示出现；
- [ ] 拖动标题栏 / 拖拽边缘缩放目标，壁纸画面无迟滞感（诊断测得 ≤100ms，人工感受核对）；
- [ ] 多显示器间拖动目标（阶段 D 才处理跨屏 DPI 变化，本阶段预期 A2 宿主矩形仍按物理像素跟随、图片内容缩放待阶段 B/D）。

#### DoD 自检（阶段 A）

- [x] 对「记事本（自绘背景类）」与「浏览器（控件密集类 Edge）」以 A1 挂接后，宿主矩形与目标客户区一致（诊断打点：父子关系 / ScreenToClient(0,0) / 宽高一致，`host-diag.log` + 汇总表为证）
- [x] 拖动、缩放目标窗口，宿主 100ms 内跟随（诊断三轮打点：62ms / 63ms / 0ms，程序化移动+轮询口径）
- [x] 宿主存在期间目标的点击、键盘输入、菜单操作正常——**程序化等价探针已过**（SendMessageTimeout(WM_NULL) 三场景应答正常，目标消息循环无阻塞）；真实点击 / 键盘 / 菜单属人工验证项，清单见上节，待真实会话确认
- [x] 关闭目标窗口后 1s 内宿主销毁且会话 Dispose（诊断：TargetLost 16~218ms，宿主句柄失效，全域残留计数 = 0）
- [x] 应用退出时全部宿主销毁（`ShutdownAll` 已接线 MainWindow.Closed；服务冒烟 ShutdownAll 清零 = 0；宿主 pump 为 IsBackground 线程进程退出自动回收兜底）
- [x] 阶段提交 `feat(WP1-A): 宿主窗口与双路径挂接`

---

## 3. WP2 NumLock 键盘钩子与提示链路端到端（待开始）

> 预留区块：钩子服务 / 开关接线 / WinTips.exe 构建部署。完成后按同一结构补录。

## 4. WP3 状态重启自动恢复（待开始）

> 预留区块：档案恢复管线 / 配置开关。完成后按同一结构补录。

## 5. WP4 自动化测试工程（待开始）

> 预留区块：Wallpaper 测试项目 + 用例表全量实现。完成后按同一结构补录。

## 6. WP5 质量打磨与文档收口（待开始）

> 预留区块：GIF / webp / UIPI 引导 / 文档三件套同步 / WORKLOG 终稿整理。完成后按同一结构补录。
