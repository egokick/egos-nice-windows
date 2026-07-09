@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "APP_DIR=%ROOT_DIR%wifidevices"
set "PROJECT_PATH=%APP_DIR%\wifidevices.csproj"
set "EXE_PATH=%APP_DIR%\bin\Debug\net10.0-windows\wifidevices.exe"
set "APP_URL=http://127.0.0.1:5136"
set "FINANCE_URL=%APP_URL%/finances"

set "DOTNET_CLI_HOME=%ROOT_DIR%.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

if not exist "%PROJECT_PATH%" (
    echo Wi-Fi Devices project not found:
    echo %PROJECT_PATH%
    exit /b 1
)

powershell -NoProfile -Command ^
  "try { Invoke-WebRequest '%FINANCE_URL%' -UseBasicParsing -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"

if errorlevel 1 (
    dotnet build "%PROJECT_PATH%"
    if errorlevel 1 (
        echo Failed to build Wi-Fi Devices.
        exit /b 1
    )

    if not exist "%EXE_PATH%" (
        echo Wi-Fi Devices executable not found at:
        echo %EXE_PATH%
        exit /b 1
    )

    start "Wi-Fi Devices" /d "%APP_DIR%" "%EXE_PATH%" --working-directory "%APP_DIR%" --urls "%APP_URL%" %*

    powershell -NoProfile -Command ^
      "$deadline = (Get-Date).AddSeconds(20);" ^
      "do {" ^
      "  try { Invoke-WebRequest '%FINANCE_URL%' -UseBasicParsing -TimeoutSec 2 | Out-Null; exit 0 } catch { Start-Sleep -Milliseconds 500 }" ^
      "} while ((Get-Date) -lt $deadline);" ^
      "exit 1"

    if errorlevel 1 (
        echo Wi-Fi Devices did not respond at %FINANCE_URL%.
        exit /b 1
    )
)

start "" "%FINANCE_URL%"

endlocal
