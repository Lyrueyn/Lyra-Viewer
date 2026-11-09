; 本地媒体播放器 —— 正规安装程序（Inno Setup 7）
; 特性：UAC 提权 / 中文向导 / 装到 Program Files / 开始菜单+桌面快捷方式 / 控制面板卸载

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
OutputDir=F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\inno-out
OutputBaseFilename=本地媒体播放器-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; ★ 关键：关掉 RedirectionGuard，否则从可写目录（桌面/下载夹）运行时会被它自己拦住写 HKLM
RedirectionGuard=no
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
DisableDirPage=no
DisableReadyPage=no
AllowNoIcons=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce

[Files]
; ★ 排除开发用的脚本：post-install.ps1（Inno 不需要）、uninstall.ps1（Inno 自己管卸载）、setup-log.txt
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "post-install.ps1,uninstall.ps1,setup-log.txt,crash.log,dm_debug.log,dm_recv.log,dsh_engine.log"

[Dirs]
; 用户数据目录（放曲库/设置/产物），卸载时保留
Name: "{app}\data"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent