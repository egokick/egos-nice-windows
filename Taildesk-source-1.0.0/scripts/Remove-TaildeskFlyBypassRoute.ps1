[CmdletBinding()]
param(
    [string]$ControllerIPv4 = '213.188.217.227'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this Taildesk route cleanup script as administrator.'
}

$prefix = "$ControllerIPv4/32"
Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue |
    Where-Object { $_.Protocol -eq 'NetMgmt' } |
    Remove-NetRoute -Confirm:$false
Restart-Service -Name 'Tailscale' -Force

