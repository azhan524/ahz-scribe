@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
set "DOTNET=%ROOT%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
"%DOTNET%" build "%ROOT%\migration\AhzScribe.Net10\AhzScribe.Net10.csproj" -c Release --nologo || exit /b 1
if /i "%~1"=="publish" (
  "%DOTNET%" publish "%ROOT%\migration\AhzScribe.Net10\AhzScribe.Net10.csproj" -c Release -r win-x64 --self-contained true -o "%ROOT%\dist\net10" -p:PublishReadyToRun=false --nologo || exit /b 1
  echo 发布到 %ROOT%\dist\net10
)
echo .NET 10 构建完成。
