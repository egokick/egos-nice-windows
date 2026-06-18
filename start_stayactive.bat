@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "APP_DIR=%ROOT_DIR%stayactive"
set "PROJECT_PATH=%APP_DIR%\stayactive.csproj"
set "EXE_PATH=%APP_DIR%\bin\Release\net10.0-windows\stayactive.exe"

set "DOTNET_CLI_HOME=%ROOT_DIR%.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

if not exist "%EXE_PATH%" (
    echo StayActive executable not found. Building Release...
    dotnet build "%PROJECT_PATH%" -c Release
    if errorlevel 1 (
        echo Failed to build StayActive.
        exit /b 1
    )
)

if not exist "%EXE_PATH%" (
    echo StayActive executable not found at:
    echo %EXE_PATH%
    exit /b 1
)

start "StayActive" /d "%APP_DIR%" "%EXE_PATH%"

endlocal
