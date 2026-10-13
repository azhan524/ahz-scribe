@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
rem UI 观测（升级需求 P0-3）：观测的是真实控件树的几何，不是像素。
rem   uiobserve.cmd tree [控件名过滤]        列出控件树
rem   uiobserve.cmd inside                  断言：所有可见控件都在父容器内（越界=布局漏了）
rem   uiobserve.cmd popovers                断言：浮层水平居中于 chip、竖直在其上方、不压住 chip
rem   uiobserve.cmd shot <控件名> <出图路径>  单控件 DrawToBitmap 出图
rem
rem 为什么不用 devtools\shot.exe：CopyFromScreen 实测会被别的窗口盖住、也会被"还没合成"骗。
set "DIR=%~dp0"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "OUT=%DIR%_build"
if not exist "%OUT%" mkdir "%OUT%"
set "EXE=%OUT%\uiobserve.exe"
set "SRC=%DIR%..\src\AhzScribe.cs"

rem 源码比 exe 新就重编（保证观测到的就是当前源码的行为）
if not exist "%EXE%" (
    set "NEED=1"
) else (
    set "NEED=0"
    for %%A in ("%SRC%") do set "ST=%%~tA"
    for %%A in ("%DIR%uiobserve.cs") do set "CT=%%~tA"
    for %%A in ("%EXE%") do set "ET=%%~tA"
    if not "!ST!"=="!ET!" set "NEED=1"
    if not "!CT!"=="!ET!" set "NEED=1"
)

if "!NEED!"=="1" (
    echo 正在编译 uiobserve.exe ...
    "%CSC%" /nologo /target:exe /main:UiObserve /out:"%EXE%" /win32manifest:"%DIR%..\src\app.manifest" ^
      /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
      "%SRC%" "%DIR%uiobserve.cs"
    if errorlevel 1 (
        echo [X] 编译失败，请把上面的报错发给我。
        exit /b 2
    )
)

"%EXE%" %*
exit /b %errorlevel%
