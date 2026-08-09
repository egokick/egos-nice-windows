[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$SourceArchive,
    [Parameter(Mandatory)][string]$SourceVersion,
    [Parameter(Mandatory)][string]$SourceSha256,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$SourceManifestKeyId,
    [Parameter(Mandatory)][ValidateSet('Production')][string]$SigningProfile,
    [Parameter(Mandatory)][string]$SourceReleaseCertificateBase64,
    [Parameter(Mandatory)][string]$ProductSignerThumbprint,
    [Parameter(Mandatory)][string]$ProductSigningCertificateBase64,
    [Parameter(Mandatory)][string]$SdkVersion,
    [Parameter(Mandatory)][string]$RuntimeVersion,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$TargetRuntime,
    [Parameter(Mandatory)][string]$BootstrapVersion,
    [Parameter(Mandatory)][string]$BootstrapFile,
    [Parameter(Mandatory)][long]$BootstrapSize,
    [Parameter(Mandatory)][string]$BootstrapSha256,
    [Parameter(Mandatory)][string]$BootstrapSignerThumbprint,
    [Parameter(Mandatory)][ValidateSet('ManagedOnly', 'ControllerAndManaged')][string]$Role,
    [Parameter(Mandatory)][string]$InvitePath,
    [Parameter(Mandatory)][string]$InviteKey,
    [Parameter(Mandatory)][string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The authenticated source build must run in the elevated signed bootstrap.'
}
if ($SourceVersion -notmatch '^[1-9][0-9]*\.[0-9]+\.[0-9]+$' -or
    $SourceSha256 -notmatch '^[a-f0-9]{64}$' -or $SourceManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
    $SourceManifestKeyId -notmatch '^[A-F0-9]{40}$' -or
    $ProductSignerThumbprint -notmatch '^[A-F0-9]{40}$' -or
    $SourceManifestKeyId -eq 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
    $ProductSignerThumbprint -eq 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
    $ProductSignerThumbprint -eq $SourceManifestKeyId -or
    $SdkVersion -ne '8.0.423' -or $RuntimeVersion -ne '8.0.29' -or
    $BootstrapVersion -ne $SourceVersion -or $BootstrapFile -ne "opticon-bootstrap-$SourceVersion.exe" -or
    $BootstrapSize -le 0 -or $BootstrapSha256 -notmatch '^[a-f0-9]{64}$' -or
    $BootstrapSignerThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
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
if ($sourceReleaseCertificate.HasPrivateKey -or $productCertificate.HasPrivateKey -or
    $sourceReleaseCertificate.Thumbprint.ToUpperInvariant() -ne $SourceManifestKeyId -or
    $productCertificate.Thumbprint.ToUpperInvariant() -ne $ProductSignerThumbprint) {
    throw 'The signed source manifest certificate bytes do not match its immutable key pins.'
}

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$SourceArchive = [IO.Path]::GetFullPath($SourceArchive)
$InvitePath = [IO.Path]::GetFullPath($InvitePath)
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
foreach ($path in @($SourceRoot, $SourceArchive, $InvitePath, $DotnetPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required protected source-build input is missing: $path" }
}
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $SourceSha256) {
    throw 'The source archive changed after the signed bootstrap verified it.'
}

$globalJson = Get-Content -Raw -LiteralPath (Join-Path $SourceRoot 'global.json') | ConvertFrom-Json
if ([string]$globalJson.sdk.version -ne $SdkVersion -or [string]$globalJson.sdk.rollForward -ne 'disable') {
    throw 'The authenticated source global.json does not enforce the invitation SDK pin.'
}
$installedSdks = & $DotnetPath --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match ('^' + [regex]::Escape($SdkVersion) + '\s') })) {
    throw "Exact .NET SDK $SdkVersion is not installed."
}
$installedRuntimes = & $DotnetPath --list-runtimes
if ($LASTEXITCODE -ne 0) { throw 'The exact .NET runtime inventory could not be read.' }
foreach ($runtime in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App')) {
    if (-not ($installedRuntimes | Where-Object { $_ -match ('^' + [regex]::Escape($runtime) + '\s+' + [regex]::Escape($RuntimeVersion) + '\s') })) {
        throw "Exact runtime $runtime $RuntimeVersion is not installed with the supported SDK."
    }
}
$expectedHostArchitecture = if ($TargetRuntime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$dotnetInfo = (& $DotnetPath --info | Out-String)
$hostArchitectureMatches = $dotnetInfo -match ('(?mi)^\s*Architecture:\s*' + [regex]::Escape($expectedHostArchitecture) + '\s*$')
$hostRidMatches = $dotnetInfo -match ('(?mi)^\s*RID:\s*' + [regex]::Escape($TargetRuntime) + '\s*$')
if ($LASTEXITCODE -ne 0 -or -not $hostArchitectureMatches -or -not $hostRidMatches) {
    throw "The fixed dotnet host architecture/RID does not exactly match $TargetRuntime. Install the native $expectedHostArchitecture SDK."
}
Push-Location $SourceRoot
try {
    $selectedSdk = (& $DotnetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $selectedSdk -ne $SdkVersion) {
        throw "global.json selected SDK '$selectedSdk', not exact SDK '$SdkVersion'."
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
        'publish', '-c', 'Release', '-r', $TargetRuntime, '--self-contained', 'false', '--no-restore',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:EnableWindowsTargeting=true', "-p:RuntimeFrameworkVersion=$RuntimeVersion",
        "-p:Version=$SourceVersion", "-p:InformationalVersion=$SourceVersion", '-p:RollForward=Disable',
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    ) + $signingProperties + $msbuildIsolation
    function Publish-OpticonProject {
        param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$Destination)
        if (Test-Path -LiteralPath $Destination) { throw "Publish destination already exists: $Destination" }
        New-Item -Path $Destination -ItemType Directory -Force | Out-Null
        $projectPath = Join-Path $SourceRoot $Project
        & $DotnetPath restore $projectPath '-r' $TargetRuntime '--configfile' (Join-Path $SourceRoot 'NuGet.Config') `
            '-p:EnableWindowsTargeting=true' "-p:RuntimeFrameworkVersion=$RuntimeVersion" @signingProperties @msbuildIsolation
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
        schemaVersion = 2
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
        bootstrapVersion = $BootstrapVersion
        bootstrapFile = $BootstrapFile
        bootstrapSize = $BootstrapSize
        bootstrapSha256 = $BootstrapSha256
        bootstrapSignerThumbprint = $BootstrapSignerThumbprint.ToUpperInvariant()
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
