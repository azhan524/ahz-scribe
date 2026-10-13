@echo off
chcp 65001 >nul
setlocal
set "DIR=%~dp0"

echo ============================================
echo   Ahz Scribe 升级（把 .new 换成正式版）
echo ============================================
echo.

if not exist "%DIR%AhzScribe.exe.new" (
    echo [!] 没找到 AhzScribe.exe.new，先运行 build.cmd 重新编译。
    pause
    exit /b 1
)

:wait_close
tasklist /fi "imagename eq AhzScribe.exe" 2>nul | find /i "AhzScribe.exe" >nul
if not errorlevel 1 (
    echo [*] 检测到 Ahz Scribe 还在运行。请先把它关掉，然后回到这个窗口按任意键继续...
    pause >nul
    goto wait_close
)

echo [*] 正在替换程序文件...
move /y "%DIR%AhzScribe.exe.new" "%DIR%AhzScribe.exe" >nul
if exist "%DIR%ahzscribe-cli.exe.new" move /y "%DIR%ahzscribe-cli.exe.new" "%DIR%ahzscribe-cli.exe" >nul
if errorlevel 1 (
    echo [X] 替换失败，请确认程序已经完全退出。
    pause
    exit /b 1
)

echo [√] 升级完成，现在启动新版。
start "" "%DIR%AhzScribe.exe"
timeout /t 2 >nul
exit /b 0
