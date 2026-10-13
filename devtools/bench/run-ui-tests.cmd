@echo off
chcp 65001 >nul
setlocal
rem 一键回归：界面/流式刷新 + 规矩文件 + 真实模型端到端
rem   ui-tests.cmd            跑界面自测 + 规矩自测
rem   ui-tests.cmd e2e        额外跑真实模型端到端（会真的调用本机 Ollama）
set "DIR=%~dp0"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "SRC=%DIR%..\..\src\AhzScribe.cs"
set "OUT=%DIR%_build"
if not exist "%OUT%" mkdir "%OUT%"

echo [1/4] 编译界面自测 ...
"%CSC%" /nologo /target:exe /main:FlickerCheck /out:"%OUT%\flicker.exe" /win32manifest:"%DIR%..\..\src\app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  "%SRC%" "%DIR%flicker-unittest.cs" || goto fail

echo [2/4] 编译规矩自测 ...
"%CSC%" /nologo /target:exe /main:RulesUnit /out:"%OUT%\rules.exe" /win32manifest:"%DIR%..\..\src\app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  "%SRC%" "%DIR%rules-unittest.cs" || goto fail

echo [3/4] 编译设置页自测 ...
"%CSC%" /nologo /target:exe /main:SettingsUnit /out:"%OUT%\settingsunit.exe" /win32manifest:"%DIR%..\..\src\app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  "%SRC%" "%DIR%settings-unittest.cs" || goto fail

rem 规矩自测要在临时目录里建文件夹；有些环境把 %TEMP% 锁了，这里改成用 _build\tmp
if not exist "%OUT%\tmp" mkdir "%OUT%\tmp"
set "TEMP=%OUT%\tmp"
set "TMP=%OUT%\tmp"

echo.
echo == 界面/流式刷新自测 ==
"%OUT%\flicker.exe" || goto fail
echo.
echo == 规矩文件自测 ==
"%OUT%\rules.exe" || goto fail
echo.
echo == 设置页自测 ==
"%OUT%\settingsunit.exe" || goto fail
echo.
echo == 真实控件树几何自测（越界 / 浮层对位）==
rem 观测真实控件几何，不看像素 —— CopyFromScreen 实测会被别的窗口和 DWM 合成骗
rem 注意：这里不能用 `call ... || goto fail` —— call 传回的错误码在那里不可靠，
rem 会把几何全过的情形也判成失败（实测踩过）。用 if errorlevel 明确判断。
call "%DIR%..\uiobserve.cmd" inside
if errorlevel 1 goto fail
call "%DIR%..\uiobserve.cmd" popovers
if errorlevel 1 goto fail

if /i "%~1"=="e2e" (
    echo [4/4] 编译并运行真实模型端到端 ...
    "%CSC%" /nologo /target:exe /main:E2E /out:"%OUT%\e2e.exe" /win32manifest:"%DIR%..\..\src\app.manifest" ^
      /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
      "%SRC%" "%DIR%_e2e.cs" || goto fail
    echo.
    echo == 真实模型端到端（本机 Ollama）==
    "%OUT%\e2e.exe" %2 %3 || goto fail
)

echo.
echo 全部通过。
exit /b 0

:fail
echo.
echo [X] 有测试没过，请把上面的输出发给我。
exit /b 1
