@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\10-install-virtualbox.ps1" -Silent
if errorlevel 1 exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\34-start-workvm-ready.ps1" %*
