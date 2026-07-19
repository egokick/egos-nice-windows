@echo off
setlocal

set "APP_DIR=%~dp0"
set "PROJECT=%APP_DIR%PowerModeToggleDesktop.csproj"
set "APP=%APP_DIR%bin\Release\net9.0-windows\PowerModeToggleDesktop.exe"
set "DOTNET_CLI_HOME=%APP_DIR%..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_EXE=dotnet"
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET_EXE=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"

tasklist /FI "IMAGENAME eq PowerModeToggleDesktop.exe" 2>NUL | find /I "PowerModeToggleDesktop.exe" >NUL
if not errorlevel 1 exit /b 0

"%DOTNET_EXE%" build "%PROJECT%" -c Release
if errorlevel 1 exit /b 1

if not exist "%APP%" (
    echo PowerModeToggleDesktop executable not found at:
    echo %APP%
    exit /b 1
)

start "PowerModeToggleDesktop" /d "%APP_DIR%" "%APP%"
