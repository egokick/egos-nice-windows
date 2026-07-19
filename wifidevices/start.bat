@echo off
setlocal
call "%~dp0..\scripts\ensure-dotnet-sdk.bat" 10
if errorlevel 1 exit /b 1
"%DOTNET_EXE%" run --project "%~dp0wifidevices.csproj" -- %*
