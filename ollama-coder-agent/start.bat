@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\ensure-python.ps1" -Version "3.12"
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\ensure-ollama-coder.ps1"
if errorlevel 1 exit /b 1
call "%~dp0start-coder-files.bat" %*
