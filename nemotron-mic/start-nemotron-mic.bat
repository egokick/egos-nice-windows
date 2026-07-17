@echo off
setlocal
"%~dp0.venv\Scripts\python.exe" "%~dp0transcribe_mic.py" %*
