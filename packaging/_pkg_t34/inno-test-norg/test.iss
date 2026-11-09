[Setup]
AppName=TestInnonorg
AppVersion=1.0
DefaultDirName={localappdata}\TestInnonorg
OutputDir=D:\DSH\_pkg_t34\inno-test-norg\out
OutputBaseFilename=TestInnonorg
PrivilegesRequired=lowest
Compression=none
Uninstallable=yes
CreateUninstallRegKey=yes
RedirectionGuard=no
[Files]
Source: "D:\DSH\_pkg_t34\probe_payload.txt"; DestDir: "{app}"
[Icons]
Name: "{userprograms}\TestInnonorg"; Filename: "{app}\probe_payload.txt"