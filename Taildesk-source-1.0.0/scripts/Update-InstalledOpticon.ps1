#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$InstallDirectory = "$env:ProgramFiles\Taildesk\Admin",
    [string]$ControllerIPv4 = '213.188.217.227',
    [string]$CoordinatorIPv4 = '100.64.0.1'
)

$ErrorActionPreference = 'Stop'
$sourceExecutable = Join-Path $SourceDirectory 'Opticon.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable)) { throw "Opticon.exe was not found in $SourceDirectory" }
$signature = Get-AuthenticodeSignature -LiteralPath $sourceExecutable
if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or $signature.Status -in @('NotSigned','HashMismatch')) {
    throw 'The staged Opticon executable does not have the pinned publisher signature.'
}

Get-Process 'Opticon','Taildesk.Admin' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
New-Item -Path $InstallDirectory -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $SourceDirectory '*') $InstallDirectory -Recurse -Force

foreach ($ruleName in @('Opticon Coordinator (Tailscale only)','Taildesk Coordinator (Tailscale only)')) {
    & netsh.exe advfirewall firewall delete rule "name=$ruleName" | Out-Null
}
& netsh.exe advfirewall firewall add rule 'name=Opticon Coordinator (Tailscale only)' 'dir=in' 'action=allow' 'protocol=TCP' 'localport=45830' "localip=$CoordinatorIPv4" 'remoteip=100.64.0.0/10' 'profile=any' 'enable=yes' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Windows could not create the Opticon coordinator firewall rule.' }

$routeInstaller = Join-Path $PSScriptRoot 'Install-TaildeskFlyRouteTask.ps1'
& $routeInstaller -ControllerIPv4 $ControllerIPv4 | Out-Null
Write-Host 'Opticon application update completed.' -ForegroundColor Green
