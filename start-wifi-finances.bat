@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "WIFI_URL=http://127.0.0.1:5136"
set "FINANCE_URL=http://127.0.0.1:5137"

call "%ROOT_DIR%wifidevices\start.bat"
if errorlevel 1 exit /b 1

call "%ROOT_DIR%finance\start.bat"
if errorlevel 1 exit /b 1

powershell -NoProfile -Command ^
  "$deadline = (Get-Date).AddSeconds(20);" ^
  "do {" ^
  "  try {" ^
  "    Invoke-WebRequest '%WIFI_URL%' -UseBasicParsing -TimeoutSec 2 | Out-Null;" ^
  "    Invoke-WebRequest '%FINANCE_URL%' -UseBasicParsing -TimeoutSec 2 | Out-Null;" ^
  "    exit 0" ^
  "  } catch { Start-Sleep -Milliseconds 500 }" ^
  "} while ((Get-Date) -lt $deadline);" ^
  "exit 1"

if errorlevel 1 (
  echo Wi-Fi Devices or Finance did not respond in time.
  exit /b 1
)

start "" "%WIFI_URL%"
start "" "%FINANCE_URL%"
