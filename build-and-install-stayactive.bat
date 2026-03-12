@echo off
setlocal

set "ROOT_DIR=C:\source\egos-nice-windows"
set "PROJECT_PATH=%ROOT_DIR%\stayactive\stayactive.csproj"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "APP_NAME=StayActive"
set "EXE_PATH=%ROOT_DIR%\stayactive\bin\Release\net10.0-windows\stayactive.exe"

set "DOTNET_CLI_HOME=%ROOT_DIR%\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

echo Building %APP_NAME%...
dotnet build "%PROJECT_PATH%" -c Release
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

if not exist "%EXE_PATH%" (
    echo Built executable not found at:
    echo %EXE_PATH%
    exit /b 1
)

echo Adding %APP_NAME% to startup...
reg add "%RUN_KEY%" /v "%APP_NAME%" /t REG_SZ /d "\"%EXE_PATH%\"" /f
if errorlevel 1 (
    echo Failed to set startup entry for %APP_NAME%.
    exit /b 1
)

echo Startup entry set for %APP_NAME%:
reg query "%RUN_KEY%" /v "%APP_NAME%"

endlocal
