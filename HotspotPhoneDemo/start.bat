@echo off
setlocal
dotnet run --project "%~dp0HotspotPhoneDemo.csproj" -- %*
