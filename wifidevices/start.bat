@echo off
setlocal
dotnet run --project "%~dp0wifidevices.csproj" -- %*
