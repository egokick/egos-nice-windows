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
$keycloakMapperTemplate = Join-Path $lanRoot 'config\keycloak\configure-scope-mappings.sh.template'
$keycloakMapperDestination = Join-Path $lanRoot 'generated\keycloak\configure-scope-mappings.sh'

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-HeadscaleUsers([string[]]$ComposePrefix) {
    $userListOutput = & docker @($ComposePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'list', '--output', 'json'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list Headscale users while checking the enrollment broker precondition.'
    }

    try {
        return @($userListOutput | Out-String | ConvertFrom-Json | Where-Object { $null -ne $_ })
    }
    catch {
        throw 'Headscale did not return a JSON user list while checking the enrollment broker precondition.'
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

if (-not (Test-Path -LiteralPath $keycloakMapperTemplate -PathType Leaf)) {
    throw "Required Keycloak mapping template is missing: $keycloakMapperTemplate"
}

# Realm import is one-time. Refresh this idempotent migration helper from the
# reviewed template on every start so an existing bootstrap realm receives new
# least-privilege scopes and clients without being recreated.
$keycloakMapperContent = [System.IO.File]::ReadAllText($keycloakMapperTemplate).Replace("`r`n", "`n").Replace("`r", "`n")
[System.IO.File]::WriteAllText($keycloakMapperDestination, $keycloakMapperContent, [System.Text.UTF8Encoding]::new($false))

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

# The broker is absent until Finalize-LanTest.ps1 has created its protected
# secrets after the Headscale policy owner exists. Startup never creates or
# rotates that API key. A present key is therefore an activation sentinel.
$brokerConfigPath = Join-Path $lanRoot 'generated\enrollmentbroker\appsettings.Production.json'
$brokerApiKeyPath = Join-Path $lanRoot 'secrets\headscale-enrollment-api-key'
$brokerJournalKeyPath = Join-Path $lanRoot 'secrets\enrollmentbroker-journal-hmac-key'
$brokerJournalDirectory = Join-Path $lanRoot 'state\enrollmentbroker\journal'
$brokerActive = $false
if (Test-Path -LiteralPath $brokerApiKeyPath -PathType Leaf) {
    foreach ($requiredBrokerPath in @($brokerConfigPath, $brokerJournalKeyPath, $brokerJournalDirectory)) {
        if (-not (Test-Path -LiteralPath $requiredBrokerPath)) {
            throw "Enrollment broker activation is incomplete; required path is missing: $requiredBrokerPath"
        }
    }

    $headscaleUsers = Get-HeadscaleUsers $composePrefix
    if (-not ($headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' -and [string]$_.id -match '^[1-9][0-9]*$' })) {
        throw 'Enrollment broker activation requires the stayactive-admin Headscale policy owner. Run Finalize-LanTest.ps1 first.'
    }

    Invoke-Docker ($composePrefix + @('up', '-d', 'enrollmentbroker')) 'Unable to start the isolated EnrollmentBroker.'
    $brokerActive = $true
}
elseif (Test-Path -LiteralPath $brokerJournalKeyPath -PathType Leaf) {
    throw 'Enrollment broker has a journal secret but no API-key secret; refusing to start a partial enrollment service.'
}

$runningServices = & docker @($composePrefix + @('ps', '--status', 'running', '--services'))
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the LAN stack after startup.'
}

$requiredServices = @('caddy', 'headscale', 'meshcentral', 'postgres', 'keycloak', 'remotehub')
if ($brokerActive) {
    $requiredServices += 'enrollmentbroker'
}
$missingServices = @($requiredServices | Where-Object { $_ -notin $runningServices })
if ($missingServices.Count -gt 0) {
    throw "The LAN stack did not leave all required services running: $($missingServices -join ', ')."
}

Write-Host 'The LAN bootstrap stack is running on loopback only.'
Write-Host 'Before opening it to the LAN, install the Caddy root certificate, create exactly one MeshCentral administrator, and then run Finalize-LanTest.ps1.'