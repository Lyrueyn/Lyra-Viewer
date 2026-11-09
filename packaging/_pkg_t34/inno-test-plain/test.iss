[Setup]
AppName=TestInnoplain
AppVersion=1.0
DefaultDirName={localappdata}\TestInnoplain
OutputDir=D:\DSH\_pkg_t34\inno-test-plain\out
OutputBaseFilename=TestInnoplain
PrivilegesRequired=lowest
Compression=none
Uninstallable=yes
CreateUninstallRegKey=yes

[Files]
Source: "D:\DSH\_pkg_t34\probe_payload.txt"; DestDir: "{app}"
[Icons]
Name: "{userprograms}\TestInnoplain"; Filename: "{app}\probe_payload.txt"