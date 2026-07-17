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

function Assert-Administrator {
    $principal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'A finalized controller deployment must be restarted from an elevated PowerShell session.'
    }
}

function Resolve-ActiveReviewedPath(
    [string]$Path,
    [string]$ReviewedStagingRoot,
    [string]$PropertyName) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The active deployment manifest has no valid $PropertyName file."
    }
    if (-not (Test-Path -LiteralPath $ReviewedStagingRoot -PathType Container)) {
        throw 'The active deployment reviewed-artifact root is missing.'
    }

    $rootItem = Get-Item -LiteralPath $ReviewedStagingRoot -Force
    $item = Get-Item -LiteralPath $Path -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The active deployment manifest refers to a reparse point.'
    }

    $rootFullPath = [System.IO.Path]::GetFullPath($rootItem.FullName).TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($item.FullName)
    if (-not $fullPath.StartsWith($rootFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The active deployment $PropertyName file is outside protected staging."
    }

    return $fullPath
}

function Get-ActiveReviewedDeployment([string]$ManifestPath, [string]$ReviewedStagingRoot) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return $null
    }

    $manifestItem = Get-Item -LiteralPath $ManifestPath -Force
    if (($manifestItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The active deployment manifest must not be a reparse point.'
    }

    try {
        $manifest = [System.IO.File]::ReadAllText($ManifestPath) | ConvertFrom-Json
    }
    catch {
        throw 'The active deployment manifest is unreadable; refuse to use mutable LAN deployment files.'
    }

    if ([int]$manifest.SchemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.SourceRevision)) {
        throw 'The active deployment manifest has an unsupported schema.'
    }

    return [pscustomobject]@{
        SourceRevision = [string]$manifest.SourceRevision
        ComposeFile = Resolve-ActiveReviewedPath ([string]$manifest.ComposeFile) $ReviewedStagingRoot 'ComposeFile'
        EnvironmentFile = Resolve-ActiveReviewedPath ([string]$manifest.EnvironmentFile) $ReviewedStagingRoot 'EnvironmentFile'
        KeycloakMapperTemplate = Resolve-ActiveReviewedPath ([string]$manifest.KeycloakMapperTemplate) $ReviewedStagingRoot 'KeycloakMapperTemplate'
    }
}
Enable-DockerDesktopCli

$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$controllerRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'StayActiveRemotes\EnrollmentController'
$reviewedStagingRoot = Join-Path $controllerRoot 'reviewed-artifacts'
$activeDeploymentPath = Join-Path $controllerRoot 'active-deployment.json'
$activeDeployment = Get-ActiveReviewedDeployment $activeDeploymentPath $reviewedStagingRoot
$isFinalizedDeployment = $null -ne $activeDeployment

if ($isFinalizedDeployment) {
    Assert-Administrator
    $environmentFile = $activeDeployment.EnvironmentFile
    $composeFile = $activeDeployment.ComposeFile
    $keycloakMapperTemplate = $activeDeployment.KeycloakMapperTemplate
}
else {
    $environmentFile = Join-Path $lanRoot '.env'
    $composeFile = Join-Path $lanRoot 'compose.yaml'
    $keycloakMapperTemplate = Join-Path $lanRoot 'config\keycloak\configure-scope-mappings.sh.template'
}

$rootCertificateSource = Join-Path $lanRoot 'state\caddy\data\caddy\pki\authorities\local\root.crt'
$rootCertificateDestination = Join-Path $lanRoot 'certs\caddy-root.crt'
$keycloakMapperPath = '/opt/keycloak/data/import/configure-scope-mappings.sh'
$keycloakMapperDestination = Join-Path $lanRoot 'generated\keycloak\configure-scope-mappings.sh'

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Assert-ControllerServiceIfInstalled {
    $service = Get-Service -Name 'StayActiveEnrollmentController' -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($service.Status -eq 'Running') {
            return
        }

        Start-Sleep -Seconds 2
        $service = Get-Service -Name 'StayActiveEnrollmentController' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            throw 'The Windows enrollment controller service disappeared during startup.'
        }
    }

    throw 'The Windows enrollment controller service is installed but is not running; refuse to leave its Caddy ticket route active.'
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

if (-not (Test-Path -LiteralPath $keycloakMapperTemplate -PathType Leaf)) {
    throw "Required Keycloak mapping template is missing: $keycloakMapperTemplate"
}

# Realm import is one-time. Refresh this idempotent migration helper from the
# reviewed template on every start so an existing bootstrap realm receives new
# least-privilege scopes and clients without being recreated.
$keycloakMapperContent = [System.IO.File]::ReadAllText($keycloakMapperTemplate).Replace("`r`n", "`n").Replace("`r", "`n")
[System.IO.File]::WriteAllText($keycloakMapperDestination, $keycloakMapperContent, [System.Text.UTF8Encoding]::new($false))

$composePrefix = if ($isFinalizedDeployment) {
    @(
        'compose',
        '--project-directory', $lanRoot,
        '--env-file', $environmentFile,
        '-f', $composeFile)
}
else {
    @('compose', '--env-file', $environmentFile, '-f', $composeFile)
}

if ($isFinalizedDeployment -and -not $SkipRemoteHubBuild) {
    # Do not rebuild a network-facing service from a mutable worktree after
    # the controller's long-lived credential has been provisioned.
    $SkipRemoteHubBuild = $true
}

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
    # kcadm writes a normal login-status line to stderr. Preserve strict error
    # handling elsewhere, but assess this readiness probe by its process exit
    # code rather than treating that informational line as a PowerShell error.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker @($composePrefix + @('exec', '-T', 'keycloak', 'sh', $keycloakMapperPath)) *> $null
        $mappingExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($mappingExitCode -eq 0) {
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
Invoke-Docker ($composePrefix + @('exec', '-T', 'remotehub', 'sh', '-ec', 'test -r /run/stayactive/caddy-root.crt')) 'RemoteHub cannot read the public Caddy root certificate required for Keycloak TLS validation.'

$runningServices = & docker @($composePrefix + @('ps', '--status', 'running', '--services'))
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the LAN stack after startup.'
}

$requiredServices = @('caddy', 'headscale', 'meshcentral', 'postgres', 'keycloak', 'remotehub')
$missingServices = @($requiredServices | Where-Object { $_ -notin $runningServices })
if ($missingServices.Count -gt 0) {
    throw "The LAN stack did not leave all required services running: $($missingServices -join ', ')."
}

Assert-ControllerServiceIfInstalled
if ($isFinalizedDeployment) {
    Write-Host 'The finalized LAN stack is running from its protected reviewed deployment bundle.'
}
else {
    Write-Host 'The LAN bootstrap stack is running on loopback only.'
    Write-Host 'Before opening it to the LAN, install the Caddy root certificate, create exactly one MeshCentral administrator, and then run Finalize-LanTest.ps1.'
}