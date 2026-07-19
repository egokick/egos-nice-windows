@echo off
setlocal
dotnet run --project "%~dp0Cli\AgentChannel.Cli.csproj" -- %*
