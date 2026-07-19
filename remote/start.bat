@echo off
setlocal
pushd "%~dp0"

if not exist ".env" (
    echo remote\.env is required. Copy .env.example to .env and fill the reviewed deployment values first.
    popd
    exit /b 1
)

docker compose --env-file .env up -d
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
