[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this Tailscale service configuration script as administrator.'
}

$directory = Join-Path $env:ProgramData 'Tailscale'
$path = Join-Path $directory 'tailscaled-env.txt'
New-Item -Path $directory -ItemType Directory -Force | Out-Null

$lines = if (Test-Path -LiteralPath $path) { @(Get-Content -LiteralPath $path) } else { @() }
$lines = @($lines | Where-Object { $_ -notmatch '^\s*TS_BIND_TO_INTERFACE_BY_ROUTE\s*=' })
$lines += 'TS_BIND_TO_INTERFACE_BY_ROUTE=1'
$temporary = "$path.taildesk.new"
$lines | Set-Content -LiteralPath $temporary -Encoding ASCII
Move-Item -LiteralPath $temporary -Destination $path -Force

Restart-Service -Name 'Tailscale' -Force

