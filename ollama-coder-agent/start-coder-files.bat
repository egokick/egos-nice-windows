@echo off
setlocal
set "PYTHON_EXE=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if exist "%PYTHON_EXE%" (
    "%PYTHON_EXE%" "%~dp0coder_files_agent.py" %*
) else (
    py -3.12 "%~dp0coder_files_agent.py" %*
)
