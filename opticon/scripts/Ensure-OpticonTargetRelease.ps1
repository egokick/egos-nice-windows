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
    # Build/upload/verify the immutable source release, but leave the live
    # invitation manifest untouched until a later explicit commit.
    [switch]$StageOnly,
    # Commit the exact local-or-S3 source stage receipt without rebuilding or
    # uploading a replacement archive.
    [switch]$CommitStaged
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.1') {
    throw 'The Opticon target-release publisher requires PowerShell 7.1 or newer. Run this script with pwsh.exe, not Windows PowerShell.'
}
Add-Type -AssemblyName System.Net.Http
$opticonRoot = Split-Path $PSScriptRoot -Parent
$publisher = Join-Path $opticonRoot 'fly-headscale\scripts\Publish-OpticonSourceRelease.ps1'

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

function Get-ReleaseManifest {
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        return Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
    }

    $origin = [Uri]$ControlOrigin
    if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or
        -not [string]::IsNullOrWhiteSpace($origin.UserInfo)) {
        throw 'The Opticon control origin must be an HTTPS origin without credentials.'
    }
    $uri = [Uri]::new($origin, '/opticon/artifacts/v1/manifest.json')
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
    if ([int]$Manifest.schemaVersion -ne 2) { return $false }
    $release = @($Manifest.artifacts | Where-Object { $_.version -eq $ReleaseVersion })
    if ($release.Count -ne 1 -or @($Manifest.artifacts | Where-Object { $_.product -ne 'OpticonSource' }).Count -ne 0) { return $false }
    $artifact = $release[0]
    if ($artifact.product -ne 'OpticonSource' -or $artifact.architecture -ne 'source' -or
        $artifact.file -ne "opticon-source-$ReleaseVersion.zip" -or [long]$artifact.size -le 0 -or
        [string]$artifact.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$artifact.sourceManifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$artifact.sdkVersion -ne '10.*.*' -or [string]$artifact.runtimeVersion -ne '10.0.10' -or
        @($artifact.targetRuntimes).Count -ne 2 -or [string]$artifact.targetRuntimes[0] -ne 'win-x64' -or
        [string]$artifact.targetRuntimes[1] -ne 'win-arm64') {
        return $false
    }
    try { $download = [Uri][string]$artifact.downloadUrl } catch { return $false }
    return $download.IsAbsoluteUri -and $download.Scheme -eq 'https' -and
        $download.AbsolutePath -eq "/opticon/releases/$ReleaseVersion/$($artifact.file)"
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
if (-not $ForceRedeploy -and $null -ne $manifest -and (Test-CompleteRelease $manifest $Version)) {
    Write-Host "Opticon target release $Version is already deployed and complete." -ForegroundColor Green
    [pscustomobject]@{ Version = $Version; DeploymentRequired = $false; Deployed = $false }
    return
}

$newer = if ($null -eq $manifest) { @() } else { @($manifest.artifacts |
    Where-Object { $_.product -eq 'OpticonSource' -and $_.version -match '^\d+\.\d+\.\d+$' } |
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
        throw 'Source-only target readiness requires explicit source-release/product certificate thumbprints, RFC3161 URL, and SignToolPath.'
    }
    # Delegate the non-mutating identity, AWS, CloudFormation, .NET, signing,
    # and DPAPI checks to the same publisher that performs the real release.
    & $publisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath `
        -ClientInstallValidationBase64 $ClientInstallValidationBase64 -ForceRedeploy:$ForceRedeploy -CheckOnly
    Write-Host "Opticon target release $Version passed non-mutating publisher readiness checks." -ForegroundColor Green
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
    throw 'Source-only target deployment requires explicit source-release/product certificate thumbprints, RFC3161 URL, and SignToolPath.'
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
    Write-Host "Staging immutable Opticon target source release $Version; the live invitation manifest will remain unchanged." -ForegroundColor Yellow
    $publisherArguments.StageOnly = $true
} elseif ($CommitStaged) {
    Write-Host "Committing the verified staged Opticon target source release $Version without rebuilding or uploading a replacement." -ForegroundColor Yellow
    $publisherArguments.CommitStaged = $true
} else {
    Write-Host "Opticon target release $Version is absent, incomplete, or unservable; publishing the one source archive now." -ForegroundColor Yellow
}
& $publisher @publisherArguments
if ($StageOnly) {
    Write-Host "Opticon target release $Version is staged and fully verified; its live invitation manifest is unchanged." -ForegroundColor Green
    [pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $false; Staged = $true }
    return
}

$manifest = Get-ReleaseManifest
if (-not (Test-CompleteRelease $manifest $Version)) {
    throw "Publisher returned successfully, but live Opticon target release $Version is incomplete."
}
Write-Host "Opticon target release $Version is deployed and complete." -ForegroundColor Green
[pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $true }
