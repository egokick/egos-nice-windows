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
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$TargetRuntime,
    [Parameter(Mandatory)][ValidateSet('ManagedOnly','ControllerAndManaged')][string]$Role,
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$AttestationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-PlainChildPath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Description)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escaped its protected root."
    }
    $cursor = $pathFull
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "$Description contains a reparse point."
            }
        }
        if ($cursor.TrimEnd('\').Equals($rootFull.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -LiteralPath $cursor -Parent
        if ([string]::IsNullOrWhiteSpace($parent)) { throw "$Description escaped its protected root." }
        $cursor = $parent
    }
    return $pathFull
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The verified source update build must run elevated under the Opticon Agent service.'
}
if ($SourceVersion -notmatch '^[1-9][0-9]*\.[0-9]+\.[0-9]+$' -or
    $SourceSha256 -notmatch '^[a-f0-9]{64}$' -or
    $SourceManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
    $SourceManifestKeyId -notmatch '^[A-F0-9]{40}$' -or
    $ProductSignerThumbprint -notmatch '^[A-F0-9]{40}$' -or
    $SourceManifestKeyId -eq 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
    $ProductSignerThumbprint -eq 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
    $SourceManifestKeyId -eq $ProductSignerThumbprint -or
    $SdkVersion -ne '10.0.302' -or $RuntimeVersion -ne '10.0.10') {
    throw 'The source update pins are invalid or unsupported.'
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
    throw 'The signed source manifest certificate bytes do not match the immutable key pins.'
}

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$SourceArchive = [IO.Path]::GetFullPath($SourceArchive)
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$AttestationPath = [IO.Path]::GetFullPath($AttestationPath)
foreach ($path in @($SourceRoot, $SourceArchive, $DotnetPath, $OutputDirectory)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required protected source-update input is missing: $path" }
}
if ((Get-Item -LiteralPath $OutputDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The protected source update output directory is a reparse point.'
}
$operationRoot = Split-Path -LiteralPath $OutputDirectory -Parent
$AttestationPath = Require-PlainChildPath -Root $operationRoot -Path $AttestationPath -Description 'The source build attestation path'
if ((Get-ChildItem -LiteralPath $OutputDirectory -Force | Measure-Object).Count -ne 0) {
    throw 'The protected source update output directory is not empty.'
}
if (Test-Path -LiteralPath $AttestationPath) { throw 'The source build attestation path is already occupied.' }
if ([IO.Path]::GetFileName($SourceArchive) -ne "opticon-source-$SourceVersion.zip") {
    throw 'The protected source archive name does not match the approved version.'
}
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $SourceSha256) {
    throw 'The source archive changed after Opticon verified it.'
}

$globalJson = Get-Content -Raw -LiteralPath (Join-Path $SourceRoot 'global.json') | ConvertFrom-Json
if ([string]$globalJson.sdk.version -ne $SdkVersion -or [string]$globalJson.sdk.rollForward -ne 'disable') {
    throw 'The authenticated source global.json does not enforce the exact SDK pin.'
}
$nugetConfig = Join-Path $SourceRoot 'NuGet.Config'
$nugetText = Get-Content -Raw -LiteralPath $nugetConfig
if ($nugetText -notmatch '(?is)<packageSources>\s*<clear\s*/>\s*<add\s+key="opticon-offline"\s+value="\./packages"\s*/>\s*</packageSources>' -or
    $nugetText -match '(?i)https?://') {
    throw 'The authenticated source NuGet configuration is not the required offline-only configuration.'
}

$installedSdks = & $DotnetPath --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match ('^' + [regex]::Escape($SdkVersion) + '\s') })) {
    throw "Exact .NET SDK $SdkVersion is not installed."
}
$installedRuntimes = & $DotnetPath --list-runtimes
if ($LASTEXITCODE -ne 0) { throw 'The exact .NET runtime inventory could not be read.' }
foreach ($runtime in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App','Microsoft.AspNetCore.App')) {
    if (-not ($installedRuntimes | Where-Object { $_ -match ('^' + [regex]::Escape($runtime) + '\s+' + [regex]::Escape($RuntimeVersion) + '\s') })) {
        throw "Exact runtime $runtime $RuntimeVersion is not installed with the supported SDK."
    }
}
$expectedHostArchitecture = if ($TargetRuntime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$dotnetInfo = (& $DotnetPath --info | Out-String)
if ($LASTEXITCODE -ne 0 -or
    $dotnetInfo -notmatch ('(?mi)^\s*Architecture:\s*' + [regex]::Escape($expectedHostArchitecture) + '\s*$') -or
    $dotnetInfo -notmatch ('(?mi)^\s*RID:\s*' + [regex]::Escape($TargetRuntime) + '\s*$')) {
    throw "The fixed dotnet host architecture/RID does not exactly match $TargetRuntime."
}

Push-Location $SourceRoot
try {
    $selectedSdk = (& $DotnetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $selectedSdk -ne $SdkVersion) {
        throw "global.json selected SDK '$selectedSdk', not exact SDK '$SdkVersion'."
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
        'publish','-c','Release','-r',$TargetRuntime,'--self-contained','false','--no-restore',
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None','-p:DebugSymbols=false','-p:EnableWindowsTargeting=true',
        "-p:Version=$SourceVersion","-p:InformationalVersion=$SourceVersion",'-p:RollForward=Disable',
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    ) + $signingProperties + $msbuildIsolation
    function Publish-SourceUpdateProject {
        param([Parameter(Mandatory)][string]$Project,[Parameter(Mandatory)][string]$Destination)
        if (Test-Path -LiteralPath $Destination) { throw "Publish destination already exists: $Destination" }
        New-Item -Path $Destination -ItemType Directory | Out-Null
        $projectPath = Join-Path $SourceRoot $Project
        & $DotnetPath restore $projectPath '-r' $TargetRuntime '--configfile' $nugetConfig `
            '-p:EnableWindowsTargeting=true' @signingProperties @msbuildIsolation
        if ($LASTEXITCODE -ne 0) { throw "Offline local source restore failed for $Project." }
        & $DotnetPath @publishCommon $projectPath '-o' $Destination
        if ($LASTEXITCODE -ne 0) { throw "Local source publish failed for $Project." }
    }

    Publish-SourceUpdateProject 'src\Taildesk.Agent\Taildesk.Agent.csproj' (Join-Path $OutputDirectory 'Payload\Agent')
    Publish-SourceUpdateProject 'src\Taildesk.UpdateGuardian\Taildesk.UpdateGuardian.csproj' (Join-Path $OutputDirectory 'Payload\UpdateGuardian')
    $agent = Join-Path $OutputDirectory 'Payload\Agent\Taildesk.Agent.exe'
    $guardian = Join-Path $OutputDirectory 'Payload\UpdateGuardian\Taildesk.UpdateGuardian.exe'
    if (-not (Test-Path -LiteralPath $agent -PathType Leaf) -or -not (Test-Path -LiteralPath $guardian -PathType Leaf)) {
        throw 'The local source build did not produce both required Opticon executables.'
    }
    $guardianFiles = @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'Payload\UpdateGuardian') -File -Recurse)
    if ($guardianFiles.Count -ne 1 -or $guardianFiles[0].Name -ne 'Taildesk.UpdateGuardian.exe') {
        throw 'The local source build Guardian payload must contain exactly one executable.'
    }

    $prefix = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Sort-Object FullName) {
        $full = [IO.Path]::GetFullPath($file.FullName)
        if (-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or
            ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'A locally built source-update payload escaped or uses a reparse point.'
        }
        $files += [ordered]@{
            path = $full.Substring($prefix.Length).Replace('\','/')
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    if ($files.Count -lt 2) { throw 'The local source update build payload is incomplete.' }
    $architecture = if ($TargetRuntime -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $attestation = [ordered]@{
        schemaVersion = 1
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
        role = $Role
        architecture = $architecture
        files = $files
    }
    [IO.File]::WriteAllText($AttestationPath, ($attestation | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
} finally {
    Pop-Location
}
