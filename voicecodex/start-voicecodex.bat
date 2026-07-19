@echo off
setlocal
cd /d "%~dp0"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"
"%DOTNET_EXE%" run --project voicecodex.csproj
