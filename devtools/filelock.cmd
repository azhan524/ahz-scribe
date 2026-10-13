@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
rem 文件锁诊断 / 安全终止（升级需求 P0-1）
rem   filelock.cmd <文件路径>            报告谁锁着
rem   filelock.cmd <文件路径> --kill     先优雅关闭、超时再强杀
rem   filelock.cmd <文件路径> --kill --quiet
rem   filelock.cmd --build-locked <输出文件>   给 build.cmd 用：被锁就打印原因，返回 1
set "DIR=%~dp0"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "EXE=%DIR%_build\filelock.exe"
set "SRC=%DIR%filelock.cs"

if not exist "%DIR%_build" mkdir "%DIR%_build"

rem 源码比 exe 新（或 exe 不在）就重编 —— 让工具永远跟源码同步
set "NEED=0"
if not exist "%EXE%" (set "NEED=1") else (
    for %%A in ("%SRC%") do set "SRCT=%%~tA"
    for %%A in ("%EXE%") do set "EXET=%%~tA"
    if not "!SRCT!"=="!EXET!" set "NEED=1"
)
if "!NEED!"=="1" (
    "%CSC%" /nologo /target:exe /out:"%EXE%" /r:System.dll /r:System.Core.dll "%SRC%" >nul 2>&1
    if not exist "%EXE%" (
        echo [X] filelock.exe 编译失败，请单独跑一次看报错：
        echo     "%CSC%" /target:exe /out:"%EXE%" "%SRC%"
        exit /b 2
    )
)

if "%~1"=="" (
    echo 用法: filelock.cmd ^<文件路径^> [--kill] [--quiet] [--wait 秒]
    echo       filelock.cmd --build-locked ^<输出文件^>
    exit /b 2
)

if /i "%~1"=="--build-locked" (
    if "%~2"=="" ( echo [X] 缺少输出文件参数 & exit /b 2 )
    "%EXE%" "%~2" --quiet
    if errorlevel 1 (
        echo.
        echo   怎么解决（三选一）：
        echo     1^) 关掉窗口版 Ahz Scribe，然后重新跑 build.cmd
        echo     2^) 让 build.cmd 出 .new 再跑「升级.cmd」替换（本脚本只诊断，不动你的程序）
        echo     3^) 明确要现在终止：devtools\filelock.cmd "%~2" --kill
        echo.
    )
    exit /b %errorlevel%
)

"%EXE%" %*
exit /b %errorlevel%
