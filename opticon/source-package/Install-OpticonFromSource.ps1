[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$SourceArchive,
    [Parameter(Mandatory)][string]$SourceVersion,
    [Parameter(Mandatory)][string]$SourceSha256,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$SourceManifestKeyId,
    [Parameter(Mandatory)][ValidateSet('Production','OwnerManaged')][string]$SigningProfile,
    [Parameter(Mandatory)][string]$SourceReleaseCertificateBase64,
    [Parameter(Mandatory)][string]$ProductSignerThumbprint,
    [Parameter(Mandatory)][string]$ProductSigningCertificateBase64,
    [Parameter(Mandatory)][string]$SdkVersion,
    [Parameter(Mandatory)][string]$RuntimeVersion,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$TargetRuntime,
    [Parameter(Mandatory)][ValidateSet('ManagedOnly', 'ControllerAndManaged')][string]$Role,
    [Parameter(Mandatory)][string]$InvitePath,
    [Parameter(Mandatory)][string]$InviteKey,
    [Parameter(Mandatory)][string]$DotnetPath,
    [string]$ClientInstallValidationBase64 = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    $clientValidation = if ([string]::IsNullOrWhiteSpace($ClientInstallValidationBase64)) {
        [pscustomobject]@{ disableAll = $false; disabledSteps = @() }
    } else {
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ClientInstallValidationBase64)) | ConvertFrom-Json
    }
} catch { throw 'The client installation validation policy is malformed.' }
function Test-ClientValidationEnabled([string]$Step) {
    return -not [bool]$clientValidation.disableAll -and @($clientValidation.disabledSteps) -notcontains $Step
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The authenticated source build must run in the elevated signed bootstrap.'
}
if ((Test-ClientValidationEnabled 'InvitationConstraints') -and ($SourceVersion -notmatch '^[1-9][0-9]*\.[0-9]+\.[0-9]+$' -or
    $SourceSha256 -notmatch '^[a-f0-9]{64}$' -or $SourceManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
    $SourceManifestKeyId -notmatch '^[A-F0-9]{40}$' -or
    $ProductSignerThumbprint -notmatch '^[A-F0-9]{40}$' -or
    $SourceManifestKeyId -eq 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
    $ProductSignerThumbprint -eq 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
    $ProductSignerThumbprint -eq $SourceManifestKeyId -or
    $SdkVersion -ne '10.*.*' -or $RuntimeVersion -ne '10.0.10')) {
    throw 'The source build pins are invalid or unsupported.'
}
try {
    $sourceReleaseCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [Convert]::FromBase64String($SourceReleaseCertificateBase64))
    $productCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [Convert]::FromBase64String($ProductSigningCertificateBase64))
} catch {
    throw 'The signed source manifest contains malformed public signing certificates.'
}
if ((Test-ClientValidationEnabled 'SourceArchiveAuthenticity') -and ($sourceReleaseCertificate.HasPrivateKey -or $productCertificate.HasPrivateKey -or
    $sourceReleaseCertificate.Thumbprint.ToUpperInvariant() -ne $SourceManifestKeyId -or
    $productCertificate.Thumbprint.ToUpperInvariant() -ne $ProductSignerThumbprint)) {
    throw 'The signed source manifest certificate bytes do not match its immutable key pins.'
}

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$SourceArchive = [IO.Path]::GetFullPath($SourceArchive)
$InvitePath = [IO.Path]::GetFullPath($InvitePath)
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
foreach ($path in @($SourceRoot, $SourceArchive, $InvitePath, $DotnetPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required protected source-build input is missing: $path" }
}
if ((Test-ClientValidationEnabled 'DownloadIntegrity') -and
    (Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $SourceSha256) {
    throw 'The source archive changed after the signed bootstrap verified it.'
}

$globalJson = Get-Content -Raw -LiteralPath (Join-Path $SourceRoot 'global.json') | ConvertFrom-Json
if ((Test-ClientValidationEnabled 'DependencyIntegrity') -and ([string]$globalJson.sdk.version -ne '10.0.100' -or
    [string]$globalJson.sdk.rollForward -ne 'latestMinor' -or
    [bool]$globalJson.sdk.allowPrerelease)) {
    throw 'The authenticated source global.json does not enforce the stable .NET 10 SDK policy.'
}
$installedSdks = & $DotnetPath --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match '^10\.[0-9]+\.[0-9]+\s' })) {
    throw "A stable .NET SDK matching $SdkVersion is not installed."
}
Push-Location $SourceRoot
try {
    $selectedSdk = (& $DotnetPath --version).Trim()
    if ((Test-ClientValidationEnabled 'DependencyIntegrity') -and
        ($LASTEXITCODE -ne 0 -or $selectedSdk -notmatch '^10\.[0-9]+\.[0-9]+$')) {
        throw "global.json selected SDK '$selectedSdk', which does not match $SdkVersion."
    }

    $handoffRoot = Split-Path $SourceRoot -Parent
    $release = Join-Path $handoffRoot 'release'
    if (Test-Path -LiteralPath $release) { throw 'The protected release directory already exists.' }
    New-Item -Path $release -ItemType Directory | Out-Null
    if ((Get-Item -LiteralPath $release -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The protected release directory is a reparse point.'
    }

    $msbuildIsolation = @(
        "-p:DirectoryBuildPropsPath=$(Join-Path $SourceRoot 'Directory.Build.props')",
        "-p:DirectoryBuildTargetsPath=$(Join-Path $SourceRoot 'Directory.Build.targets')",
        '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false',
        '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false',
        '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false',
        '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false',
        '-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false',
        '-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false',
        '-p:ImportByWildcardBeforeMicrosoftCommonProps=false',
        '-p:ImportByWildcardAfterMicrosoftCommonProps=false',
        '-p:ImportByWildcardBeforeMicrosoftCommonTargets=false',
        '-p:ImportByWildcardAfterMicrosoftCommonTargets=false',
        '-p:ImportByWildcardBeforeMicrosoftCSharpTargets=false',
        '-p:ImportByWildcardAfterMicrosoftCSharpTargets=false',
        '-p:ImportByWildcardBeforeMicrosoftCommonCrossTargetingTargets=false',
        '-p:ImportByWildcardAfterMicrosoftCommonCrossTargetingTargets=false',
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false'
    )
    $signingProperties = @(
        "-p:OpticonSigningProfile=$SigningProfile",
        "-p:OpticonSourceReleaseKeyId=$SourceManifestKeyId",
        "-p:OpticonSourceReleaseCertificateBase64=$SourceReleaseCertificateBase64",
        "-p:OpticonProductSignerThumbprint=$ProductSignerThumbprint",
        "-p:OpticonProductSigningCertificateBase64=$ProductSigningCertificateBase64"
    )

    $publishCommon = @(
        'publish', '-c', 'Release', '-r', $TargetRuntime, '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:EnableWindowsTargeting=true',
        "-p:Version=$SourceVersion", "-p:InformationalVersion=$SourceVersion", '-p:RollForward=Disable',
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    ) + $signingProperties + $msbuildIsolation
    function Publish-OpticonProject {
        param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$Destination)
        if (Test-Path -LiteralPath $Destination) { throw "Publish destination already exists: $Destination" }
        New-Item -Path $Destination -ItemType Directory -Force | Out-Null
        $projectPath = Join-Path $SourceRoot $Project
        & $DotnetPath restore $projectPath '-r' $TargetRuntime '--configfile' (Join-Path $SourceRoot 'NuGet.Config') `
            '-p:EnableWindowsTargeting=true' @signingProperties @msbuildIsolation
        if ($LASTEXITCODE -ne 0) { throw "Offline local source restore failed for $Project." }
        & $DotnetPath @publishCommon $projectPath '-o' $Destination
        if ($LASTEXITCODE -ne 0) { throw "Local source publish failed for $Project." }
    }

    $setupBuild = Join-Path $release '.setup-build'
    Publish-OpticonProject 'src\Taildesk.Setup\Taildesk.Setup.csproj' $setupBuild
    $setupFiles = @(Get-ChildItem -LiteralPath $setupBuild -File -Recurse)
    if ($setupFiles.Count -ne 1 -or $setupFiles[0].Name -ne 'Taildesk.Setup.exe' -or
        -not $setupFiles[0].DirectoryName.Equals([IO.Path]::GetFullPath($setupBuild), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The local Setup publish was not the expected complete single-file application.'
    }
    Move-Item -LiteralPath $setupFiles[0].FullName -Destination (Join-Path $release 'Taildesk.Setup.exe')
    Remove-Item -LiteralPath $setupBuild -Recurse -Force
    Publish-OpticonProject 'src\Taildesk.Agent\Taildesk.Agent.csproj' (Join-Path $release 'Payload\Agent')
    Publish-OpticonProject 'src\Taildesk.UpdateGuardian\Taildesk.UpdateGuardian.csproj' (Join-Path $release 'Payload\UpdateGuardian')
    if ($Role -eq 'ControllerAndManaged') {
        Publish-OpticonProject 'src\Taildesk.Admin\Taildesk.Admin.csproj' (Join-Path $release 'Payload\Admin')
        Publish-OpticonProject 'src\Taildesk.Cli\Taildesk.Cli.csproj' (Join-Path $release 'Payload\Admin\Cli')
        $cli = Join-Path $release 'Payload\Admin\Cli\Taildesk.OpticonCli.exe'
        if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'The locally built CLI apphost is missing.' }
        Move-Item -LiteralPath $cli -Destination (Join-Path $release 'Payload\Admin\Cli\opticon.exe')
        $adminRuntimeConfig = Join-Path $release 'Payload\Admin\Cli\Opticon.runtimeconfig.json'
        if (Test-Path -LiteralPath $adminRuntimeConfig) { Remove-Item -LiteralPath $adminRuntimeConfig -Force }
        Publish-OpticonProject 'src\Taildesk.RouteKeeper\Taildesk.RouteKeeper.csproj' (Join-Path $release 'Payload\Admin\Tools')
    }

    $files = @()
    $releasePrefix = [IO.Path]::GetFullPath($release).TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse | Sort-Object FullName) {
        $full = [IO.Path]::GetFullPath($file.FullName)
        if (-not $full.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A locally built payload escaped the protected release stage.'
        }
        $files += [ordered]@{
            path = $full.Substring($releasePrefix.Length).Replace('\', '/')
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    if ($files.Count -lt 3) { throw 'The locally built payload is incomplete.' }
    $attestation = [ordered]@{
        schemaVersion = 3
        releaseVersion = $SourceVersion
        sourceFile = [IO.Path]::GetFileName($SourceArchive)
        sourceSize = (Get-Item -LiteralPath $SourceArchive).Length
        sourceSha256 = $SourceSha256
        sourceManifestSha256 = $SourceManifestSha256
        sourceManifestKeyId = $SourceManifestKeyId
        signingProfile = $SigningProfile
        productSignerThumbprint = $ProductSignerThumbprint
        sdkVersion = $SdkVersion
        runtimeVersion = $RuntimeVersion
        targetRuntime = $TargetRuntime
        inviteCiphertextSha256 = (Get-FileHash -LiteralPath $InvitePath -Algorithm SHA256).Hash.ToLowerInvariant()
        files = $files
    }
    $attestationPath = Join-Path $release 'source-build-attestation.json'
    [IO.File]::WriteAllText($attestationPath, ($attestation | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

    $setup = Join-Path $release 'Taildesk.Setup.exe'
    $arguments = @(
        "--hosted-invite=$InvitePath",
        "--invite-key=$InviteKey",
        "--source-attestation=$attestationPath"
    )
    & $setup @arguments
    $setupExitCode = $LASTEXITCODE
    if ($setupExitCode -ne 0) { throw "Locally built Opticon Setup returned $setupExitCode." }
} finally {
    Pop-Location
}
