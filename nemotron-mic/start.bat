@echo off
setlocal
for %%I in ("%~dp0.") do set "APP_DIR=%%~fI"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\ensure-python.ps1" -Version "3.12" -AppDirectory "%APP_DIR%" -RequirementsFile "%APP_DIR%\requirements.txt"
if errorlevel 1 exit /b 1
call "%~dp0start-nemotron-mic.bat" %*
