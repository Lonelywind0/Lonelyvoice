@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%.dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_HOME=%ROOT%.dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"
set "NUGET_PACKAGES=%ROOT%.nuget\packages"

set "EXE=%ROOT%gui\bin\Release\net8.0-windows\vscard-gui.exe"

if not exist "%EXE%" (
  echo 首次运行，正在编译界面，请稍候...
  "%DOTNET_ROOT%\dotnet.exe" build "%ROOT%gui\VirtualSoundCard.Gui.csproj" -c Release -v minimal
  if errorlevel 1 (
    echo.
    echo 编译失败，请把上面的错误信息发出来。
    pause
    exit /b 1
  )
)

start "" "%EXE%"
