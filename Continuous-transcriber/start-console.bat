@echo off
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "APP_DIR=%%~fI"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%APP_DIR%\prepare-runtime.ps1"
if errorlevel 1 exit /b 1

pushd "%APP_DIR%"
if exist "%APP_DIR%\.venv\Scripts\python.exe" goto run_venv

where py.exe >nul 2>nul
if not errorlevel 1 goto run_py

where python.exe >nul 2>nul
if not errorlevel 1 goto run_python

echo Python 3 was not found. Install Python or prepare the app through the Admin Panel. 1>&2
popd
exit /b 1

:run_venv
"%APP_DIR%\.venv\Scripts\python.exe" "%APP_DIR%\monitor_transcriber.py" --console %*
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%

:run_py
py.exe -3 "%APP_DIR%\monitor_transcriber.py" --console %*
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%

:run_python
python.exe "%APP_DIR%\monitor_transcriber.py" --console %*
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%
