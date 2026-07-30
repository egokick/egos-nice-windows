@echo off
setlocal

set "APP_DIR=%~dp0"
set "PROJECT=%APP_DIR%stayactive.csproj"
set "APP=%APP_DIR%bin\Release\net10.0-windows\stayactive.exe"
set "DOTNET_CLI_HOME=%APP_DIR%..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

if not exist "%APP%" (
    call "%APP_DIR%..\scripts\ensure-dotnet-sdk.bat" 10
    if errorlevel 1 exit /b 1
    "%DOTNET_EXE%" build "%PROJECT%" -c Release
    if errorlevel 1 exit /b 1
)
if not exist "%APP%" exit /b 1
start "StayActive" /d "%APP_DIR%" "%APP%"
