; 本地媒体播放器 —— Inno Setup 6 正式安装脚本（当前用户安装模式）
; Inno 6 是业界主流稳定版；Inno 7 有进程缓解 bug 会在本机误判"拒绝访问"

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
DefaultDirName={localappdata}\LocalMediaPlayer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\inno-out6
OutputBaseFilename=本地媒体播放器-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
AllowNoIcons=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "post-install.ps1,uninstall.ps1,setup-log.txt,crash.log,dm_debug.log,dm_recv.log,dsh_engine.log,audio_dump.f32"

[Dirs]
Name: "{app}\data"; Flags: uninsneveruninstall

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userprograms}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent