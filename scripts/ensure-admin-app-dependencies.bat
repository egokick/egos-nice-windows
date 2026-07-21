@echo off
setlocal EnableExtensions

set "APP_ID=%~1"
set "APP_DIR=%~f2"

if not defined APP_ID (
    echo An admin app id is required.
    exit /b 2
)

if not exist "%APP_DIR%\" (
    echo App folder not found: %APP_DIR%
    exit /b 2
)

if /I "%APP_ID%"=="parakeet-mic" goto :parakeet
if /I "%APP_ID%"=="nemotron-mic" goto :nemotron
if /I "%APP_ID%"=="ollama-coder-agent" goto :ollama
if /I "%APP_ID%"=="workflow-manager" goto :workflow
if /I "%APP_ID%"=="power-mode-toggle" goto :powerMode
if /I "%APP_ID%"=="stayactive" goto :stayActive
if /I "%APP_ID%"=="voicecodex" goto :voiceCodex
if /I "%APP_ID%"=="wifidevices" goto :wifiDevices
if /I "%APP_ID%"=="finance" goto :finance
if /I "%APP_ID%"=="youtube-sync-tray" goto :youtubeSync
if /I "%APP_ID%"=="light-dark-toggle" goto :lightDark

echo No dependency profile is registered for %APP_ID%.
exit /b 2

:parakeet
if not exist "%APP_DIR%\transcribe_mic.py" (
    echo Required app file not found: %APP_DIR%\transcribe_mic.py
    exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-python.ps1" -Version "3.12" -AppDirectory "%APP_DIR%" -RequirementsFile "%APP_DIR%\requirements.txt"
exit /b %ERRORLEVEL%

:nemotron
if not exist "%APP_DIR%\transcribe_mic.py" (
    echo Required app file not found: %APP_DIR%\transcribe_mic.py
    exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-python.ps1" -Version "3.12" -AppDirectory "%APP_DIR%" -RequirementsFile "%APP_DIR%\requirements.txt"
exit /b %ERRORLEVEL%

:ollama
if not exist "%APP_DIR%\coder_files_agent.py" (
    echo Required app file not found: %APP_DIR%\coder_files_agent.py
    exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-python.ps1" -Version "3.12"
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-ollama-coder.ps1"
exit /b %ERRORLEVEL%

:workflow
if not exist "%APP_DIR%\ai_workbench_redesigned.html" (
    echo Required app file not found: %APP_DIR%\ai_workbench_redesigned.html
    exit /b 2
)
echo Workflow Manager has no external runtime dependencies.
exit /b 0

:powerMode
call :restoreDotnet "%APP_DIR%\PowerModeToggle.csproj"
exit /b %ERRORLEVEL%

:stayActive
call :restoreDotnet "%APP_DIR%\stayactive.csproj"
exit /b %ERRORLEVEL%

:voiceCodex
call :restoreDotnet "%APP_DIR%\voicecodex.csproj"
exit /b %ERRORLEVEL%

:wifiDevices
call :restoreDotnet "%APP_DIR%\wifidevices.csproj"
exit /b %ERRORLEVEL%

:finance
call :restoreDotnet "%APP_DIR%\finance.csproj"
exit /b %ERRORLEVEL%

:youtubeSync
call :restoreDotnet "%APP_DIR%\YouTubeSyncTray.csproj"
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ensure-youtube-sync-tools.ps1" -AppDirectory "%APP_DIR%"
exit /b %ERRORLEVEL%

:lightDark
call :restoreDotnet "%APP_DIR%\LightDarkToggle.csproj"
exit /b %ERRORLEVEL%

:restoreDotnet
set "PROJECT=%~1"
if not exist "%PROJECT%" (
    echo Project file not found: %PROJECT%
    exit /b 2
)
call "%~dp0ensure-dotnet-sdk.bat" 10
if errorlevel 1 exit /b 1
if not defined DOTNET_EXE (
    echo .NET SDK setup did not provide a dotnet executable.
    exit /b 1
)
set "DOTNET_CLI_HOME=%~dp0..\.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
"%DOTNET_EXE%" restore "%PROJECT%" --nologo
exit /b %ERRORLEVEL%
