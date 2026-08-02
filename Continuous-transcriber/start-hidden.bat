@echo off
setlocal
for %%I in ("%~dp0.") do set "APP_DIR=%%~fI"

call "%APP_DIR%\start.bat" %*
exit /b %ERRORLEVEL%
