[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, [uint64]::MaxValue)]
    [uint64]$NodeId,

    [Parameter(Mandatory)]
    [switch]$ApproveExitNode,

    [string]$EnvironmentFile = 'C:\source\babelfish\.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FlyAdmin.Common.ps1')

if (-not $ApproveExitNode) {
    throw 'Refusing to approve a default route without -ApproveExitNode.'
}

$deadline = [DateTime]::UtcNow.AddSeconds(45)
$node = @()
$availableRoutes = @()
do {
    $nodesJson = Invoke-HeadscaleFlyCommand 'headscale nodes list --output json' $EnvironmentFile
    $parsedNodes = $nodesJson | ConvertFrom-Json
    $node = @(@($parsedNodes) | Where-Object { [uint64]$_.id -eq $NodeId })
    if ($node.Count -ne 1) {
        Start-Sleep -Seconds 2
        continue
    }
    $tags = @($node[0].tags)
    if ($tags -notcontains 'tag:stayactive-exit') {
        throw "Headscale node $NodeId was not enrolled with the exit-capable role."
    }
    $availableRoutes = if ($node[0].PSObject.Properties.Name -contains 'available_routes') { @($node[0].available_routes) } else { @() }
    if ($availableRoutes -contains '0.0.0.0/0' -and $availableRoutes -contains '::/0') { break }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)
if ($node.Count -ne 1) {
    throw "Headscale node $NodeId is missing or ambiguous."
}
if ($availableRoutes -notcontains '0.0.0.0/0' -or $availableRoutes -notcontains '::/0') {
    throw "Headscale node $NodeId is not advertising both default routes."
}

$command = "headscale nodes approve-routes --force --identifier $NodeId --routes 0.0.0.0/0,::/0 --output json"
$null = Invoke-HeadscaleFlyCommand $command $EnvironmentFile
Write-Host "Approved the IPv4 and IPv6 default routes for Headscale node $NodeId."
