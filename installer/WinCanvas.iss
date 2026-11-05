; ===== WinCanvas 安装包脚本（Inno Setup 6） =====
;
; 用法（在仓库根目录执行）：
;   ISCC.exe /DSourceDir="<publish 输出目录>" /DMyAppVersion="0.3.0" installer\WinCanvas.iss
;
; 本脚本已在 Inno Setup 6.7 上实测编译通过（v0.3.0）。这一步不能省：
; [Setup] 段的非法指令、[Code] 段的未定义标识符都只在编译期暴露，
; 而 Inno 一遇错就中止 —— 只看文件内容 review 会全部漏过。
; v0.3.0 自检时一次撞出四处：SetupAppTitle 不是 [Setup] 指令、
; OnlyBelowVersion=0,6.4 等于「只允许装在 Windows 10 以下」（与 MinVersion 矛盾）、
; TaskSelected 在卸载阶段根本不存在（Tasks 属于安装向导，没有读回接口）、
; MsgBox 的按钮参数是 mb_YesNo 这样的单常量而不是 [mbYes, mbNo]。
; CI 的「编译 Inno Setup 安装包」步骤把这件事变成每次标签推送都跑的门禁。
;
; 版本单一来源：/DMyAppVersion 的值应取自 Directory.Build.props 的 <WinCanvasVersion>。
; CI（.github/workflows/build-and-test.yml）已从该文件解析后再传入，不要手工另写一份数字 ——
; 迁移前 exe 文件版本恒为 1.0.0.0、git tag 是 v0.1.1、安装包版本手工传，
; 三套口径互不相干，且缺 VersionInfoVersion 会让 Inno 的升降级文件版本比较失效。
;
; SourceDir 指向 dotnet publish 的产物目录，应含 WinCanvas.App.exe、*.xbf、
; WinCanvas.App.pri、WinCanvas.ico 与 WinTips\ 子目录（见 App.csproj 的
; IncludeRuntimeArtifactsInPublish Target；缺 .xbf/.pri 的包启动即崩溃，
; CI 有独立步骤校验这几个文件齐备）。

#ifndef SourceDir
  #error 请用 /DSourceDir="..." 指定 dotnet publish 产物目录
#endif
#ifndef MyAppVersion
  #error 请用 /DMyAppVersion="..." 指定版本号（取自 Directory.Build.props 的 WinCanvasVersion）
#endif

#define MyAppName "WinCanvas"
#define MyAppExeName "WinCanvas.App.exe"
; AppId 固定 GUID：保证升级/卸载时识别为同一程序
#define MyAppId "{{8F2A6D9C-4B3E-4E7A-9C51-2D7B0A5E1F30}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
; 写入 setup.exe 与卸载程序的文件版本属性。缺这两行的后果：
; Windows 与 Inno 都无法据文件版本判断升降级，覆盖安装行为不可预期。
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
AppPublisher=chenmoulaile
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; 最低系统版本：圆角需 Windows 11 DWM 属性，但 1809 起其余功能可用
; （Mica 在 code-behind 里按 build>=22621 判定，低版本自动降级亚克力/纯色）。
; 与 csproj 的 TargetPlatformMinVersion=10.0.17763 保持一致口径。
;
; 这里刻意【不写】OnlyBelowVersion：v0.3.0 自检时发现曾写成 `0,6.4`，
; 而 Inno 的版本号里 6.4 就是 Windows 10 的前一版 —— 那条上限等于「只允许装在
; Windows 10 以下」，与 MinVersion=10.0.17763 直接矛盾，会让所有 Win10/11 机器
; 在安装第一步被拒。下限有 MinVersion 一处表达就够了。
MinVersion=10.0.17763

; 仅 64 位：产物固定 win-x64，装在 ARM64 上走模拟运行时 WinUI 3 宿主不可靠
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 升级时先关应用：本程序是 self-contained 的 200MB+ 目录、500+ 文件，
; 运行中覆盖会因文件被锁而半途失败，留下一个既不是新版也不是旧版的目录。
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
; 向导体标题由 AppName 派生（Inno 没有 SetupAppTitle 这条 [Setup] 指令，
; 写上去编译期就中止：Unrecognized [Setup] section directive）。

; 与 App.xaml.cs 里的单实例互斥体同名（Inno 会同时试 Local\ 与 Global\ 前缀）。
; 配合 CloseApplications，安装/卸载前提示并允许关闭正在运行的实例。
AppMutex=WinCanvas.App.SingleInstance

; 沿用上次安装目录（注意：含义是「复用旧目录」，不是「允许降级重装」——
; 迁移前这里的注释把它误解成了后者）。
UsePreviousAppDir=yes

Compression=lzma2/max
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=WinCanvas-setup-{#MyAppVersion}
WizardStyle=modern
DisableWelcomePage=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; 壁纸解包缓存的清理【不在这里做】：Inno 的 Tasks 只在安装向导里存在，
; 卸载阶段没有向导、也就没有任何 API 能读回用户当初勾没勾（TaskSelected 在
; [Code] 里是未定义标识符，编译期就报 Unknown identifier）。
; 改成卸载时直接问 —— 见下面 CurUninstallStepChanged。

[Files]
; 发布产物整体（含 WinTips\ 子目录、WinCanvas.ico 与全部运行时依赖）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\WinCanvas.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\WinCanvas.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清【程序目录内的运行产物】。诊断日志实际写在 %LOCALAPPDATA%\WinCanvas\logs，
; 迁移前这里写的是 {app}\*.log —— 一条永远命中不到任何东西的空规则。
Type: filesandordirs; Name: "{app}\logs"

[InstallDelete]
; 升级时先清掉上一版程序目录，避免 self-contained 产物在版本迭代中累积孤儿 DLL
; （500+ 文件的目录，逐次覆盖会留下新版不再引用的旧文件）。
Type: filesandordirs; Name: "{app}\WinTips"

[code]
// 卸载时按用户意愿清理壁纸解包缓存。
//
// 为什么在这里问而不是做成安装期的 Task：Tasks 属于安装向导，卸载阶段
// 既没有向导也没有读回接口，「卸载时按当初的勾选清理」在 Inno 里做不到。
// 配置 %APPDATA%\WinCanvas\config.json 【始终不删】：那是用户的窗口档案，
// 误删的代价远大于省下的磁盘空间。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CachePath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 开机自启动兜底清理（开机自启动工作包）：应用内开关正常关闭时该值已不存在，
    // 这里覆盖「卸载时开关还开着 / 用户手工改过注册表」的情形，避免留下指向已删除
    // 程序目录的死条目（登录时 explorer 启动失败）。只删值名 WinCanvas 一个值，
    // 同键下其它程序的自启动条目不受影响；DeleteValue 对不存在的值安静返回。
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WinCanvas');

    CachePath := ExpandConstant('{localappdata}\WinCanvas\wallpaper-cache');
    if DirExists(CachePath) then
    begin
      if MsgBox('同时删除壁纸解包缓存？' + #13#10 +
                CachePath + #13#10 +
                '（这里放的是 Wallpaper Engine .pkg 解包出来的图片与视频，可能占用数 GB。' +
                '选「否」则保留，重装后不必重新解包。）',
                mbConfirmation, mb_YesNo) = IDYES then
      begin
        DelTree(CachePath, True, True, True);
      end;
    end;
  end;
end;

[Messages]
; 中文环境由 Inno 按系统语言自动选择；这里补充安装完成后提示
FinishedLabelNoIcons=安装完成。%n%nWinCanvas 已就绪：窗口置顶 / 透明 / 穿透 / 圆角 + 独立壁纸背景。%n%n程序已放入系统托盘，默认全局快捷键：Ctrl+Alt+C 显示/隐藏主界面。
FinishedLabel=安装完成。%n%nWinCanvas 已就绪：窗口置顶 / 透明 / 穿透 / 圆角 + 独立壁纸背景。%n%n程序已放入系统托盘，默认全局快捷键：Ctrl+Alt+C 显示/隐藏主界面。
