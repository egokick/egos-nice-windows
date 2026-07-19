@echo off
setlocal
pushd "%~dp0"

if not exist ".env" (
    echo remote\.env is required. Copy .env.example to .env and fill the reviewed deployment values first.
    popd
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\ensure-docker-desktop.ps1"
if errorlevel 1 (
    popd
    exit /b 1
)

set "DOCKER_EXE=docker"
if exist "%ProgramFiles%\Docker\Docker\resources\bin\docker.exe" set "DOCKER_EXE=%ProgramFiles%\Docker\Docker\resources\bin\docker.exe"
if exist "%LOCALAPPDATA%\Programs\Docker\Docker\resources\bin\docker.exe" set "DOCKER_EXE=%LOCALAPPDATA%\Programs\Docker\Docker\resources\bin\docker.exe"
if exist "%LOCALAPPDATA%\Programs\DockerDesktop\resources\bin\docker.exe" set "DOCKER_EXE=%LOCALAPPDATA%\Programs\DockerDesktop\resources\bin\docker.exe"

"%DOCKER_EXE%" compose --env-file .env up -d
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
