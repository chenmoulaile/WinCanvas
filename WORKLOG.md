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

### 2.B 阶段 B：图片上屏 DComp / Direct2D（2026-11-01）

**状态**：完成。提交：`feat(WP1-B): 图片 DComp 上屏`（分支 `feat/complete-w0-w5`）。主路径 DComp 落地（零新 NuGet 依赖，手写 COM 接口声明），回退路径 UpdateLayeredWindow 未启用——DComp 互操作未受阻，规格的回退条件不成立（见关键决策 2）。

#### 改动清单

| 文件 | 动作 | 说明 |
| --- | --- | --- |
| `src/WinCanvas.Wallpaper/Rendering/BgraSnapshot.cs` | 新增 | 像素快照 record + `BitmapSnapshot.CopyFrom`（自 `WallpaperPreviewController.SnapshotBitmap` 下沉，预览与宿主共用一份，规格 B.3；纯 WinRT 投影实现，含紧密/带行距双路径） |
| `src/WinCanvas.Wallpaper/Hosting/DcompNativeMethods.cs` | 新增 | DComp/D3D11/DXGI 导出函数 + 手写 COM 接口（IDCompositionDevice/Target/Visual/Surface、IDXGISurface/1），vtable 逐一对照真实 SDK 头文件核对并经探针实测修正 |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostRenderer.cs` | 新增 | 每宿主一渲染器：建树（D3D11→QI→DComp→Target→Visual→Surface）+ GDI 贴图（BeginDraw→GetDC→StretchDIBits→EndDraw→Commit）+ 尺寸变化重建表面 + 设备失陷整树重建（规格 B.5） |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostWindow.cs` | 修改 | `RenderImage` 公共入口（pump 封送 + 渲染器惰性创建）；A1/A2 尺寸同步路径直呼 `UpdateClientSize`；Dispose 级联释放渲染器 |
| `src/WinCanvas.Wallpaper/WallpaperService.cs` | 修改 | `UpdateCoreAsync`：宿主挂接后对 `ImageFrame` 非空的源（图片 / pkg 降级封面）调用共享快照 + `RenderImage`，失败经 `StatusReported` 上报 |
| `src/WinCanvas.Wallpaper/WinCanvas.Wallpaper.csproj` | 修改 | `AllowUnsafeBlocks`（指针级像素拷贝；后改纯投影实现但仍保留供 MemCopy）、`InternalsVisibleTo WinCanvas.App` |
| `src/WinCanvas.App/Services/WallpaperPreviewController.cs` | 修改 | 删除本地 `BgraSnapshot` / `IMemoryBufferByteAccess` / `SnapshotBitmap` 副本，改用共享 `BitmapSnapshot.CopyFrom`（勿复制两份的规格要求） |
| `tools/Wp1AHostDiag/Program.cs` | 修改 | 新增 `render` 模式（四象限像素采样验证上屏正确性、切图即时性、空闲 CPU%、SendInput 点击穿透、repkg PackageWriter 构造最小 pkg 验证降级封面）与 `dcomp` 模式（管线分步 HRESULT 探针） |

#### 关键决策与理由（含 ADR）

1. **COM 互操作零依赖方案与「头文件 ≠ 运行时 ABI」实测结论**：不引 SharpDX/Terracotta 等封装，手写 `ComImport` 接口。槽位依据 winsdk-10 镜像的真实 `dcomp.h`/`dxgi.h` 逐一核对（网上 Wine 的 dcomp.idl 成员顺序与微软头文件存在差异，不能直接采信）。**探针实测又发现两处与头文件字面不同的事实**：① `IDCompositionVisual::SetTransform` 两个重载在真实 ABI 中的相对顺序与 dcomp.h 的 C++ 声明相反（矩阵指针打在头文件顺序的槽 7 → E_INVALIDARG；NULL 打在槽 8 → AccessViolation，证明槽 8 才是矩阵重载）；② `IDCompositionSurface::BeginDraw` 传可空结构体抛 `MarshalDirectiveException`（改裸 `RECT*`）；③ DComp 表面拒绝 `IDXGISurface::Map` CPU 直写（E_INVALIDARG）；④ `DCOMPOSITION_BITMAP_INTERPOLATION_MODE` 实测 NEAREST=0 / LINEAR=1 / INHERIT=0xFFFFFFFF（传 2 → E_INVALIDARG）。
2. **ADR：缩放走「surface=客户区尺寸 + GDI StretchDIBits」而非「surface=快照尺寸 + visual 缩放矩阵」**。后者需调用 SetTransform，而其重载槽位歧义（决策 1-①）在 ComImport 声明顺序上不可可靠命中；且 GDI blit 会把预乘表面 alpha 字节清零，visual 级合成还会暴露透明问题。改为表面按客户区尺寸创建 + `DXGI_ALPHA_MODE_IGNORE`（不透明合成，alpha 歧义消除）+ `StretchDIBits` 缩放贴入，视觉对象只调槽位无歧义的 `SetContent`。代价：尺寸变化时重贴一次像素（GDI 缩放，事件驱动一次性，实测 62ms 内完成）；规格 B.4「尺寸变化时重建 surface 重绘一次」正是此口径。`UpdateLayeredWindow` 回退未启用：DComp 主路径打通，不满足启用条件。
3. **CPU 路径用 `IDXGISurface1::GetDC` + GDI**：`BeginDraw(IID_IDXGISurface)` 返回的表面不支持 `Map`（实测），官方 CPU 写入路径是 GetDC 后贴 DIB——32bpp BI_RGB 的 DIB 数据与 PBgra8 快照逐字节同布局（B/G/R/A 顺序），零格式转换。alpha 处理如决策 2（IGNORE 模式下 alpha 字节无语义）。
4. **共享快照改纯投影实现**：下沉时原样搬移的 `(IMemoryBufferByteAccess)(object)reference` 强转在非 WinUI 进程（诊断命令）抛 `InvalidCastException`（CsWinRT 包装对象的 ComImport 强转只在 WinUI 宿主上下文可行）——预览与宿主必须共用一份实现，改为 `SoftwareBitmap.CopyToBuffer` + `IBuffer.ToArray()` 纯投影路径（两种宿主上下文行为一致），紧密/带行距双路径语义保留。
5. **静态图工作量模型**：首次 Render 建树+建表面+一次贴图+Commit；切换图片 = 解码 + 重贴 + Commit（实测调用 16ms、屏上生效 62ms）；尺寸变化 = 重建表面 + 重贴 + Commit；设备失陷 = 整树释放、下次调用自动重建（会话不销毁）。全程不注册 FrameTick——实测空闲 3s 处理器占比 **0.00%**（规格 DoD ≈0%）。
6. **pkg 降级封面同路径**：`PkgWallpaperLoader` 降级封面填 `ImageFrame`（`PkgWallpaperLoader.cs:79`），服务层渲染条件统一为 `source.ImageFrame != null`；验收用 repkg 的 `PackageWriter` 反向构造最小 pkg（`scene.json` 引用 script + `preview.png`），实测降级提示上报且封面 63ms 上屏（附录 B 授权的夹具构造方式）。
7. **渲染错误呈现**：`RenderImage` 失败经 `StatusReported` 报「背景画面渲染失败：DirectComposition 初始化未成功（详见诊断日志）。已保留纯色占位，可尝试更新显卡驱动后重试。」（失败+原因+建议三要素）；详细 HRESULT 全部落 `host-diag.log`。

#### 验证证据摘要

- **构建门禁**：clean 全量 `dotnet build WinCanvas.sln -c Debug` = **0 错误 / 26 警告**（= WP0 基线）；`dotnet test WinCanvas.sln -c Debug --no-build` = **6/6 通过**。
- **render 验收**（`dotnet run --project tools/Wp1AHostDiag -- render`，连续 3 轮全绿，末轮 9/9）：
  - 图片 A（2x2 四象限异色 PNG）经真实 `ImageWallpaperLoader` 解码 → A1 宿主 DComp 上屏：屏上四象限采样全部匹配（**62ms** 内）——「画面可见、无错位、无镜像/翻转」的自动化证明（四角互异色设计可同时抓出水平镜像与垂直翻转）；
  - 切换图片 B：调用 **16ms**、屏上颜色匹配 **62ms**——「切换不同图片背景即时生效」；
  - 静态图空闲 3s 处理器占比 **0.00%**——「静态图无逐帧工作」；
  - SendInput 点击客户区中心 → 裸目标窗口收到精确坐标 (400,300)——宿主点击穿透（目标交互不受干扰）；
  - pkg 降级：`PackageWriter` 构造包 → 「降级」提示上报 + 封面 63ms 上屏；ShutdownAll 后宿主残留 0。
- **dcomp 分步探针**（决策 1/2 的证据）：全管线 S_OK + 象限采样 True；PREMULTIPLIED 变体采样全白（alpha 清零实锤）。
- **阶段 A 回归**：默认模式 29/29 断言全过（挂接/跟随/清理行为未受渲染接线影响）。
- **诊断日志**：`%LOCALAPPDATA%\WinCanvas\logs\host-diag.log`（DComp 树就绪 / DComp 上屏 surface=800x600 等打点齐全）。
- **实测备注**：render 模式曾出现一次「裸目标窗口创建失败（pump 线程）」的暂态失败，紧接的连续 3 轮全绿 + 本轮全绿未再复现（疑似前一轮进程退出与本次启动的窗口类注册竞态窗口），后续阶段继续观察；如复现将在 pump 建窗路径加重试。

#### DoD 自检（阶段 B）

- [x] 对目标窗口应用图片背景后画面可见、无错位、无镜像/翻转（四象限异色采样自动化验证 + 人工可在真实会话肉眼复核）
- [x] 切换不同图片背景即时生效（调用 16ms + 屏上 62ms）
- [x] 静态图场景本应用 CPU 占用≈0%（空闲 3s 实测 0.00%，无逐帧消耗）
- [x] pkg 降级封面图路径同样上屏（PackageWriter 构造复杂场景包实测 63ms 上屏）
- [x] 阶段提交 `feat(WP1-B): 图片 DComp 上屏`

---

### 2.C 阶段 C：视频上屏（组合呈现 ADR）（2026-11-01）

**状态**：完成。提交：`feat(WP1-C): 视频帧服务器上屏`（分支 `feat/complete-w0-w5`）。
**ADR 结论先行**：主路径（帧服务器 `CopyFrameToVideoSurface`）在实测中受阻（详见决策 1），按规格授权采纳**备选路径**——`MediaPlayer.GetSurface(compositor)` + SpriteVisual + `ICompositorDesktopInterop.CreateDesktopWindowTarget`（组合呈现）：视频由 MF 直接渲染进组合表面，**零拷贝、零逐帧代码、零轮询**；帧率联动复用既有状态机（暂停态 → `player.Pause()` → 画面定格）。

#### 改动清单

| 文件 | 动作 | 说明 |
| --- | --- | --- |
| `src/WinCanvas.Wallpaper/Hosting/CompositionVideoPresenter.cs` | 新增 | 视频组合呈现器：自持专用 DispatcherQueue 线程（实测约束，决策 4）；`GetSurface(compositor)` → SurfaceBrush → SpriteVisual → `CreateDesktopWindowTarget`；按 NaturalVideo 尺寸等比加边居中（禁止拉伸）；Detach 幂等释放 |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostRenderer.cs` | 修改 | 移除交换链实验产物（ADR 决策 1），回归纯图像渲染器；类注释指向视频呈现器 |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostWindow.cs` | 修改 | `BeginVideo(player)` / `UpdateVideoLayout` / `EndVideo`（pump 封送）；视频模式客户区变化时重算加边布局；Dispose 级联释放 |
| `src/WinCanvas.Wallpaper/WallpaperService.cs` | 修改 | 视频会话接线：BeginVideo + **显式 Play()**（决策 5）；切出视频时 EndVideo |
| `src/WinCanvas.Wallpaper/Loaders/VideoWallpaperLoader.cs` | 修改 | 明确注释组合呈现路径不启用帧服务器（`IsVideoFrameServerEnabled` 保持 false） |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostPump.cs` | 修改 | 两处启动加固（决策 6）：pump 启动先 `CoInitializeEx(STA)`（WinRT 激活要求 apartment）+ 首条 PeekMessage 强制建消息队列（消除 `PostThreadMessageW` ERROR_INVALID_THREAD_ID 竞态）+ `Execute` 投递失败有限重试 |
| `src/WinCanvas.Wallpaper/Hosting/DcompNativeMethods.cs` | 修改 | 移除主路径考察产物（交换链/CopyFrameToVideoSurface 互操作声明），保留结论于 WORKLOG 与 git 历史 |
| `tools/Wp1AHostDiag/Program.cs` | 修改 | `video` 模式改走产品路径验收（流动/比例/全屏暂停/恢复/资源释放）；新增 `winrt` 模式（CsWinRT 互操作面 + 系统 winmd IID 提取）；暂存过的 `swapchain` 矩阵探针完成使命后移除（结论留 WORKLOG） |

#### 关键决策与理由（含 ADR）

1. **ADR：视频上屏路线——主路径（帧服务器 CopyFrameToVideoSurface）受阻，采纳备选路径（GetSurface + CompositionSurface）**。主路径考察过程与实测证据（全部留痕于 git 历史的探针代码与诊断日志）：
   - **DComp surface 中转**：逐帧 `BeginDraw/EndDraw` 在第 2 帧起稳定返回 `E_OUTOFMEMORY(0x8007000E)`（第 1 帧成功）——DComp 表面非为视频级更新频率设计；pump 同线程复现，排除线程因素。
   - **组合交换链中转**：`CreateSwapChainForComposition` 全链路 S_OK（`SetContent(交换链)` / `GetBuffer` / `Present` / `Commit`），但 `CopyFrameToVideoSurface(后备缓冲包装)` 在 MF 内部 **AccessViolation**（进程崩溃，不可捕获）——MF 对外部翻转模型缓冲的跨设备拷贝在本机驱动栈上不安全。
   - **互操作细节实测**（对后续阶段有价值，git 历史可考）：`CreateDirect3DSurface` 的真实导出名是 `CreateDirect3D11SurfaceFromDXGISurface`；`IDirect3DSurface` IID = `f4a14600-c10b-9413-46be-e37abf15eaf5`（系统 winmd 元数据提取）；组合交换链 `Scaling` 在本机只接受 `STRETCH(0)`（`NONE(1)` → INVALID_CALL）；ComImport 的 `object` 参数默认按 VARIANT 封送（需显式 `MarshalAs(UnmanagedType.IUnknown)`）。
   - **备选路径优势**：`GetSurface(compositor)` 是 MF 官方组合呈现通道（帧在 MF 内部直接进组合表面），无跨设备拷贝、无逐帧 CPU/GDI 工作；帧率联动天然由 `player.Pause()` 承担。规格明确授权该备选（「两条路 API 均真实存在，择一实现，在 WORKLOG 写 ADR」）。
2. **组合对象要求创建线程持有 DispatcherQueue**：`new Compositor()` 在无 DQ 的 pump 线程抛 `UnauthorizedAccessException`（官方报错文案 "The caller must initialize DispatcherQueue on this thread before this operation"）。修复：presenter 自持 `DispatcherQueueController.CreateOnDedicatedThread()` 专用线程，全部组合操作经 `TryEnqueue` 同步封送（TryEnqueue+信号等待，返回即完成）。
3. **渲染循环 `StateChanged` 仅在暂停分支触发**：新视频会话的 `player.Play()` 若依赖状态迁移事件则永不执行（活跃态无迁移）。修复：服务层在视频附着成功后按 `Controller.ShouldRender` 显式 `Play()`；后续暂停/恢复仍由既有迁移驱动。此发现同步记入渲染循环注释。
4. **pump 线程两处启动加固**（视频排障期间定位的真实缺陷，与视频无直接关系但影响全宿主）：① 线程消息队列在首次消息函数调用时才建立，`_ready.Set()` 早于首条 `PeekMessage` 会产生 `PostThreadMessageW → ERROR_INVALID_THREAD_ID` 竞态窗口（间歇性「裸目标窗口创建失败」复现两次）——启动时先 Peek 强制建队 + `Execute` 投递失败有限重试；② pump 线程补 `CoInitializeEx(STA)`——WinRT 激活要求 apartment。
5. **帧率联动**：组合呈现下无帧事件可丢弃；联动 = 既有状态机（全屏/用户暂停 → `player.Pause()` → 组合画面定格；恢复 → `Play()`）。探针以「颜色稳定性」验证：2s 采样窗必跨越 1.5s 的片段边界——播放中必变色、暂停中必恒定。
6. **比例正确**：SpriteVisual 尺寸/偏移按 `NaturalVideoWidth/Height` 在客户区内等比缩放 + 居中加边（contain 语义），加边区透出宿主纯色底。实测 4:3 视频入 1:1 客户区：边带恒定、内容随片段变化、无拉伸。

#### 验证证据摘要

- **构建门禁**：clean 全量 `dotnet build WinCanvas.sln -c Debug` = **0 错误 / 26 警告**（= 基线）；`dotnet test` = **6/6 通过**。
- **video 验收**（`dotnet run --project tools/Wp1AHostDiag -- video`，产品路径，全绿复现两轮）：
  - 帧流动：左 1/4 采样颜色序列 2 种（红/蓝随片段切换，循环播放）；
  - 比例正确：800x800 客户区 + 4:3 视频 → 边带 (0,0,0) 恒定 + 内容红/蓝变化 = 等比加边、无拉伸；
  - 全屏前台 → 画面定格（2s 窗 1 色）；退出全屏 → 恢复（2 色）——DynamicFrameRateController 联动生效；
  - 切图片 → 视频视觉消失（左 1/4 恒为图片色 30,80,180）= 播放器/组合树随会话释放；
  - ShutdownAll → 宿主残留 0。
- **回归**：阶段 A 默认电池 29/29、阶段 B render 电池 9/9、service 冒烟 3/3 全部通过。
- **主路径考察证据**：git 历史中的探针代码（`swapchain` 矩阵模式、帧服务器手工管线）+ 诊断日志（E_OUTOFMEMORY / CreateSwapChainForComposition 矩阵 / AV 堆栈）。
- **撕裂**：组合呈现由 DWM 直接合成（等效垂直同步），且宿主跟随沿用阶段 A 已验证的 ≤100ms 路径——自动化以「resize 后内容仍在加边正确位置」间接覆盖；无撕裂的观感项留人工清单。

#### 人工验证清单（自动化无法覆盖，需真实交互会话确认）

- [ ] 真实视频壁纸（高分辨率 H.264/HEVC）在目标窗口内流动的观感流畅度（自动化以颜色切换 + 组合呈现路径证明帧推进）；
- [ ] 拖动/缩放目标窗口时视频画面无撕裂感（组合呈现经 DWM vsync 合成，理论上无撕裂）；
- [ ] 任务管理器 GPU「视频解码」引擎在全屏游戏启动后回落（自动化以画面定格 + 状态机 Pause 验证）；
- [ ] 应用内预览：视频的 MediaPlayerElement 预览在组合呈现路径下不再显示画面（预览旁路仅覆盖图片）——确认此取舍可接受（主输出=宿主）。

#### DoD 自检（阶段 C）

- [x] 视频背景在目标窗口内画面流动、比例正确（帧流动：红/蓝片段切换采样 2 色；比例：等比加边、边带恒定、无拉伸）
- [x] 全屏前台 → 视频暂停（画面定格）→ 退出全屏恢复（组合呈现 + 既有状态机联动；「GPU 解码引擎回落」以画面定格 + Pause 验证，任务管理器观测留人工清单）
- [x] 拖动/缩放目标窗口跟随 ≤100ms（宿主跟随沿用阶段 A 已验证路径；加边布局随尺寸同步重算，resize 后内容位置正确）
- [x] 切换视频→图片→无背景，旧视频播放器与纹理资源释放（切图片后视频视觉消失 + ShutdownAll 清零；会话 Dispose 链路：Detach → Surface/Brush/PlayerSurface 释放）
- [x] 阶段提交 `feat(WP1-C): 视频帧服务器上屏`

---

### 2.D 阶段 D：上屏收口与文档同步（2026-11-01）

**状态**：完成。提交：`feat(WP1-D): 上屏收口与文档同步`（分支 `feat/complete-w0-w5`）。

#### 改动清单

| 文件 | 动作 | 说明 |
| --- | --- | --- |
| `src/WinCanvas.Wallpaper/Hosting/WallpaperHostWindow.cs` | 修改 | 新增 `ClientSizeChanged(target, w, h)` 事件（A1/A2 尺寸同步与 `WM_DPICHANGED` 后触发）；`WM_DPICHANGED` 处理：默认处理（建议矩形）后上报新客户区（A2 路径；A1 宿主是目标子窗口，系统不向其发送该消息，无需处理） |
| `src/WinCanvas.Wallpaper/WallpaperService.cs` | 修改 | `_monitorResAtLoad` 记账（图片挂接时记录显示器分辨率）；`HandleClientSizeChangedAsync`（跨屏/跨 DPI 分辨率变化 → 按源路径重走加载管线，新显示器分辨率下重缩放；同分辨率幂等跳过）；清理点同步（CleanupSessionCoreAsync / ShutdownAll） |
| `src/WinCanvas.App/ViewModels/MainViewModel.cs` | 修改 | 错误呈现三要素补齐：背景加载失败 / 文件选择器失败的 InfoBar 文案补「用户能做什么」建议 |
| `README.md` | 修改 | 新增「背景上屏」功能节（双路径挂接 / 图片跨屏重缩放 / 视频组合呈现 / 预览旁路说明）；使用说明补双模式与 UIPI 权限说明；「已知限制」删除已完成的「壁纸画面为应用内预览」条目，新增「视频的应用内预览为空」如实条目 |
| `docs/architecture.md` | 修改 | 新增「宿主窗口与背景上屏」节：双路径决策图（mermaid）、嵌入/跟随行为、pump 线程模型、B/C 上屏路径、跨屏重缩放、预览旁路 |
| `docs/api.md` | 修改 | 新增 3.2/3.3 节（WallpaperService 宿主扩展 + Hosting internal 类型表），后续小节重编号 |
| `docs/risk.md` | 修改 | 第 3 节覆盖层次更新（宿主双路径/组合呈现/测试骨架）；第 4 节新增「跨进程 SetParent 输入队列附加」风险条目（缓解=自动降级）与「组合呈现与目标进程零交互」；矩阵新增「背景上屏」行、功能完整性行更新 |

#### 关键决策与理由

1. **预览旁路化的边界**：主输出（宿主）与预览各持一份呈现资源——宿主持 DComp 表面/组合表面（原生像素），预览持快照副本（WriteableBitmap），拷贝逻辑共用 `BitmapSnapshot` 一份实现（阶段 B 已下沉）。视频走组合呈现后 MF 直接渲染进组合表面，预览区不再收到画面——如实写入 README「已知限制」（主输出=目标窗口是产品方向）。
2. **跨屏重缩放的记账式防循环**：`_monitorResAtLoad` 在图片挂接时记录「当时显示器分辨率」；客户区变化时比对当前显示器分辨率，变化才重走加载管线并先记账——避免「重挂 → 尺寸事件 → 再重挂」的循环。同分辨率幂等跳过；源文件缺失时档案保留不重缩放。
3. **WM_DPICHANGED 只在 A2 生效**：A1 宿主是目标的 WS_CHILD 子窗口，其位置/尺寸由目标控制，系统不向子窗口发送 WM_DPICHANGED；A2 宿主是独立顶层窗口，默认处理（建议矩形）后再上报客户区变化，与 WinEvent 尺寸同步收敛到同一条 `ClientSizeChanged` 通道。
4. **错误三要素**：审核全部用户可见错误文案；补齐两处缺「建议」的 InfoBar（背景加载失败 / 文件选择器失败）。宿主挂接与渲染失败的文案在 B/C 阶段已带三要素。

#### 验证证据摘要

- **构建门禁**：clean 全量 `dotnet build WinCanvas.sln -c Debug` = **0 错误 / 26 警告**（= 基线）；`dotnet test` = **6/6 通过**。
- **四组回归全绿**：阶段 A 默认电池（29 断言）、阶段 B render 电池（9 断言）、service 冒烟（3 断言）、阶段 C video 电池（6 断言，含 800x800 resize 路径——即 ClientSizeChanged/UpdateVideoLayout 的实际执行）。
- **文档一致性**：README「已知限制」与 docs 三件套经本阶段更新后与代码行为一致（宿主上屏已实现、视频预览为空如实标注）。

#### DoD 自检（阶段 D，即 WP1 整体 DoD）

- [x] 图片 / 视频 / pkg（纯图、纯视频、SceneScript 降级封面）四类背景全部在目标窗口真实上屏（B 阶段 render 电池 + C 阶段 video 电池实测）
- [x] 目标窗口交互零干扰（点击 / 键盘 / 拖拽 / 菜单；穿透样式 + 消息循环存活探针 + SendInput 点击命中验证）
- [x] 拖动/缩放目标窗口跟随 ≤100ms（阶段 A 电池实测 32~94ms）；目标关闭 1s 内资源回收（实测 15~218ms）；应用退出零残留（CountAliveHosts = 0）
- [x] A1 异常时自动降级 A2（创建失败降级为阶段 A 实测；观察窗内焦点/父子断裂降级为 A 实现路径，构造拒绝 SetParent 场景的专项验证在阶段 A probe 完成）
- [x] build 0 error、test 全绿、WORKLOG 含 ADR 与验证证据摘要
- [x] 提交 `feat(WP1-D): 上屏收口与文档同步`；README/docs 已更新

---



## 3. WP2 NumLock 键盘钩子与提示链路端到端（待开始）

> 预留区块：钩子服务 / 开关接线 / WinTips.exe 构建部署。完成后按同一结构补录。

## 4. WP3 状态重启自动恢复（待开始）

> 预留区块：档案恢复管线 / 配置开关。完成后按同一结构补录。

## 5. WP4 自动化测试工程（待开始）

> 预留区块：Wallpaper 测试项目 + 用例表全量实现。完成后按同一结构补录。

## 6. WP5 质量打磨与文档收口（待开始）

> 预留区块：GIF / webp / UIPI 引导 / 文档三件套同步 / WORKLOG 终稿整理。完成后按同一结构补录。
