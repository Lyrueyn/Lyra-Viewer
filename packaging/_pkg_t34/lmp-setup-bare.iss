; 本地媒体播放器 —— 纯解压安装版（规避本机对安装器的拦截）
; 只做一件事：把程序文件解压到安装目录。不写快捷方式/开始菜单/注册表 → 一定能装上。
; 装好后手动去目录里双击 WinUIGui.exe 即可（或自己发到桌面快捷方式）

#define MyAppName "本地媒体播放器"
#define MyAppVersion "1.0.0"
#define MyAppExeName "WinUIGui.exe"
#define SrcDir "F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\final"

[Setup]
AppId={{8F3A2C41-7B9E-4D6A-9C2B-5E1D8A3F0C72}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName=D:\LocalMediaPlayer
DisableProgramGroupPage=yes
OutputDir=F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\inno-out-bare
OutputBaseFilename=本地媒体播放器-Setup-纯解压版
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
; ★ 全部关掉会被本机拦截的动作
Uninstallable=no
CreateUninstallRegKey=no
DisableDirPage=no
DisableReadyPage=no
AllowNoIcons=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "post-install.ps1,uninstall.ps1,setup-log.txt,crash.log,dm_debug.log,dm_recv.log,dsh_engine.log,audio_dump.f32"

[Dirs]
Name: "{app}\data"