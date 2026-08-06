@echo off
setlocal EnableExtensions

set "TAILDESK_SOURCE=%~dp0..\opticon"
set "REBUILD_SCRIPT=%~dp0rebuild-if-source-changed.ps1"
set "LAUNCH_SCRIPT=%~dp0launch-opticon.ps1"

set "OPTICON_EXE=%ProgramFiles%\Taildesk\Admin\Opticon.exe"
if not exist "%OPTICON_EXE%" set "OPTICON_EXE=%LocalAppData%\Programs\Opticon\Opticon.exe"
set "CONTROL_URL=https://taildesk-egokick-control.fly.dev"

if not exist "%LAUNCH_SCRIPT%" (
    echo Opticon's startup helper is missing: "%LAUNCH_SCRIPT%" 1>&2
    exit /b 2
)

start "Opticon startup" /d "%~dp0" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LAUNCH_SCRIPT%" -SourceRoot "%TAILDESK_SOURCE%" -RebuildScript "%REBUILD_SCRIPT%" -OpticonExecutable "%OPTICON_EXE%" -ControlUrl "%CONTROL_URL%"
exit /b 0