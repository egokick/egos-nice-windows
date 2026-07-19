@echo off
setlocal
call "%~dp0..\scripts\ensure-dotnet-sdk.bat" 10
if errorlevel 1 exit /b 1
call "%~dp0start-voicecodex.bat" %*
