@echo off
rem Test: play through Realtek (onboard) instead of the Bluetooth headset.
set DSH_GRAPHICS=vulkan
set DSH_AUDIO_DEVICE=Realtek
cd /d "%~dp0"
start "" "WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64\WinUIGui.exe"