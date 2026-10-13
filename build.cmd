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

"%DOTNET_ROOT%\dotnet.exe" build "%ROOT%Lonelyvoice.sln" -c Release %*
