[CmdletBinding()]
param(
    [switch]$SkipRemoteHubBuild
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
$rootCertificateSource = Join-Path $lanRoot 'state\caddy\data\caddy\pki\authorities\local\root.crt'
$rootCertificateDestination = Join-Path $lanRoot 'certs\caddy-root.crt'
$keycloakMapperPath = '/opt/keycloak/data/import/configure-scope-mappings.sh'

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Wait-ForFile([string]$Path, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }

        Start-Sleep -Seconds 2
    }

    throw "Timed out waiting for $Path."
}

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw 'LAN test is not initialized. Run Initialize-LanTest.ps1 first.'
}

if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) {
    throw "Required compose file is missing: $composeFile"
}

$composePrefix = @('compose', '--env-file', $environmentFile, '-f', $composeFile)

# Caddy must create its local CA before RemoteHub starts. This prevents a
# missing certificate path from becoming an accidental directory bind mount.
Invoke-Docker ($composePrefix + @('up', '-d', 'caddy', 'headscale', 'meshcentral', 'postgres', 'keycloak')) 'Unable to start the local-only bootstrap services.'

Wait-ForFile $rootCertificateSource 90
Copy-Item -LiteralPath $rootCertificateSource -Destination $rootCertificateDestination -Force

# The realm import contains clients and scopes; this idempotent command adds
# their required role mappings only after Keycloak is actually ready.
$mappingDeadline = [DateTime]::UtcNow.AddSeconds(180)
$mappingReady = $false
while ([DateTime]::UtcNow -lt $mappingDeadline) {
    & docker @($composePrefix + @('exec', '-T', 'keycloak', 'sh', $keycloakMapperPath)) *> $null
    if ($LASTEXITCODE -eq 0) {
        $mappingReady = $true
        break
    }

    Start-Sleep -Seconds 3
}

if (-not $mappingReady) {
    throw 'Keycloak did not become ready with the required scope-to-role mappings. The LAN stack remains loopback-only.'
}

$remoteHubArguments = @('up', '-d')
if (-not $SkipRemoteHubBuild) {
    $remoteHubArguments += '--build'
}
$remoteHubArguments += 'remotehub'
Invoke-Docker ($composePrefix + $remoteHubArguments) 'Unable to build and start RemoteHub.'

$runningServices = & docker @($composePrefix + @('ps', '--status', 'running', '--services'))
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the LAN stack after startup.'
}

$requiredServices = @('caddy', 'headscale', 'meshcentral', 'postgres', 'keycloak', 'remotehub')
$missingServices = $requiredServices | Where-Object { $_ -notin $runningServices }
if ($missingServices.Count -gt 0) {
    throw "The LAN stack did not leave all required services running: $($missingServices -join ', ')."
}

Write-Host 'The LAN bootstrap stack is running on loopback only.'
Write-Host 'Before opening it to the LAN, install the Caddy root certificate, create exactly one MeshCentral administrator, and then run Finalize-LanTest.ps1.'