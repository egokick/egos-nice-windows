@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\ensure-python.ps1" -Version "3.12" -AppDirectory "%~dp0" -RequirementsFile "%~dp0requirements.txt"
if errorlevel 1 exit /b 1
call "%~dp0start-nemotron-mic.bat" %*
