@echo off
REM =====================================================================
REM  HDR passthrough launcher for the WinUI3 player (dsh_engine.dll)
REM  Requires: Windows HDR (Advanced Color) turned ON for this display.
REM  Effect:   HDR (PQ/BT.2020) videos are output as real 10-bit HDR10
REM            (10bit swapchain + ST2084 + MaxCLL metadata) instead of
REM            being tone-mapped to SDR. SDR videos are unaffected.
REM  ASCII only (cmd reads the GBK codepage; Chinese comments break it).
REM =====================================================================
set DSH_HDR_PASSTHROUGH=1
set "_exe=F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64\WinUIGui.exe"

if not exist "%_exe%" (
    echo [ERROR] WinUIGui.exe not found. Build WinUIGui first.
    pause
    exit /b 1
)

echo =============================================
echo   Local Media Player  [HDR passthrough]
echo   HDR videos -^> 10bit PQ/BT.2020 direct output
echo   SDR videos -^> normal (tone-map path untouched)
echo =============================================
echo.
echo If the picture looks washed out, Windows HDR is OFF
echo   (Settings ^> System ^> Display ^> HDR) - close and use the
echo   normal launcher instead; it tone-maps HDR to SDR correctly.
echo.
start "" "%_exe%"
