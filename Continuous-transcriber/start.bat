@echo off
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "APP_DIR=%%~fI"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%APP_DIR%\prepare-runtime.ps1"
if errorlevel 1 exit /b 1

pushd "%APP_DIR%"
if exist "%APP_DIR%\.venv\Scripts\pythonw.exe" goto start_venv

where pyw.exe >nul 2>nul
if not errorlevel 1 goto start_pyw

where pythonw.exe >nul 2>nul
if not errorlevel 1 goto start_pythonw

echo Python 3 was not found. Install Python or prepare the app through the Admin Panel. 1>&2
popd
exit /b 1

:start_venv
start "" /b "%APP_DIR%\.venv\Scripts\pythonw.exe" "%APP_DIR%\monitor_transcriber.py" %*
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%

:start_pyw
start "" /b pyw.exe -3 "%APP_DIR%\monitor_transcriber.py" %*
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%

:start_pythonw
start "" /b pythonw.exe "%APP_DIR%\monitor_transcriber.py" %*
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%
