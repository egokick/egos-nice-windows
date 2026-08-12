[CmdletBinding()]
param(
    [string]$StackName = "opticon-release-distribution",
    [string]$Region = "us-east-1",
    [string]$ArtifactDirectory = "",
    [string]$Version = "",
    [string]$ControlOrigin = "https://taildesk-egokick-control.fly.dev",
    [ValidateSet("Production", "OwnerManaged")]
    [string]$SigningProfile = "Production",
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SourceReleaseCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ProductCertificateThumbprint,
    [ValidatePattern('^$|^[A-Fa-f0-9]{40}$')][string]$LegacyMigrationSignerThumbprint = '',
    [Parameter(Mandatory)][string]$Rfc3161TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath,
    [string]$ClientInstallValidationBase64 = '',
    [switch]$ForceRedeploy,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$AwsProfile = 'default',
    # Publish exactly one signed source archive and a schema-2 source-only
    # manifest. No executable bundle/bootstrap object is uploaded to S3.
    [switch]$SourceOnly,
    # Verify all non-mutating publisher prerequisites before an operator
    # revokes active invitations. This never builds, uploads, or changes the
    # live manifest.
    [switch]$CheckOnly,
    [switch]$SkipBuild,
    [Alias("SkipFlyDeploy")]
    [switch]$SkipManifestPublish,
    # Build (unless -SkipBuild), validate, upload, and fully read back the
    # immutable source archive, then write a local stage receipt.  It never
    # changes the live invite manifest.
    [switch]$StageOnly,
    # Commit the exact source-only manifest captured by -StageOnly.  This
    # deliberately revalidates the staged local archive plus S3/CloudFront,
    # but refuses to build or upload anything.
    [switch]$CommitStaged
)

# AWS authority comes only from the operator's authenticated CLI. The tiny
# manifest uses the existing DPAPI-protected Opticon admin HMAC credential.
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.1') {
    throw 'The Opticon bundle publisher requires PowerShell 7.1 or newer. Run this script with pwsh.exe, not Windows PowerShell.'
}
Add-Type -AssemblyName System.Net.Http
$invitationSigningThumbprint = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'
$legacyMigrationBridgeVersion = '1.1.41'
$SourceReleaseCertificateThumbprint = $SourceReleaseCertificateThumbprint.ToUpperInvariant()
$ProductCertificateThumbprint = $ProductCertificateThumbprint.ToUpperInvariant()
$knownClientValidationSteps = @(
    'InvitationAuthenticity','InvitationConstraints','ProtectedPaths','DownloadIntegrity',
    'SourceArchiveAuthenticity','LauncherBinding','SourceBuildProvenance','SetupPreflight',
    'MachineState','PayloadAuthenticity','DependencyIntegrity','ComponentPostconditions',
    'NetworkIdentity','FirewallPolicy','EnrollmentConfirmation')
try {
    $clientInstallValidation = if ([string]::IsNullOrWhiteSpace($ClientInstallValidationBase64)) {
        [pscustomobject]@{ disableAll = $false; disabledSteps = @() }
    } else {
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ClientInstallValidationBase64)) | ConvertFrom-Json
    }
} catch { throw 'The client installation validation policy is malformed.' }
$disabledValidationSteps = @($clientInstallValidation.disabledSteps)
if ($disabledValidationSteps.Count -ne @($disabledValidationSteps | Sort-Object -Unique).Count -or
    @($disabledValidationSteps | Where-Object { [string]$_ -notin $knownClientValidationSteps }).Count -ne 0) {
    throw 'The client installation validation policy contains an unknown or duplicate step.'
}
$clientInstallValidation = [ordered]@{
    disableAll = [bool]$clientInstallValidation.disableAll
    disabledSteps = @($disabledValidationSteps | Sort-Object)
}
$LegacyMigrationSignerThumbprint = $LegacyMigrationSignerThumbprint.ToUpperInvariant()
$isLegacyMigration = -not [string]::IsNullOrWhiteSpace($LegacyMigrationSignerThumbprint)
if ($SourceReleaseCertificateThumbprint -eq $invitationSigningThumbprint -or
    $ProductCertificateThumbprint -eq $invitationSigningThumbprint -or
    $SourceReleaseCertificateThumbprint -eq $ProductCertificateThumbprint) {
    throw 'Production invitation, source-release, and Authenticode trust domains must be pairwise distinct.'
}
if ($isLegacyMigration -and ($SigningProfile -ne 'OwnerManaged' -or
        $Version -cne $legacyMigrationBridgeVersion -or
        $LegacyMigrationSignerThumbprint -ne $invitationSigningThumbprint)) {
    throw 'A legacy Agent migration must be the exact OwnerManaged 1.1.41 release signed only with the exact retired invitation certificate.'
}
if ($SourceOnly -and $isLegacyMigration) {
    throw 'A source-only release cannot use the retired legacy migration signer.'
}
$timestampUri = $null
if (-not [Uri]::TryCreate($Rfc3161TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
    -not [string]::IsNullOrEmpty($timestampUri.UserInfo)) {
    throw 'Rfc3161TimestampUrl is invalid.'
}
$officialDigiCertRfc3161 = $timestampUri.Scheme -eq [Uri]::UriSchemeHttp -and
    $timestampUri.IsDefaultPort -and $timestampUri.Host.Equals('timestamp.digicert.com', [StringComparison]::OrdinalIgnoreCase) -and
    $timestampUri.AbsolutePath -eq '/' -and [string]::IsNullOrEmpty($timestampUri.Query) -and
    [string]::IsNullOrEmpty($timestampUri.Fragment)
if ($timestampUri.Scheme -ne [Uri]::UriSchemeHttps -and -not $officialDigiCertRfc3161) {
    throw 'Rfc3161TimestampUrl must use HTTPS or the exact Microsoft-documented DigiCert RFC3161 endpoint.'
}
$controlOriginUri = $null
if (-not [Uri]::TryCreate($ControlOrigin, [UriKind]::Absolute, [ref]$controlOriginUri) -or
    $controlOriginUri.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($controlOriginUri.UserInfo) -or
    $controlOriginUri.AbsolutePath -ne '/' -or -not [string]::IsNullOrEmpty($controlOriginUri.Query) -or
    -not [string]::IsNullOrEmpty($controlOriginUri.Fragment)) {
    throw 'ControlOrigin must be an absolute HTTPS origin without credentials, path, query, or fragment.'
}
$expectedAccount = "053663732727"
$bucket = "opticon-053663732727"
$flyRoot = Split-Path $PSScriptRoot -Parent
$ArtifactDirectory = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) { Join-Path $flyRoot "artifacts" } else { $ArtifactDirectory }
$ArtifactDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ArtifactDirectory))
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container) -or
    ((Get-Item -LiteralPath $ArtifactDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'ArtifactDirectory must be an existing regular directory, not a reparse point.'
}
$manifestPath = Join-Path $ArtifactDirectory "manifest.json"
$script:VerifiedSourceReleaseCertificateRawData = $null
$script:AwsScratchDirectory = $null
$stageReceiptKind = 'OpticonSourceReleaseStage'
# Schema 3 receipts are immutable, receipt-bound stage records.  A random
# stage ID is embedded in the S3 archive metadata, so the immutable ZIP itself
# selects its exact receipt after a crash or a concurrent publisher race.
# Keep accepting older local receipts as a one-time compatibility fallback.
$stageReceiptSchemaVersion = 3
if ($StageOnly -and $CommitStaged) {
    throw '-StageOnly and -CommitStaged cannot be combined.'
}
if (($StageOnly -or $CommitStaged) -and -not $SourceOnly) {
    throw 'Two-phase staging is supported only for the source-only invite release channel.'
}
if ($StageOnly -and ($CheckOnly -or $SkipManifestPublish)) {
    throw '-StageOnly cannot be combined with -CheckOnly or -SkipManifestPublish.'
}
if ($CommitStaged -and ($CheckOnly -or $SkipManifestPublish)) {
    throw '-CommitStaged cannot be combined with -CheckOnly or -SkipManifestPublish.'
}
if ($CommitStaged -and [string]::IsNullOrWhiteSpace($Version)) {
    throw '-CommitStaged requires an explicit -Version.'
}
if ($StageOnly) {
    # Keep the existing generic skip switch for compatibility, but make the
    # stage action self-describing and impossible to accidentally publish.
    $SkipManifestPublish = $true
}
if ($CommitStaged) {
    # A commit must never quietly replace a staged archive with a fresh build.
    $SkipBuild = $true
}
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$awsPath = Join-Path $programFiles 'Amazon\AWSCLIV2\aws.exe'
if (-not (Test-Path -LiteralPath $awsPath -PathType Leaf)) { throw 'AWS CLI v2 is required at its fixed Program Files path.' }
$current = [IO.Path]::GetFullPath($awsPath)
$programFiles = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($programFiles))
while ($true) {
    if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "The fixed AWS CLI path contains a reparse point: $current"
    }
    if ($current.Equals($programFiles, [StringComparison]::OrdinalIgnoreCase)) { break }
    $current = Split-Path $current -Parent
    if ([string]::IsNullOrWhiteSpace($current)) { throw 'The AWS CLI escaped Program Files.' }
}
$awsHome = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.aws'
$script:AwsConfigFile = Join-Path $awsHome 'config'
$awsCredentialsFile = Join-Path $awsHome 'credentials'

function Invoke-AwsCliOnce {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $windows = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $awsPath
    $start.WorkingDirectory = Split-Path $awsPath -Parent
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $start.Environment['SystemRoot'] = $windows
    $start.Environment['WINDIR'] = $windows
    $start.Environment['ProgramFiles'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $start.Environment['ProgramData'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $start.Environment['USERPROFILE'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $start.Environment['HOME'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $start.Environment['PATH'] = [string]::Join([IO.Path]::PathSeparator, @((Split-Path $awsPath -Parent), $system32))
    $start.Environment['PATHEXT'] = '.COM;.EXE'
    $start.Environment['AWS_PROFILE'] = $AwsProfile
    $start.Environment['AWS_CONFIG_FILE'] = $script:AwsConfigFile
    $start.Environment['AWS_EC2_METADATA_DISABLED'] = 'true'
    if (-not [string]::IsNullOrWhiteSpace($script:AwsScratchDirectory)) {
        $start.Environment['TEMP'] = $script:AwsScratchDirectory
        $start.Environment['TMP'] = $script:AwsScratchDirectory
    }
    if (Test-Path -LiteralPath $awsCredentialsFile -PathType Leaf) {
        $start.Environment['AWS_SHARED_CREDENTIALS_FILE'] = $awsCredentialsFile
    }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start the fixed AWS CLI.' }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $stdoutTask.GetAwaiter().GetResult()
            Error = $stderrTask.GetAwaiter().GetResult()
        }
    } finally { $process.Dispose() }
}

function Invoke-AwsCli {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(1, 3)][int]$MaximumAttempts = 1
    )
    $result = $null
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $result = Invoke-AwsCliOnce -Arguments $Arguments
        if ($result.ExitCode -eq 0 -or $attempt -eq $MaximumAttempts) { return $result }
        Write-Warning "AWS CLI operation failed on attempt $attempt of $MaximumAttempts; retrying the identical request."
        Start-Sleep -Seconds ([Math]::Pow(2, $attempt - 1))
    }
    return $result
}

function Enter-OpticonReleasePublisherLock {
    param(
        [Parameter(Mandatory)][string]$Path,
        [TimeSpan]$Timeout = [TimeSpan]::FromMinutes(50)
    )
    $deadline = [DateTime]::UtcNow.Add($Timeout)
    $reportedWait = $false
    while ($true) {
        try {
            return [IO.File]::Open(
                $Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for another Opticon release publisher to finish."
            }
            if (-not $reportedWait) {
                Write-Host 'Another Opticon release operation is active; waiting for it to finish.' -ForegroundColor Yellow
                $reportedWait = $true
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Assert-ProductionArtifactTrust {
    param([Parameter(Mandatory)]$Artifact)
    $profile = Get-ArtifactString $Artifact 'signingProfile'
    $sourceKey = Get-ArtifactString $Artifact 'sourceManifestKeyId'
    $productSigner = Get-ArtifactString $Artifact 'productSignerThumbprint'
    if ($profile -cne $SigningProfile -or
        -not $sourceKey.Equals($SourceReleaseCertificateThumbprint, [StringComparison]::Ordinal) -or
        -not $productSigner.Equals($ProductCertificateThumbprint, [StringComparison]::Ordinal) -or
        $sourceKey -eq $invitationSigningThumbprint -or
        $productSigner -eq $invitationSigningThumbprint -or
        $sourceKey -eq $productSigner) {
        throw "Artifact $($Artifact.file) is not bound to the configured production trust domains."
    }
}

function Get-ArtifactString {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)][string]$Name)
    $property = $Artifact.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    return [string]$property.Value
}

function Get-LegacyMigrationBundleFileName {
    param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Role)
    switch ($Role) {
        'ManagedOnly' { return "opticon-bundle-$Version-managed-win-x64.zip" }
        'ControllerAndManaged' { return "opticon-bundle-$Version-controller-win-x64.zip" }
        default { return '' }
    }
}

function Test-ExactLegacyMigrationArtifact {
    param([Parameter(Mandatory)]$Artifact)

    $role = Get-ArtifactString $Artifact 'role'
    $expectedFile = Get-LegacyMigrationBundleFileName -Version $legacyMigrationBridgeVersion -Role $role
    $size = 0L
    try { $size = [long]$Artifact.size } catch { return $false }
    return (Get-ArtifactString $Artifact 'legacyMigrationSignerThumbprint') -ceq $invitationSigningThumbprint -and
        (Get-ArtifactString $Artifact 'product') -ceq 'OpticonBundle' -and
        (Get-ArtifactString $Artifact 'version') -ceq $legacyMigrationBridgeVersion -and
        (Get-ArtifactString $Artifact 'signingProfile') -ceq 'OwnerManaged' -and
        (Get-ArtifactString $Artifact 'sourceManifestKeyId') -ceq $SourceReleaseCertificateThumbprint -and
        (Get-ArtifactString $Artifact 'productSignerThumbprint') -ceq $ProductCertificateThumbprint -and
        (Get-ArtifactString $Artifact 'architecture') -ceq 'x64' -and
        -not [string]::IsNullOrWhiteSpace($expectedFile) -and
        (Get-ArtifactString $Artifact 'file') -ceq $expectedFile -and
        $size -ge 1024 -and $size -le 2GB -and
        (Get-ArtifactString $Artifact 'sha256') -match '^[A-Fa-f0-9]{64}$'
}

function Assert-LegacyMigrationArtifact {
    param([Parameter(Mandatory)]$Artifact)

    $marker = Get-ArtifactString $Artifact 'legacyMigrationSignerThumbprint'
    if ([string]::IsNullOrWhiteSpace($marker)) { return $false }
    if (-not (Test-ExactLegacyMigrationArtifact -Artifact $Artifact)) {
        throw "A legacy migration artifact must be the exact OwnerManaged $legacyMigrationBridgeVersion bridge with the canonical retired signer: $($Artifact.file)."
    }
    return $true
}

function Get-LocalArtifactDestinationPath {
    param([Parameter(Mandatory)][string]$FileName)
    if ([string]::IsNullOrWhiteSpace($FileName) -or
        -not [IO.Path]::GetFileName($FileName).Equals($FileName, [StringComparison]::Ordinal) -or
        $FileName.Contains('/') -or $FileName.Contains('\')) {
        throw 'An Opticon artifact filename is unsafe.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $ArtifactDirectory $FileName))
    if (-not [IO.Path]::GetDirectoryName($path).Equals([IO.Path]::GetFullPath($ArtifactDirectory), [StringComparison]::OrdinalIgnoreCase)) {
        throw "The local release artifact escaped its directory: $FileName"
    }
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "A local release artifact is a reparse point: $path"
    }
    $current = [IO.Path]::GetDirectoryName($path)
    while ($true) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "A local release artifact traverses a reparse point: $current"
        }
        if ($current.Equals($ArtifactDirectory, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = Split-Path $current -Parent
        if ([string]::IsNullOrWhiteSpace($current)) { throw 'A local release artifact escaped ArtifactDirectory.' }
    }
    return $path
}

function Get-LocalArtifactPath {
    param([Parameter(Mandatory)][string]$FileName)
    $path = Get-LocalArtifactDestinationPath -FileName $FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The exact local artifact is missing: $FileName"
    }
    return $path
}

function Move-FileAtomically {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "The atomic replacement source is missing: $Source"
    }
    if (Test-Path -LiteralPath $Destination) {
        $existing = Get-Item -LiteralPath $Destination -Force
        if (($existing.Attributes -band [IO.FileAttributes]::Directory) -or
            ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "The atomic replacement destination is unsafe: $Destination"
        }
        # File.Replace is available in Windows PowerShell 5.1/.NET Framework.
        # Retaining its same-directory backup until the replace succeeds makes
        # a process death recoverable without relying on the newer .NET
        # File.Move(source, destination, overwrite) overload.
        $backup = "$Destination.$([Guid]::NewGuid().ToString('N')).bak"
        $replaced = $false
        try {
            [IO.File]::Replace($Source, $Destination, $backup)
            $replaced = $true
        } catch {
            # File.Replace normally leaves Destination intact on failure.  If
            # it instead reached the backup step before failing, restore that
            # last known-valid generation when Destination is absent.  A failed
            # restoration deliberately leaves the backup in place for manual
            # recovery rather than deleting the only durable copy.
            if (-not (Test-Path -LiteralPath $Destination -PathType Leaf) -and
                (Test-Path -LiteralPath $backup -PathType Leaf)) {
                try { [IO.File]::Move($backup, $Destination) } catch { }
            }
            throw
        } finally {
            # On a failed replace the backup is the only possible copy of the
            # prior valid receipt/manifest.  Leave it for recovery instead of
            # treating cleanup as more important than durability.
            if ($replaced) {
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            }
        }
    } else {
        [IO.File]::Move($Source, $Destination)
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-SourceStageReceiptPath {
    param([Parameter(Mandatory)][string]$ReleaseVersion)
    if ($ReleaseVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'The source stage receipt version is invalid.'
    }
    return [IO.Path]::GetFullPath((Join-Path $ArtifactDirectory ".opticon-source-stage-$ReleaseVersion.json"))
}

function Get-SourceStageReceiptObjectKey {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{43}$')][string]$StageId
    )
    if ($ReleaseVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'The source stage receipt version is invalid.'
    }
    return "opticon/releases/$ReleaseVersion/.stages/$StageId.json"
}

function Test-AwsObjectNotFound {
    param([Parameter(Mandatory)]$Result)
    return $Result.ExitCode -ne 0 -and [string]$Result.Error -match '(?i)(\b404\b|NoSuchKey|Not[ -]?Found)'
}

function Test-AwsPreconditionFailed {
    param([Parameter(Mandatory)]$Result)
    return $Result.ExitCode -ne 0 -and [string]$Result.Error -match '(?i)(\b412\b|PreconditionFailed|ConditionalRequestConflict)'
}

function Get-S3ObjectMetadataValue {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{1,64}$')][string]$Name
    )
    # Avoid dereferencing an absent JSON property under StrictMode.  Missing
    # metadata is an invalid protected representation, not a scripting error.
    $metadataProperty = $Object.PSObject.Properties['Metadata']
    if ($null -eq $metadataProperty -or $null -eq $metadataProperty.Value) { return '' }
    $valueProperty = $metadataProperty.Value.PSObject.Properties[$Name]
    if ($null -eq $valueProperty -or $null -eq $valueProperty.Value) { return '' }
    return [string]$valueProperty.Value
}

function New-SourceStageReceiptBytes {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][byte[]]$ManifestBytes,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{43}$')][string]$StageId
    )
    if ($ManifestBytes.Length -le 0 -or $ManifestBytes.Length -gt 1MB) {
        throw 'The staged source manifest is outside its bounded size.'
    }
    $receipt = [ordered]@{
        schemaVersion = $stageReceiptSchemaVersion
        kind = $stageReceiptKind
        version = $ReleaseVersion
        stageId = $StageId
        manifestSha256 = Get-Sha256Hex -Bytes $ManifestBytes
        manifestBase64 = [Convert]::ToBase64String($ManifestBytes)
    }
    return ,([Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 4)))
}

function ConvertFrom-SourceStageReceiptBytes {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][byte[]]$ReceiptBytes
    )
    if ($ReceiptBytes.Length -le 0 -or $ReceiptBytes.Length -gt 2MB) {
        throw 'The staged source receipt is unsafe or outside its bounded size.'
    }
    try { $receipt = [Text.Encoding]::UTF8.GetString($ReceiptBytes) | ConvertFrom-Json }
    catch { throw 'The staged source receipt is malformed.' }
    $schema = 0
    try { $schema = [int]$receipt.schemaVersion } catch { throw 'The staged source receipt has an invalid schema version.' }
    if ($null -eq $receipt -or $schema -notin @(1, 2, $stageReceiptSchemaVersion) -or
        [string]$receipt.kind -cne $stageReceiptKind -or [string]$receipt.version -cne $ReleaseVersion -or
        [string]$receipt.manifestSha256 -notmatch '^[a-f0-9]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$receipt.manifestBase64)) {
        throw 'The staged source receipt has an unsupported shape.'
    }
    $stageId = if ($schema -eq $stageReceiptSchemaVersion) { [string]$receipt.stageId } else { '' }
    if ($schema -eq $stageReceiptSchemaVersion -and $stageId -notmatch '^[A-Za-z0-9_-]{43}$') {
        throw 'The staged source receipt has an invalid immutable stage identity.'
    }
    try { [byte[]]$manifestBytes = [Convert]::FromBase64String([string]$receipt.manifestBase64) }
    catch { throw 'The staged source receipt manifest is not valid base64.' }
    try {
        $manifestHash = Get-Sha256Hex -Bytes $manifestBytes
        if ($manifestBytes.Length -le 0 -or $manifestBytes.Length -gt 1MB -or
            -not $manifestHash.Equals([string]$receipt.manifestSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The staged source receipt manifest hash is invalid.'
        }
        try { $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json }
        catch { throw 'The staged source receipt manifest is malformed.' }
        return [pscustomobject]@{
            Manifest = $manifest
            ManifestBytes = $manifestBytes
            ManifestSha256 = $manifestHash
            StageId = $stageId
            IsLegacy = $schema -ne $stageReceiptSchemaVersion
            ReceiptBytes = $ReceiptBytes
            ReceiptSha256 = Get-Sha256Hex -Bytes $ReceiptBytes
        }
    } catch {
        [Array]::Clear($manifestBytes, 0, $manifestBytes.Length)
        throw
    }
}

function Write-SourceStageReceipt {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][byte[]]$ReceiptBytes
    )
    $null = ConvertFrom-SourceStageReceiptBytes -ReleaseVersion $ReleaseVersion -ReceiptBytes $ReceiptBytes
    $path = Get-SourceStageReceiptPath -ReleaseVersion $ReleaseVersion
    if (Test-Path -LiteralPath $path) {
        $existing = Get-Item -LiteralPath $path -Force
        if (($existing.Attributes -band [IO.FileAttributes]::Directory) -or
            ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The existing source stage receipt is unsafe.'
        }
    }
    $temporary = "$path.$([Guid]::NewGuid().ToString('N')).new"
    try {
        [IO.File]::WriteAllBytes($temporary, $ReceiptBytes)
        Move-FileAtomically -Source $temporary -Destination $path
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
    return $path
}

function Read-LocalSourceStageReceipt {
    param([Parameter(Mandatory)][string]$ReleaseVersion)
    $path = Get-SourceStageReceiptPath -ReleaseVersion $ReleaseVersion
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -le 0 -or $item.Length -gt 2MB) {
        throw 'The staged source receipt is unsafe or outside its bounded size.'
    }
    [byte[]]$receiptBytes = [IO.File]::ReadAllBytes($path)
    return ConvertFrom-SourceStageReceiptBytes -ReleaseVersion $ReleaseVersion -ReceiptBytes $receiptBytes
}

function New-SourceStageId {
    $bytes = [byte[]]::new(32)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
        return ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'))
    } finally {
        $rng.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Read-SourceStageManifest {
    param([Parameter(Mandatory)][string]$ReleaseVersion)
    # Kept for manifest-level callers; remote recovery is resolved from the
    # canonical ZIP's stage metadata, not from a mutable per-version pointer.
    $local = Read-LocalSourceStageReceipt -ReleaseVersion $ReleaseVersion
    if ($null -ne $local) { return $local.Manifest }
    throw "The staged source receipt is missing locally: $ReleaseVersion. Run -StageOnly first."
}

function Read-DurableSourceStageReceipt {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{43}$')][string]$StageId
    )
    $key = Get-SourceStageReceiptObjectKey -ReleaseVersion $ReleaseVersion -StageId $StageId
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $headResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket,
            '--key', $key, '--checksum-mode', 'ENABLED', '--output', 'json')
    } finally { $ErrorActionPreference = $savedPreference }
    if ($headResult.ExitCode -ne 0) {
        if (Test-AwsObjectNotFound -Result $headResult) { return $null }
        throw "Could not inspect the durable source stage receipt: $($headResult.Error.Trim())"
    }
    $head = $headResult.Output | ConvertFrom-Json
    if ([long]$head.ContentLength -le 0 -or [long]$head.ContentLength -gt 2MB -or
        (Get-S3ObjectMetadataValue -Object $head -Name 'sha256') -notmatch '^[a-f0-9]{64}$' -or
        [string]$head.ContentType -ne 'application/json' -or [string]$head.CacheControl -ne 'no-store' -or
        [string]$head.ServerSideEncryption -ne 'AES256' -or [string]$head.ChecksumSHA256 -notmatch '^[A-Za-z0-9+/]{43}=$' -or
        (Get-S3ObjectMetadataValue -Object $head -Name 'stage') -cne $StageId) {
        throw 'The durable source stage receipt metadata is not an exact protected representation.'
    }
    $temporary = Join-Path $script:AwsScratchDirectory ("stage-receipt-$([Guid]::NewGuid().ToString('N')).json")
    try {
        $getResult = Invoke-AwsCli -Arguments @('s3api', 'get-object', '--bucket', $bucket, '--key', $key,
            '--checksum-mode', 'ENABLED', '--output', 'json', $temporary)
        if ($getResult.ExitCode -ne 0) { throw "Could not read the durable source stage receipt: $($getResult.Error.Trim())" }
        $item = Get-Item -LiteralPath $temporary -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne [long]$head.ContentLength) {
            throw 'The durable source stage receipt download was not an exact regular file.'
        }
        [byte[]]$receiptBytes = [IO.File]::ReadAllBytes($temporary)
        $receiptHash = Get-Sha256Hex -Bytes $receiptBytes
        $expectedChecksum = [Convert]::ToBase64String([Convert]::FromHexString($receiptHash))
        if (-not $receiptHash.Equals((Get-S3ObjectMetadataValue -Object $head -Name 'sha256'), [StringComparison]::OrdinalIgnoreCase) -or
            -not $expectedChecksum.Equals([string]$head.ChecksumSHA256, [StringComparison]::Ordinal)) {
            throw 'The durable source stage receipt content did not match its immutable S3 metadata.'
        }
        $parsed = ConvertFrom-SourceStageReceiptBytes -ReleaseVersion $ReleaseVersion -ReceiptBytes $receiptBytes
        if ($parsed.IsLegacy -or $parsed.StageId -cne $StageId) {
            throw 'The durable source stage receipt does not bind to the requested immutable stage identity.'
        }
        return $parsed
    } finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function Get-StagedSourceArchiveHead {
    param([Parameter(Mandatory)][string]$ReleaseVersion)
    $key = "opticon/releases/$ReleaseVersion/opticon-source-$ReleaseVersion.zip"
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $head = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket, '--key', $key,
            '--checksum-mode', 'ENABLED', '--output', 'json')
    } finally { $ErrorActionPreference = $savedPreference }
    if ($head.ExitCode -eq 0) { return ($head.Output | ConvertFrom-Json) }
    if (Test-AwsObjectNotFound -Result $head) { return $null }
    throw "Could not determine whether the staged immutable source archive exists: $($head.Error.Trim())"
}

function Test-StagedSourceArchiveExists {
    param([Parameter(Mandatory)][string]$ReleaseVersion)
    return $null -ne (Get-StagedSourceArchiveHead -ReleaseVersion $ReleaseVersion)
}

function Get-SourceStageForExistingArchive {
    param([Parameter(Mandatory)][string]$ReleaseVersion)
    $head = Get-StagedSourceArchiveHead -ReleaseVersion $ReleaseVersion
    if ($null -eq $head) { return $null }
    $stageId = Get-S3ObjectMetadataValue -Object $head -Name 'stage'
    if ($stageId -notmatch '^[A-Za-z0-9_-]{43}$') {
        return [pscustomobject]@{ Head = $head; StageId = ''; Receipt = $null }
    }
    $receipt = Read-DurableSourceStageReceipt -ReleaseVersion $ReleaseVersion -StageId $stageId
    return [pscustomobject]@{ Head = $head; StageId = $stageId; Receipt = $receipt }
}

function Ensure-DurableSourceStageReceipt {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{43}$')][string]$StageId,
        [Parameter(Mandatory)][byte[]]$ReceiptBytes
    )
    $local = ConvertFrom-SourceStageReceiptBytes -ReleaseVersion $ReleaseVersion -ReceiptBytes $ReceiptBytes
    if ($local.IsLegacy -or $local.StageId -cne $StageId) {
        throw 'The local source stage receipt does not bind to the requested immutable stage identity.'
    }
    $existing = Read-DurableSourceStageReceipt -ReleaseVersion $ReleaseVersion -StageId $StageId
    if ($null -ne $existing) {
        if ($existing.ReceiptSha256.Equals($local.ReceiptSha256, [StringComparison]::OrdinalIgnoreCase)) {
            return Get-SourceStageReceiptObjectKey -ReleaseVersion $ReleaseVersion -StageId $StageId
        }
        throw "Refusing to replace the immutable source stage receipt for $ReleaseVersion/$StageId."
    }
    $onDisk = Read-LocalSourceStageReceipt -ReleaseVersion $ReleaseVersion
    if ($null -eq $onDisk -or
        -not $onDisk.ReceiptSha256.Equals($local.ReceiptSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The local source stage receipt disappeared or changed before it could be journaled to S3.'
    }
    $key = Get-SourceStageReceiptObjectKey -ReleaseVersion $ReleaseVersion -StageId $StageId
    $uploadPath = Join-Path $script:AwsScratchDirectory ("stage-receipt-upload-$([Guid]::NewGuid().ToString('N')).json")
    try {
        # Upload a private immutable byte snapshot, not the mutable copy in
        # ArtifactDirectory.  The latter was still written first as the local
        # crash journal, but cannot race the exact S3 receipt payload.
        [IO.File]::WriteAllBytes($uploadPath, $ReceiptBytes)
        $putArguments = @('s3api', 'put-object', '--bucket', $bucket, '--key', $key, '--body', $uploadPath,
            '--content-type', 'application/json', '--cache-control', 'no-store', '--server-side-encryption', 'AES256',
            '--checksum-algorithm', 'SHA256', '--metadata', "sha256=$($local.ReceiptSha256),stage=$StageId",
            '--if-none-match', '*', '--output', 'json')
        $putResult = Invoke-AwsCli -Arguments $putArguments
        if ($putResult.ExitCode -ne 0) {
            if (Test-AwsPreconditionFailed -Result $putResult) {
                $winner = Read-DurableSourceStageReceipt -ReleaseVersion $ReleaseVersion -StageId $StageId
                if ($null -ne $winner -and $winner.ReceiptSha256.Equals($local.ReceiptSha256, [StringComparison]::OrdinalIgnoreCase)) {
                    return $key
                }
                throw "Another publisher claimed the immutable source stage receipt for $ReleaseVersion/$StageId."
            }
            throw "Conditional durable source stage receipt publication failed: $($putResult.Error.Trim())"
        }
    } finally {
        Remove-Item -LiteralPath $uploadPath -Force -ErrorAction SilentlyContinue
    }
    $verified = Read-DurableSourceStageReceipt -ReleaseVersion $ReleaseVersion -StageId $StageId
    if ($null -eq $verified -or -not $verified.ManifestSha256.Equals($local.ManifestSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not $verified.ReceiptSha256.Equals($local.ReceiptSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The durable source stage receipt could not be verified after upload.'
    }
    return $key
}

function Test-LocalArtifactMatchesRecord {
    param([Parameter(Mandatory)]$Artifact)
    $path = Get-LocalArtifactDestinationPath -FileName ([string]$Artifact.file)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne [long]$Artifact.size) { return $false }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.Equals([string]$Artifact.sha256, [StringComparison]::OrdinalIgnoreCase)
}

function Test-LocalSourceStageArchiveAvailable {
    param([Parameter(Mandatory)]$Receipt)
    try {
        if ([int]$Receipt.Manifest.schemaVersion -ne 2) { return $false }
        $artifacts = @($Receipt.Manifest.artifacts)
        $sources = @($artifacts | Where-Object { [string]$_.product -ceq 'OpticonSource' })
        if ($artifacts.Count -ne 1 -or $sources.Count -ne 1) {
            return $false
        }
        return Test-LocalArtifactMatchesRecord -Artifact $sources[0]
    } catch { return $false }
}

function Restore-ImmutableSourceArchiveFromS3 {
    param(
        [Parameter(Mandatory)]$Artifact,
        [ValidatePattern('^$|^[A-Za-z0-9_-]{43}$')][string]$StageId = ''
    )
    $file = [string]$Artifact.file
    $destination = Get-LocalArtifactDestinationPath -FileName $file
    $key = "opticon/releases/$([string]$Artifact.version)/$file"
    $expectedChecksum = [Convert]::ToBase64String([Convert]::FromHexString([string]$Artifact.sha256))
    $headResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket, '--key', $key,
        '--checksum-mode', 'ENABLED', '--output', 'json')
    if ($headResult.ExitCode -ne 0) { throw "The exact staged immutable source archive is unavailable in S3: s3://$bucket/$key" }
    $head = $headResult.Output | ConvertFrom-Json
    if ([long]$head.ContentLength -ne [long]$Artifact.size -or
        -not (Get-S3ObjectMetadataValue -Object $head -Name 'sha256').Equals([string]$Artifact.sha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$head.ChecksumSHA256).Equals($expectedChecksum, [StringComparison]::Ordinal) -or
        [string]$head.ContentType -ne 'application/zip' -or
        [string]$head.CacheControl -ne 'public, max-age=31536000, immutable' -or
        [string]$head.ServerSideEncryption -ne 'AES256' -or
        (-not [string]::IsNullOrWhiteSpace($StageId) -and (Get-S3ObjectMetadataValue -Object $head -Name 'stage') -cne $StageId)) {
        throw "The exact staged immutable source archive metadata is invalid: s3://$bucket/$key"
    }
    $temporary = Join-Path $script:AwsScratchDirectory ("source-archive-$([Guid]::NewGuid().ToString('N')).zip")
    try {
        $getResult = Invoke-AwsCli -Arguments @('s3api', 'get-object', '--bucket', $bucket, '--key', $key,
            '--checksum-mode', 'ENABLED', '--output', 'json', $temporary)
        if ($getResult.ExitCode -ne 0) { throw "Could not restore the exact staged source archive: $($getResult.Error.Trim())" }
        $item = Get-Item -LiteralPath $temporary -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne [long]$Artifact.size -or
            -not (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.Equals([string]$Artifact.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The restored source archive did not match the durable stage receipt.'
        }
        Move-FileAtomically -Source $temporary -Destination $destination
    } finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
    return $destination
}

function Ensure-SourceLauncherSidecar {
    param([Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)]$Artifact)
    $sidecarFile = "opticon-source-launcher-$([string]$Artifact.version).exe"
    $destination = Get-LocalArtifactDestinationPath -FileName $sidecarFile
    $matches = $false
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $item = Get-Item -LiteralPath $destination -Force
        $matches = -not (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $item.Length -ne [long]$Artifact.sourceLauncherSize) -and
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.Equals([string]$Artifact.sourceLauncherSha256, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($matches) { return $destination }
    # The archive has already passed its signed inner-manifest verification
    # before this function is used.  Extract the exact embedded launcher to a
    # same-directory temporary file, verify it, then atomically promote it.
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $temporary = "$destination.$([Guid]::NewGuid().ToString('N')).new"
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq 'OpticonSourceLauncher.exe' })
        if ($entries.Count -ne 1 -or [long]$entries[0].Length -ne [long]$Artifact.sourceLauncherSize) {
            throw 'The signed source archive does not contain its declared source launcher.'
        }
        [IO.File]::WriteAllBytes($temporary, (Read-ZipEntryBounded -Entry $entries[0] -MaximumBytes 128MB))
        if (-not (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.Equals(
                [string]$Artifact.sourceLauncherSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The extracted source launcher did not match the durable stage receipt.'
        }
        Assert-ProductSignature -Path $temporary
        Move-FileAtomically -Source $temporary -Destination $destination
    } finally {
        $zip.Dispose()
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
    return $destination
}

function Ensure-StagedSourceArchiveLocally {
    param(
        [Parameter(Mandatory)]$Artifact,
        [ValidatePattern('^$|^[A-Za-z0-9_-]{43}$')][string]$StageId = ''
    )
    if ([string]$Artifact.product -cne 'OpticonSource' -or [string]$Artifact.file -cne "opticon-source-$([string]$Artifact.version).zip") {
        throw 'The durable stage receipt does not name the canonical source archive.'
    }
    if (-not (Test-LocalArtifactMatchesRecord -Artifact $Artifact)) {
        $null = Restore-ImmutableSourceArchiveFromS3 -Artifact $Artifact -StageId $StageId
    }
    $archivePath = Get-LocalArtifactPath -FileName ([string]$Artifact.file)
    # Verify the downloaded bytes and all signed inner content before allowing
    # them to repair the Fly sidecar.
    Assert-OpticonSourceArchive -Path $archivePath -Record $Artifact
    $null = Ensure-SourceLauncherSidecar -ArchivePath $archivePath -Artifact $Artifact
    Assert-OpticonSourceArchive -Path $archivePath -Record $Artifact -RequireSourceLauncher
    return $archivePath
}

function New-PrivatePublisherDirectory {
    param([Parameter(Mandatory)][string]$Prefix)
    if ($Prefix -notmatch '^[A-Za-z0-9-]{1,32}$') { throw 'The private publisher directory prefix is invalid.' }
    $path = Join-Path $ArtifactDirectory ('.' + $Prefix + '-' + [Guid]::NewGuid().ToString('N'))
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $sid = $identity.User
        if ($null -eq $sid) { throw 'The publisher could not resolve its Windows account SID.' }
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetOwner($sid)
        $security.SetAccessRuleProtection($true, $false)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,
            [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
        # DirectoryInfo.Create(DirectorySecurity) is exposed as a static
        # FileSystemAclExtensions method on modern .NET/PowerShell, not as an
        # instance overload. This preserves atomic create-with-ACL semantics.
        [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($path), $security)
    } finally { $identity.Dispose() }
    if (-not (Test-Path -LiteralPath $path -PathType Container) -or
        ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The private publisher directory could not be created safely.'
    }
    return $path
}

function Assert-ProductSignature {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ExpectedThumbprint = $ProductCertificateThumbprint
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $allowedStatus = if ($SigningProfile -eq 'Production') {
        @([Management.Automation.SignatureStatus]::Valid)
    } else {
        @([Management.Automation.SignatureStatus]::Valid, [Management.Automation.SignatureStatus]::UnknownError)
    }
    if ($signature.Status -notin $allowedStatus -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Thumbprint.Equals($ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "$SigningProfile Authenticode verification, publisher pinning, or RFC3161 timestamp validation failed for $Path."
    }
    $eku = $signature.SignerCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if ($null -eq $eku -or -not (([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$eku).EnhancedKeyUsages |
            Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw "The verified production signer for $Path lacks the Code Signing EKU."
    }
    $timestampEku = $signature.TimeStamperCertificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if ($null -eq $timestampEku -or -not (([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$timestampEku).EnhancedKeyUsages |
            Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.8' })) {
        throw "The RFC3161 timestamp for $Path lacks the Time Stamping EKU."
    }
}

function Read-ZipEntryBounded {
    param([Parameter(Mandatory)]$Entry, [Parameter(Mandatory)][long]$MaximumBytes)
    if ([long]$Entry.Length -le 0 -or [long]$Entry.Length -gt $MaximumBytes) {
        throw "ZIP entry $($Entry.FullName) has an invalid declared size."
    }
    $input = $Entry.Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(65536)
        $total = 0L
        while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $read
            if ($total -gt $MaximumBytes -or $total -gt [long]$Entry.Length) {
                throw "ZIP entry $($Entry.FullName) exceeded its signed bound."
            }
            $memory.Write($buffer, 0, $read)
        }
        if ($total -ne [long]$Entry.Length) { throw "ZIP entry $($Entry.FullName) ended at the wrong size." }
        return $memory.ToArray()
    } finally { $memory.Dispose(); $input.Dispose() }
}

function Test-CompositeSha256Checksum {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value -notmatch '^(?<digest>[A-Za-z0-9+/]{43}=)-(?<parts>[1-9][0-9]*)$' -or [int]$Matches.parts -lt 2) {
        return $false
    }
    try { return [Convert]::FromBase64String($Matches.digest).Length -eq 32 } catch { return $false }
}

function Get-NextReleaseVersion {
    param([switch]$SourceOnly)
    $listResult = Invoke-AwsCli -Arguments @('s3api', 'list-objects-v2', '--bucket', $bucket,
        '--prefix', 'opticon/releases/', '--query', 'Contents[].Key', '--output', 'json')
    if ($listResult.ExitCode -ne 0) { throw "Could not list published Opticon releases: $($listResult.Error.Trim())" }
    $keys = @($listResult.Output | ConvertFrom-Json)
    $versions = @($keys | ForEach-Object {
        if ($_ -match '^opticon/releases/(?<version>[0-9]+\.[0-9]+\.[0-9]+)/opticon-bundle-.+-(managed|controller)-win-x64\.zip$' -or
            ($SourceOnly -and $_ -match '^opticon/releases/(?<version>[0-9]+\.[0-9]+\.[0-9]+)/opticon-source-\k<version>\.zip$')) {
            try { [version]$Matches.version } catch { $null }
        }
    } | Where-Object { $_ })
    if (Test-Path -LiteralPath $manifestPath) {
        $versions += @((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).artifacts |
            Where-Object { $_.product -eq "OpticonBundle" -or ($SourceOnly -and $_.product -eq "OpticonSource") } |
            ForEach-Object { try { [version]$_.version } catch { $null } } |
            Where-Object { $_ })
    }
    if ($versions.Count -eq 0) {
        if ($SourceOnly) { return '1.2.0' }
        return '1.0.0'
    }
    $highest = $versions | Sort-Object -Descending | Select-Object -First 1
    if ($SourceOnly -and $highest -lt [version]'1.2.0') { return '1.2.0' }
    return "$($highest.Major).$($highest.Minor).$($highest.Build + 1)"
}

function Invoke-Aws([string[]]$Arguments) {
    $result = Invoke-AwsCli -Arguments $Arguments
    if ($result.ExitCode -ne 0) { throw "AWS CLI command failed: aws $($Arguments -join ' '): $($result.Error.Trim())" }
    if (-not [string]::IsNullOrWhiteSpace($result.Output)) { Write-Host $result.Output.TrimEnd() }
}

function Invoke-CloudFrontVerification {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$ExpectedHash,
        [Parameter(Mandatory)][long]$ExpectedSize,
        [switch]$FullStream
    )
    if ($ExpectedSize -le 0 -or $ExpectedHash -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'CloudFront verification requires an exact positive size and SHA-256.'
    }
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $handler.CheckCertificateRevocationList = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = if ($FullStream) { [TimeSpan]::FromMinutes(15) } else { [TimeSpan]::FromSeconds(45) }
    try {
        $head = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, $Url)
        $null = $head.Headers.TryAddWithoutValidation('Accept-Encoding', 'identity')
        $headResult = $client.SendAsync($head).GetAwaiter().GetResult()
        try {
            if (-not $headResult.IsSuccessStatusCode -or $headResult.Content.Headers.ContentLength -ne $ExpectedSize -or
                $headResult.Content.Headers.ContentEncoding.Count -ne 0) {
                throw "CloudFront HEAD did not return the expected immutable object metadata."
            }
        } finally { $headResult.Dispose(); $head.Dispose() }

        $range = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
        $range.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new(0, 1023)
        $null = $range.Headers.TryAddWithoutValidation('Accept-Encoding', 'identity')
        $rangeResult = $client.SendAsync($range, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $contentRange = $rangeResult.Content.Headers.ContentRange
            if ([int]$rangeResult.StatusCode -ne 206 -or $null -eq $contentRange -or $contentRange.From -ne 0 -or
                $contentRange.To -ne 1023 -or $contentRange.Length -ne $ExpectedSize -or
                $rangeResult.Content.Headers.ContentEncoding.Count -ne 0) {
                throw "CloudFront did not return the expected byte range."
            }
        } finally { $rangeResult.Dispose(); $range.Dispose() }

        if ($FullStream) {
            $full = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
            $null = $full.Headers.TryAddWithoutValidation('Accept-Encoding', 'identity')
            $response = $client.SendAsync($full, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -ne $ExpectedSize -or
                    $response.Content.Headers.ContentEncoding.Count -ne 0) { throw "CloudFront full-object GET failed." }
                $sha = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try {
                    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    try {
                        $buffer = [byte[]]::new(131072)
                        $total = 0L
                        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $total += [long]$read
                            if ($total -gt $ExpectedSize) { throw 'CloudFront returned more bytes than the immutable object size.' }
                            $sha.AppendData($buffer, 0, $read)
                        }
                        if ($total -ne $ExpectedSize) { throw 'CloudFront returned fewer bytes than the immutable object size.' }
                        $actual = ([BitConverter]::ToString($sha.GetHashAndReset())).Replace('-', '').ToLowerInvariant()
                    }
                    finally { $stream.Dispose() }
                } finally { $sha.Dispose() }
                if ($actual -ne $ExpectedHash.ToLowerInvariant()) { throw "CloudFront streamed bytes did not match the local SHA-256." }
            } finally { $response.Dispose(); $full.Dispose() }
        }
    } finally { $client.Dispose(); $handler.Dispose() }
}

function Read-PublicManifestBounded {
    $uri = [Uri]::new($controlOriginUri, '/opticon/artifacts/v1/manifest.json')
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
    $handler.CheckCertificateRevocationList = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(45)
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $uri)
        $null = $request.Headers.TryAddWithoutValidation('Accept-Encoding', 'identity')
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $length = $response.Content.Headers.ContentLength
            if ([int]$response.StatusCode -ne 200 -or $null -eq $length -or $length -le 0 -or $length -gt 1MB -or
                $response.Content.Headers.ContentEncoding.Count -ne 0) {
                throw 'The public gateway manifest response is not an exact bounded identity representation.'
            }
            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $memory = [IO.MemoryStream]::new([int]$length)
            try {
                $buffer = [byte[]]::new(65536)
                $total = 0L
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $total += [long]$read
                    if ($total -gt $length -or $total -gt 1MB) { throw 'The public gateway manifest exceeded its declared bound.' }
                    $memory.Write($buffer, 0, $read)
                }
                if ($total -ne $length) { throw 'The public gateway manifest ended at the wrong size.' }
                try { return [Text.Encoding]::UTF8.GetString($memory.ToArray()) | ConvertFrom-Json }
                catch { throw 'The public gateway manifest is malformed.' }
            } finally { $memory.Dispose(); $input.Dispose() }
        } finally { $response.Dispose(); $request.Dispose() }
    } finally { $client.Dispose(); $handler.Dispose() }
}

function Get-OpticonAdminSecret {
    $configPath = Join-Path $env:LOCALAPPDATA "Taildesk\Admin\admin.json"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "The local Opticon admin configuration is unavailable." }
    $protected = [string](Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json).headscaleApiKeyProtected
    if ([string]::IsNullOrWhiteSpace($protected)) { throw "The local Opticon admin HMAC credential is unavailable." }
    Add-Type -AssemblyName System.Security
    $encrypted = [Convert]::FromBase64String($protected)
    try {
        $clear = [Security.Cryptography.ProtectedData]::Unprotect($encrypted, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { return [Text.Encoding]::UTF8.GetString($clear) }
        finally { [Array]::Clear($clear, 0, $clear.Length) }
    } finally { [Array]::Clear($encrypted, 0, $encrypted.Length) }
}

function Assert-PublisherReadiness {
    # This is intentionally a non-mutating proof of the local identities and
    # external control planes used later by the publisher. It runs after the
    # AWS identity/stack checks below and before an invitation-removal prompt.
    $windowsKitsRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) 'Windows Kits\10\bin'
    $fullSignTool = [IO.Path]::GetFullPath($SignToolPath)
    if (-not (Test-Path -LiteralPath $fullSignTool -PathType Leaf) -or
        -not (Test-Path -LiteralPath $windowsKitsRoot -PathType Container) -or
        -not $fullSignTool.StartsWith([IO.Path]::GetFullPath($windowsKitsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $fullSignTool -Leaf) -ne 'signtool.exe' -or
        (Split-Path (Split-Path $fullSignTool -Parent) -Leaf) -ne 'x64') {
        throw 'SignToolPath must name the fixed x64 signtool.exe under Program Files (x86)\Windows Kits\10\bin\<version>\x64.'
    }
    $dotnet = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw 'A stable .NET 10 SDK is required to publish an Opticon source release.'
    }
    $sdkText = & $dotnet --list-sdks 2>&1
    $dotnetExitCode = $LASTEXITCODE
    if ($dotnetExitCode -ne 0 -or -not (@($sdkText) -match '^10\.[0-9]+\.[0-9]+\s')) {
        throw 'A stable .NET SDK matching 10.*.* is required to publish an Opticon source release.'
    }
    foreach ($item in @(
            @{ Thumbprint = $SourceReleaseCertificateThumbprint; Purpose = 'source-release signing' },
            @{ Thumbprint = $ProductCertificateThumbprint; Purpose = "$SigningProfile Authenticode signing" })) {
        $certificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint.ToUpperInvariant() -eq $item.Thumbprint -and $_.HasPrivateKey } |
            Select-Object -First 1
        if ($null -eq $certificate) {
            throw "The $($item.Purpose) certificate $($item.Thumbprint) with an accessible private key is unavailable in CurrentUser\\My."
        }
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if ($null -eq $rsa) {
            throw "The $($item.Purpose) certificate $($item.Thumbprint) does not expose an RSA private key."
        }
        try {
            if ($rsa.KeySize -lt 2048) { throw "The $($item.Purpose) certificate has an unsafe RSA key size." }
            $probe = $rsa.SignData([byte[]]::new(32), [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            if ($probe.Length -eq 0) { throw "The $($item.Purpose) private key could not sign a readiness probe." }
        } finally { $rsa.Dispose(); $certificate.Dispose() }
    }
    $secretText = Get-OpticonAdminSecret
    if ([string]::IsNullOrWhiteSpace($secretText) -or $secretText.Length -lt 32) {
        throw 'The local Opticon admin HMAC credential is unavailable or too short.'
    }
}

function Publish-ManifestAtomically([byte[]]$Body) {
    $secretText = Get-OpticonAdminSecret
    $secret = [Text.Encoding]::UTF8.GetBytes($secretText)
    $secretText = $null
    try {
        $uri = [Uri]::new($controlOriginUri, "/opticon/v1/releases/manifest")
        $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
        $nonceBytes = [byte[]]::new(18)
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($nonceBytes) } finally { $rng.Dispose() }
        $nonce = [Convert]::ToBase64String($nonceBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $bodyHash = ([BitConverter]::ToString($sha.ComputeHash($Body))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        $canonical = "PUT`n$($uri.PathAndQuery)`n$timestamp`n$nonce`n$bodyHash"
        $hmac = [Security.Cryptography.HMACSHA256]::new($secret)
        try { $signature = ([BitConverter]::ToString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() }
        finally { $hmac.Dispose() }

        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.UseProxy = $false
        $handler.AllowAutoRedirect = $false
        $handler.CheckCertificateRevocationList = $true
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(45)
        try {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, $uri)
            $request.Content = [System.Net.Http.ByteArrayContent]::new($Body)
            $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/json")
            $request.Headers.Add("X-Opticon-Key-Id", "primary")
            $request.Headers.Add("X-Opticon-Timestamp", $timestamp)
            $request.Headers.Add("X-Opticon-Nonce", $nonce)
            $request.Headers.Add("X-Opticon-Content-SHA256", $bodyHash)
            $request.Headers.Add("X-Opticon-Signature", $signature)
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode) {
                    $detail = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    throw "Atomic Fly manifest publication failed ($([int]$response.StatusCode)): $detail"
                }
            } finally { $response.Dispose(); $request.Dispose() }
        } finally { $client.Dispose(); $handler.Dispose() }
    } finally { [Array]::Clear($secret, 0, $secret.Length) }
}

function Assert-OpticonSourceArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Record,
        [switch]$RequireSourceLauncher
    )
    Assert-ProductionArtifactTrust -Artifact $Record
    if ([long]$Record.size -lt 1024 -or [long]$Record.size -gt 256MB -or
        (Get-Item -LiteralPath $Path).Length -ne [long]$Record.size -or
        [string]$Record.sdkVersion -ne '10.*.*' -or [string]$Record.runtimeVersion -ne '10.0.10' -or
        [string]$Record.sourceManifestKeyId -ne $SourceReleaseCertificateThumbprint -or
        [string]$Record.productSignerThumbprint -ne $ProductCertificateThumbprint -or
        @($Record.targetRuntimes).Count -ne 2 -or [string]$Record.targetRuntimes[0] -ne 'win-x64' -or
        [string]$Record.targetRuntimes[1] -ne 'win-arm64' -or [string]$Record.sourceManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'The source artifact does not carry the exact supported build pins.'
    }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($zip.Entries.Count -lt 3 -or $zip.Entries.Count -gt 4096) {
            throw 'The source archive entry count is outside the runtime limit.'
        }
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name.Contains(':') -or
                $name.EndsWith('/') -or $name.Split('/') -contains '..' -or $entries.ContainsKey($name.ToLowerInvariant())) {
                throw "The source archive contains an unsafe, directory, or duplicate entry: $name"
            }
            $entries[$name.ToLowerInvariant()] = $entry
        }
        if (-not $entries.ContainsKey('source-manifest.json') -or -not $entries.ContainsKey('source-manifest.sig')) {
            throw 'The source archive lacks its signed inner manifest.'
        }
        $manifestEntry = $entries['source-manifest.json']
        $manifestBytes = Read-ZipEntryBounded -Entry $manifestEntry -MaximumBytes 1MB
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $manifestHash = ([BitConverter]::ToString($sha.ComputeHash($manifestBytes))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        if ($manifestHash -ne [string]$Record.sourceManifestSha256) { throw 'The source inner-manifest hash does not match the outer artifact record.' }
        $signatureEntry = $entries['source-manifest.sig']
        try { $signature = [Convert]::FromBase64String([Text.Encoding]::UTF8.GetString(
                    (Read-ZipEntryBounded -Entry $signatureEntry -MaximumBytes 16KB)).Trim()) }
        catch { throw 'The source inner-manifest signature is malformed.' }
        $inner = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
        try {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                [Convert]::FromBase64String([string]$inner.sourceReleaseCertificateBase64))
            $productCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                [Convert]::FromBase64String([string]$inner.productSigningCertificateBase64))
        } catch { throw 'The source inner manifest contains malformed public certificates.' }
        if (-not $certificate.Thumbprint.Equals($SourceReleaseCertificateThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
            -not $productCertificate.Thumbprint.Equals($ProductCertificateThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The source inner-manifest public certificates do not match the configured production identities.'
        }
        $script:VerifiedSourceReleaseCertificateRawData = $certificate.RawData.Clone()
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try {
            if (-not $rsa.VerifyData($manifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'The source inner-manifest RSA-PSS signature is invalid.' }
        } finally { $rsa.Dispose(); $certificate.Dispose(); $productCertificate.Dispose() }
        if ([int]$inner.schemaVersion -ne 1 -or [string]$inner.version -ne [string]$Record.version -or
            [string]$inner.signingProfile -cne $SigningProfile -or
            [string]$inner.sourceReleaseKeyId -ne $SourceReleaseCertificateThumbprint -or
            [string]$inner.productSignerThumbprint -ne $ProductCertificateThumbprint -or
            [string]$inner.sdkVersion -ne [string]$Record.sdkVersion -or [string]$inner.runtimeVersion -ne [string]$Record.runtimeVersion -or
            @($inner.targetRuntimes).Count -ne 2 -or [string]$inner.targetRuntimes[0] -ne [string]$Record.targetRuntimes[0] -or
            [string]$inner.targetRuntimes[1] -ne [string]$Record.targetRuntimes[1]) {
            throw 'The source inner-manifest release metadata does not match the outer record.'
        }
        if (-not $entries.ContainsKey('directory.build.props')) {
            throw 'The source archive lacks its production trust configuration.'
        }
        try { $archivedProps = [xml][Text.Encoding]::UTF8.GetString(
                (Read-ZipEntryBounded -Entry $entries['directory.build.props'] -MaximumBytes 1MB)) }
        catch { throw 'The archived Directory.Build.props is malformed.' }
        $props = $archivedProps.Project.PropertyGroup
        if ([string]$props.OpticonSigningProfile -cne $SigningProfile -or
            [string]$props.OpticonSourceReleaseKeyId -ne $SourceReleaseCertificateThumbprint -or
            [string]$props.OpticonSourceReleaseCertificateBase64 -ne [string]$inner.sourceReleaseCertificateBase64 -or
            [string]$props.OpticonProductSignerThumbprint -ne $ProductCertificateThumbprint -or
            [string]$props.OpticonProductSigningCertificateBase64 -ne [string]$inner.productSigningCertificateBase64) {
            throw 'The archived build properties do not preserve the signed production trust identities.'
        }
        $files = @($inner.files)
        if ($files.Count -lt 1 -or $files.Count -gt 4094) {
            throw 'The source inner manifest file count is outside the runtime limit.'
        }
        $declared = @{'source-manifest.json' = $true; 'source-manifest.sig' = $true}
        $expanded = 0L
        foreach ($file in $files) {
            $name = ([string]$file.path).Replace('\', '/')
            $key = $name.ToLowerInvariant()
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name.Split('/') -contains '..' -or
                $declared.ContainsKey($key) -or -not $entries.ContainsKey($key) -or
                [long]$file.size -le 0 -or [long]$file.size -ne [long]$entries[$key].Length -or
                [string]$file.sha256 -notmatch '^[a-f0-9]{64}$') {
                throw "The source inner manifest has an invalid declaration for $name."
            }
            if ([long]$file.size -gt (512MB - $expanded)) { throw 'The source archive expands beyond the runtime limit.' }
            $expanded += [long]$file.size
            $declared[$key] = $true
            $input = $entries[$key].Open()
            $fileSha = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = [byte[]]::new(131072)
                $total = 0L
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $total += [long]$read
                    if ($total -gt [long]$file.size) { throw "Source entry $name exceeded its signed size." }
                    $fileSha.AppendData($buffer, 0, $read)
                }
                if ($total -ne [long]$file.size) { throw "Source entry $name ended at the wrong size." }
                $actual = ([BitConverter]::ToString($fileSha.GetHashAndReset())).Replace('-', '').ToLowerInvariant()
            } finally { $fileSha.Dispose(); $input.Dispose() }
            if ($actual -ne [string]$file.sha256) { throw "The source file hash is invalid for $name." }
        }
        if ($declared.Count -ne $entries.Count) { throw 'The source archive contains undeclared extra files.' }
        if ($RequireSourceLauncher) {
            $launcherKey = 'opticonsourcelauncher.exe'
            $expectedLauncherFile = "opticon-source-launcher-$([string]$Record.version).exe"
            if (-not $entries.ContainsKey($launcherKey) -or -not $declared.ContainsKey($launcherKey) -or
                (Get-ArtifactString $Record 'sourceLauncherFile') -cne $expectedLauncherFile -or
                [long]$Record.sourceLauncherSize -le 0 -or [long]$Record.sourceLauncherSize -gt 128MB -or
                (Get-ArtifactString $Record 'sourceLauncherSha256') -notmatch '^[a-f0-9]{64}$') {
                throw 'The source-only archive lacks its declared fixed source launcher.'
            }
            $launcherRoot = New-PrivatePublisherDirectory -Prefix 'source-launcher-verify'
            try {
                $launcherPath = Join-Path $launcherRoot 'OpticonSourceLauncher.exe'
                [IO.File]::WriteAllBytes($launcherPath, (Read-ZipEntryBounded -Entry $entries[$launcherKey] -MaximumBytes 128MB))
                $sidecarPath = Join-Path (Split-Path -Parent $Path) $expectedLauncherFile
                if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf) -or
                    (Get-Item -LiteralPath $launcherPath).Length -ne [long]$Record.sourceLauncherSize -or
                    (Get-Item -LiteralPath $sidecarPath).Length -ne [long]$Record.sourceLauncherSize -or
                    -not (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash.Equals(
                        [string]$Record.sourceLauncherSha256, [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Get-FileHash -LiteralPath $sidecarPath -Algorithm SHA256).Hash.Equals(
                        [string]$Record.sourceLauncherSha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The Fly-embedded source launcher does not exactly match the signed source archive.'
                }
                Assert-ProductSignature -Path $launcherPath
                Assert-ProductSignature -Path $sidecarPath
            } finally {
                if (Test-Path -LiteralPath $launcherRoot) { Remove-Item -LiteralPath $launcherRoot -Recurse -Force -ErrorAction SilentlyContinue }
            }
        }
    } finally { $zip.Dispose() }
}

function Assert-OpticonBundleArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Record
    )
    Assert-ProductionArtifactTrust -Artifact $Record
    if ([long]$Record.size -lt 1024 -or [long]$Record.size -gt 1GB -or
        (Get-Item -LiteralPath $Path).Length -ne [long]$Record.size) {
        throw 'The outer bundle size is outside the runtime limit.'
    }
    if ($null -eq $script:VerifiedSourceReleaseCertificateRawData) {
        throw 'The source-release public certificate was not verified before the bundle.'
    }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    $verifyRoot = New-PrivatePublisherDirectory -Prefix 'bundle-verify'
    try {
        if ($zip.Entries.Count -lt 3 -or $zip.Entries.Count -gt 4096) {
            throw 'The bundle archive entry count is outside the runtime limit.'
        }
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name.Contains(':') -or
                $name.EndsWith('/') -or $name.Split('/') -contains '..' -or $entries.ContainsKey($name.ToLowerInvariant())) {
                throw "The bundle contains an unsafe, directory, or duplicate entry: $name"
            }
            $entries[$name.ToLowerInvariant()] = $entry
        }
        if (-not $entries.ContainsKey('release-manifest.json') -or -not $entries.ContainsKey('release-manifest.sig')) {
            throw 'The bundle lacks its signed inner release manifest.'
        }
        $manifestBytes = Read-ZipEntryBounded -Entry $entries['release-manifest.json'] -MaximumBytes 1MB
        try { $signature = [Convert]::FromBase64String([Text.Encoding]::UTF8.GetString(
                    (Read-ZipEntryBounded -Entry $entries['release-manifest.sig'] -MaximumBytes 16KB)).Trim()) }
        catch { throw 'The bundle release-manifest signature is malformed.' }
        $legacyMigrationSigner = Get-ArtifactString $Record 'legacyMigrationSignerThumbprint'
        $isLegacyMigration = -not [string]::IsNullOrWhiteSpace($legacyMigrationSigner)
        if ($isLegacyMigration) { $null = Assert-LegacyMigrationArtifact -Artifact $Record }
        $certificate = if ($isLegacyMigration) {
            Get-ChildItem Cert:\CurrentUser\My | Where-Object {
                $_.Thumbprint -eq $invitationSigningThumbprint
            } | Select-Object -First 1
        } else {
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                [byte[]]$script:VerifiedSourceReleaseCertificateRawData)
        }
        if ($null -eq $certificate) { throw 'The exact retired invitation public certificate is unavailable for migration verification.' }
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try {
            if (-not $rsa.VerifyData($manifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.RSASignaturePadding]::Pss)) {
                throw 'The bundle release-manifest RSA-PSS signature is invalid.'
            }
        } finally { $rsa.Dispose(); $certificate.Dispose() }
        $inner = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
        $expectedPayloadSigner = if ($isLegacyMigration) { $invitationSigningThumbprint } else { $ProductCertificateThumbprint }
        if ([int]$inner.schemaVersion -ne 1 -or [string]$inner.version -ne [string]$Record.version -or
            [string]$inner.role -ne [string]$Record.role -or [string]$inner.architecture -ne [string]$Record.architecture -or
            [string]$inner.signingProfile -cne $SigningProfile -or
            [string]$inner.sourceReleaseKeyId -ne $SourceReleaseCertificateThumbprint -or
            [string]$inner.productSignerThumbprint -ne $expectedPayloadSigner -or
            [bool]$inner.legacyMigration -ne $isLegacyMigration -or
            [string]$inner.legacyMigrationSignerThumbprint -ne $legacyMigrationSigner) {
            throw 'The signed bundle release identity does not match its outer production record.'
        }
        $files = @($inner.files)
        if ($files.Count -lt 1 -or $files.Count -gt 4094) {
            throw 'The bundle release manifest file count is outside the runtime limit.'
        }
        $declared = @{'release-manifest.json' = $true; 'release-manifest.sig' = $true}
        $expanded = 0L
        foreach ($file in $files) {
            $name = ([string]$file.path).Replace('\', '/')
            $key = $name.ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name.Contains(':') -or
                $name.Split('/') -contains '..' -or $declared.ContainsKey($key) -or -not $entries.ContainsKey($key) -or
                [long]$file.size -le 0 -or [long]$file.size -ne [long]$entries[$key].Length -or
                [string]$file.sha256 -notmatch '^[a-f0-9]{64}$') {
                throw "The signed bundle manifest has an invalid declaration for $name."
            }
            if ([long]$file.size -gt (2GB - $expanded)) { throw 'The signed bundle expands beyond its runtime limit.' }
            $expanded += [long]$file.size
            $declared[$key] = $true
            $destination = Join-Path $verifyRoot ([Guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($name))
            $input = $entries[$key].Open()
            $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = [byte[]]::new(131072)
                $total = 0L
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $total += $read
                    if ($total -gt [long]$file.size) { throw "Bundle entry $name exceeded its signed size." }
                    $hasher.AppendData($buffer, 0, $read)
                    $output.Write($buffer, 0, $read)
                }
                $output.Flush()
                if ($total -ne [long]$file.size) { throw "Bundle entry $name ended at the wrong size." }
                $actualHash = ([BitConverter]::ToString($hasher.GetHashAndReset())).Replace('-', '').ToLowerInvariant()
                if ($actualHash -ne [string]$file.sha256) { throw "Bundle entry $name failed its signed SHA-256." }
            } finally { $hasher.Dispose(); $output.Dispose(); $input.Dispose() }
            if ([IO.Path]::GetExtension($name).Equals('.exe', [StringComparison]::OrdinalIgnoreCase)) {
                if ([string]$file.signerThumbprint -ne $expectedPayloadSigner) {
                    throw "Bundle executable $name has the wrong signed publisher declaration."
                }
                Assert-ProductSignature -Path $destination -ExpectedThumbprint $expectedPayloadSigner
            } elseif (-not [string]::IsNullOrEmpty([string]$file.signerThumbprint)) {
                throw "Non-executable bundle entry $name declares a code signer."
            }
        }
        if ($declared.Count -ne $entries.Count) { throw 'The bundle contains undeclared extra files.' }
    } finally {
        $zip.Dispose()
        if (Test-Path -LiteralPath $verifyRoot) { Remove-Item -LiteralPath $verifyRoot -Recurse -Force }
    }
}

$script:AwsScratchDirectory = New-PrivatePublisherDirectory -Prefix 'publish-work'
$releasePublisherLock = $null
try {
$identityResult = Invoke-AwsCli -Arguments @('sts', 'get-caller-identity', '--output', 'json')
if ($identityResult.ExitCode -ne 0) { throw "AWS identity lookup failed: $($identityResult.Error.Trim())" }
$identity = $identityResult.Output | ConvertFrom-Json
if ($identity.Account -ne $expectedAccount) { throw "Refusing to publish outside AWS account $expectedAccount." }
$outputsResult = Invoke-AwsCli -Arguments @('cloudformation', 'describe-stacks', '--region', $Region,
    '--stack-name', $StackName, '--query', 'Stacks[0].Outputs', '--output', 'json')
if ($outputsResult.ExitCode -ne 0) { throw "Opticon CloudFormation stack '$StackName' was not found. Run Provision-OpticonReleaseDistribution.ps1 first." }
$outputs = $outputsResult.Output | ConvertFrom-Json
$output = @{}; foreach ($item in $outputs) { $output[$item.OutputKey] = $item.OutputValue }
if ($output.BucketName -ne $bucket -or $output.DistributionDomainName -notmatch '^[a-z0-9-]+\.cloudfront\.net$') { throw "CloudFormation outputs do not identify the expected private Opticon distribution." }

$version = if ([string]::IsNullOrWhiteSpace($Version)) { Get-NextReleaseVersion -SourceOnly:$SourceOnly } else { $Version }
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "Version must be a stable major.minor.patch release." }
if ($CheckOnly) {
    if ([string]::IsNullOrWhiteSpace($Version)) { throw '-CheckOnly requires an explicit target version.' }
    Assert-PublisherReadiness
    [pscustomobject]@{
        Version = $version
        Bucket = $bucket
        Distribution = $output.DistributionDomainName
        Ready = $true
    }
    return
}
$releasePublisherLock = Enter-OpticonReleasePublisherLock (
    Join-Path $ArtifactDirectory '.opticon-release-publisher.lock')
$recoveredStage = $null
$recoveredStageLocal = $null
$recoveredManifestBytes = $null
$archiveStage = $null
$stageId = ''
if (($StageOnly -or $CommitStaged) -and -not $ForceRedeploy) {
    # The canonical archive is the authority once it exists: its immutable S3
    # metadata selects one immutable receipt object.  This avoids a mutable
    # per-version journal and prevents two concurrent publishers from pairing
    # one signed ZIP with the other publisher's manifest.
    $localFailure = $null
    try { $recoveredStageLocal = Read-LocalSourceStageReceipt -ReleaseVersion $version }
    catch { $localFailure = $_ }
    $archiveStage = Get-SourceStageForExistingArchive -ReleaseVersion $version
    if ($null -ne $archiveStage) {
        if ([string]::IsNullOrWhiteSpace($archiveStage.StageId) -or $null -eq $archiveStage.Receipt) {
            throw "The existing immutable source archive for $version lacks a verified receipt-bound stage identity; refusing to select or rebuild it."
        }
        $recoveredStage = $archiveStage.Receipt
        $stageId = $archiveStage.StageId
        if ($null -eq $recoveredStageLocal -or -not $recoveredStageLocal.ReceiptSha256.Equals($recoveredStage.ReceiptSha256, [StringComparison]::OrdinalIgnoreCase)) {
            # This remote object was selected by the immutable ZIP and its
            # content plus S3 checksum were verified before writing locally.
            $null = Write-SourceStageReceipt -ReleaseVersion $version -ReceiptBytes $recoveredStage.ReceiptBytes
        }
        $recoveredManifestBytes = $recoveredStage.ManifestBytes
        $SkipBuild = $true
    } elseif ($CommitStaged) {
        throw "No staged immutable source archive exists for $version. Commit refuses to build or upload a replacement; run -StageOnly again."
    } elseif ($null -ne $recoveredStageLocal -and (Test-LocalSourceStageArchiveAvailable -Receipt $recoveredStageLocal)) {
        # No external archive exists yet, so a valid local stage is safe to
        # resume.  A schema-3 receipt keeps its ID; a legacy local receipt is
        # upgraded into a fresh immutable receipt before upload.
        $recoveredStage = $recoveredStageLocal
        $recoveredManifestBytes = $recoveredStage.ManifestBytes
        $stageId = if ($recoveredStage.IsLegacy) { New-SourceStageId } else { $recoveredStage.StageId }
        $SkipBuild = $true
    } elseif ($null -ne $localFailure) {
        # There is no immutable archive yet.  A corrupt or lost local
        # pre-upload journal is therefore safely discardable; StageOnly will
        # build a new stage and create a new unique receipt object.
    }
} elseif ($ForceRedeploy -and $CommitStaged) {
    $recoveredStageLocal = Read-LocalSourceStageReceipt -ReleaseVersion $version
    if ($null -eq $recoveredStageLocal -or $recoveredStageLocal.IsLegacy) {
        throw "The forced same-version deployment has no current local stage receipt for $version. Run -StageOnly first."
    }
    $recoveredStage = $recoveredStageLocal
    $recoveredManifestBytes = $recoveredStage.ManifestBytes
    $stageId = $recoveredStage.StageId
    $SkipBuild = $true
}
if ($SkipBuild -and [string]::IsNullOrWhiteSpace($Version)) { throw "-SkipBuild requires an explicit -Version so an existing build is never misidentified." }
if (-not $SkipBuild) {
    # PowerShell array splatting is positional: strings such as '-Version'
    # become the value of the first parameter instead of named arguments.
    # Use a hashtable so the source builder receives the exact bound values.
    $buildArguments = @{
        Version = $version
        SigningProfile = $SigningProfile
        SourceReleaseCertificateThumbprint = $SourceReleaseCertificateThumbprint
        ProductCertificateThumbprint = $ProductCertificateThumbprint
        Rfc3161TimestampUrl = $Rfc3161TimestampUrl
        SignToolPath = $SignToolPath
    }
    if ($isLegacyMigration) {
        $buildArguments.LegacyMigrationSignerThumbprint = $LegacyMigrationSignerThumbprint
    }
    if ($SourceOnly) {
        $buildArguments.SourceOnly = $true
    }
    if ($ForceRedeploy) {
        $buildArguments.ForceRedeploy = $true
    }
    & (Join-Path $PSScriptRoot "Build-OpticonBundles.ps1") @buildArguments
}
$manifest = if ($null -ne $recoveredStage) {
    # A resumed stage/commit is authorized only by the captured receipt.  Do
    # not reopen the mutable workspace manifest after the archive was staged.
    $recoveredStage.Manifest
} else {
    Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
}
foreach ($artifact in @($manifest.artifacts | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-ArtifactString $_ 'legacyMigrationSignerThumbprint'))
    })) {
    $null = Assert-LegacyMigrationArtifact -Artifact $artifact
}
$releaseArtifacts = @()
if ($SourceOnly) {
    if ([int]$manifest.schemaVersion -ne 2) { throw 'The source-only build did not produce schema version 2.' }
    $unexpected = @($manifest.artifacts | Where-Object { $_.product -ne 'OpticonSource' })
    $sources = @($manifest.artifacts | Where-Object { $_.product -eq 'OpticonSource' })
    if ($unexpected.Count -ne 0 -or $sources.Count -ne 1 -or [string]$sources[0].version -cne $version) {
        throw "A source-only publication must contain exactly one current OpticonSource archive for $version."
    }
    $sources[0] | Add-Member -NotePropertyName clientInstallValidation -NotePropertyValue $clientInstallValidation -Force
    $key = "opticon/releases/$version/$([string]$sources[0].file)"
    $expectedUrl = "https://$($output.DistributionDomainName)/$key"
    if ($null -ne $recoveredStage) {
        if ([string]$sources[0].downloadUrl -cne $expectedUrl) {
            throw 'The durable source stage receipt has a noncanonical immutable download URL.'
        }
        # If S3 already owns this immutable key, require its bound stage ID
        # while repairing local bytes and the extracted launcher sidecar.
        $null = Ensure-StagedSourceArchiveLocally -Artifact $sources[0] -StageId $stageId
    } else {
        $sources[0] | Add-Member -NotePropertyName downloadUrl -NotePropertyValue $expectedUrl -Force
    }
    Assert-ProductionArtifactTrust -Artifact $sources[0]
    Assert-OpticonSourceArchive -Path (Get-LocalArtifactPath ([string]$sources[0].file)) -Record $sources[0] -RequireSourceLauncher
    $releaseArtifacts = @($sources[0])
} else {
    $allOpticonArtifacts = @($manifest.artifacts | Where-Object { $_.product -in @('OpticonBundle', 'OpticonBootstrap', 'OpticonSource') })
    if ($allOpticonArtifacts.Count -eq 0) { throw 'The release manifest has no Opticon artifacts.' }
    foreach ($artifact in $allOpticonArtifacts) { Assert-ProductionArtifactTrust -Artifact $artifact }
    $releaseArtifacts = @($manifest.artifacts | Where-Object { $_.version -eq $version -and $_.product -in @("OpticonBundle", "OpticonBootstrap", "OpticonSource") })
    $bundles = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonBundle" })
    $bootstraps = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonBootstrap" })
    $sources = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonSource" })
    if ($bundles.Count -ne 2 -or $bootstraps.Count -ne 1 -or $sources.Count -ne 1) { throw "Build did not produce two bundles, one bootstrap, and one source archive for $version." }
    foreach ($bundle in $bundles) {
        $actualLegacyMigrationSigner = Get-ArtifactString $bundle 'legacyMigrationSignerThumbprint'
        if ($isLegacyMigration) {
            if ($actualLegacyMigrationSigner -cne $LegacyMigrationSignerThumbprint) {
                throw "The requested legacy migration signer was not embedded in $($bundle.file)."
            }
        } elseif (-not [string]::IsNullOrWhiteSpace($actualLegacyMigrationSigner)) {
            throw "An ordinary release must not publish a legacy migration bundle: $($bundle.file)."
        }
    }
    $bootstrapPath = Get-LocalArtifactPath ([string]$bootstraps[0].file)
    if ([string]$bootstraps[0].signerThumbprint -ne $ProductCertificateThumbprint) {
        throw 'The source bootstrap outer signer pin does not match the production product signer.'
    }
    Assert-ProductSignature -Path $bootstrapPath
    Assert-OpticonSourceArchive -Path (Get-LocalArtifactPath ([string]$sources[0].file)) -Record $sources[0]
    foreach ($bundle in @($allOpticonArtifacts | Where-Object { $_.product -eq 'OpticonBundle' })) {
        Assert-OpticonBundleArchive -Path (Get-LocalArtifactPath ([string]$bundle.file)) -Record $bundle
    }
    foreach ($artifact in $releaseArtifacts) {
        $key = "opticon/releases/$version/$([string]$artifact.file)"
        $artifact | Add-Member -NotePropertyName downloadUrl -NotePropertyValue "https://$($output.DistributionDomainName)/$key" -Force
    }
}
$fullStreamFiles = @($releaseArtifacts | ForEach-Object { [string]$_.file })
$stageReceiptPath = ''
$manifestBytes = if ($null -ne $recoveredManifestBytes) {
    $recoveredManifestBytes
} else {
    [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
}
$stageReceiptBytes = $null
$ensureStageReceiptBeforeUpload = $false
if ($StageOnly -or $CommitStaged) {
    if ([string]::IsNullOrWhiteSpace($stageId)) { $stageId = New-SourceStageId }
    if ($null -ne $recoveredStage -and -not $recoveredStage.IsLegacy -and
        $recoveredStage.StageId -ceq $stageId) {
        # Reuse the raw remote receipt bytes selected by the immutable archive;
        # reserializing its JSON would create a different protected object.
        $stageReceiptBytes = $recoveredStage.ReceiptBytes
    } else {
        $stageReceiptBytes = New-SourceStageReceiptBytes -ReleaseVersion $version -ManifestBytes $manifestBytes -StageId $stageId
    }
    # The local record is written first.  If S3 has no ZIP yet, its exact
    # immutable receipt is then created and read back before upload begins.
    # A crash before the ZIP leaves an orphaned unique receipt, never a mutable
    # pointer; the next stage may safely start with a new ID.
    $stageReceiptPath = Write-SourceStageReceipt -ReleaseVersion $version -ReceiptBytes $stageReceiptBytes
    $ensureStageReceiptBeforeUpload = $null -eq $archiveStage
}

$temporaryConfig = Join-Path $script:AwsScratchDirectory 'aws.config'
@("[default]", "s3 =", "    max_concurrent_requests = 20", "    multipart_threshold = 5GB", "    multipart_chunksize = 64MB") | Set-Content -LiteralPath $temporaryConfig -Encoding ascii
$previousConfig = $script:AwsConfigFile
$script:AwsConfigFile = $temporaryConfig
try {
    if ($ensureStageReceiptBeforeUpload) {
        # This is intentionally durable before the irreversible ZIP upload.
        # The receipt key contains a random stage ID and cannot race another
        # publisher's receipt for the same release version.
        $null = Ensure-DurableSourceStageReceipt -ReleaseVersion $version -StageId $stageId -ReceiptBytes $stageReceiptBytes
    }
    foreach ($artifact in $releaseArtifacts) {
        $path = Get-LocalArtifactPath ([string]$artifact.file)
        $info = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedChecksum = [Convert]::ToBase64String([Convert]::FromHexString($hash))
        if ($info.Length -ne [long]$artifact.size -or $hash -ne [string]$artifact.sha256) { throw "Local release verification failed for $($artifact.file)." }
        $key = "opticon/releases/$version/$($artifact.file)"
        $contentType = if ($artifact.product -eq "OpticonBootstrap") { "application/vnd.microsoft.portable-executable" } else { "application/zip" }
        $isStagedSourceArchive = ($StageOnly -or $CommitStaged) -and [string]$artifact.product -ceq 'OpticonSource'
        $objectMetadata = "sha256=$hash"
        if ($isStagedSourceArchive) {
            if ($stageId -notmatch '^[A-Za-z0-9_-]{43}$') {
                throw 'The staged source archive does not have a valid immutable stage identity.'
            }
            # This metadata is the atomic binding between the conditionally
            # created ZIP and its separately durable immutable receipt.
            $objectMetadata = "$objectMetadata,stage=$stageId"
        }
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $existingHeadResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket,
                '--key', $key, '--checksum-mode', 'ENABLED', '--output', 'json')
            $existingHeadJson = $existingHeadResult.Output
            $objectExists = $existingHeadResult.ExitCode -eq 0
        } finally { $ErrorActionPreference = $savedPreference }
        if ($objectExists) {
            if ($ForceRedeploy -and $StageOnly -and $isStagedSourceArchive) {
                $putResult = Invoke-AwsCli -MaximumAttempts 3 -Arguments @('s3api', 'put-object', '--bucket', $bucket, '--key', $key,
                    '--body', $path, '--content-type', $contentType, '--cache-control', 'no-cache',
                    '--server-side-encryption', 'AES256', '--checksum-algorithm', 'SHA256', '--metadata', $objectMetadata,
                    '--output', 'json')
                if ($putResult.ExitCode -ne 0) { throw "Forced S3 replacement failed: $($putResult.Error.Trim())" }
                $headResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket,
                    '--key', $key, '--checksum-mode', 'ENABLED', '--output', 'json')
                if ($headResult.ExitCode -ne 0) { throw "Forced S3 replacement verification failed: $($headResult.Error.Trim())" }
                $head = $headResult.Output | ConvertFrom-Json
                $objectExists = $false
            } else {
                $head = $existingHeadJson | ConvertFrom-Json
            }
        } else {
            if ($CommitStaged) {
                throw "The staged immutable release object is missing from S3: s3://$bucket/$key. Commit refuses to upload or rebuild; run -StageOnly again."
            }
            # Conditional creation prevents a concurrent publisher from
            # overwriting an immutable filename after both callers observed a
            # missing object.  On the losing path, re-read and accept only the
            # exact same immutable bytes below.
            $putResult = Invoke-AwsCli -MaximumAttempts 3 -Arguments @('s3api', 'put-object', '--bucket', $bucket, '--key', $key,
                '--body', $path, '--content-type', $contentType, '--cache-control', 'public, max-age=31536000, immutable',
                '--server-side-encryption', 'AES256', '--checksum-algorithm', 'SHA256', '--metadata', $objectMetadata,
                '--if-none-match', '*', '--output', 'json')
            if ($putResult.ExitCode -ne 0 -and -not (Test-AwsPreconditionFailed -Result $putResult)) {
                throw "Conditional immutable S3 upload failed: $($putResult.Error.Trim())"
            }
            $headResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket,
                '--key', $key, '--checksum-mode', 'ENABLED', '--output', 'json')
            if ($headResult.ExitCode -ne 0) { throw "S3 head-object verification failed: $($headResult.Error.Trim())" }
            $head = $headResult.Output | ConvertFrom-Json
            $objectExists = $putResult.ExitCode -ne 0
        }
        if ($isStagedSourceArchive -and (Get-S3ObjectMetadataValue -Object $head -Name 'stage') -cne $stageId) {
            # A concurrent conditional create won.  Do not accept its ZIP
            # under this invocation's receipt even if both byte streams happen
            # to have the same hash; a rerun follows the winner's bound stage.
            throw "Another immutable source stage won the release key for $version. Re-run to recover the archive-bound stage receipt."
        }
        $directChecksum = ([string]$head.ChecksumSHA256).Equals($expectedChecksum, [StringComparison]::Ordinal)
        $migratedCompositeChecksum = $objectExists -and [string]$head.ChecksumType -eq 'COMPOSITE' -and
            (Test-CompositeSha256Checksum ([string]$head.ChecksumSHA256))
        if ($head.ContentLength -ne $info.Length -or
            -not (Get-S3ObjectMetadataValue -Object $head -Name 'sha256').Equals($hash, [StringComparison]::OrdinalIgnoreCase) -or
            (-not $directChecksum -and -not $migratedCompositeChecksum) -or
            $head.ContentType -ne $contentType -or
            ($head.CacheControl -ne "public, max-age=31536000, immutable" -and
             -not ($ForceRedeploy -and [string]$head.CacheControl -eq 'no-cache')) -or
            $head.ServerSideEncryption -ne "AES256") {
            if ($objectExists) { throw "Refusing to overwrite immutable release object s3://$bucket/$key because it does not match the local release." }
            throw "S3 verification failed for $key."
        }
        $url = "https://$($output.DistributionDomainName)/$key"
        if ([string]$artifact.downloadUrl -cne $url) {
            throw "The release manifest download URL was not the canonical immutable URL for $($artifact.file)."
        }
        if ($ForceRedeploy -and $StageOnly) {
            if ([string]$output.DistributionId -notmatch '^[A-Z0-9]+$') {
                throw 'CloudFormation outputs do not expose the CloudFront distribution ID required for a forced replacement.'
            }
            $invalidation = Invoke-AwsCli -Arguments @('cloudfront', 'create-invalidation',
                '--distribution-id', [string]$output.DistributionId, '--paths', "/$key", '--output', 'json')
            if ($invalidation.ExitCode -ne 0) { throw "CloudFront invalidation failed: $($invalidation.Error.Trim())" }
            $invalidationId = [string](($invalidation.Output | ConvertFrom-Json).Invalidation.Id)
            $wait = Invoke-AwsCli -Arguments @('cloudfront', 'wait', 'invalidation-completed',
                '--distribution-id', [string]$output.DistributionId, '--id', $invalidationId)
            if ($wait.ExitCode -ne 0) { throw "CloudFront invalidation did not complete: $($wait.Error.Trim())" }
        }
        $deadline = [DateTime]::UtcNow.AddMinutes(12)
        do {
            try {
                Invoke-CloudFrontVerification -Url $url -ExpectedHash $hash -ExpectedSize $info.Length -FullStream
                break
            } catch {
                if ([DateTime]::UtcNow -ge $deadline) { throw }
                Start-Sleep -Seconds 5
            }
        } while ($true)
    }

    [IO.File]::WriteAllBytes("$manifestPath.new", $manifestBytes)
    Move-FileAtomically -Source "$manifestPath.new" -Destination $manifestPath
} finally {
    $script:AwsConfigFile = $previousConfig
    Remove-Item -LiteralPath $temporaryConfig -Force -ErrorAction SilentlyContinue
}

if (-not $SkipManifestPublish) {
    # Publish the canonical in-memory bytes which were just validated and, for
    # a staged commit, originated from the durable receipt. Do not reopen the
    # mutable workspace manifest between verification and the atomic gateway
    # write.
    Publish-ManifestAtomically $manifestBytes
    $live = Read-PublicManifestBounded
    foreach ($artifact in @($live.artifacts | Where-Object {
            -not [string]::IsNullOrWhiteSpace((Get-ArtifactString $_ 'legacyMigrationSignerThumbprint'))
        })) {
        $null = Assert-LegacyMigrationArtifact -Artifact $artifact
    }
    if ($SourceOnly) {
        if ([int]$live.schemaVersion -ne 2 -or @($live.artifacts | Where-Object { $_.product -ne 'OpticonSource' }).Count -ne 0) {
            throw 'Fly accepted the source-only manifest but served a binary release artifact.'
        }
        $liveRelease = @($live.artifacts | Where-Object { $_.version -eq $version -and $_.product -eq 'OpticonSource' })
        if ($liveRelease.Count -ne 1 -or @($liveRelease | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.downloadUrl) }).Count -ne 0) {
            throw 'Fly accepted the source-only manifest but did not serve exactly one CloudFront source archive.'
        }
    } else {
        $liveRelease = @($live.artifacts | Where-Object { $_.version -eq $version -and $_.product -in @("OpticonBundle", "OpticonBootstrap", "OpticonSource") })
        if ($liveRelease.Count -ne 4 -or @($liveRelease | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.downloadUrl) }).Count -ne 0) {
            throw "Fly accepted the manifest but did not serve the complete CloudFront release."
        }
    }
    foreach ($expected in $releaseArtifacts) {
        $actual = @($liveRelease | Where-Object { [string]$_.file -ceq [string]$expected.file })
        if ($actual.Count -ne 1 -or [string]$actual[0].product -cne [string]$expected.product -or
            [string]$actual[0].version -cne [string]$expected.version -or [long]$actual[0].size -ne [long]$expected.size -or
            [string]$actual[0].sha256 -cne [string]$expected.sha256 -or [string]$actual[0].downloadUrl -cne [string]$expected.downloadUrl -or
            [string]$actual[0].signingProfile -cne $SigningProfile -or
            [string]$actual[0].sourceManifestKeyId -cne $SourceReleaseCertificateThumbprint -or
            [string]$actual[0].productSignerThumbprint -cne $ProductCertificateThumbprint -or
            ([string]$expected.product -ceq 'OpticonSource' -and (
                (Get-ArtifactString $actual[0] 'sourceLauncherFile') -cne (Get-ArtifactString $expected 'sourceLauncherFile') -or
                [long]$actual[0].sourceLauncherSize -ne [long]$expected.sourceLauncherSize -or
                (Get-ArtifactString $actual[0] 'sourceLauncherSha256') -cne (Get-ArtifactString $expected 'sourceLauncherSha256'))) -or
            (Get-ArtifactString $actual[0] 'legacyMigrationSignerThumbprint') -cne
                (Get-ArtifactString $expected 'legacyMigrationSignerThumbprint')) {
            throw "Fly served release metadata that differed from the verified publication for $($expected.file)."
        }
    }
}

[pscustomobject]@{
    Version = $version
    Bucket = $bucket
    Distribution = $output.DistributionDomainName
    FullStreamVerified = $fullStreamFiles
    Artifacts = $releaseArtifacts | Select-Object product, file, size, sha256, downloadUrl
    StageReceiptPath = $stageReceiptPath
    Staged = $StageOnly
    CommittedStaged = $CommitStaged
}
} finally {
    if ($null -ne $releasePublisherLock) { $releasePublisherLock.Dispose() }
    if (-not [string]::IsNullOrWhiteSpace($script:AwsScratchDirectory) -and
        (Test-Path -LiteralPath $script:AwsScratchDirectory -PathType Container)) {
        Remove-Item -LiteralPath $script:AwsScratchDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
