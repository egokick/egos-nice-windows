@echo off
setlocal EnableExtensions

set "TAILDESK_SOURCE=%~dp0..\opticon"
set "REBUILD_SCRIPT=%~dp0rebuild-if-source-changed.ps1"

set "OPTICON_EXE=%ProgramFiles%\Taildesk\Admin\Opticon.exe"
if not exist "%OPTICON_EXE%" set "OPTICON_EXE=%LocalAppData%\Programs\Opticon\Opticon.exe"
set "CONTROL_URL=https://taildesk-egokick-control.fly.dev"

if exist "%TAILDESK_SOURCE%\Taildesk.sln" if exist "%REBUILD_SCRIPT%" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REBUILD_SCRIPT%" -SourceRoot "%TAILDESK_SOURCE%"
    if errorlevel 1 exit /b 1
)

if not exist "%OPTICON_EXE%" (
    echo Opticon is not installed. Run Opticon's Install-Opticon.ps1 first. 1>&2
    exit /b 2
)

powershell.exe -NoProfile -Command "try { $response = Invoke-WebRequest -UseBasicParsing '%CONTROL_URL%/health' -TimeoutSec 8; if ($response.StatusCode -eq 200) { exit 0 } } catch {}; exit 1" >nul 2>&1
if errorlevel 1 echo Warning: Opticon's Fly control server is currently unreachable. The command center will still open. 1>&2

for %%I in ("%OPTICON_EXE%") do set "OPTICON_DIR=%%~dpI"
start "Opticon" /d "%OPTICON_DIR%" "%OPTICON_EXE%"
exit /b 0
