[CmdletBinding()]
param(
    [string]$ControllerIPv4 = '213.188.217.227'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this Taildesk route script as administrator.'
}

$address = $null
if (-not [Net.IPAddress]::TryParse($ControllerIPv4, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'ControllerIPv4 must be a valid IPv4 address.'
}

$physical = @(Get-NetIPConfiguration | Where-Object {
    $_.NetAdapter.Status -eq 'Up' -and
    $_.NetAdapter.HardwareInterface -and
    $_.IPv4Address -and
    $_.IPv4DefaultGateway
})
if ($physical.Count -eq 0) {
    throw 'No active physical IPv4 adapter with a default gateway was found.'
}

$selected = $physical | Sort-Object {
    (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric
} | Select-Object -First 1
$prefix = "$ControllerIPv4/32"
$gateway = $selected.IPv4DefaultGateway.NextHop

$managedRoutes = @(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue |
    Where-Object { $_.Protocol -eq 'NetMgmt' })
$correctRoute = @($managedRoutes | Where-Object {
    $_.InterfaceIndex -eq $selected.InterfaceIndex -and $_.NextHop -eq $gateway
})
if ($correctRoute.Count -gt 0 -and $managedRoutes.Count -eq $correctRoute.Count) {
    return
}

$managedRoutes | Remove-NetRoute -Confirm:$false
New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $selected.InterfaceIndex -NextHop $gateway -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
Restart-Service -Name 'Tailscale' -Force

