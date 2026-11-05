# ADR：隐私窗口（失焦自动遮蔽内容）

状态：已采纳　|　日期：2026-11-06　|　关联工作包：隐私窗口（仿 QQ 新版「隐私窗口」）

## 背景与目标

窗口内容在失焦（用户切到别的窗口）时可能被旁人窥视（肩窥）。目标：

1. **本应用主窗口**失焦时叠加隐私遮罩层（磨砂质感 + 「已进入隐私模式」文案 + 眼睛图标），
   重新聚焦瞬间移除；任务栏 / Alt-Tab 预览同步呈现遮罩；设置区总开关，默认开启。
2. **被管理窗口**（列表中的跨进程目标窗口）可逐个开启「窗口隐私模式」：
   失焦时自动遮蔽内容、聚焦时自动恢复；提供可选全局热键。
3. 失焦判定：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`（被管理窗口）与
   WinUI `Window.Activated/Deactivated`（主窗口）；监听全部在后台线程，UI 只做状态更新。
4. **不得破坏用户手动设置的透明度**：恢复必须用档案值（`WindowState.Alpha`），禁止硬编码 255。
5. 与既有置顶 / 透明 / 穿透 / 壁纸属性协同无冲突。

## 决策一：主窗口遮罩 = 视觉树内叠加层（XAML Grid）

`MainWindow.xaml` 根 Grid 末尾追加一层跨两行的遮罩 Grid（半透明深色底 + RedEye 图标 +
「已进入隐私模式」文案），可见性由 `MainViewModel.MainWindowPrivacyActive` 驱动；
`Window.Activated / Deactivated` 事件由 code-behind 只做装配转发给
`MainViewModel.SetMainWindowFocusState(bool)`，判定与开关逻辑全部在 ViewModel。

- **为什么可见**：WinUI 3 窗口失焦后合成器仍然继续呈现该窗口的视觉树，遮罩层可见后
  DWM 的窗口采样随之更新——任务栏悬停预览与 Alt-Tab 预览正是这份采样，
  因此预览里同步呈现遮罩（本机经 DwmRegisterThumbnail 探针实测，见
  `tools/PrivacyMaskDiag` 输出）。
- **为何弃用对本窗口降 alpha**：`SetLayeredWindowAttributes` 作用于整个 HWND，
  会把「我们想清晰显示的遮罩文案」与被遮内容一起压暗，遮罩感不可控；
  且 WinUI 顶层窗口加 WS_EX_LAYERED 属于对自绘管线的侵入，没必要。
- **为何不用 DWM Cloak**：整窗隐藏会把主窗口连预览一起弄没，且失去可编程的文案提示。

## 决策二：被管理窗口遮蔽 = SetLayeredWindowAttributes 降 alpha（方案 a）

失焦时 `SetTransparencyAsync(hwnd, ComputeMaskAlpha(档案Alpha))` 把窗口 alpha 降到
**min(30, 档案 State.Alpha)**（档案值低于 30 时绝不放大可见度）；
聚焦时 `SetTransparencyAsync(hwnd, 档案 State.Alpha)` 恢复。
复用 `IWindowControlService.SetTransparencyAsync`（内含 `EnsureLayeredStyle`，
扩展样式读-改-写全程持 `_exStyleGate` 锁——**锁语义只扩展不绕过**）。

- **为何弃用方案 b（A2 式遮罩宿主盖在目标上方）**：
  WP1 Hosting 基建的 A2 宿主按「壁纸背景」语义设计——WS_EX_TRANSPARENT 鼠标穿透、
  Z 序钉在目标**正下方**、目标需半透明透出壁纸；隐私遮罩需要的恰恰相反
  （在目标**正上方**、可显示文案、不穿透）。复用等于改写挂接语义，
  新写一套则每窗口多一条宿主窗口 + 一组 WinEvent 钩子（跟随之前的所有矩形/显隐/销毁
  同步都要做一遍），而 alpha 方案用一次 API 调用就达到「旁人看不清内容」的核心目标。
  A2 宿主路线保留为将来「遮罩上要显示磨砂文字/品牌图案」的升级路径，不是本期必需。
- **为何弃用方案 c（DWM Cloak）**：Cloak 把窗口从屏幕、任务栏、Alt-Tab 全部隐藏。
  最致命的是交互死锁——被 cloak 的窗口收不到鼠标点击，**用户无法通过点击把它重新
  聚焦**，Alt-Tab 里也不可见，「重新聚焦立即恢复」无从谈起；只能靠本程序感知并主动
  解 cloak，任何一处失误用户就「丢了窗口」。alpha 方案下窗口仍可见（暗态）、仍可点击、
  点击即聚焦即恢复，语义自然。且任务自评「整窗隐藏，偏重」属实。

## 决策三：失焦判定 = 全局 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 单钩子

`ForegroundWatcherService`（Core）：专用后台消息线程（复用 KeyboardIndicatorService 的
HookCycle 生命周期模型：就绪信号 + WM_QUIT 收尾），安装
`SetWinEventHook(EVENT_SYSTEM_FOREGROUND, 同值, 全局进程/线程过滤 0,0, WINEVENT_OUTOFCONTEXT)`，
回调在 pump 线程只做「窗口本体事件过滤 + 事件转发」，绝不抛异常。

- 一条全局钩子覆盖全部被管理窗口，回调量与隐私窗口数无关；
- 主窗口走 WinUI 自己的 Activated/Deactivated（零额外钩子）；
- 钩子不加 WINEVENT_SKIPOWNPROCESS：本应用主窗口夺走前台时，被管理窗口同样失焦，
  必须照常遮蔽——跳过自身进程会漏掉这一最常见的遮蔽时机。

## 决策四：并发纪律（共享状态与保护方式）

| 共享状态 | 保护方式 |
| --- | --- |
| `WindowItemViewModel.IsPrivacyMasked`、`State.PrivacyEnabled`、遮蔽迁移任务链 `_privacyTransitionChain` | **只在 UI 线程读写**：watcher 回调经 `DispatcherQueue.TryEnqueue` 封送；链条节点在 UI 线程 `await previous` 续接，同一窗口的遮蔽/恢复严格按事件顺序串行（Win32 调用内部由 WindowControlService 派发线程池） |
| `ForegroundWatcherService._cycle / _disposed` | `_stateGate` 锁内引用置换；HookCycle 内部字段 Volatile.Read/Write（与 KeyboardIndicatorService 同规格） |
| `AppConfig.PrivacyOverlayOnDeactivated`、`WindowState.PrivacyEnabled` | 复用既有 `_configGate` 配置锁 + ConfigurationService 防抖落盘 |

## 决策五：与既有属性的协同规则

- 恢复 alpha 一律取档案 `State.Alpha`（可能为 255，也可能是用户设的 100），
  不硬编码——热键透明度步进（SyncApplied）与滑块都会先落档案再被恢复路径读到。
- **遮蔽期间用户拖透明度滑块**：`SetAlphaAsync` 检测到该窗口处于遮蔽态时跳过 Win32
  调用（否则用户一次拖动就把遮蔽抬亮了），只更新档案值与界面显示；聚焦恢复时生效新档案值。
- **选中已遮蔽窗口触发 RestoreAsync**：档案恢复会把 alpha 抬回档案值——若该窗口仍处于
  遮蔽态，RestoreAsync 末尾重新套一次遮蔽 alpha，防止「在列表里点一下就把内容曝光」。
- 关闭隐私开关时恢复档案 alpha；不摘 `WS_EX_LAYERED` 位（与既有穿透关闭语义一致，
  避免抹掉用户透明度）。遮蔽本身会给无 LAYERED 的窗口补上该位（既有 EnsureLayeredStyle
  全程持 `_exStyleGate`，与置顶/穿透路径天然互斥）。
- 与壁纸 / 圆角 / 置顶零交互：alpha 遮蔽不碰 GWL_STYLE、DWM 属性与宿主窗口。

## 提示文案（失败 + 原因 + 怎么办）

- 监听线程启动失败：「窗口隐私的失焦监听未能启动：后台监听线程创建或钩子安装失败（Win32 错误 X）。
  隐私开关仍可设置，但失焦时不会自动遮蔽；请重启应用重试。」
- 遮蔽/恢复失败：复用 `ProcessElevation.DescribeFailure`（错误 5 自动附「以管理员身份重启」引导）。
- 热键作用于不在列表中的窗口：「隐私模式未切换：当前焦点窗口不在窗口列表中（可能是刚打开、尚未刷新）。
  请先点主界面「刷新」再按热键，或在列表中选中该窗口打开隐私开关。」

## 验证记录（本机实测，Windows 11 26300）

### 单元/真窗口测试（dotnet test）
- 纯决策规则（遮蔽值 = min(30, 档案)、恢复 = 档案值、混合多窗口决策）：`PrivacyMaskRulesTests` 全过。
- 配置持久化往返 + 旧版本配置兼容（新字段缺省不报错）：`PrivacyConfigPersistenceTests` 全过。
- 真实窗口（RequiresWindowsSession）：`SetTransparencyAsync(30)` 写入后 `GetLayeredWindowAttributes` 回读一致；
  档案值 128 的恢复路径回读 128（非硬编码 255）；未分层窗口自动补 WS_EX_LAYERED。
- 前台监听端到端：`ForegroundWatcher_最小化还原_收到目标窗口前台事件`——真实桌面会话下
  「最小化→还原」触发 EVENT_SYSTEM_FOREGROUND，5 秒内收到目标窗口句柄的事件，全过。

### 探针实测（tools/PrivacyMaskDiag，本机桌面会话）
```
[PASS] DWM 缩略图能采样普通窗口 —— 缩略图亮度 203.8（白底基准）
[PASS] SetLayeredWindowAttributes 使屏幕显著变暗（方案 a 屏幕效果）—— alpha 255→30，屏幕亮度 255.0→50.0，像素差 204.4
[PASS] WinCanvas.App 主窗口定位
[PASS] 主窗口失焦 → 遮罩呈现（App 区域画面显著变化）—— 像素差 22.2
[PASS] 主窗口失焦 → Alt-Tab 类缩略图同步呈现遮罩 —— 缩略图区域像素差 20.4
[PASS] 主窗口重聚焦 → 遮罩即时移除 —— 恢复差 13.3，显著小于遮罩差 22.2
```
屏照（%TEMP%/priv-diag-*.png）肉眼核验：失焦态 App 窗口呈现暗色遮罩 + 眼睛图标 +
「已进入隐私模式」文案；预览窗中 App 的 DWM 缩略图同步呈现遮罩画面；重聚焦后全部恢复。

### 实测发现的 OS 行为边界（如实记录）
- **分层窗口的 DWM 缩略图不呈现原始内容**：被管理窗口经 SetLayeredWindowAttributes 遮蔽后，
  DwmRegisterThumbnail 类预览（与任务栏/Alt-Tab 同机制）实测呈现空瓦片（多数读数 0.0）——
  内容不会在预览里泄露，但形态是空瓦片而非暗态画面；个别轮次观察到缩略图呈现原始亮度的读数，
  说明该行为存在竞态、**不能依赖 alpha 遮蔽来遮蔽预览**。被管理窗口的预览遮蔽属已知边界，
  后续若需「预览也是暗态画面」，走 ADR 决策二预留的遮罩宿主（盖在目标上方，预览采样宿主画面）。
- 主窗口（WinUI，非分层）遮罩不受此影响：XAML 层直接进入窗口合成，缩略图如实呈现遮罩（已实测）。
