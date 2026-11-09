@echo off
rem Start the player with Vulkan video rendering AND HDR passthrough enabled from the start.
rem (PLAY_VULKAN.bat = Vulkan only; PLAY_HDR.bat = HDR passthrough only with D3D11.)
set DSH_GRAPHICS=vulkan
set DSH_HDR_PASSTHROUGH=1
cd /d "%~dp0WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64"
start "" WinUIGui.exe
