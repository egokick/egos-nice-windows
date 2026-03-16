@echo off
setlocal

set "ROOT=C:\source\egos-nice-windows"
set "PROJECT=%ROOT%\YouTubeSyncTray\AuthTool\YouTubeSyncTray.AuthTool.csproj"

if not exist "%PROJECT%" (
    echo YouTube Sync auth tool project not found:
    echo %PROJECT%
    exit /b 1
)

dotnet run --project "%PROJECT%" -- %*
