@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
title 本地媒体播放器 - 安装程序

echo ================================================
echo   本地媒体播放器  安装程序
echo ================================================
echo.
echo 即将安装到: %LOCALAPPDATA%\LocalMediaPlayer
echo （不需要管理员权限；不会写入系统目录）
echo.
choice /c YN /m "确认安装?"
if errorlevel 2 goto :cancelled

set "TARGET=%LOCALAPPDATA%\LocalMediaPlayer"
set "SRC=%~dp0payload"

echo.
echo [1/4] 创建目录...
if not exist "%TARGET%" mkdir "%TARGET%"

echo [2/4] 复制文件（约 400MB，请稍等）...
robocopy "%SRC%" "%TARGET%" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if errorlevel 8 (
  echo 复制失败！请检查磁盘空间。
  pause
  exit /b 1
)

echo [3/4] 创建快捷方式...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell; $exe = Join-Path $env:LOCALAPPDATA 'LocalMediaPlayer\WinUIGui.exe'; $sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'; $lnk = $ws.CreateShortcut((Join-Path $sm '本地媒体播放器.lnk')); $lnk.TargetPath = $exe; $lnk.WorkingDirectory = (Split-Path $exe); $lnk.Description = '本地媒体播放器'; $lnk.Save(); $desk = [Environment]::GetFolderPath('Desktop'); $lnk2 = $ws.CreateShortcut((Join-Path $desk '本地媒体播放器.lnk')); $lnk2.TargetPath = $exe; $lnk2.WorkingDirectory = (Split-Path $exe); $lnk2.Save()"

echo [4/4] 写入卸载信息...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalMediaPlayer'; New-Item -Path $k -Force | Out-Null; Set-ItemProperty $k 'DisplayName' '本地媒体播放器'; Set-ItemProperty $k 'DisplayVersion' '1.0.0'; Set-ItemProperty $k 'Publisher' '测试版'; Set-ItemProperty $k 'InstallLocation' (Join-Path $env:LOCALAPPDATA 'LocalMediaPlayer'); Set-ItemProperty $k 'UninstallString' ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $env:LOCALAPPDATA 'LocalMediaPlayer\uninstall.ps1') + '"'); Set-ItemProperty $k 'NoModify' 1 -Type DWord; Set-ItemProperty $k 'NoRepair' 1 -Type DWord"

echo.
echo ================================================
echo   安装完成！
echo   开始菜单/桌面 已创建「本地媒体播放器」
echo   首次启动会弹 Windows 防火墙提示，请点「允许」
echo ================================================
echo.
choice /c YN /m "现在启动?"
if errorlevel 2 goto :done
start "" "%TARGET%\WinUIGui.exe"
:done
echo 安装结束。
pause
exit /b 0

:cancelled
echo 已取消。
pause
exit /b 1