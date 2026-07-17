#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ControllerArtifactDirectory,

    [Parameter(Mandatory)]
    [string]$ConfigurationTemplatePath,

    [Parameter(Mandatory)]
    [string]$HeadscaleUserId,

    [Parameter(Mandatory)]
    [string]$ControllerListenAddress,

    [Parameter(Mandatory)]
    [string]$EnvironmentFile,

    [Parameter(Mandatory)]
    [string]$ComposeFile,

    [Parameter(Mandatory)]
    [string]$ComposeProjectDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'StayActiveEnrollmentController'
$serviceDisplayName = 'StayActive Headscale Enrollment Controller'
$serviceAccountName = 'StayActiveHeadscaleController'


function Assert-Ipv4([string]$Value, [string]$ParameterName) {
    $address = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "$ParameterName must be an IPv4 address."
    }

    return $address.IPAddressToString
}

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
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

function New-ServicePassword {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_'
    $bytes = New-Object byte[] 48
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
        $characters = foreach ($value in $bytes) { $alphabet[$value % $alphabet.Length] }
        return -join $characters
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

function Protect-ControllerPath(
    [string]$Path,
    [string]$ServiceIdentity,
    [ValidateSet('R', 'RX', 'M')]
    [string]$ServiceAccess,
    [switch]$Recursive) {
    $isDirectory = Test-Path -LiteralPath $Path -PathType Container
    if (-not $isDirectory -and -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot protect a missing controller path: $Path"
    }

    if ($isDirectory) {
        $icaclsArguments = @(
            $Path,
            '/inheritance:r',
            '/grant:r',
            'SYSTEM:(OI)(CI)F',
            'BUILTIN\Administrators:(OI)(CI)F',
            "${ServiceIdentity}:(OI)(CI)$ServiceAccess")
        if ($Recursive) {
            $icaclsArguments += '/T'
        }
        & icacls @icaclsArguments | Out-Null
    }
    else {
        & icacls $Path /inheritance:r /grant:r `
            'SYSTEM:(F)' `
            'BUILTIN\Administrators:(F)' `
            "${ServiceIdentity}:($ServiceAccess)" | Out-Null
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to restrict controller path: $Path"
    }
}

function Resolve-ReviewedStagedPath(
    [string]$Path,
    [string]$ReviewedStagingRoot,
    [string]$ParameterName,
    [switch]$Directory) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$ParameterName does not exist."
    }
    if (-not (Test-Path -LiteralPath $ReviewedStagingRoot -PathType Container)) {
        throw 'The protected reviewed-artifact root does not exist.'
    }

    $rootItem = Get-Item -LiteralPath $ReviewedStagingRoot -Force
    $item = Get-Item -LiteralPath $Path -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$ParameterName must not be a reparse point."
    }
    if ($Directory -and -not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$ParameterName must be a directory."
    }
    if (-not $Directory -and -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$ParameterName must be a file."
    }

    $rootFullPath = [System.IO.Path]::GetFullPath($rootItem.FullName).TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($item.FullName)
    if (-not $fullPath.StartsWith($rootFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$ParameterName must be below the protected reviewed-artifact root."
    }

    return $fullPath
}

function Copy-ReviewedControllerArtifact(
    [string]$SourceDirectory,
    [string]$DestinationDirectory) {
    $reparsePoint = Get-ChildItem -LiteralPath $SourceDirectory -Force -Recurse |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw 'The protected reviewed controller artifact contains a reparse point.'
    }

    $items = @(Get-ChildItem -LiteralPath $SourceDirectory -Force)
    if ($items.Count -eq 0) {
        throw 'The protected reviewed controller artifact is empty.'
    }

    [System.IO.Directory]::CreateDirectory($DestinationDirectory) | Out-Null
    foreach ($item in $items) {
        Copy-Item -LiteralPath $item.FullName -Destination $DestinationDirectory -Recurse -Force
    }
}
function Get-ControllerService {
    return Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Assert-ServiceAccountIsIsolated([string]$AccountName) {
    foreach ($groupName in @('Administrators', 'docker-users')) {
        $group = Get-LocalGroup -Name $groupName -ErrorAction SilentlyContinue
        if ($null -eq $group) {
            continue
        }

        $member = Get-LocalGroupMember -Group $group -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "\\$([regex]::Escape($AccountName))$" } |
            Select-Object -First 1
        if ($null -ne $member) {
            throw "The controller service account must not belong to the local $groupName group."
        }
    }
}

function New-ControllerServiceAccount {
    $existing = Get-LocalUser -Name $serviceAccountName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        throw "The local $serviceAccountName account already exists but the controller service is not installed; refusing to guess or reset its credential."
    }

    $plainPassword = New-ServicePassword
    $securePassword = ConvertTo-SecureString -String $plainPassword -AsPlainText -Force
    $createdAccount = $false
    try {
        New-LocalUser `
            -Name $serviceAccountName `
            -Password $securePassword `
            -AccountNeverExpires `
            -PasswordNeverExpires `
            -UserMayNotChangePassword `
            -Description 'Dedicated local identity for the StayActive Headscale enrollment controller.' | Out-Null
        $createdAccount = $true
        $identity = "$env:COMPUTERNAME\$serviceAccountName"
        Assert-ServiceAccountIsIsolated $serviceAccountName
        return [pscustomobject]@{
            Identity = $identity
            Credential = [System.Management.Automation.PSCredential]::new($identity, $securePassword)
            PlainPassword = $plainPassword
        }
    }
    catch {
        $failure = $_
        $plainPassword = $null
        $securePassword = $null
        if ($createdAccount) {
            try {
                # Isolation is verified immediately after creation. If it fails,
                # remove only the account this invocation just created; it has not
                # received a controller key or loaded a user profile yet.
                Remove-LocalUser -Name $serviceAccountName -ErrorAction Stop
            }
            catch {
                throw 'A newly-created dedicated controller account could not be removed after isolation validation failed. Resolve that local account state before retrying.'
            }
        }

        throw $failure
    }
}

function New-ControllerApiKey([string[]]$ComposePrefix) {
    # Headscale returns a controller key exactly once. Capture it only in this
    # process's memory; never emit it, write it, or pass it on a command line.
    $apiKeyOutput = & docker @($ComposePrefix + @(
        'exec', '-T',
        'headscale',
        'headscale', 'apikeys', 'create',
        '--force',
        '--expiration', '3650d',
        '--output', 'json')) 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to create the long-lived Headscale controller API key.'
    }

    try {
        $apiKey = $apiKeyOutput | Out-String | ConvertFrom-Json
    }
    catch {
        throw 'Headscale returned an unreadable controller API-key response.'
    }

    $key = [string]$apiKey.key
    $id = [string]$apiKey.id
    if ($key -notmatch '^hskey-api-[A-Za-z0-9_-]+$' -or $id -notmatch '^[1-9][0-9]*$') {
        throw 'Headscale returned an invalid controller API-key response.'
    }

    return [pscustomobject]@{
        Key = $key
        Id = $id
        ExpiresAtUtc = [string]$apiKey.expiration
    }
}

function Expire-ControllerApiKey([string[]]$ComposePrefix, [string]$KeyId) {
    if ($KeyId -notmatch '^[1-9][0-9]*$') {
        throw 'A numeric Headscale controller key id is required for expiration.'
    }

    Invoke-Docker ($ComposePrefix + @(
        'exec', '-T',
        'headscale',
        'headscale', 'apikeys', 'expire',
        '--force', '--id', $KeyId)) 'Unable to expire the unused Headscale controller API key.'
}

function Get-ControllerServiceProfiles([string]$ServiceSid = '') {
    try {
        $profiles = @(Get-CimInstance Win32_UserProfile -ErrorAction Stop)
    }
    catch {
        throw 'Unable to inspect the dedicated controller profile during protected recovery.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ServiceSid)) {
        return @($profiles | Where-Object { [string]$_.SID -eq $ServiceSid })
    }

    # If the local account was removed in a previous interrupted recovery, the
    # fixed profile directory is the only safe residue we can recognize without
    # retaining any credential, password, or additional identity metadata.
    $systemDrive = [Environment]::GetEnvironmentVariable('SystemDrive')
    if ([string]::IsNullOrWhiteSpace($systemDrive)) {
        throw 'Windows SystemDrive is unavailable for protected controller recovery.'
    }
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $systemDrive "Users\$serviceAccountName")).TrimEnd('\')
    return @($profiles | Where-Object {
            $localPath = [string]$_.LocalPath
            if ([string]::IsNullOrWhiteSpace($localPath)) {
                return $false
            }
            try {
                return [System.IO.Path]::GetFullPath($localPath).TrimEnd('\') -ieq $expectedPath
            }
            catch {
                return $false
            }
        })
}

function Remove-ControllerServiceProfiles([string]$ServiceSid = '') {
    $profiles = @(Get-ControllerServiceProfiles $ServiceSid)
    foreach ($profile in $profiles) {
        if ([bool]$profile.Special -or [bool]$profile.Loaded) {
            throw 'The dedicated controller credential profile is special or still loaded; do not remove it automatically.'
        }
        try {
            # Win32_UserProfile removal clears the matching profile directory and
            # registry hive, including DPAPI/Credential Manager residue, without
            # ever requiring or reconstructing the service-account password.
            Remove-CimInstance -InputObject $profile -ErrorAction Stop
        }
        catch {
            throw 'Unable to remove the dedicated controller credential profile during protected recovery.'
        }
    }

    if (@(Get-ControllerServiceProfiles $ServiceSid).Count -ne 0) {
        throw 'The dedicated controller credential profile remains after protected recovery cleanup.'
    }
}

function Resolve-PendingControllerRecovery(
    [string[]]$ComposePrefix,
    [string]$MetadataPath) {
    if (-not (Test-Path -LiteralPath $MetadataPath -PathType Leaf)) {
        return
    }

    try {
        $metadata = [System.IO.File]::ReadAllText($MetadataPath) | ConvertFrom-Json
    }
    catch {
        throw 'Protected controller recovery metadata is unreadable; refuse to create another long-lived controller key.'
    }

    $keyId = [string]$metadata.Id
    $expiresAtUtc = [string]$metadata.ExpiresAtUtc
    if ($keyId -notmatch '^[1-9][0-9]*$' -or [string]::IsNullOrWhiteSpace($expiresAtUtc)) {
        throw 'Protected controller recovery metadata is invalid; refuse to create another long-lived controller key.'
    }
    try {
        [DateTimeOffset]::Parse($expiresAtUtc, [Globalization.CultureInfo]::InvariantCulture) | Out-Null
    }
    catch {
        throw 'Protected controller recovery metadata has an invalid expiration; refuse to create another long-lived controller key.'
    }

    $orphan = Get-LocalUser -Name $serviceAccountName -ErrorAction SilentlyContinue
    $orphanSid = ''
    if ($null -ne $orphan) {
        if ($orphan.Description -ne 'Dedicated local identity for the StayActive Headscale enrollment controller.') {
            throw 'The pending controller recovery account has an unexpected identity; refuse to remove it automatically.'
        }
        $orphanSid = [string]$orphan.SID
        if ($orphanSid -notmatch '^S-1-5-21-(?:[0-9]+-){3}[0-9]+$') {
            throw 'The pending controller recovery account has an invalid local SID; refuse to remove it automatically.'
        }
    }

    # Fail closed: the raw key might have reached CredMan immediately before a
    # crash. Revoke it before deleting an account, profile, or credential residue.
    try {
        Expire-ControllerApiKey $ComposePrefix $keyId
    }
    catch {
        throw 'Unable to expire the pending long-lived controller key; resolve the protected recovery state before retrying.'
    }

    try {
        Remove-ControllerServiceProfiles $orphanSid
    }
    catch {
        throw 'Unable to remove the pending dedicated controller credential profile after key expiration; resolve the protected recovery state before retrying.'
    }

    if ($null -ne $orphan) {
        try {
            Remove-LocalUser -Name $serviceAccountName -ErrorAction Stop
        }
        catch {
            throw 'Unable to remove the pending dedicated controller account after key expiration; resolve the protected recovery state before retrying.'
        }
    }

    try {
        Remove-Item -LiteralPath $MetadataPath -Force
    }
    catch {
        throw 'Unable to clear protected controller recovery metadata after key expiration and residue cleanup.'
    }
}
function Invoke-ControllerCredentialProvisioner(
    [string]$ControllerExecutable,
    [System.Management.Automation.PSCredential]$ServiceCredential,
    [ValidateSet('Store', 'Delete')]
    [string]$Operation,
    [string]$ControllerKey = '') {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ControllerExecutable
    $startInfo.ArgumentList.Add((if ($Operation -eq 'Store') { '--store-controller-key' } else { '--delete-controller-key' }))
    $startInfo.UserName = $ServiceCredential.UserName
    $startInfo.Password = $ServiceCredential.Password
    $startInfo.LoadUserProfile = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $Operation -eq 'Store'
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Unable to start the isolated controller credential provisioner.'
        }

        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if ($Operation -eq 'Store') {
            $process.StandardInput.Write($ControllerKey)
            $process.StandardInput.Close()
        }
        $process.WaitForExit()
        $null = $standardOutput.GetAwaiter().GetResult()
        $null = $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "The isolated controller credential provisioner could not $($Operation.ToLowerInvariant()) the Headscale controller credential."
        }
    }
    finally {
        $process.Dispose()
    }
}
function Wait-ForControllerHealth([string]$Address) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $response = $client.GetAsync("http://${Address}:5091/healthz").GetAwaiter().GetResult()
                try {
                    if ([int]$response.StatusCode -eq 200) {
                        return
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            catch {
                # The service may still be creating its Kestrel listener.
            }

            Start-Sleep -Seconds 2
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    throw 'The Windows enrollment controller did not become healthy.'
}

function Render-ControllerConfiguration(
    [string]$TemplatePath,
    [string]$DestinationPath,
    [string]$EnrollmentUserId,
    [string]$ListenAddress,
    [string]$JournalPath,
    [string]$JournalKeyPath) {
    $replacements = @{
        '__HEADSCALE_ENROLLMENT_USER_ID__' = $EnrollmentUserId
        '__WINDOWS_ENROLLMENT_CONTROLLER_LISTEN_URL__' = "http://${ListenAddress}:5091"
        '__ENROLLMENT_CONTROLLER_JOURNAL_PATH__' = ($JournalPath -replace '\\', '\\\\')
        '__ENROLLMENT_CONTROLLER_JOURNAL_HMAC_KEY_FILE__' = ($JournalKeyPath -replace '\\', '\\\\')
    }
    $text = [System.IO.File]::ReadAllText($TemplatePath)
    foreach ($entry in $replacements.GetEnumerator()) {
        $text = $text.Replace($entry.Key, $entry.Value)
    }
    if ($text -match '__[A-Z0-9_]+__') {
        throw 'The rendered Windows enrollment-controller configuration contains an unrendered placeholder.'
    }

    try {
        $null = $text | ConvertFrom-Json
    }
    catch {
        throw 'The rendered Windows enrollment-controller configuration is not valid JSON.'
    }

    Write-AtomicUtf8File $DestinationPath $text
}

$ControllerListenAddress = Assert-Ipv4 $ControllerListenAddress 'ControllerListenAddress'
if ($HeadscaleUserId -notmatch '^[1-9][0-9]*$') {
    throw 'HeadscaleUserId must be a positive numeric Headscale user id.'
}

foreach ($path in @($EnvironmentFile, $ComposeFile, $ComposeProjectDirectory)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required controller-installation path is missing: $path"
    }
}

$controllerDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
if ([string]::IsNullOrWhiteSpace($controllerDataRoot)) {
    throw 'Windows CommonApplicationData is unavailable for the protected enrollment-controller state.'
}

# Every service-loaded artifact must come from Finalize-LanTest.ps1's reviewed,
# administrator-only staging area. The installer will never publish from a
# working-tree source path or render from a working-tree template.
$controllerRoot = Join-Path $controllerDataRoot 'StayActiveRemotes\EnrollmentController'
$reviewedStagingRoot = Join-Path $controllerRoot 'reviewed-artifacts'
$ControllerArtifactDirectory = Resolve-ReviewedStagedPath $ControllerArtifactDirectory $reviewedStagingRoot 'ControllerArtifactDirectory' -Directory
$ConfigurationTemplatePath = Resolve-ReviewedStagedPath $ConfigurationTemplatePath $reviewedStagingRoot 'ConfigurationTemplatePath'
$EnvironmentFile = Resolve-ReviewedStagedPath $EnvironmentFile $reviewedStagingRoot 'EnvironmentFile'
$ComposeFile = Resolve-ReviewedStagedPath $ComposeFile $reviewedStagingRoot 'ComposeFile'
$ComposeProjectDirectory = (Resolve-Path -LiteralPath $ComposeProjectDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $ControllerArtifactDirectory 'EnrollmentBroker.exe') -PathType Leaf)) {
    throw 'The protected reviewed controller artifact does not contain EnrollmentBroker.exe.'
}

$publishDirectory = Join-Path $controllerRoot 'app'
$configPath = Join-Path $publishDirectory 'appsettings.Production.json'
$journalDirectory = Join-Path $controllerRoot 'journal'
$journalPath = Join-Path $journalDirectory 'tickets.journal.jsonl'
$journalKeyPath = Join-Path $controllerRoot 'secrets\journal-hmac-key'
$metadataPath = Join-Path $controllerRoot 'controller-api-key.metadata.json'

$composePrefix = @(
    'compose',
    '--project-directory', $ComposeProjectDirectory,
    '--env-file', $EnvironmentFile,
    '-f', $ComposeFile)
$existingService = Get-ControllerService
$newIdentity = $null
$newApiKey = $null
$serviceCreated = $false
$metadataWritten = $false
$controllerIdentity = "$env:COMPUTERNAME\$serviceAccountName"
if ($null -eq $existingService) {
    Resolve-PendingControllerRecovery $composePrefix $metadataPath
    $newIdentity = New-ControllerServiceAccount
    $controllerIdentity = $newIdentity.Identity
}
else {
    if ($existingService.StartName -notmatch "(?i)(^|\\)$([regex]::Escape($serviceAccountName))$") {
        throw "The existing $serviceName service does not run under the dedicated $serviceAccountName identity."
    }
    Assert-ServiceAccountIsIsolated $serviceAccountName
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw 'The controller service already exists without controller-key metadata; refuse to create or store another long-lived API key.'
    }
    if ((Get-Service -Name $serviceName).Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
    }
}

try {
    [System.IO.Directory]::CreateDirectory($controllerRoot) | Out-Null
    # Remove inherited interactive-user access before creating artifacts the
    # service will trust. The service identity receives only RX here; the
    # journal directory receives its narrower write grant below.
    Protect-ControllerPath $controllerRoot $controllerIdentity 'RX'
    foreach ($directory in @($publishDirectory, $journalDirectory, (Split-Path -Parent $journalKeyPath))) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    if (-not (Test-Path -LiteralPath $journalKeyPath -PathType Leaf)) {
        $journalKey = New-Base64Secret 48
        try {
            Write-AtomicUtf8File $journalKeyPath $journalKey
        }
        finally {
            $journalKey = $null
        }
    }

    Copy-ReviewedControllerArtifact $ControllerArtifactDirectory $publishDirectory
    Render-ControllerConfiguration $ConfigurationTemplatePath $configPath $HeadscaleUserId $ControllerListenAddress $journalPath $journalKeyPath
    Protect-ControllerPath $controllerRoot $controllerIdentity 'RX'
    Protect-ControllerPath $publishDirectory $controllerIdentity 'RX' -Recursive
    Protect-ControllerPath $journalDirectory $controllerIdentity 'M' -Recursive
    Protect-ControllerPath $journalKeyPath $controllerIdentity 'R'
    Protect-ControllerPath $configPath $controllerIdentity 'R'

    $controllerExecutable = Join-Path $publishDirectory 'EnrollmentBroker.exe'
    if (-not (Test-Path -LiteralPath $controllerExecutable -PathType Leaf)) {
        throw 'The Windows enrollment-controller publish did not produce EnrollmentBroker.exe.'
    }

    if ($null -eq $existingService) {
        $newApiKey = New-ControllerApiKey $composePrefix
        # Persist only non-secret recovery metadata before the key crosses the
        # service identity boundary. This makes a failed installation
        # recoverable without ever writing the raw API key to disk.
        $metadata = [ordered]@{
            Id = [string]$newApiKey.Id
            ExpiresAtUtc = [string]$newApiKey.ExpiresAtUtc
        } | ConvertTo-Json -Compress
        Write-AtomicUtf8File $metadataPath $metadata
        $metadataWritten = $true
        Protect-ControllerPath $metadataPath $controllerIdentity 'R'

        Invoke-ControllerCredentialProvisioner $controllerExecutable $newIdentity.Credential 'Store' ([string]$newApiKey.Key)

        New-Service `
            -Name $serviceName `
            -DisplayName $serviceDisplayName `
            -Description 'Owns the local Headscale controller API key in Windows Credential Manager and serves only one-use enrollment tickets.' `
            -BinaryPathName "`"$controllerExecutable`" --service" `
            -StartupType Automatic `
            -Credential $newIdentity.Credential | Out-Null
        $serviceCreated = $true
        & sc.exe sidtype $serviceName unrestricted | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to configure a dedicated Windows service SID for the enrollment controller.'
        }
    }

    Start-Service -Name $serviceName
    Wait-ForControllerHealth $ControllerListenAddress
}
catch {
    $failure = $_
    $controllerKeyExpired = $true
    $serviceRemoved = $true
    $profileRemoved = $true
    $accountRemoved = $true
    if ($null -eq $existingService -and $null -ne $newIdentity) {
        if ($serviceCreated) {
            try {
                Stop-Service -Name $serviceName -Force -ErrorAction Stop
                & sc.exe delete $serviceName | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    throw 'Unable to delete the newly-created enrollment controller service.'
                }
            }
            catch {
                $serviceRemoved = $false
            }
        }

        # Once Headscale has minted a key, revocation is the first cleanup
        # action. Do not remove the account, its profile, or its CredMan entry
        # while a crash-recovery key could still be active.
        if ($null -ne $newApiKey) {
            try {
                Expire-ControllerApiKey $composePrefix ([string]$newApiKey.Id)
            }
            catch {
                $controllerKeyExpired = $false
            }
        }

        if ($controllerKeyExpired -and $serviceRemoved) {
            if ($null -ne $newApiKey) {
                try {
                    # This command carries no secret. It runs as the same local
                    # service identity that owns the generic CredMan entry.
                    Invoke-ControllerCredentialProvisioner $controllerExecutable $newIdentity.Credential 'Delete'
                }
                catch {
                    # Profile removal below is the password-free fallback. The
                    # key is already expired, so preserving recovery evidence is
                    # safer than replacing the original failure here.
                }
            }

            $pendingAccount = Get-LocalUser -Name $serviceAccountName -ErrorAction SilentlyContinue
            $pendingSid = ''
            if ($null -ne $pendingAccount) {
                if ($pendingAccount.Description -ne 'Dedicated local identity for the StayActive Headscale enrollment controller.') {
                    $profileRemoved = $false
                    $accountRemoved = $false
                }
                else {
                    $pendingSid = [string]$pendingAccount.SID
                }
            }

            if ($profileRemoved) {
                try {
                    Remove-ControllerServiceProfiles $pendingSid
                }
                catch {
                    $profileRemoved = $false
                }
            }

            if ($profileRemoved -and $null -ne $pendingAccount) {
                try {
                    Remove-LocalUser -Name $serviceAccountName -ErrorAction Stop
                }
                catch {
                    $accountRemoved = $false
                }
            }
        }
        else {
            # Keep the account/profile and non-secret metadata intact for the
            # next protected recovery attempt when key revocation or service
            # teardown could not be proven complete.
            $profileRemoved = $false
            $accountRemoved = $false
        }

        if ($metadataWritten -and $controllerKeyExpired -and $serviceRemoved -and $profileRemoved -and $accountRemoved -and (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            try {
                Remove-Item -LiteralPath $metadataPath -Force
            }
            catch {
                # Retain only non-secret recovery metadata if the local ACL or
                # filesystem cannot remove it. The next run resolves it first.
                $controllerKeyExpired = $false
            }
        }
    }

    if (-not $controllerKeyExpired -or -not $serviceRemoved -or -not $profileRemoved -or -not $accountRemoved) {
        throw 'The enrollment controller installation could not complete rollback. Protected recovery metadata remains; resolve it before retrying.'
    }

    throw $failure
}
finally {
    if ($null -ne $newApiKey) {
        $newApiKey.Key = $null
    }
    if ($null -ne $newIdentity) {
        $newIdentity.PlainPassword = $null
    }
}

Write-Host 'The dedicated Windows enrollment controller is running. Its long-lived Headscale API key is stored only in that service account''s Windows Credential Manager.'

