[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tailscale = 'C:\Program Files\Tailscale\tailscale.exe'
if (-not (Test-Path -LiteralPath $tailscale -PathType Leaf)) {
    throw 'The Tailscale client is not installed on this laptop.'
}
& $tailscale set --exit-node= 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'Tailscale refused to disable the exit node.'
}
$prefs = ((& $tailscale debug prefs 2>$null) -join [Environment]::NewLine) | ConvertFrom-Json
if (-not [string]::IsNullOrWhiteSpace([string]$prefs.ExitNodeIP)) {
    throw 'The exit node remains enabled.'
}
Write-Host 'Exit-node routing is disabled.'
