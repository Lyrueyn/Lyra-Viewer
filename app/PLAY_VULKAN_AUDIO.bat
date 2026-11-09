@echo off
rem Vulkan video + WASAPI audio.
rem Default: use the Windows DEFAULT output device (the one you actually hear).
rem To force a specific device, uncomment these two lines and put any substring of its name:
rem   S1X | Realtek | MOONDROP | MAG | CABLE
rem NOTE: a device can initialize fine and still be silent (powered off / wrong input / its own mute).
set DSH_GRAPHICS=vulkan
rem set AUDIO_DEVICE=S1X
rem set DSH_AUDIO_DEVICE=%AUDIO_DEVICE%
cd /d "%~dp0WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64"
start "" WinUIGui.exe