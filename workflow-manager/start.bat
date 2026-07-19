@echo off
setlocal
if not exist "%~dp0ai_workbench_redesigned.html" (
    echo Required app file not found: %~dp0ai_workbench_redesigned.html
    exit /b 1
)
start "" "%~dp0ai_workbench_redesigned.html"
