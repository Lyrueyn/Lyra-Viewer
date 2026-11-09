; 本地媒体播放器 —— Inno Setup 正式安装脚本（管理员安装模式）
; ★ PrivilegesRequired=admin → 双击时 Windows 自动弹 UAC（"是否允许此应用对你的设备进行更改"）
; ★ 装到 C:\Program Files\本地媒体播放器
; ★ 全用户开始菜单 + 桌面图标 + 控制面板"应用和功能"里可卸载

#define MyAppName "本地媒体播放器"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "个人测试版"
#define MyAppExeName "WinUIGui.exe"
#define SrcDir "F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\final"

[Setup]
AppId={{8F3A2C41-7B9E-4D6A-9C2B-5E1D8A3F0C72}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\inno-out-admin
OutputBaseFilename=本地媒体播放器-Setup-管理员版
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; ★★ 关键：需要管理员 → 双击自动弹 UAC
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
AllowNoIcons=yes
DisableDirPage=no
DisableReadyPage=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "post-install.ps1,uninstall.ps1,setup-log.txt,crash.log,dm_debug.log,dm_recv.log,dsh_engine.log,audio_dump.f32"

[Dirs]
Name: "{app}\data"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent