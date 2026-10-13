@echo off
setlocal
pushd "%~dp0"

set "DOTNET_ROOT=%~dp0.dotnet"
set "DOTNET_CLI_HOME=%~dp0.dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"
set "NUGET_PACKAGES=%~dp0.nuget\packages"

if not exist "%DOTNET_ROOT%\dotnet.exe" (
  echo [ERROR] Local .NET SDK not found: %DOTNET_ROOT%\dotnet.exe
  echo         See README section "Self-contained environment" for how to install it.
  popd
  exit /b 1
)

"%DOTNET_ROOT%\dotnet.exe" run --project "%~dp0src\VirtualSoundCard.csproj" -c Release -v quiet -- %*
set "CODE=%ERRORLEVEL%"

popd
exit /b %CODE%
