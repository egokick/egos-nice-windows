[CmdletBinding()]
param(
    [string]$HostName = 'taildesk-egokick-control.fly.dev',
    [string]$IPv4Address = '213.188.217.227'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this command-center DNS pin script as administrator.'
}

$address = $null
if (-not [Net.IPAddress]::TryParse($IPv4Address, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'IPv4Address must be a valid IPv4 address.'
}
if ($HostName -notmatch '^[A-Za-z0-9.-]+$') {
    throw 'HostName contains unsupported characters.'
}

$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$backupPath = "$hostsPath.taildesk.bak"
if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $hostsPath -Destination $backupPath
}

$escapedHost = [regex]::Escape($HostName)
$lines = @(Get-Content -LiteralPath $hostsPath | Where-Object { $_ -notmatch "(?i)^\s*\S+\s+$escapedHost(?:\s|$)" })
$lines += "$IPv4Address $HostName # Taildesk Fly control plane"
$temporaryPath = "$hostsPath.taildesk.new"
$lines | Set-Content -LiteralPath $temporaryPath -Encoding ASCII
Move-Item -LiteralPath $temporaryPath -Destination $hostsPath -Force

Clear-DnsClientCache
Restart-Service -Name 'Tailscale' -Force

