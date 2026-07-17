[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, [uint64]::MaxValue)]
    [uint64]$NodeId,

    [Parameter(Mandatory)]
    [switch]$ApproveExitNode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ApproveExitNode) {
    throw 'Refusing to approve a default route without -ApproveExitNode.'
}

function Enable-DockerDesktopCli {
    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    $dockerPath = if ($null -ne $command) {
        $command.Source
    }
    else {
        Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    }

    if (-not (Test-Path -LiteralPath $dockerPath -PathType Leaf)) {
        throw 'Docker Desktop is required. Start Docker Desktop and try again.'
    }

    $dockerDirectory = Split-Path -Parent $dockerPath
    if ($dockerDirectory -notin ($env:Path -split ';')) {
        $env:Path = $dockerDirectory + ';' + $env:Path
    }
}

Enable-DockerDesktopCli

$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$environmentFile = Join-Path $lanRoot '.env'
$composeFile = Join-Path $lanRoot 'compose.yaml'
if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw 'LAN test is not initialized.'
}

# Headscale verifies that the node is actually advertising this route. This
# command cannot turn an arbitrary node into an exit node if it has not opted in.
$arguments = @(
    'compose', '--env-file', $environmentFile, '-f', $composeFile,
    'exec', '-T',
    'headscale',
    'headscale', 'nodes', 'approve-routes',
    '--force',
    '--identifier', [string]$NodeId,
    '--routes', '0.0.0.0/0'
)
& docker @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to approve the requested exit-node route. Confirm the node ID and that the remote laptop is advertising 0.0.0.0/0.'
}

Write-Host "Approved default-route exit traffic only for Headscale node $NodeId."