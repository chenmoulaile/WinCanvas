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

## 2. WP1 壁纸真实上屏（待开始）

> 预留区块：阶段 A 宿主窗口与双路径挂接 → 阶段 B 图片 DComp 上屏 → 阶段 C 视频帧服务器上屏 → 阶段 D 上屏收口与文档同步。完成后按「改动清单 / 关键决策与理由（含 ADR）/ 验证证据摘要 / DoD 自检」结构补录。

## 3. WP2 NumLock 键盘钩子与提示链路端到端（待开始）

> 预留区块：钩子服务 / 开关接线 / WinTips.exe 构建部署。完成后按同一结构补录。

## 4. WP3 状态重启自动恢复（待开始）

> 预留区块：档案恢复管线 / 配置开关。完成后按同一结构补录。

## 5. WP4 自动化测试工程（待开始）

> 预留区块：Wallpaper 测试项目 + 用例表全量实现。完成后按同一结构补录。

## 6. WP5 质量打磨与文档收口（待开始）

> 预留区块：GIF / webp / UIPI 引导 / 文档三件套同步 / WORKLOG 终稿整理。完成后按同一结构补录。
