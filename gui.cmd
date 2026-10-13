@echo off
setlocal
pushd "%~dp0"

set "DOTNET_ROOT=%~dp0.dotnet"
set "DOTNET_CLI_HOME=%~dp0.dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"
set "NUGET_PACKAGES=%~dp0.nuget\packages"
set "EXE=%~dp0gui\bin\Release\net8.0-windows\vscard-gui.exe"

if not exist "%DOTNET_ROOT%\dotnet.exe" (
  echo [ERROR] Local .NET SDK not found: %DOTNET_ROOT%\dotnet.exe
  echo         See README section "Self-contained environment" for how to install it.
  popd
  exit /b 1
)

if not exist "%EXE%" (
  echo First run: building the GUI, please wait...
  "%DOTNET_ROOT%\dotnet.exe" build "%~dp0gui\VirtualSoundCard.Gui.csproj" -c Release -v minimal
  if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. Please copy the error messages above.
    popd
    pause
    exit /b 1
  )
)

start "" "%EXE%"
popd
exit /b 0
