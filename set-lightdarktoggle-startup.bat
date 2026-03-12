@echo off
setlocal

set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "APP_NAME=LightDarkToggle"
set "EXE_PATH=C:\source\egos-nice-windows\LightDarkToggle\bin\Release\net10.0-windows\LightDarkToggle.exe"

reg add "%RUN_KEY%" /v "%APP_NAME%" /t REG_SZ /d "\"%EXE_PATH%\"" /f
if errorlevel 1 (
    echo Failed to set startup entry for %APP_NAME%.
    exit /b 1
)

echo Startup entry set for %APP_NAME%:
reg query "%RUN_KEY%" /v "%APP_NAME%"

endlocal
