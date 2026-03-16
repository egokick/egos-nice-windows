@echo off
setlocal

set "ROOT=C:\source\egos-nice-windows"
set "PROJECT=%ROOT%\YouTubeSyncTray\YouTubeSyncTray.csproj"
set "APP=C:\source\egos-nice-windows\YouTubeSyncTray\bin\Debug\net10.0-windows\YouTubeSyncTray.exe"

if not exist "%PROJECT%" (
    echo YouTube Sync Tray project not found:
    echo %PROJECT%
    exit /b 1
)

dotnet build "%PROJECT%"
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

if not exist "%APP%" (
    echo YouTube Sync Tray executable not found:
    echo %APP%
    exit /b 1
)

start "" "%APP%"
