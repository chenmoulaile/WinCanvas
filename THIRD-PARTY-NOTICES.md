# 第三方组件声明

WinCanvas 使用了下列第三方组件。本文件只登记**实际进入构建或运行产物**的依赖，
以及各自的许可证与来源，便于合规审查与后续升级判断。

## Git 子模块

| 组件 | 来源 | 版本 | 许可证 | 集成方式 |
| --- | --- | --- | --- | --- |
| RePKG | <https://github.com/notscuffed/repkg> | `v0.4.0-alpha`（提交 `8005eb2`） | MIT © 2019 notscuffed | `ThirdParty/repkg`，源码项目引用（`RePKG.Core` / `RePKG.Application`，netstandard2.0） |
| WinTips | <https://github.com/DoYouNatureQ/WinTips> | 提交 `f479cb3` | MIT © 2026 自然卷 | `ThirdParty/WinTips`，不进解决方案，由 `WinCanvas.App.csproj` 的 `BuildAndDeployWinTips` Target 外挂构建并拷贝产物 |

两者均为 MIT，**不含 GPL/AGPL 等具有传染性的许可证**，闭源分发不冲突。
MIT 要求随发行保留版权声明与许可全文 —— 两个子模块目录内各自带有 `LICENSE` 文件，
随源码分发时一并保留即可。

> 说明：仓库根目录**尚未选择本项目自身的许可证**。这属于项目所有者的权利决定，
> 不由构建流程代为指定，见 `README.md` 的待办。

## NuGet 包

| 包 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| `Microsoft.WindowsAppSDK` | 1.8.260921001 | MIT（微软） | WinUI 3 运行时与 WinRT 投影；`WindowsAppSDKSelfContained=true` 打进产物 |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | `[ObservableProperty]` / `[RelayCommand]` 源生成器 |
| `SixLabors.ImageSharp` | 2.1.13 | 见下方许可证说明 | 图片层解码与 Lanczos 缩放；`.tex → PNG` 转换 |
| `K4os.Compression.LZ4` | 1.3.8 | Apache-2.0 | RePKG 解包 pkg 内的 LZ4 压缩数据 |
| `Newtonsoft.Json` | 13.0.3 | MIT | RePKG 读 scene.json |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | MIT | 测试宿主（仅测试工程） |
| `xunit` / `xunit.runner.visualstudio` | 2.9.2 / 2.8.2 | Apache-2.0 | 测试框架（仅测试工程） |

### SixLabors.ImageSharp 的许可证口径（升级前必须读）

- **2.x**：Apache License 2.0。
- **3.0.0 起**：改为 **Six Labors Split License** —— 开源项目、或个人/年营收低于
  100 万美元的场景仍可按 Apache 2.0 使用；否则需要 Six Labors 商业授权。

本项目当前钉在 **2.1.13**（v0.2.0 从 2.1.9 升上来，清除两条可被恶意 GIF 触发的
解码路径高危公告）。升到 3.2.0+ 才能清掉剩余 3 条 High 公告（TIFF 编码器与
HistogramEqualization，本项目不调用那些代码路径，判定不可达），但那会引入上述
许可证变化与 repkg 的 API 适配成本，属于需要单独决策的事项。

## 已被移除的死依赖

| 包 | 原来源 | 移除原因 |
| --- | --- | --- |
| `System.Drawing.Common` 9.0.0 | `RePKG.Application` 的 `PackageReference` | RePKG 源码**零处** `using System.Drawing`（全量检索 0 命中），却把 `Immutable` / `Reflection.Metadata` 9.0.0 拉进本工程，每次构建产生 48 行 `MSB3277` 版本冲突，并把两个无用 DLL 打进发布包与 `deps.json`。 |

移除方式见 `Directory.Build.targets`：按工程名条件 `PackageReference Remove`，
不修改子模块本身（子模块提交推不到上游，改了会让本仓库构建不可复现）。

## 系统运行时（非打包依赖）

- .NET 10 运行时：`SelfContained=true`，随产物分发，目标机器无需另装。
- Windows App Runtime：经 `WindowsAppSDKSelfContained=true` + `WindowsPackageType=None`
  自带，目标机器无需单独安装。
- Windows 11 DWM：窗口圆角（`DWMWA_WINDOW_CORNER_PREFERENCE`）需 Windows 11；
  Mica 背景需 Windows 11 22H2（10.0.22621）以上，低版本自动降级为桌面亚克力 / 纯色。
