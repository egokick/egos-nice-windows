[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$ControlOrigin = 'https://taildesk-egokick-control.fly.dev',
    [string]$ManifestPath = '',
    [ValidateSet('Production', 'OwnerManaged')][string]$SigningProfile = 'Production',
    [string]$SourceReleaseCertificateThumbprint = '',
    [string]$ProductCertificateThumbprint = '',
    [string]$Rfc3161TimestampUrl = '',
    [string]$SignToolPath = '',
    [string]$ClientInstallValidationBase64 = '',
    [switch]$ForceRedeploy,
    [switch]$CheckOnly,
    # Build/upload/verify the immutable install bundles and source update, but
    # leave both live manifests untouched until a later explicit commit.
    [switch]$StageOnly,
    # Publish both locally verified release manifests without rebuilding.
    [switch]$CommitStaged
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.1') {
    throw 'The Opticon target-release publisher requires PowerShell 7.1 or newer. Run this script with pwsh.exe, not Windows PowerShell.'
}
Add-Type -AssemblyName System.Net.Http
$opticonRoot = Split-Path $PSScriptRoot -Parent
$publisher = Join-Path $opticonRoot 'fly-headscale\scripts\Publish-OpticonBundles.ps1'
$sourcePublisher = Join-Path $opticonRoot 'fly-headscale\scripts\Publish-OpticonSourceRelease.ps1'

function Get-SourceVersion {
    $propertiesPath = Join-Path $opticonRoot 'Directory.Build.props'
    [xml]$properties = Get-Content -Raw -LiteralPath $propertiesPath
    $node = $properties.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Opticon has no version in '$propertiesPath'."
    }
    return $node.InnerText.Trim()
}

function Assert-StableVersion([string]$Value) {
    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Opticon target releases require a stable major.minor.patch version; found '$Value'."
    }
}

function Get-ReleaseManifest([switch]$Update) {
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        return Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
    }

    $origin = [Uri]$ControlOrigin
    if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or
        -not [string]::IsNullOrWhiteSpace($origin.UserInfo)) {
        throw 'The Opticon control origin must be an HTTPS origin without credentials.'
    }
    $manifestRoute = if ($Update) {
        '/opticon/artifacts/v1/update-manifest.json'
    } else {
        '/opticon/artifacts/v1/manifest.json'
    }
    $uri = [Uri]::new($origin, $manifestRoute)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.CheckCertificateRevocationList = $true
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(45)
    try {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
        $request.Headers.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
        $request.Headers.CacheControl.NoCache = $true
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) {
                throw "The live Opticon release manifest returned HTTP $([int]$response.StatusCode)."
            }
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return $json | ConvertFrom-Json
        } finally {
            $response.Dispose()
            $request.Dispose()
        }
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Test-CompleteRelease($Manifest, [string]$ReleaseVersion) {
    if ([int]$Manifest.schemaVersion -ne 1) { return $false }
    $release = @($Manifest.artifacts | Where-Object { $_.version -eq $ReleaseVersion })
    $bundles = @($release | Where-Object { $_.product -eq 'OpticonBundle' })
    $bootstraps = @($release | Where-Object { $_.product -eq 'OpticonBootstrap' })
    if ($release.Count -ne 3 -or $bundles.Count -ne 2 -or $bootstraps.Count -ne 1 -or
        @($bundles.role | Sort-Object -Unique).Count -ne 2 -or
        @($bundles | Where-Object { $_.architecture -ne 'x64' }).Count -ne 0) { return $false }
    foreach ($artifact in $release) {
        if ([long]$artifact.size -le 0 -or [string]$artifact.sha256 -notmatch '^[0-9a-fA-F]{64}$') { return $false }
        try { $download = [Uri][string]$artifact.downloadUrl } catch { return $false }
        if (-not $download.IsAbsoluteUri -or $download.Scheme -ne 'https' -or
            $download.AbsolutePath -ne "/opticon/releases/$ReleaseVersion/$($artifact.file)") { return $false }
    }
    return $true
}

function Test-CompleteUpdateRelease($Manifest, [string]$ReleaseVersion) {
    if ([int]$Manifest.schemaVersion -ne 2) { return $false }
    $release = @($Manifest.artifacts | Where-Object { $_.version -eq $ReleaseVersion })
    if ($release.Count -ne 1 -or [string]$release[0].product -ne 'OpticonSource' -or
        [string]$release[0].architecture -ne 'source' -or
        [string]$release[0].file -ne "opticon-source-$ReleaseVersion.zip" -or
        [long]$release[0].size -le 0 -or [string]$release[0].sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        return $false
    }
    try { $download = [Uri][string]$release[0].downloadUrl } catch { return $false }
    return $download.IsAbsoluteUri -and $download.Scheme -eq 'https' -and
        $download.AbsolutePath -eq "/opticon/releases/$ReleaseVersion/$($release[0].file)"
}

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-SourceVersion }
Assert-StableVersion $Version
if ($StageOnly -and $CommitStaged) {
    throw '-StageOnly and -CommitStaged cannot be combined.'
}
if (($StageOnly -or $CommitStaged) -and $CheckOnly) {
    throw '-StageOnly and -CommitStaged cannot be combined with -CheckOnly.'
}
$manifest = $null
$manifestReadFailure = $null
try { $manifest = Get-ReleaseManifest } catch { $manifestReadFailure = $_ }
$updateManifest = $null
$updateManifestReadFailure = $null
try { $updateManifest = Get-ReleaseManifest -Update } catch { $updateManifestReadFailure = $_ }
if (-not $ForceRedeploy -and $null -ne $manifest -and $null -ne $updateManifest -and
    (Test-CompleteRelease $manifest $Version) -and (Test-CompleteUpdateRelease $updateManifest $Version)) {
    Write-Host "Opticon install and update release $Version are already deployed and complete." -ForegroundColor Green
    [pscustomobject]@{ Version = $Version; DeploymentRequired = $false; Deployed = $false }
    return
}

$newer = if ($null -eq $manifest) { @() } else { @($manifest.artifacts |
    Where-Object { $_.product -eq 'OpticonBundle' -and $_.version -match '^\d+\.\d+\.\d+$' } |
    ForEach-Object { [Version]$_.version } |
    Where-Object { $_ -gt [Version]$Version }) }
if ($newer.Count -ne 0) {
    $highest = $newer | Sort-Object -Descending | Select-Object -First 1
    throw "The live target release $highest is newer than source version $Version. Refusing a downgrade."
}

if ($CheckOnly) {
    if ($null -ne $manifestReadFailure) {
        throw "The live Opticon release manifest is unavailable: $($manifestReadFailure.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($SourceReleaseCertificateThumbprint) -or
        [string]::IsNullOrWhiteSpace($ProductCertificateThumbprint) -or
        [string]::IsNullOrWhiteSpace($Rfc3161TimestampUrl) -or
        [string]::IsNullOrWhiteSpace($SignToolPath)) {
        throw 'Device-bundle readiness requires explicit release/product certificate thumbprints, RFC3161 URL, and SignToolPath.'
    }
    # Delegate the non-mutating identity, AWS, CloudFormation, .NET, signing,
    # and DPAPI checks to the same publisher that performs the real release.
    & $publisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath `
        -ClientInstallValidationBase64 $ClientInstallValidationBase64 -ForceRedeploy:$ForceRedeploy -CheckOnly
    & $sourcePublisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath `
        -ClientInstallValidationBase64 $ClientInstallValidationBase64 -ForceRedeploy:$ForceRedeploy -CheckOnly
    Write-Host "Opticon install and update release $Version passed non-mutating publisher readiness checks." -ForegroundColor Green
    [pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $false; Ready = $true }
    return
}
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    throw '-ManifestPath is permitted only with -CheckOnly; a local manifest can never authorize production deployment.'
}

# StageOnly proves the exact checked-in source before producing the immutable
# receipt/archive. CommitStaged consumes that receipt after invitation
# cancellation, so it must not introduce a fresh mutable source dependency
# at the irreversible point. The publisher still independently revalidates the
# signed archive, S3 object, CloudFront bytes, and leased manifest commit.
if ([string]::IsNullOrWhiteSpace($SourceReleaseCertificateThumbprint) -or
    [string]::IsNullOrWhiteSpace($ProductCertificateThumbprint) -or
    [string]::IsNullOrWhiteSpace($Rfc3161TimestampUrl) -or
    [string]::IsNullOrWhiteSpace($SignToolPath)) {
    throw 'Device-bundle deployment requires explicit release/product certificate thumbprints, RFC3161 URL, and SignToolPath.'
}
$publisherArguments = @{
    Version = $Version
    ControlOrigin = $ControlOrigin
    SigningProfile = $SigningProfile
    SourceReleaseCertificateThumbprint = $SourceReleaseCertificateThumbprint
    ProductCertificateThumbprint = $ProductCertificateThumbprint
    Rfc3161TimestampUrl = $Rfc3161TimestampUrl
    SignToolPath = $SignToolPath
    ClientInstallValidationBase64 = $ClientInstallValidationBase64
    ForceRedeploy = $ForceRedeploy
}
if ($StageOnly) {
    Write-Host "Staging immutable Opticon install and update artifacts $Version; both live manifests will remain unchanged." -ForegroundColor Yellow
    $publisherArguments.SkipManifestPublish = $true
} elseif ($CommitStaged) {
    Write-Host "Publishing the verified Opticon install and update manifests $Version without rebuilding." -ForegroundColor Yellow
    $publisherArguments.SkipBuild = $true
} else {
    Write-Host "Opticon target release $Version is absent, incomplete, or unservable; publishing the signed device bundles now." -ForegroundColor Yellow
}
& $publisher @publisherArguments
if ($StageOnly) {
    & $sourcePublisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath `
        -ClientInstallValidationBase64 $ClientInstallValidationBase64 -ForceRedeploy:$ForceRedeploy -StageOnly
    Write-Host "Opticon install and update artifacts $Version are staged and fully verified; both live manifests are unchanged." -ForegroundColor Green
    [pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $false; Staged = $true }
    return
}

if ($CommitStaged) {
    & $sourcePublisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath `
        -ClientInstallValidationBase64 $ClientInstallValidationBase64 -ForceRedeploy:$ForceRedeploy -CommitStaged
} else {
    & $sourcePublisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath `
        -ClientInstallValidationBase64 $ClientInstallValidationBase64 -ForceRedeploy:$ForceRedeploy
}

$manifest = Get-ReleaseManifest
$updateManifest = Get-ReleaseManifest -Update
if (-not (Test-CompleteRelease $manifest $Version) -or
    -not (Test-CompleteUpdateRelease $updateManifest $Version)) {
    throw "Publisher returned successfully, but the live Opticon install or update release $Version is incomplete."
}
Write-Host "Opticon install and update release $Version are deployed and complete." -ForegroundColor Green
[pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $true }
