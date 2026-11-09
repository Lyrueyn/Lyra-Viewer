@echo off
rem Test: play through the HP S1X sound bar (wired) instead of the Bluetooth headset.
rem If the "sizzle/pop" disappears here -> the Bluetooth link is the culprit, not the player.
set DSH_GRAPHICS=vulkan
set DSH_AUDIO_DEVICE=S1X
cd /d "%~dp0"
start "" "WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64\WinUIGui.exe"