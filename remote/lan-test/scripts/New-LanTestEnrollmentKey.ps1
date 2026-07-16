[CmdletBinding()]
param(
    [switch]$ExitNode,

    [ValidateRange(1, 24)]
    [int]$LifetimeHours = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
        # Docker registry credentials are resolved by a helper beside docker.exe.
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

$tags = @('tag:stayactive')
if ($ExitNode) {
    $tags += 'tag:stayactive-exit'
}

$composePrefix = @('compose', '--env-file', $environmentFile, '-f', $composeFile)
$arguments = $composePrefix + @(
    'exec', '-T',
    'headscale',
    'headscale', 'preauthkeys', 'create',
    '--user', 'stayactive-admin',
    '--expiration', "$($LifetimeHours)h",
    '--tags', ($tags -join ',')
)

# Headscale defaults this key to non-reusable. It is intentionally printed only
# to this local terminal and is never written to the repository or LAN state.
& docker @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to create the one-time Headscale enrollment key. Ensure Finalize-LanTest.ps1 has completed.'
}

Write-Host 'Transfer the displayed enrollment key only through a secure out-of-band channel. It expires automatically and must not be pasted into chat, email, source control, or a shell history you do not control.'