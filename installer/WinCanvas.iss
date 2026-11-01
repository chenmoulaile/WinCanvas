; ===== WinCanvas 安装包脚本（Inno Setup 6） =====
; 用法（在仓库根目录执行）：
;   ISCC.exe /DSourceDir="<publish 输出目录>" /DMyAppVersion="0.1.1" installer\WinCanvas.iss
; SourceDir 指向 dotnet publish 的产物目录（应含 WinCanvas.App.exe、*.xbf、
; WinCanvas.App.pri 与 WinTips\ 子目录——见 App.csproj 的
; IncludeRuntimeArtifactsInPublish Target，缺 .xbf/.pri 的包启动即崩溃）。

#ifndef SourceDir
  #error 请用 /DSourceDir="..." 指定 dotnet publish 产物目录
#endif
#ifndef MyAppVersion
  #error 请用 /DMyAppVersion="..." 指定版本号（与 release tag 对应）
#endif

#define MyAppName "WinCanvas"
#define MyAppExeName "WinCanvas.App.exe"
; AppId 固定 GUID：保证升级/卸载时识别为同一程序
#define MyAppId "{{8F2A6D9C-4B3E-4E7A-9C51-2D7B0A5E1F30}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=chenmoulaile
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 输出目录与基础文件名（编译产物：installer/Output/WinCanvas-setup-<版本>.exe）
OutputDir=Output
OutputBaseFilename=WinCanvas-setup-{#MyAppVersion}
; 允许降级重装以便回退旧版本
UsePreviousAppDir=yes
; 压缩率与体积平衡：发布产物约 210MB（自包含），lzma2 压到 80MB 级
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
; 普通用户可安装到自身目录场景已覆盖 {autopf}，默认管理员权限
PrivilegesRequired=admin
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 发布产物整体（含 WinTips\ 子目录与全部运行时依赖）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清理运行期配置（用户数据：如需保留请勿勾选「删除配置」，此处默认保留）
Type: files; Name: "{app}\*.log"

[Messages]
; 中文环境由 Inno 按系统语言自动选择；这里补充安装完成后提示
FinishedLabelNoIcons=安装完成。%n%nWinCanvas 已就绪：窗口置顶 / 透明 / 穿透 / 圆角 + 独立壁纸背景。
FinishedLabel=安装完成。%n%nWinCanvas 已就绪：窗口置顶 / 透明 / 穿透 / 圆角 + 独立壁纸背景。
