@echo off
setlocal
dotnet run --project "%~dp0CopilotScreenshotProvider.csproj" -- %*
