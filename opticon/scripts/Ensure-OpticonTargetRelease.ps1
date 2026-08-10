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
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
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
        [string]$artifact.sdkVersion -ne '10.0.302' -or [string]$artifact.runtimeVersion -ne '10.0.10' -or
        @($artifact.targetRuntimes).Count -ne 2 -or [string]$artifact.targetRuntimes[0] -ne 'win-x64' -or
        [string]$artifact.targetRuntimes[1] -ne 'win-arm64') {
        return $false
    }
    try { $download = [Uri][string]$artifact.downloadUrl } catch { return $false }
    return $download.IsAbsoluteUri -and $download.Scheme -eq 'https' -and
        $download.AbsolutePath -eq "/opticon/releases/$ReleaseVersion/$($artifact.file)"
}

function Assert-ReleaseSourceIsPublishable {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'Git is required to prove that an automatic Opticon release is reproducible.'
    }
    $changes = @(& git -C $opticonRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the Opticon Git worktree.' }
    if ($changes.Count -ne 0) {
        throw 'Target deployment is required, but the worktree is not clean. Commit and push the release source, then build again.'
    }
    $branch = [string](& git -C $opticonRoot symbolic-ref --short HEAD)
    if ($LASTEXITCODE -ne 0 -or $branch.Trim() -ne 'main') {
        throw 'Automatic Opticon target deployment is allowed only from the main branch.'
    }
    & git -C $opticonRoot fetch --quiet origin main
    if ($LASTEXITCODE -ne 0) { throw 'Could not refresh origin/main before target deployment.' }
    $head = ([string](& git -C $opticonRoot rev-parse HEAD)).Trim()
    $originMain = ([string](& git -C $opticonRoot rev-parse refs/remotes/origin/main)).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $originMain) {
        throw 'Target deployment is required, but main is not exactly synchronized with origin/main. Push the release commit, then build again.'
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-SourceVersion }
Assert-StableVersion $Version
$manifest = $null
$manifestReadFailure = $null
try { $manifest = Get-ReleaseManifest } catch { $manifestReadFailure = $_ }
if ($null -ne $manifest -and (Test-CompleteRelease $manifest $Version)) {
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
    $reason = if ($null -ne $manifestReadFailure) { " The live manifest is unavailable: $($manifestReadFailure.Exception.Message)" } else { '' }
    Write-Host "Opticon target release $Version requires source-only deployment.$reason" -ForegroundColor Yellow
    [pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $false }
    return
}
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    throw '-ManifestPath is permitted only with -CheckOnly; a local manifest can never authorize production deployment.'
}

Assert-ReleaseSourceIsPublishable
if ([string]::IsNullOrWhiteSpace($SourceReleaseCertificateThumbprint) -or
    [string]::IsNullOrWhiteSpace($ProductCertificateThumbprint) -or
    [string]::IsNullOrWhiteSpace($Rfc3161TimestampUrl) -or
    [string]::IsNullOrWhiteSpace($SignToolPath)) {
    throw 'Source-only target deployment requires explicit source-release/product certificate thumbprints, RFC3161 URL, and SignToolPath.'
}
Write-Host "Opticon target release $Version is absent, incomplete, or unservable; publishing the one source archive now." -ForegroundColor Yellow
& $publisher -Version $Version -ControlOrigin $ControlOrigin -SigningProfile $SigningProfile `
    -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
    -ProductCertificateThumbprint $ProductCertificateThumbprint `
    -Rfc3161TimestampUrl $Rfc3161TimestampUrl -SignToolPath $SignToolPath
if ($LASTEXITCODE -ne 0) { throw "Publishing Opticon target release $Version failed." }

$manifest = Get-ReleaseManifest
if (-not (Test-CompleteRelease $manifest $Version)) {
    throw "Publisher returned successfully, but live Opticon target release $Version is incomplete."
}
Write-Host "Opticon target release $Version is deployed and complete." -ForegroundColor Green
[pscustomobject]@{ Version = $Version; DeploymentRequired = $true; Deployed = $true }
