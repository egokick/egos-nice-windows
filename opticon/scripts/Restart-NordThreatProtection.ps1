#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Restart-Service -Name 'nordsec-threatprotection-service' -Force
Start-Sleep -Seconds 5
Clear-DnsClientCache
Write-Host 'NordVPN Threat Protection and the Windows DNS cache were refreshed.'
