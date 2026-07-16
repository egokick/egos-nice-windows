#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$httpsName = 'StayActive Remotes LAN - HTTPS'
$stunName = 'StayActive Remotes LAN - STUN'

foreach ($name in @($httpsName, $stunName)) {
    $existing = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($null -ne $existing -and $PSCmdlet.ShouldProcess($name, 'Replace the managed firewall rule')) {
        $existing | Remove-NetFirewallRule
    }
}

$common = @{
    Direction = 'Inbound'
    Action = 'Allow'
    Enabled = 'True'
    Profile = 'Any'
    RemoteAddress = 'LocalSubnet'
    EdgeTraversalPolicy = 'Block'
}

if ($PSCmdlet.ShouldProcess($httpsName, 'Allow local-subnet HTTPS traffic to the LAN control plane')) {
    New-NetFirewallRule @common -DisplayName $httpsName -Protocol TCP -LocalPort 443 | Out-Null
}

if ($PSCmdlet.ShouldProcess($stunName, 'Allow local-subnet STUN traffic to Headscale DERP')) {
    New-NetFirewallRule @common -DisplayName $stunName -Protocol UDP -LocalPort 3478 | Out-Null
}

$installed = Get-NetFirewallRule -DisplayName $httpsName, $stunName -ErrorAction SilentlyContinue
if ($installed.Count -ne 2) {
    throw 'The two managed LAN firewall rules were not both present after creation.'
}

Write-Host 'Enabled only local-subnet inbound TCP 443 and UDP 3478 for the StayActive LAN test.'