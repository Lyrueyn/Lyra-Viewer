@echo off
rem 用 Vulkan 渲染视频启动播放器（D3D11 仍负责呈现/弹幕；HDR 内容暂时自动回退 D3D11）
set DSH_GRAPHICS=vulkan
cd /d "%~dp0WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64"
start "" WinUIGui.exe
