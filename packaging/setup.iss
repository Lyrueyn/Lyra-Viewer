; 本地媒体播放器 —— Inno Setup 正规安装程序（当前用户安装模式）
; 为什么用 lowest：本机 UAC/进程防护导致写 Program Files / ProgramData 一律被拒，
;                 而 per-user 安装是微软自家 VS Code、Chrome 用户版等采用的标准做法。
;                 → 不需要管理员权限、不弹 UAC、装到 %LOCALAPPDATA%，控制面板可卸载。

#define MyAppName "璃鸢镜"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Lyrueyn"
#define MyAppExeName "WinUIGui.exe"
#define SrcDir "final"

[Setup]
AppId={{8F3A2C41-7B9E-4D6A-9C2B-5E1D8A3F0C72}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; ★ 当前用户安装：装到 %LOCALAPPDATA%\LocalMediaPlayer，无需管理员
DefaultDirName={localappdata}\LocalMediaPlayer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\inno-out-1.0.0
OutputBaseFilename=璃鸢镜-Setup-1.0.0
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; ★ 不需要管理员 → 不会弹 UAC、不会碰 Program Files/ProgramData
PrivilegesRequired=lowest
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
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "post-install.ps1,uninstall.ps1,setup-log.txt,crash.log,*.log,*.log.old,dm_debug.log,dm_recv.log,dsh_engine.log,audio_dump.f32,local_media.db,local_media.db-wal,local_media.db-shm,config.json,player_settings.json,playwright_cookies.json,data\*"

[Dirs]
Name: "{app}\data"; Flags: uninsneveruninstall

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userprograms}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent