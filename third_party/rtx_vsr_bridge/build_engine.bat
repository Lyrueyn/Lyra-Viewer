@echo off
set PATH=E:\Qt\Tools\mingw1310_64\bin;%PATH%
setlocal
set GPP=E:\Qt\Tools\mingw1310_64\bin\g++.exe
set ENG=F:\DSH\LocalMediaPlayer-Workspace\app\dsh_engine
set APP=F:\DSH\LocalMediaPlayer-Workspace\app
set VKINC=F:\DSH\LocalMediaPlayer-Workspace\app\Vulkan-Headers-main\Vulkan-Headers-main\include
set FFINC=D:\ETHEREIS\third_party\ffmpeg\include
set FFLIB=D:\ETHEREIS\third_party\ffmpeg\lib
set QT=E:\Qt\6.11.1\mingw_64
cd /d "%ENG%"
"%GPP%" -shared -std=c++17 -O2 -DNDEBUG -DVK_USE_PLATFORM_WIN32_KHR -DVK_NO_PROTOTYPES ^
  dsh_engine.cpp danmaku_lanes.cpp zstub.cpp ^
  -I"%ENG%" -I"%VKINC%" -I"%FFINC%" -I"%QT%\include\QtFreetype" -I"%QT%\include" ^
  -L"%FFLIB%" -L"%QT%\lib" ^
  -lavformat -lavcodec -lavfilter -lavutil -lswscale -lswresample ^
  -lQt6BundledFreetype -lQt6BundledLibpng ^
  -ld3d11 -ldxgi -ld3dcompiler_47 -lgdi32 -luser32 -lole32 -lws2_32 -lshcore -lksuser ^
  -static -static-libgcc -static-libstdc++ -o dsh_engine_vsr.dll > build_engine.log 2>&1
echo EXIT_CODE=%ERRORLEVEL% >> build_engine.log
endlocal
