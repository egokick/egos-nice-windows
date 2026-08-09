@echo off
setlocal

set "APP_DIR=%~dp0"
set "PROJECT=%APP_DIR%ContinuousTranscriber.Dashboard.csproj"
set "APP=%APP_DIR%bin\Debug\net8.0-windows10.0.19041.0\ContinuousTranscriber.Dashboard.exe"
set "DOTNET_CLI_HOME=%APP_DIR%..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_ROLL_FORWARD=Major"

powershell.exe -NoProfile -Command "if (Get-Process -Name 'ContinuousTranscriber.Dashboard' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
    start "Continuous Transcriber Dashboard" /d "%APP_DIR%" "%APP%" --transcriber-directory "%APP_DIR%..\Continuous-transcriber" --open
    exit /b 0
)

call "%APP_DIR%..\scripts\ensure-dotnet-sdk.bat" 10
if errorlevel 1 exit /b 1

"%DOTNET_EXE%" build "%PROJECT%" --nologo
if errorlevel 1 exit /b 1
if not exist "%APP%" exit /b 1

start "Continuous Transcriber Dashboard" /d "%APP_DIR%" "%APP%" --transcriber-directory "%APP_DIR%..\Continuous-transcriber" --open
