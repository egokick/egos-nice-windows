[CmdletBinding()]
param(
    [string]$StackName = "opticon-release-distribution",
    [string]$Region = "us-east-1",
    [string]$ArtifactDirectory = "",
    [string]$Version = "",
    [switch]$SkipBuild,
    [switch]$SkipFlyDeploy
)

# This script deliberately has no AWS credentials parameter.  The operator's
# authenticated AWS CLI provides short-lived publishing authority only.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$expectedAccount = "053663732727"
$bucket = "opticon-053663732727"
$flyRoot = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $flyRoot -Parent
$ArtifactDirectory = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) { Join-Path $flyRoot "artifacts" } else { $ArtifactDirectory }
$manifestPath = Join-Path $ArtifactDirectory "manifest.json"

function Get-NextReleaseVersion {
    $keys = @(aws s3api list-objects-v2 --bucket $bucket --prefix "opticon/releases/" --query "Contents[].Key" --output json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw "Could not list published Opticon releases." }
    $versions = @($keys | ForEach-Object {
        if ($_ -match '^opticon/releases/(?<version>[0-9]+\.[0-9]+\.[0-9]+)/opticon-bundle-.+-(managed|controller)-win-x64\.zip$') {
            try { [version]$Matches.version } catch { $null }
        }
    } | Where-Object { $_ })
    # During the one-time migration, Fly's existing public manifest is also a
    # published release source. Including it prevents an S3-first rollout from
    # accidentally assigning a lower version than the release Agents see now.
    if (Test-Path -LiteralPath $manifestPath) {
        $legacyVersions = @((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).artifacts |
            Where-Object { $_.product -eq "OpticonBundle" } |
            ForEach-Object { try { [version]$_.version } catch { $null } } |
            Where-Object { $_ })
        $versions += $legacyVersions
    }
    if ($versions.Count -eq 0) { return "1.0.0" }
    $highest = $versions | Sort-Object -Descending | Select-Object -First 1
    return "$($highest.Major).$($highest.Minor).$($highest.Build + 1)"
}
function Invoke-Aws([string[]]$Arguments) {
    & aws @Arguments
    if ($LASTEXITCODE -ne 0) { throw "AWS CLI command failed: aws $($Arguments -join ' ')" }
}
function Invoke-CloudFrontVerification([string]$Url, [string]$ExpectedHash, [long]$ExpectedSize) {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.CheckCertificateRevocationList = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(45)
    try {
        $head = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, $Url)
        $headResult = $client.SendAsync($head).GetAwaiter().GetResult()
        try {
            if (-not $headResult.IsSuccessStatusCode -or $headResult.Content.Headers.ContentLength -ne $ExpectedSize) {
                throw "CloudFront HEAD did not return the expected immutable object metadata."
            }
        } finally { $headResult.Dispose(); $head.Dispose() }
        $range = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
        $range.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new(0, 1023)
        $rangeResult = $client.SendAsync($range, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if ([int]$rangeResult.StatusCode -ne 206) { throw "CloudFront did not honor a byte range request." }
        } finally { $rangeResult.Dispose(); $range.Dispose() }
        $response = $client.GetAsync($Url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) { throw "CloudFront full-object GET failed." }
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                } finally { $stream.Dispose() }
            } finally { $sha.Dispose() }
            if ($actual -ne $ExpectedHash.ToLowerInvariant()) { throw "CloudFront streamed bytes did not match the local SHA-256." }
        } finally { $response.Dispose() }
    } finally { $client.Dispose(); $handler.Dispose() }
}

$identity = aws sts get-caller-identity --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $identity.Account -ne $expectedAccount) { throw "Refusing to publish outside AWS account $expectedAccount." }
$outputs = aws cloudformation describe-stacks --region $Region --stack-name $StackName --query "Stacks[0].Outputs" --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "Opticon CloudFormation stack '$StackName' was not found. Run Provision-OpticonReleaseDistribution.ps1 first." }
$output = @{}; foreach ($item in $outputs) { $output[$item.OutputKey] = $item.OutputValue }
if ($output.BucketName -ne $bucket -or $output.DistributionDomainName -notmatch '^[a-z0-9-]+\.cloudfront\.net$') { throw "CloudFormation outputs do not identify the expected private Opticon distribution." }

$version = if ([string]::IsNullOrWhiteSpace($Version)) { Get-NextReleaseVersion } else { $Version }
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "Version must be a stable major.minor.patch release." }
if ($SkipBuild -and [string]::IsNullOrWhiteSpace($Version)) { throw "-SkipBuild requires an explicit -Version so an existing build is never misidentified." }
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "Build-OpticonBundles.ps1") -Version $version
    if ($LASTEXITCODE -ne 0) { throw "Opticon bundle build failed." }
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$bundles = @($manifest.artifacts | Where-Object { $_.product -eq "OpticonBundle" -and $_.version -eq $version })
if ($bundles.Count -ne 2) { throw "Build did not produce exactly two $version Opticon bundles." }

# Keep transfer tuning process-local.  It allows AWS CLI multipart uploads to
# saturate normal upstream bandwidth without mutating the operator's profile.
$temporaryConfig = Join-Path ([IO.Path]::GetTempPath()) ("opticon-aws-" + [Guid]::NewGuid().ToString("N") + ".config")
@("[default]", "s3 =", "    max_concurrent_requests = 20", "    multipart_threshold = 64MB", "    multipart_chunksize = 16MB") | Set-Content -LiteralPath $temporaryConfig -Encoding ascii
$previousConfig = $env:AWS_CONFIG_FILE
$env:AWS_CONFIG_FILE = $temporaryConfig
try {
    foreach ($artifact in $bundles) {
        $path = Join-Path $ArtifactDirectory ([string]$artifact.file)
        $info = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($info.Length -ne [long]$artifact.size -or $hash -ne [string]$artifact.sha256) { throw "Local release verification failed for $($artifact.file)." }
        $key = "opticon/releases/$version/$($artifact.file)"
        # A missing key is the expected first-release state. Suppress only the
        # AWS CLI's nonzero diagnostic while preserving the immutable-write
        # refusal when the object does exist.
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $null = aws s3api head-object --bucket $bucket --key $key --output json 2>$null
            $objectExists = $LASTEXITCODE -eq 0
        } finally { $ErrorActionPreference = $savedPreference }
        if ($objectExists) { throw "Refusing to overwrite immutable release object s3://$bucket/$key." }
        Invoke-Aws @("s3", "cp", $path, "s3://$bucket/$key", "--expected-size", "$($info.Length)", "--content-type", "application/zip", "--cache-control", "public, max-age=31536000, immutable", "--sse", "AES256", "--checksum-algorithm", "SHA256", "--metadata", "sha256=$hash", "--only-show-errors")
        $head = aws s3api head-object --bucket $bucket --key $key --checksum-mode ENABLED --output json | ConvertFrom-Json
        # Multipart uploads expose a composite native checksum.  Preserve it,
        # and separately require the operator-calculated full-file SHA-256 in
        # immutable S3 metadata before CloudFront verification can begin.
        if ($LASTEXITCODE -ne 0 -or $head.ContentLength -ne $info.Length -or
            -not ([string]$head.Metadata.sha256).Equals($hash, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::IsNullOrWhiteSpace([string]$head.ChecksumSHA256)) {
            throw "S3 verification failed for $key."
        }
        $url = "https://$($output.DistributionDomainName)/$key"
        $deadline = [DateTime]::UtcNow.AddMinutes(12)
        do {
            try { Invoke-CloudFrontVerification $url $hash $info.Length; break } catch {
                if ([DateTime]::UtcNow -ge $deadline) { throw }
                Start-Sleep -Seconds 5
            }
        } while ($true)
        $artifact | Add-Member -NotePropertyName downloadUrl -NotePropertyValue $url -Force
    }

    # Publishing this tiny Fly manifest is the commit point: it happens only
    # after both independently streamed CloudFront hashes match local bytes.
    $temporaryManifest = "$manifestPath.new"
    [IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryManifest -Destination $manifestPath -Force
} finally {
    $env:AWS_CONFIG_FILE = $previousConfig
    Remove-Item -LiteralPath $temporaryConfig -Force -ErrorAction SilentlyContinue
}

if (-not $SkipFlyDeploy) {
    $tokenLine = Get-Content 'C:\source\babelfish\.env' | Where-Object { $_ -match '^FLY_API_TOKEN=' } | Select-Object -First 1
    if (-not $tokenLine) { throw "FLY_API_TOKEN was not found in the approved operator environment." }
    $env:FLY_API_TOKEN = ($tokenLine -split '=', 2)[1].Trim().Trim('"').Trim("'")
    try {
        Push-Location $flyRoot
        flyctl deploy --remote-only --app taildesk-egokick-control --yes
        if ($LASTEXITCODE -ne 0) { throw "Fly deployment failed; the old manifest remains live." }
    } finally {
        Pop-Location
        Remove-Item Env:\FLY_API_TOKEN -ErrorAction SilentlyContinue
    }
}
[pscustomobject]@{ Version = $version; Bucket = $bucket; Distribution = $output.DistributionDomainName; Bundles = $bundles | Select-Object file, size, sha256, downloadUrl }
