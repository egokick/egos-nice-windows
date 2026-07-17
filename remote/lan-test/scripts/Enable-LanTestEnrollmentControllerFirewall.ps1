#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$ControllerAddress,

    [Parameter(Mandatory)]
    [string]$CaddyAddress,

    [Parameter(Mandatory)]
    [string]$InterfaceAlias
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Ipv4([string]$Value, [string]$ParameterName) {
    $address = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "$ParameterName must be an IPv4 address."
    }

    return $address.IPAddressToString
}

$controllerAddress = Assert-Ipv4 $ControllerAddress 'ControllerAddress'
$caddyAddress = Assert-Ipv4 $CaddyAddress 'CaddyAddress'
$ruleName = 'StayActive Remotes - Windows enrollment controller'

if ($null -eq (Get-NetAdapter -Name $InterfaceAlias -ErrorAction SilentlyContinue)) {
    throw "The requested controller interface does not exist: $InterfaceAlias"
}

# The service binds to the WSL/Docker virtual-interface address rather than a
# physical NIC. The exact Caddy control-network address is the only allowed
# remote peer; ordinary LAN clients never receive a listener or firewall rule.
$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($null -ne $existing -and $PSCmdlet.ShouldProcess($ruleName, 'Replace the managed controller firewall rule')) {
    $existing | Remove-NetFirewallRule
}

if ($PSCmdlet.ShouldProcess($ruleName, 'Allow only Caddy to reach the Windows enrollment controller')) {
    New-NetFirewallRule `
        -DisplayName $ruleName `
        -Direction Inbound `
        -Action Allow `
        -Enabled True `
        -Profile Any `
        -Protocol TCP `
        -LocalAddress $controllerAddress `
        -LocalPort 5091 `
        -RemoteAddress $caddyAddress `
        -InterfaceAlias $InterfaceAlias `
        -EdgeTraversalPolicy Block | Out-Null
}

$installed = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($null -eq $installed) {
    throw 'The Windows enrollment-controller firewall rule was not present after creation.'
}

$addressFilter = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $installed
$portFilter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $installed
if ($addressFilter.LocalAddress -notcontains $controllerAddress -or $addressFilter.RemoteAddress -notcontains $caddyAddress -or $portFilter.LocalPort -notcontains '5091') {
    throw 'The Windows enrollment-controller firewall rule does not retain its reviewed endpoint restrictions.'
}

Write-Host 'Enabled the Windows enrollment controller only for the fixed Caddy control-network peer.'
