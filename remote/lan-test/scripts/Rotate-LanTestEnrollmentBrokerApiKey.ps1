#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$Rotate,

    [ValidateRange(1, 90)]
    [int]$LifetimeDays = 30
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
        $env:Path = $dockerDirectory + ';' + $env:Path
    }
}

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Invoke-HeadscaleApiKeyExpiration([string[]]$ComposePrefix, [string]$ApiKeyId, [string]$FailureMessage) {
    # Suppress the CLI response: this operation must never disclose a key or
    # make callers depend on a human-readable Headscale output format.
    & docker @($ComposePrefix + @(
        'exec', '-T', 'headscale',
        'headscale', 'apikeys', 'expire',
        '--force', '--id', $ApiKeyId)) *> $null
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Protect-LocalSecret([string]$Path) {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $fileGrants = @(
        "${identity}:(F)",
        'SYSTEM:(F)',
        'BUILTIN\Administrators:(F)')
    $isDirectory = Test-Path -LiteralPath $Path -PathType Container

    if ($isDirectory) {
        & icacls $Path /inheritance:r /grant:r @fileGrants /T | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict existing local state below $Path."
        }

        $directoryGrants = @(
            "${identity}:(OI)(CI)F",
            'SYSTEM:(OI)(CI)F',
            'BUILTIN\Administrators:(OI)(CI)F')
        & icacls $Path /inheritance:r /grant:r @directoryGrants | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict future local state below $Path."
        }

        return
    }

    & icacls $Path /inheritance:r /grant:r @fileGrants | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to restrict access to $Path."
    }
}

function Write-AtomicUtf8File([string]$Path, [string]$Content) {
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $pendingPath = "$Path.pending"
    [System.IO.File]::WriteAllText($pendingPath, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $pendingPath -Destination $Path -Force
    Protect-LocalSecret $Path
}

function Get-EnvironmentValue([string[]]$Lines, [string]$Name) {
    $line = $Lines | Where-Object { $_ -like "$Name=*" } | Select-Object -First 1
    if ($null -eq $line) {
        throw "Missing $Name in the LAN test environment file."
    }

    return $line.Substring($Name.Length + 1)
}

function Get-HeadscaleUsers([string[]]$ComposePrefix) {
    $userListOutput = & docker @($ComposePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'list', '--output', 'json'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list Headscale users. Verify the self-hosted control plane is running.'
    }

    try {
        return @($userListOutput | Out-String | ConvertFrom-Json | Where-Object { $null -ne $_ })
    }
    catch {
        throw 'Headscale did not return a JSON user list; refusing to rotate the broker credential.'
    }
}

function New-EnrollmentBrokerApiKey([string[]]$ComposePrefix, [int]$RequestedLifetimeDays) {
    # Headscale returns the raw key exactly once. Capture it in memory without
    # writing to the console, logs, command line, .env, or JSON metadata.
    $apiKeyOutput = & docker @($ComposePrefix + @(
        'exec', '-T',
        'headscale',
        'headscale', 'apikeys', 'create',
        '--force',
        '--expiration', "${RequestedLifetimeDays}d",
        '--output', 'json')) 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to create the replacement EnrollmentBroker Headscale API key.'
    }

    try {
        $apiKey = $apiKeyOutput | Out-String | ConvertFrom-Json
    }
    catch {
        throw 'Headscale returned an unreadable replacement API-key response.'
    }

    $key = [string]$apiKey.key
    $id = [string]$apiKey.id
    if ($key -notmatch '^hskey-api-[A-Za-z0-9_-]+$' -or $id -notmatch '^[1-9][0-9]*$') {
        throw 'Headscale returned an invalid replacement API-key response.'
    }

    return [pscustomobject]@{
        Key = $key
        Id = $id
        Prefix = [string]$apiKey.prefix
        ExpiresAtUtc = [string]$apiKey.expiration
    }
}

function Wait-For-RunningService([string[]]$ComposePrefix, [string]$ServiceName, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableChecks = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        $runningServices = & docker @($ComposePrefix + @('ps', '--status', 'running', '--services'))
        if ($LASTEXITCODE -eq 0 -and $ServiceName -in $runningServices) {
            $stableChecks++
            if ($stableChecks -ge 3) {
                return
            }
        }
        else {
            $stableChecks = 0
        }

        Start-Sleep -Seconds 2
    }

    throw "The $ServiceName service did not remain running after credential rotation."
}

Enable-DockerDesktopCli

if (-not $Rotate) {
    throw 'Pass -Rotate to confirm replacement of the EnrollmentBroker Headscale API credential.'
}

$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$environmentFile = Join-Path $lanRoot '.env'
$composeFile = Join-Path $lanRoot 'compose.yaml'
$brokerConfigPath = Join-Path $lanRoot 'generated\enrollmentbroker\appsettings.Production.json'
$brokerApiKeyPath = Join-Path $lanRoot 'secrets\headscale-enrollment-api-key'
$brokerApiKeyMetadataPath = Join-Path $lanRoot 'secrets\headscale-enrollment-api-key.metadata.json'
$brokerJournalKeyPath = Join-Path $lanRoot 'secrets\enrollmentbroker-journal-hmac-key'
$pendingRevocationPath = Join-Path $lanRoot 'secrets\headscale-enrollment-api-key.pending-revocation.json'
$rollbackApiKeyPath = "$brokerApiKeyPath.rollback"
$rollbackMetadataPath = "$brokerApiKeyMetadataPath.rollback"

foreach ($path in @($environmentFile, $composeFile, $brokerConfigPath, $brokerApiKeyPath, $brokerApiKeyMetadataPath, $brokerJournalKeyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required broker rotation file is missing: $path"
    }
}

$environmentLines = [System.IO.File]::ReadAllLines($environmentFile)
$composePrefix = @('compose', '--env-file', $environmentFile, '-f', $composeFile)
$headscaleUsers = Get-HeadscaleUsers $composePrefix
$policyOwner = $headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' } | Select-Object -First 1
if ($null -eq $policyOwner -or [string]$policyOwner.id -notmatch '^[1-9][0-9]*$') {
    throw 'The stayactive-admin Headscale policy owner is required before rotating the broker credential.'
}

try {
    $brokerConfig = [System.IO.File]::ReadAllText($brokerConfigPath) | ConvertFrom-Json
}
catch {
    throw 'The rendered EnrollmentBroker configuration is unreadable; refusing credential rotation.'
}
if ([string]$brokerConfig.EnrollmentBroker.Headscale.UserId -ne [string]$policyOwner.id) {
    throw 'EnrollmentBroker configuration does not target the current Headscale policy owner.'
}

# If a previous run activated the new key but could not revoke the old one,
# complete that cleanup before minting any additional administrative key.
if (Test-Path -LiteralPath $pendingRevocationPath -PathType Leaf) {
    try {
        $pendingRevocation = [System.IO.File]::ReadAllText($pendingRevocationPath) | ConvertFrom-Json
    }
    catch {
        throw 'Pending EnrollmentBroker API-key revocation metadata is unreadable.'
    }
    if ([string]$pendingRevocation.Id -notmatch '^[1-9][0-9]*$') {
        throw 'Pending EnrollmentBroker API-key revocation metadata lacks a valid Headscale key id.'
    }
    Invoke-HeadscaleApiKeyExpiration $composePrefix ([string]$pendingRevocation.Id) 'Unable to complete the previous EnrollmentBroker API-key revocation.'
    Remove-Item -LiteralPath $pendingRevocationPath -Force
}

$oldKey = [System.IO.File]::ReadAllText($brokerApiKeyPath).Trim()
if ($oldKey -notmatch '^hskey-api-[A-Za-z0-9_-]+$') {
    throw 'The current EnrollmentBroker Headscale API key is invalid.'
}
try {
    $oldMetadataText = [System.IO.File]::ReadAllText($brokerApiKeyMetadataPath)
    $oldMetadata = $oldMetadataText | ConvertFrom-Json
}
catch {
    throw 'The current EnrollmentBroker API-key metadata is unreadable.'
}
if ([string]$oldMetadata.Id -notmatch '^[1-9][0-9]*$') {
    throw 'The current EnrollmentBroker API-key metadata lacks a valid Headscale key id.'
}

$newApiKey = $null
$newBrokerActive = $false
try {
    # This is deliberately after every owner/configuration check above.
    $newApiKey = New-EnrollmentBrokerApiKey $composePrefix $LifetimeDays
    $newMetadataText = [ordered]@{
        Id = $newApiKey.Id
        Prefix = $newApiKey.Prefix
        ExpiresAtUtc = $newApiKey.ExpiresAtUtc
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Compress

    # Keep protected rollback copies only until the replacement service has
    # survived its restart. They are removed before the old key is revoked.
    Write-AtomicUtf8File $rollbackApiKeyPath $oldKey
    Write-AtomicUtf8File $rollbackMetadataPath $oldMetadataText
    Write-AtomicUtf8File $brokerApiKeyPath $newApiKey.Key
    Write-AtomicUtf8File $brokerApiKeyMetadataPath $newMetadataText

    Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'enrollmentbroker')) 'Unable to restart EnrollmentBroker with the replacement credential.'
    Wait-For-RunningService $composePrefix 'enrollmentbroker' 30
    Invoke-Docker ($composePrefix + @('exec', '-T', 'enrollmentbroker', 'sh', '-ec', 'test -r /run/secrets/headscale-enrollment-api-key && test -r /run/secrets/enrollmentbroker-journal-hmac-key')) 'EnrollmentBroker cannot read its rotated credential files.'
    $newBrokerActive = $true

    # Persist only the old key id, not its raw value, while revocation is in
    # progress. If Headscale is briefly unavailable, the next explicit rotate
    # attempt finishes this cleanup before it mints another key.
    $pendingMetadata = [ordered]@{
        Id = [string]$oldMetadata.Id
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Compress
    Write-AtomicUtf8File $pendingRevocationPath $pendingMetadata
    Remove-Item -LiteralPath $rollbackApiKeyPath, $rollbackMetadataPath -Force -ErrorAction SilentlyContinue
    Invoke-HeadscaleApiKeyExpiration $composePrefix ([string]$oldMetadata.Id) 'Replacement broker credential is active, but the old Headscale API key could not be revoked. Re-run this command to complete the pending revocation.'
    Remove-Item -LiteralPath $pendingRevocationPath -Force
}
catch {
    $failure = $_
    if (-not $newBrokerActive -and $null -ne $newApiKey) {
        # Do not leave a new credential mounted after a failed restart. Restore
        # the old protected files first, then make a best-effort revocation of
        # the unactivated replacement key without emitting its value.
        try {
            Write-AtomicUtf8File $brokerApiKeyPath $oldKey
            Write-AtomicUtf8File $brokerApiKeyMetadataPath $oldMetadataText
            Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'enrollmentbroker')) 'Unable to restore EnrollmentBroker after a failed credential rotation.'
            Wait-For-RunningService $composePrefix 'enrollmentbroker' 30
        }
        finally {
            try {
                Invoke-HeadscaleApiKeyExpiration $composePrefix ([string]$newApiKey.Id) 'Unable to revoke the unused replacement EnrollmentBroker API key.'
            }
            catch {
                # A failed cleanup must not disclose a raw key. The operator is
                # told only that rotation did not complete; Headscale can be
                # retried once it is healthy.
            }
            Remove-Item -LiteralPath $rollbackApiKeyPath, $rollbackMetadataPath -Force -ErrorAction SilentlyContinue
        }
    }
    throw $failure
}
finally {
    $oldKey = $null
    $oldMetadataText = $null
    $newApiKey = $null
}

Write-Host 'The EnrollmentBroker Headscale API credential was rotated without displaying the raw key.'
