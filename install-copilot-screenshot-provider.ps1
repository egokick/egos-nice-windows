$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Run this script from an elevated PowerShell window."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installer = Join-Path $scriptDir "CopilotScreenshotProvider\install-provider-admin.ps1"

if (-not (Test-Path $installer)) {
    throw "Missing installer: $installer"
}

powershell -ExecutionPolicy Bypass -File $installer

Write-Host ""
Write-Host "Setup complete."
Write-Host "Confirm Windows is using the app at:"
Write-Host "Settings > Personalization > Text input > Customize Copilot key on keyboard > Custom"
Write-Host "Selected app: Copilot Screenshot Provider"
