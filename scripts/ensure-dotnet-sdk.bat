@echo off

set "DOTNET_SDK_MAJOR=%~1"
if not defined DOTNET_SDK_MAJOR (
    echo A .NET SDK major version is required.
    exit /b 1
)

set "DOTNET_EXE="
set "DOTNET_INSTALL_DIR=%LOCALAPPDATA%\Microsoft\dotnet"

if exist "%DOTNET_INSTALL_DIR%\dotnet.exe" (
    "%DOTNET_INSTALL_DIR%\dotnet.exe" --list-sdks 2>NUL | findstr /B /C:"%DOTNET_SDK_MAJOR%." >NUL
    if not errorlevel 1 set "DOTNET_EXE=%DOTNET_INSTALL_DIR%\dotnet.exe"
)

if not defined DOTNET_EXE (
    dotnet --list-sdks 2>NUL | findstr /B /C:"%DOTNET_SDK_MAJOR%." >NUL
    if not errorlevel 1 set "DOTNET_EXE=dotnet"
)

if defined DOTNET_EXE exit /b 0

echo .NET %DOTNET_SDK_MAJOR% SDK was not found. Installing it for the current user...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-dotnet-sdk.ps1" -MajorVersion %DOTNET_SDK_MAJOR% -InstallDirectory "%DOTNET_INSTALL_DIR%"
if errorlevel 1 exit /b 1

set "DOTNET_EXE=%DOTNET_INSTALL_DIR%\dotnet.exe"
"%DOTNET_EXE%" --list-sdks 2>NUL | findstr /B /C:"%DOTNET_SDK_MAJOR%." >NUL
if errorlevel 1 (
    echo The .NET SDK installation completed, but version %DOTNET_SDK_MAJOR%.x was not found.
    exit /b 1
)

exit /b 0
