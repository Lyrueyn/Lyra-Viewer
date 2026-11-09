@echo off
REM ============================================================
REM  Realtek ASIO 64-bit manual deployment (fixed locations)
REM  Run this file AS ADMINISTRATOR
REM ============================================================
setlocal
set SRC=C:\Users\Administrator\Downloads\Realtek-ASIO-Driver-main\Realtek-ASIO-Driver-main\ASIO
set RTC=C:\Windows\System32\rtcom
set CPD=C:\Program Files\Realtek\Audio\ASIO

echo [1/4] Creating System32\rtcom (64-bit driver side)
if not exist "%RTC%" mkdir "%RTC%"
if not exist "%CPD%" mkdir "%CPD%"

echo [2/4] Copying 64-bit driver + APO components to System32\rtcom
copy /y "%SRC%\RTHDASIO64.dll"   "%RTC%\RTHDASIO64.dll" >nul
copy /y "%SRC%\CX32APO.dll"      "%RTC%\CX32APO.dll" >nul
copy /y "%SRC%\RTCOMDLL.dll"     "%RTC%\RTCOMDLL.dll" >nul
copy /y "%SRC%\RtDataProc.dll"   "%RTC%\RtDataProc.dll" >nul
copy /y "%SRC%\RTHDASIO.dll"     "%RTC%\RTHDASIO.dll" >nul
copy /y "%SRC%\RtkCfg.dll"       "%RTC%\RtkCfg.dll" >nul
copy /y "%SRC%\RTLCPAPI.dll"     "%RTC%\RTLCPAPI.dll" >nul

echo [3/4] Copying 64-bit Control Panel to Program Files\Realtek\Audio\ASIO
copy /y "%SRC%\RTASIOCP64.dll"   "%CPD%\RTASIOCP64.dll" >nul

echo [4/4] Registering ASIO COM server and pointing CLSID to rtcom\RTHDASIO64.dll
regsvr32 /s "%RTC%\RTHDASIO64.dll"
reg add "HKLM\SOFTWARE\Classes\CLSID\{A80362FF-CE76-4DD9-874A-704C57BF0D6A}\InprocServer32" /ve /d "C:\Windows\System32\rtcom\RTHDASIO64.dll" /f >nul

echo.
echo Done. Now test Realtek ASIO with asio_probe.
pause
