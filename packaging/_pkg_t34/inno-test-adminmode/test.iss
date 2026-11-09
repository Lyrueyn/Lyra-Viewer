[Setup]
AppName=TestInnoadminmode
AppVersion=1.0
DefaultDirName={localappdata}\TestInnoadminmode
OutputDir=D:\DSH\_pkg_t34\inno-test-adminmode\out
OutputBaseFilename=TestInnoadminmode
PrivilegesRequired=lowest
Compression=none
Uninstallable=yes
CreateUninstallRegKey=yes
PrivilegesRequired=admin
[Files]
Source: "D:\DSH\_pkg_t34\probe_payload.txt"; DestDir: "{app}"
[Icons]
Name: "{userprograms}\TestInnoadminmode"; Filename: "{app}\probe_payload.txt"