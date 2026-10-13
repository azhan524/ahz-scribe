@echo off
chcp 65001 >nul
setlocal
rem 重新生成 src\app.ico（改了图标设计就跑它，然后重跑 build.cmd）
set "DIR=%~dp0"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" ( echo [X] 找不到 csc.exe & exit /b 1 )
if not exist "%DIR%..\_build" mkdir "%DIR%..\_build" 2>nul
"%CSC%" /nologo /target:exe /out:"%DIR%..\_build\make-icon.exe" /r:System.dll /r:System.Drawing.dll "%DIR%make-icon.cs"
if errorlevel 1 ( echo [X] 编译 make-icon 失败 & exit /b 1 )
"%DIR%..\_build\make-icon.exe" "%DIR%..\src\app.ico"
if errorlevel 1 ( echo [X] 生成图标失败 & exit /b 1 )
rem 注意：这里别用中文括号，cmd 会把 ( 当代码块开始，整行解析崩掉（2026-10-13 踩过）
echo [√] 图标已更新: %DIR%..\src\app.ico
echo     重跑 build.cmd 才会生效
exit /b 0
