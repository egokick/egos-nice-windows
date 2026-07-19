[CmdletBinding()]
param(
    [string]$EnvironmentFile = 'C:\source\babelfish\.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FlyAdmin.Common.ps1')

$json = Invoke-HeadscaleFlyCommand 'headscale nodes list --output json' $EnvironmentFile
$parsed = $json | ConvertFrom-Json
$nodes = if ($null -eq $parsed) {
    @()
}
elseif ($parsed -is [System.Array]) {
    @($parsed)
}
else {
    @($parsed)
}
if (@($nodes).Count -eq 0) {
    Write-Host 'No devices are enrolled.'
    return
}
foreach ($node in $nodes) {
    $approvedRoutes = if ($node.PSObject.Properties.Name -contains 'approved_routes') { @($node.approved_routes) } else { @() }
    $availableRoutes = if ($node.PSObject.Properties.Name -contains 'available_routes') { @($node.available_routes) } else { @() }
    [pscustomobject]@{
        Id = $node.id
        Name = $node.name
        User = $node.user.name
        Online = ($node.PSObject.Properties.Name -contains 'online' -and [bool]$node.online)
        IPs = @($node.ip_addresses) -join ', '
        Tags = @($node.tags) -join ', '
        ApprovedRoutes = $approvedRoutes -join ', '
        AvailableRoutes = $availableRoutes -join ', '
    }
}
