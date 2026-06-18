@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo Installing MediaTek Bluetooth drivers from %~dp0
pnputil /add-driver "%~dp0*.inf" /subdirs /install
echo.
echo Restarting Bluetooth services if present...
sc stop bthserv >nul 2>&1
sc start bthserv >nul 2>&1
echo.
echo Done. Press any key to close.
pause >nul
