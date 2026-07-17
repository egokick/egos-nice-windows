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
$userList = & docker @($composePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'list', '--output', 'json'))
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to list Headscale users. Ensure Finalize-LanTest.ps1 has completed.'
}

try {
    $parsedUsers = $userList | Out-String | ConvertFrom-Json
    $headscaleUsers = @($parsedUsers | Where-Object { $null -ne $_ })
}
catch {
    throw 'Headscale did not return a JSON user list.'
}

$policyOwner = $headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' } | Select-Object -First 1
if ($null -eq $policyOwner -or $null -eq $policyOwner.id) {
    throw 'The stayactive-admin Headscale policy owner does not exist.'
}

$arguments = $composePrefix + @(
    'exec', '-T',
    'headscale',
    'headscale', 'preauthkeys', 'create',
    '--force',
    '--user', [string]$policyOwner.id,
    '--expiration', "$($LifetimeHours)h",
    '--tags', ($tags -join ','),
    '--output', 'json'
)

# Headscale defaults this key to non-reusable. It is intentionally printed only
# to this local terminal and is never written to the repository or LAN state.
$keyOutput = & docker @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to create the one-time Headscale enrollment key.'
}

try {
    $preauthKey = $keyOutput | Out-String | ConvertFrom-Json
}
catch {
    throw 'Headscale did not return the generated enrollment key as JSON.'
}

if ([string]::IsNullOrWhiteSpace($preauthKey.key)) {
    throw 'Headscale returned an enrollment response without a key.'
}

Write-Host $preauthKey.key
Write-Host 'Transfer the displayed enrollment key only through a secure out-of-band channel. It expires automatically and must not be pasted into chat, email, source control, or a shell history you do not control.'