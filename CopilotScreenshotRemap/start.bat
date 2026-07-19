@echo off
setlocal
dotnet run --project "%~dp0CopilotScreenshotRemap.csproj" -p:StartupObject=CopilotScreenshotRemap.Program -- %*
