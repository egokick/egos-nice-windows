@echo off
setlocal

set "APP_DIR=%~dp0"
set "PROJECT=%APP_DIR%wifidevices.csproj"
set "APP=%APP_DIR%bin\Debug\net10.0-windows\wifidevices.exe"
set "DOTNET_CLI_HOME=%APP_DIR%..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

call "%APP_DIR%..\scripts\ensure-dotnet-sdk.bat" 10
if errorlevel 1 exit /b 1

tasklist /FI "IMAGENAME eq wifidevices.exe" 2>NUL | find /I "wifidevices.exe" >NUL
if not errorlevel 1 exit /b 0

"%DOTNET_EXE%" build "%PROJECT%"
if errorlevel 1 exit /b 1
if not exist "%APP%" exit /b 1

start "Wi-Fi Devices" /d "%APP_DIR%" "%APP%" --working-directory "%APP_DIR%" %*
