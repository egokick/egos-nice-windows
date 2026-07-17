#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$MeshCentralAdministratorCreated,

    [Parameter(Mandatory)]
    [switch]$EnableLan
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

if (-not $MeshCentralAdministratorCreated -or -not $EnableLan) {
    throw 'Refusing to publish the LAN endpoint. Re-run only after creating the single MeshCentral administrator, with both confirmation switches.'
}

$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $lanRoot '..\..')).Path
$environmentFile = Join-Path $lanRoot '.env'
$composeFile = Join-Path $lanRoot 'compose.yaml'
$policySource = Join-Path $lanRoot 'config\headscale\policy.hujson'
$policyDestination = Join-Path $lanRoot 'generated\headscale\policy.hujson'
$meshCentralConfigPath = Join-Path $lanRoot 'generated\meshcentral\config.json'
$rootCertificatePath = Join-Path $lanRoot 'certs\caddy-root.crt'
$firewallScript = Join-Path $PSScriptRoot 'Enable-LanTestFirewall.ps1'
$hostsScript = Join-Path $PSScriptRoot 'Set-LanTestHosts.ps1'
$brokerConfigTemplatePath = Join-Path $lanRoot 'config\enrollmentbroker\appsettings.Production.json.template'
$brokerConfigPath = Join-Path $lanRoot 'generated\enrollmentbroker\appsettings.Production.json'
$brokerJournalDirectory = Join-Path $lanRoot 'state\enrollmentbroker\journal'
$brokerApiKeyPath = Join-Path $lanRoot 'secrets\headscale-enrollment-api-key'
$brokerJournalKeyPath = Join-Path $lanRoot 'secrets\enrollmentbroker-journal-hmac-key'
$brokerApiKeyMetadataPath = Join-Path $lanRoot 'secrets\headscale-enrollment-api-key.metadata.json'
$brokerDockerfilePath = Join-Path $lanRoot '..\EnrollmentBroker.Dockerfile'

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-EnvironmentValue([string[]]$Lines, [string]$Name) {
    $line = $Lines | Where-Object { $_ -like "$Name=*" } | Select-Object -First 1
    if ($null -eq $line) {
        throw "Missing $Name in the LAN test environment file."
    }

    return $line.Substring($Name.Length + 1)
}

function Assert-PrivateIpv4([string]$Value) {
    $address = $null
    if (
        -not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'LAN_IP must be an IPv4 address.'
    }

    $bytes = $address.GetAddressBytes()
    if (-not (
            $bytes[0] -eq 10 -or
            ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
            ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) {
        throw 'LAN_IP must remain an RFC1918 private IPv4 address.'
    }

    return $address.IPAddressToString
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

function New-Base64Secret([int]$ByteCount) {
    [byte[]]$bytes = New-Object byte[] $ByteCount
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $random.Dispose()
    }
}

function Write-AtomicUtf8File([string]$Path, [string]$Content) {
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $pendingPath = "$Path.pending"
    [System.IO.File]::WriteAllText($pendingPath, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $pendingPath -Destination $Path -Force
}

function Set-EnvironmentValue([string[]]$Lines, [string]$Name, [string]$Value) {
    $updatedLines = [System.Collections.Generic.List[string]]::new()
    $found = $false
    foreach ($line in $Lines) {
        if ($line -like "$Name=*") {
            if (-not $found) {
                $updatedLines.Add("$Name=$Value")
                $found = $true
            }
            continue
        }

        $updatedLines.Add($line)
    }

    if (-not $found) {
        $updatedLines.Add("$Name=$Value")
    }

    return $updatedLines.ToArray()
}

function Get-OptionalEnvironmentValue([string[]]$Lines, [string]$Name) {
    $line = $Lines | Where-Object { $_ -like "$Name=*" } | Select-Object -First 1
    if ($null -eq $line) {
        return $null
    }

    return $line.Substring($Name.Length + 1)
}

function Render-EnrollmentBrokerConfig([string]$TemplatePath, [string]$DestinationPath, [string]$HeadscaleUserId) {
    $text = [System.IO.File]::ReadAllText($TemplatePath).Replace('__HEADSCALE_ENROLLMENT_USER_ID__', $HeadscaleUserId)
    if ($text -match '__[A-Z0-9_]+__') {
        throw 'EnrollmentBroker configuration contains an unrendered placeholder.'
    }

    try {
        $null = $text | ConvertFrom-Json
    }
    catch {
        throw 'Rendered EnrollmentBroker configuration is not valid JSON.'
    }

    Write-AtomicUtf8File $DestinationPath $text
    Protect-LocalSecret $DestinationPath
}

function Get-HeadscaleUsers([string[]]$ComposePrefix) {
    $userListOutput = & docker @($ComposePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'list', '--output', 'json'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list Headscale users. Verify the bootstrap stack is running.'
    }

    try {
        return @($userListOutput | Out-String | ConvertFrom-Json | Where-Object { $null -ne $_ })
    }
    catch {
        throw 'Headscale did not return a JSON user list; refusing to provision the enrollment broker.'
    }
}

function New-EnrollmentBrokerApiKey([string[]]$ComposePrefix, [string]$SecretPath, [string]$MetadataPath) {
    # The raw API key is returned only once by Headscale. Capture it without
    # writing it to the host console, logs, .env, or rendered configuration.
    $apiKeyOutput = & docker @($ComposePrefix + @(
        'exec', '-T',
        'headscale',
        'headscale', 'apikeys', 'create',
        '--force',
        '--expiration', '30d',
        '--output', 'json')) 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to create the restricted EnrollmentBroker Headscale API key.'
    }

    try {
        $apiKey = $apiKeyOutput | Out-String | ConvertFrom-Json
    }
    catch {
        throw 'Headscale returned an unreadable EnrollmentBroker API-key response.'
    }

    $key = [string]$apiKey.key
    $id = [string]$apiKey.id
    $prefix = [string]$apiKey.prefix
    if ($key -notmatch '^hskey-api-[A-Za-z0-9_-]+$' -or $id -notmatch '^[1-9][0-9]*$') {
        throw 'Headscale returned an invalid EnrollmentBroker API-key response.'
    }

    Write-AtomicUtf8File $SecretPath $key
    Protect-LocalSecret $SecretPath

    $metadata = [ordered]@{
        Id = $id
        Prefix = $prefix
        ExpiresAtUtc = [string]$apiKey.expiration
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Compress
    Write-AtomicUtf8File $MetadataPath $metadata
    Protect-LocalSecret $MetadataPath
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

    throw "The $ServiceName service did not remain running. The LAN listener is still loopback-only."
}

function Assert-HeadscaleApiIsBlockedOnHost {
    # This is intentionally made from the Windows host, never from the broker
    # address. A live Caddy route must return 404 here; a 401/other response
    # would mean the Headscale management API bypassed the source restriction.
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $response = $client.GetAsync('https://headscale.stayactive.test/api/v1/users').GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ne 404) {
                throw "Caddy returned HTTP $([int]$response.StatusCode) for a host-originated Headscale API request."
            }
        }
        finally {
            $response.Dispose()
        }
    }
    catch {
        throw "Unable to verify that Caddy blocks host-originated Headscale API requests: $($_.Exception.Message)"
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw 'LAN test is not initialized. Run Initialize-LanTest.ps1 first.'
}

foreach ($path in @($composeFile, $policySource, $policyDestination, $meshCentralConfigPath, $rootCertificatePath, $firewallScript, $hostsScript, $brokerConfigTemplatePath, $brokerDockerfilePath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required LAN test file is missing: $path"
    }
}

$environmentLines = [System.IO.File]::ReadAllLines($environmentFile)
$lanIp = Assert-PrivateIpv4 (Get-EnvironmentValue $environmentLines 'LAN_IP')
$caddyBindAddress = Get-EnvironmentValue $environmentLines 'CADDY_BIND_ADDRESS'
$stunBindAddress = Get-EnvironmentValue $environmentLines 'STUN_BIND_ADDRESS'
if ($caddyBindAddress -ne '127.0.0.1' -or $stunBindAddress -ne '127.0.0.1') {
    throw 'Finalization requires both HTTPS and STUN to still be loopback-only. Refusing to proceed from a previously LAN-bound bootstrap state.'
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
try {
    if ($null -eq (Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue)) {
        throw 'Install the Caddy root certificate into LocalMachine\Root before exposing the LAN endpoint.'
    }
}
finally {
    $certificate.Dispose()
}

$composePrefix = @('compose', '--env-file', $environmentFile, '-f', $composeFile)
$headscaleUsers = Get-HeadscaleUsers $composePrefix
$policyOwner = $headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' } | Select-Object -First 1
if ($null -eq $policyOwner) {
    Invoke-Docker ($composePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'create', 'stayactive-admin')) 'Unable to create the Headscale policy owner.'
    $headscaleUsers = Get-HeadscaleUsers $composePrefix
    $policyOwner = $headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' } | Select-Object -First 1
}

if ($null -eq $policyOwner -or [string]$policyOwner.id -notmatch '^[1-9][0-9]*$') {
    throw 'The stayactive-admin Headscale policy owner was not created with a valid numeric id.'
}

Copy-Item -LiteralPath $policySource -Destination $policyDestination -Force
Invoke-Docker ($composePrefix + @('restart', 'headscale')) 'Unable to apply the reviewed Headscale policy.'

# A bind-mounted Caddyfile is not reloaded merely because the host file
# changes. Recreate it while HTTPS is still loopback-only, prove the management
# API deny route is live from this host, and only then give the broker a path to
# Headscale.
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'caddy')) 'Unable to reload Caddy with the Headscale API source restriction.'
Wait-For-RunningService $composePrefix 'caddy' 30
& $hostsScript -Mode Bootstrap -Confirm:$false
Assert-HeadscaleApiIsBlockedOnHost
# The broker image carries a source revision label. Do not publish a build from
# an uncommitted broker tree: later investigation must be able to reproduce the
# exact code that had access to this isolated Headscale API credential.
$brokerSourceChanges = (& git -C $repoRoot status --porcelain -- 'remote/EnrollmentBroker' 'remote/EnrollmentBroker.Dockerfile') | Out-String
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to verify the EnrollmentBroker source revision.'
}
if (-not [string]::IsNullOrWhiteSpace($brokerSourceChanges)) {
    throw 'Commit the reviewed EnrollmentBroker source before finalizing the LAN test.'
}
$brokerRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($brokerRevision)) {
    throw 'Unable to determine the immutable EnrollmentBroker source revision.'
}

# Older loopback-only LAN installations have no broker entries in .env. Migrate
# only the non-secret entries in place, preserve the file's restricted ACL, and
# keep the well-known control-network IP stable for Caddy's source restriction.
$brokerControlIp = Get-OptionalEnvironmentValue $environmentLines 'ENROLLMENTBROKER_CONTROL_IP'
if ($null -ne $brokerControlIp -and $brokerControlIp -ne '172.30.60.16') {
    throw 'ENROLLMENTBROKER_CONTROL_IP must remain 172.30.60.16 for the reviewed Caddy restriction.'
}
$remoteHubSdkImage = Get-EnvironmentValue $environmentLines 'REMOTEHUB_SDK_IMAGE'
$remoteHubRuntimeImage = Get-EnvironmentValue $environmentLines 'REMOTEHUB_RUNTIME_IMAGE'
$environmentLines = @(Set-EnvironmentValue $environmentLines 'ENROLLMENTBROKER_CONTROL_IP' '172.30.60.16')
$environmentLines = @(Set-EnvironmentValue $environmentLines 'ENROLLMENTBROKER_SDK_IMAGE' $remoteHubSdkImage)
$environmentLines = @(Set-EnvironmentValue $environmentLines 'ENROLLMENTBROKER_RUNTIME_IMAGE' $remoteHubRuntimeImage)
$environmentLines = @(Set-EnvironmentValue $environmentLines 'ENROLLMENTBROKER_SOURCE_REVISION' $brokerRevision)
Write-AtomicUtf8File $environmentFile (($environmentLines -join "`n") + "`n")
Protect-LocalSecret $environmentFile

# Keep the broker's configuration, journal, and two secrets inside the
# existing restricted local state tree. It receives neither the Docker socket
# nor Headscale's state database.
foreach ($brokerPath in @(
        (Split-Path -Parent $brokerConfigPath),
        $brokerJournalDirectory,
        (Split-Path -Parent $brokerApiKeyPath))) {
    [System.IO.Directory]::CreateDirectory($brokerPath) | Out-Null
    Protect-LocalSecret $brokerPath
}
Render-EnrollmentBrokerConfig $brokerConfigTemplatePath $brokerConfigPath ([string]$policyOwner.id)

if (-not (Test-Path -LiteralPath $brokerJournalKeyPath -PathType Leaf)) {
    $journalHmacKey = New-Base64Secret 48
    try {
        Write-AtomicUtf8File $brokerJournalKeyPath $journalHmacKey
        Protect-LocalSecret $brokerJournalKeyPath
    }
    finally {
        $journalHmacKey = $null
    }
}
else {
    $existingJournalKey = [System.IO.File]::ReadAllText($brokerJournalKeyPath).Trim()
    [byte[]]$decodedJournalKey = $null
    try {
        $decodedJournalKey = [Convert]::FromBase64String($existingJournalKey)
        if ($decodedJournalKey.Length -lt 32) {
            throw 'EnrollmentBroker journal key is too short.'
        }
    }
    catch {
        throw 'The existing EnrollmentBroker journal key is invalid; refusing to overwrite it.'
    }
    finally {
        if ($null -ne $decodedJournalKey) {
            [Array]::Clear($decodedJournalKey, 0, $decodedJournalKey.Length)
        }
        $existingJournalKey = $null
    }
    Protect-LocalSecret $brokerJournalKeyPath
}

if (Test-Path -LiteralPath $brokerApiKeyPath -PathType Leaf) {
    $existingBrokerApiKey = [System.IO.File]::ReadAllText($brokerApiKeyPath).Trim()
    try {
        if ($existingBrokerApiKey -notmatch '^hskey-api-[A-Za-z0-9_-]+$') {
            throw 'EnrollmentBroker Headscale API key is invalid.'
        }
    }
    finally {
        $existingBrokerApiKey = $null
    }
    if (-not (Test-Path -LiteralPath $brokerApiKeyMetadataPath -PathType Leaf)) {
        throw 'EnrollmentBroker API-key metadata is missing; refuse to start a key that cannot be safely rotated.'
    }
    try {
        $existingBrokerApiKeyMetadata = [System.IO.File]::ReadAllText($brokerApiKeyMetadataPath) | ConvertFrom-Json
    }
    catch {
        throw 'EnrollmentBroker API-key metadata is unreadable; refuse to start a key that cannot be safely rotated.'
    }
    if ([string]$existingBrokerApiKeyMetadata.Id -notmatch '^[1-9][0-9]*$') {
        throw 'EnrollmentBroker API-key metadata lacks a valid Headscale key id.'
    }
    Protect-LocalSecret $brokerApiKeyPath
    Protect-LocalSecret $brokerApiKeyMetadataPath
}
else {
    # Headscale returns this broadly capable management key only once. It is
    # created after the policy owner is known, captured without console output,
    # and mounted exclusively into the non-root broker service.
    New-EnrollmentBrokerApiKey $composePrefix $brokerApiKeyPath $brokerApiKeyMetadataPath
}

# Build and prove that the least-privilege service survives before publishing
# the LAN listener. A failure here leaves Caddy and STUN loopback-only.
Invoke-Docker ($composePrefix + @('up', '-d', '--build', 'enrollmentbroker')) 'Unable to build and start the isolated EnrollmentBroker.'
Wait-For-RunningService $composePrefix 'enrollmentbroker' 30
Invoke-Docker ($composePrefix + @('exec', '-T', 'enrollmentbroker', 'sh', '-ec', 'test -r /run/stayactive/caddy-root.crt && test -r /run/secrets/headscale-enrollment-api-key && test -r /run/secrets/enrollmentbroker-journal-hmac-key')) 'EnrollmentBroker cannot read its required public CA or protected secret files.'



# The one-time administrator was created while Caddy was loopback-only. Turn
# off all future account registration before any listener or firewall rule is
# published to the LAN. The generated directory already has a restricted ACL.
$meshCentralConfig = [System.IO.File]::ReadAllText($meshCentralConfigPath)
if ($meshCentralConfig -match '"newAccounts"\s*:\s*true\b') {
    $meshCentralConfig = [regex]::Replace($meshCentralConfig, '("newAccounts"\s*:\s*)true\b', '$1false', 1)
}
elseif ($meshCentralConfig -notmatch '"newAccounts"\s*:\s*false\b') {
    throw 'MeshCentral configuration does not contain a boolean newAccounts setting.'
}

if ($meshCentralConfig -notmatch '"newAccounts"\s*:\s*false\b') {
    throw 'Refusing to expose the LAN endpoint while MeshCentral account registration is enabled.'
}

$tempMeshCentralConfigPath = "$meshCentralConfigPath.pending"
[System.IO.File]::WriteAllText($tempMeshCentralConfigPath, $meshCentralConfig, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempMeshCentralConfigPath -Destination $meshCentralConfigPath -Force
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'meshcentral')) 'Unable to restart MeshCentral with account registration disabled.'

# Update the existing protected .env file in place. Do not replace it from the
# parent directory, which would risk inheriting a broader ACL with secrets.
$updatedEnvironmentLines = $environmentLines | ForEach-Object {
    if ($_ -like 'CADDY_BIND_ADDRESS=*') {
        "CADDY_BIND_ADDRESS=$lanIp"
    }
    elseif ($_ -like 'STUN_BIND_ADDRESS=*') {
        "STUN_BIND_ADDRESS=$lanIp"
    }
    else {
        $_
    }
}
[System.IO.File]::WriteAllLines($environmentFile, $updatedEnvironmentLines, [System.Text.UTF8Encoding]::new($false))

# Open the two narrowly scoped firewall rules while both service listeners are
# still loopback-only, then publish HTTPS and STUN together.
& $firewallScript -Confirm:$false
& $hostsScript -Mode Lan -ServerIp $lanIp -Confirm:$false
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'caddy', 'headscale')) 'Unable to rebind Caddy and Headscale STUN to the configured LAN address.'

Write-Host "The self-hosted control plane is now available to the local subnet at $lanIp on HTTPS and STUN only."
Write-Host 'The EnrollmentBroker is enabled through the self-hosted Remotes menu; its Headscale API key stays in a protected local secret and is never displayed.'
Write-Host 'Do not distribute enrollment keys or the operator password through chat or email.'