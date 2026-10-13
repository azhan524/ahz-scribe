@echo off
chcp 65001 >nul
setlocal
set "DIR=%~dp0"

rem 沙箱/低完整性进程写不进系统 TEMP（csc 会报 CS0016/CS1567 拒绝访问），
rem 把临时目录固定到项目内，保证 csc 临时文件总能写入。
if not exist "%DIR%_build\tmp" mkdir "%DIR%_build\tmp" 2>nul
set "TMP=%DIR%_build\tmp"
set "TEMP=%DIR%_build\tmp"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [X] 找不到系统自带的 C# 编译器 csc.exe（Windows 通常自带），无法编译。
    exit /b 1
)

set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll"
set "SRC=%DIR%src\AhzScribe.cs"

echo 编译器: %CSC%
cd /d "%DIR%"

rem ============ 窗口版 ============
rem 判"这次有没有真的写成"，不能只看 csc 的 errorlevel：
rem   · csc 在"未能写入输出文件"时**可能返回 0**（只打 CS0016 就退），只看 errorlevel 会漏判；
rem   · 早先的版本更糟 —— 把"回退出 .new 成功了"当成"编译成功"，于是打印
rem     BUILD_RESULT: UPDATED、退出 0，而正式版其实一个字都没更新。那正是本节要治的病。
rem 所以：编译前记下 exe 的修改时间，编译后比对 —— 变了才算真的写成。
for %%A in ("%DIR%AhzScribe.exe") do set "GUI_BEFORE=%%~tA"
if not exist "%DIR%AhzScribe.exe" set "GUI_BEFORE=__none__"
echo 正在编译窗口版 AhzScribe.exe ...
"%CSC%" /nologo /target:winexe /out:"%DIR%AhzScribe.exe" /win32manifest:"%DIR%src\app.manifest" ^
  %REFS% /win32icon:src\app.ico /optimize+ "%SRC%"
set "GUI_RC=%errorlevel%"
for %%A in ("%DIR%AhzScribe.exe") do set "GUI_AFTER=%%~tA"
if not exist "%DIR%AhzScribe.exe" set "GUI_AFTER=__none__"
if "%GUI_AFTER%"=="%GUI_BEFORE%" set "GUI_RC=1"

if not "%GUI_RC%"=="0" (
    rem 正式版没更新（多半是程序还在运行占着 exe）→ 退到 .new，别让整次构建白费。
    rem 替换正式版由「升级.cmd」负责（它会等程序退出再 move）。
    call :try_new "AhzScribe.exe" "/target:winexe /win32manifest:""%DIR%src\app.manifest"" /win32icon:src\app.ico /optimize+"
    if errorlevel 1 goto fail
    set "GUI_NEW=1"
) else (
    set "GUI_NEW=0"
)

rem ============ 命令行版 ============
for %%A in ("%DIR%ahzscribe-cli.exe") do set "CLI_BEFORE=%%~tA"
if not exist "%DIR%ahzscribe-cli.exe" set "CLI_BEFORE=__none__"
echo 正在编译命令行版 ahzscribe-cli.exe ...
"%CSC%" /nologo /target:exe /define:CLI_BUILD /out:"%DIR%ahzscribe-cli.exe" ^
  %REFS% /win32icon:src\app.ico /optimize+ "%SRC%"
set "CLI_RC=%errorlevel%"
for %%A in ("%DIR%ahzscribe-cli.exe") do set "CLI_AFTER=%%~tA"
if not exist "%DIR%ahzscribe-cli.exe" set "CLI_AFTER=__none__"
if "%CLI_AFTER%"=="%CLI_BEFORE%" set "CLI_RC=1"

if not "%CLI_RC%"=="0" (
    call :try_new "ahzscribe-cli.exe" "/target:exe /define:CLI_BUILD /win32icon:src\app.ico /optimize+"
    if errorlevel 1 goto fail
    set "CLI_NEW=1"
) else (
    set "CLI_NEW=0"
)
rem ---- 可选：目录里有 sign.cmd 就调用它给 exe 签名（没有就跳过）----
if exist "%DIR%sign.cmd" (
    echo 发现 sign.cmd，正在签名 ...
    call "%DIR%sign.cmd" "%DIR%AhzScribe.exe" "%DIR%ahzscribe-cli.exe"
    if errorlevel 1 (
        echo [!] 签名失败：exe 已经编译好了，只是没签上名。
    ) else (
        echo 签名完成。
    )
)

echo.
echo 编译完成：
if "%GUI_NEW%"=="1" (
    echo   AhzScribe.exe.new        窗口版（正式版被占用，没能覆盖）
) else (
    echo   AhzScribe.exe            窗口版，双击运行
)
if "%CLI_NEW%"=="1" (
    echo   ahzscribe-cli.exe.new    命令行版（正式版被占用，没能覆盖）
) else (
    echo   ahzscribe-cli.exe        命令行版（可脚本调用）
)
rem ---- 结果标记 + 退出码 ----
rem 为什么要有这个：以前被占用时也返回 0，调用方（脚本 / 智能体）会以为"正式版已更新"，
rem 于是拿着**旧的 exe** 继续干活 —— 比直接失败更危险。现在说清楚：
rem   0 = 两个正式版都更新了
rem   3 = 编译成功，但有 .new（正式版没换）→ 先跑「升级.cmd」，再重跑本脚本确认
rem   1 = 真的编译失败
if "%GUI_NEW%%CLI_NEW%"=="00" (
    echo BUILD_RESULT: UPDATED
    exit /b 0
)
echo.
echo 要换上 .new：先退出 Ahz Scribe，再双击「升级.cmd」；换完**重跑一次本脚本**确认变成 UPDATED。
echo BUILD_RESULT: NEW_ONLY
exit /b 3

rem ============================================================
rem :try_new <输出文件名> <csc 的其它参数>
rem   主输出被占用时，改写成 <输出文件名>.new。
rem   顺带用 filelock 报出到底是哪个进程占着（有 PID），并给出处置建议。
rem ============================================================
:try_new
setlocal
set "OUTNAME=%~1"
set "EXTRA=%~2"
set "LOCKED=%DIR%%OUTNAME%"
echo.
echo [!] 写不进 %OUTNAME%（多半是它还在运行）。
if exist "%DIR%devtools\filelock.cmd" (
    call "%DIR%devtools\filelock.cmd" --build-locked "%LOCKED%"
)
echo [*] 改写成 %OUTNAME%.new ...
"%CSC%" /nologo /out:"%DIR%%OUTNAME%.new" %EXTRA% %REFS% "%SRC%"
rem 必须先存下真实错误码：下面那些 if 命令会把它冲掉（errorlevel 是"上一条命令"的），
rem 以前这里靠 IF 清 errorlevel 的副作用，结果外层把"回退成功"判成了"编译成功"。
set "RC=%errorlevel%"
if not "%RC%"=="0" (
    echo [X] 写成 .new 也失败了（csc 退出码 %RC%），这次是真的编译错误，不是占用问题。
)
endlocal & exit /b %RC%

:fail
echo.
echo [X] 编译失败，请把上面的报错发给我。
echo BUILD_RESULT: FAILED
exit /b 1
