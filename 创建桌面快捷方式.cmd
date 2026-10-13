@echo off
chcp 65001 >nul
setlocal
rem 在桌面放一个 Ahz Scribe 快捷方式（带图标）。快捷方式丢了或换机器跑它就行。
set "DIR=%~dp0"
set "EXE=%DIR%AhzScribe.exe"
if not exist "%EXE%" ( echo [X] 没找到 %EXE% ，先跑 build.cmd & exit /b 1 )
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Ahz Scribe.lnk'; $sc = $ws.CreateShortcut($lnk); $sc.TargetPath = '%EXE%'; $sc.WorkingDirectory = '%DIR%'; $sc.IconLocation = '%EXE%,0'; $sc.Description = 'Ahz Scribe - 本地智能体'; $sc.Save(); Write-Host ('[√] 已创建 ' + $lnk)"
exit /b 0
