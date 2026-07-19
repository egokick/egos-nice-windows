@echo off
setlocal

set "APP_DIR=%~dp0"
set "PROJECT=%APP_DIR%stayactive.csproj"
set "APP=%APP_DIR%bin\Release\net10.0-windows\stayactive.exe"
set "DOTNET_CLI_HOME=%APP_DIR%..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

tasklist /FI "IMAGENAME eq stayactive.exe" 2>NUL | find /I "stayactive.exe" >NUL
if not errorlevel 1 exit /b 0

dotnet build "%PROJECT%" -c Release
if errorlevel 1 exit /b 1
if not exist "%APP%" exit /b 1
start "StayActive" /d "%APP_DIR%" "%APP%"
