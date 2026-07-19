@echo off
setlocal

set "APP_DIR=%~dp0"
set "PROJECT=%APP_DIR%PowerModeToggle.csproj"
set "APP=%APP_DIR%bin\Release\net10.0-windows\win-x64\publish\PowerModeToggle.exe"
set "DOTNET_CLI_HOME=%APP_DIR%..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

call "%APP_DIR%..\scripts\ensure-dotnet-sdk.bat" 10
if errorlevel 1 exit /b 1

tasklist /FI "IMAGENAME eq PowerModeToggle.exe" 2>NUL | find /I "PowerModeToggle.exe" >NUL
if not errorlevel 1 exit /b 0

taskkill /IM PowerModeToggleDesktop.exe /F >NUL 2>NUL

"%DOTNET_EXE%" publish "%PROJECT%" -c Release
if errorlevel 1 exit /b 1

if not exist "%APP%" (
    echo PowerModeToggle executable not found at:
    echo %APP%
    exit /b 1
)

start "PowerModeToggle" /d "%APP_DIR%" "%APP%"
exit /b 0
