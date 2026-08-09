[CmdletBinding()]
param(
    [string]$StackName = "opticon-release-distribution",
    [string]$Region = "us-east-1",
    [string]$ArtifactDirectory = "",
    [string]$Version = "",
    [string]$ControlOrigin = "https://taildesk-egokick-control.fly.dev",
    [switch]$SkipBuild,
    [Alias("SkipFlyDeploy")]
    [switch]$SkipManifestPublish
)

# AWS authority comes only from the operator's authenticated CLI. The tiny
# manifest uses the existing DPAPI-protected Opticon admin HMAC credential.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$expectedAccount = "053663732727"
$bucket = "opticon-053663732727"
$flyRoot = Split-Path $PSScriptRoot -Parent
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
    if (Test-Path -LiteralPath $manifestPath) {
        $versions += @((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).artifacts |
            Where-Object { $_.product -eq "OpticonBundle" } |
            ForEach-Object { try { [version]$_.version } catch { $null } } |
            Where-Object { $_ })
    }
    if ($versions.Count -eq 0) { return "1.0.0" }
    $highest = $versions | Sort-Object -Descending | Select-Object -First 1
    return "$($highest.Major).$($highest.Minor).$($highest.Build + 1)"
}

function Invoke-Aws([string[]]$Arguments) {
    & aws @Arguments
    if ($LASTEXITCODE -ne 0) { throw "AWS CLI command failed: aws $($Arguments -join ' ')" }
}

function Invoke-CloudFrontVerification {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$ExpectedHash,
        [Parameter(Mandatory)][long]$ExpectedSize,
        [switch]$FullStream
    )
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.CheckCertificateRevocationList = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = if ($FullStream) { [TimeSpan]::FromMinutes(15) } else { [TimeSpan]::FromSeconds(45) }
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
            $contentRange = $rangeResult.Content.Headers.ContentRange
            if ([int]$rangeResult.StatusCode -ne 206 -or $null -eq $contentRange -or $contentRange.From -ne 0 -or
                $contentRange.To -ne 1023 -or $contentRange.Length -ne $ExpectedSize) {
                throw "CloudFront did not return the expected byte range."
            }
        } finally { $rangeResult.Dispose(); $range.Dispose() }

        if ($FullStream) {
            $response = $client.GetAsync($Url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode -or $response.Content.Headers.ContentLength -ne $ExpectedSize) { throw "CloudFront full-object GET failed." }
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
                    finally { $stream.Dispose() }
                } finally { $sha.Dispose() }
                if ($actual -ne $ExpectedHash.ToLowerInvariant()) { throw "CloudFront streamed bytes did not match the local SHA-256." }
            } finally { $response.Dispose() }
        }
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

function Publish-ManifestAtomically([byte[]]$Body) {
    $secretText = Get-OpticonAdminSecret
    $secret = [Text.Encoding]::UTF8.GetBytes($secretText)
    $secretText = $null
    try {
        $uri = [Uri]::new(([Uri]::new($ControlOrigin)), "/opticon/v1/releases/manifest")
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
        [Parameter(Mandatory)]$Record
    )
    if ([string]$Record.sdkVersion -ne '8.0.423' -or [string]$Record.runtimeVersion -ne '8.0.29' -or
        [string]$Record.sourceManifestKeyId -ne 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
        @($Record.targetRuntimes).Count -ne 2 -or [string]$Record.targetRuntimes[0] -ne 'win-x64' -or
        [string]$Record.targetRuntimes[1] -ne 'win-arm64' -or [string]$Record.sourceManifestSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'The source artifact does not carry the exact supported build pins.'
    }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
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
        if ($manifestEntry.Length -le 0 -or $manifestEntry.Length -gt 1MB) { throw 'The source inner manifest has an invalid size.' }
        $manifestStream = $manifestEntry.Open()
        try {
            $memory = [IO.MemoryStream]::new()
            try { $manifestStream.CopyTo($memory); $manifestBytes = $memory.ToArray() } finally { $memory.Dispose() }
        } finally { $manifestStream.Dispose() }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $manifestHash = ([BitConverter]::ToString($sha.ComputeHash($manifestBytes))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        if ($manifestHash -ne [string]$Record.sourceManifestSha256) { throw 'The source inner-manifest hash does not match the outer artifact record.' }
        $signatureEntry = $entries['source-manifest.sig']
        if ($signatureEntry.Length -le 0 -or $signatureEntry.Length -gt 16KB) { throw 'The source inner-manifest signature has an invalid size.' }
        $signatureReader = [IO.StreamReader]::new($signatureEntry.Open(), [Text.Encoding]::UTF8, $false)
        try { $signature = [Convert]::FromBase64String($signatureReader.ReadToEnd().Trim()) } finally { $signatureReader.Dispose() }
        $certificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint -eq [string]$Record.sourceManifestKeyId } | Select-Object -First 1
        if (-not $certificate) { throw 'The pinned Opticon source-manifest public certificate is unavailable.' }
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try {
            if (-not $rsa.VerifyData($manifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'The source inner-manifest RSA-PSS signature is invalid.' }
        } finally { $rsa.Dispose(); $certificate.Dispose() }
        $inner = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
        if ([int]$inner.schemaVersion -ne 1 -or [string]$inner.version -ne [string]$Record.version -or
            [string]$inner.sdkVersion -ne [string]$Record.sdkVersion -or [string]$inner.runtimeVersion -ne [string]$Record.runtimeVersion -or
            @($inner.targetRuntimes).Count -ne 2 -or [string]$inner.targetRuntimes[0] -ne [string]$Record.targetRuntimes[0] -or
            [string]$inner.targetRuntimes[1] -ne [string]$Record.targetRuntimes[1]) {
            throw 'The source inner-manifest release metadata does not match the outer record.'
        }
        $declared = @{'source-manifest.json' = $true; 'source-manifest.sig' = $true}
        foreach ($file in @($inner.files)) {
            $name = ([string]$file.path).Replace('\', '/')
            $key = $name.ToLowerInvariant()
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name.Split('/') -contains '..' -or
                $declared.ContainsKey($key) -or -not $entries.ContainsKey($key) -or
                [long]$file.size -ne [long]$entries[$key].Length -or [string]$file.sha256 -notmatch '^[a-f0-9]{64}$') {
                throw "The source inner manifest has an invalid declaration for $name."
            }
            $declared[$key] = $true
            $input = $entries[$key].Open()
            $fileSha = [Security.Cryptography.SHA256]::Create()
            try { $actual = ([BitConverter]::ToString($fileSha.ComputeHash($input))).Replace('-', '').ToLowerInvariant() }
            finally { $fileSha.Dispose(); $input.Dispose() }
            if ($actual -ne [string]$file.sha256) { throw "The source file hash is invalid for $name." }
        }
        if ($declared.Count -ne $entries.Count) { throw 'The source archive contains undeclared extra files.' }
    } finally { $zip.Dispose() }
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
$releaseArtifacts = @($manifest.artifacts | Where-Object { $_.version -eq $version -and $_.product -in @("OpticonBundle", "OpticonBootstrap", "OpticonSource") })
$bundles = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonBundle" })
$bootstraps = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonBootstrap" })
$sources = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonSource" })
if ($bundles.Count -ne 2 -or $bootstraps.Count -ne 1 -or $sources.Count -ne 1) { throw "Build did not produce two bundles, one bootstrap, and one source archive for $version." }
$bootstrapPath = Join-Path $ArtifactDirectory ([string]$bootstraps[0].file)
$bootstrapSignature = Get-AuthenticodeSignature -LiteralPath $bootstrapPath
if (-not $bootstrapSignature.SignerCertificate -or
    $bootstrapSignature.SignerCertificate.Thumbprint -ne [string]$bootstraps[0].signerThumbprint -or
    $bootstrapSignature.Status -in @('NotSigned', 'HashMismatch')) {
    throw 'The source bootstrap is unsigned, altered, or signed by an unexpected certificate.'
}
Assert-OpticonSourceArchive -Path (Join-Path $ArtifactDirectory ([string]$sources[0].file)) -Record $sources[0]
$fullStreamFile = [string]($bundles | Sort-Object { [long]$_.size } | Select-Object -First 1).file

$temporaryConfig = Join-Path ([IO.Path]::GetTempPath()) ("opticon-aws-" + [Guid]::NewGuid().ToString("N") + ".config")
@("[default]", "s3 =", "    max_concurrent_requests = 20", "    multipart_threshold = 64MB", "    multipart_chunksize = 16MB") | Set-Content -LiteralPath $temporaryConfig -Encoding ascii
$previousConfig = $env:AWS_CONFIG_FILE
$env:AWS_CONFIG_FILE = $temporaryConfig
try {
    foreach ($artifact in $releaseArtifacts) {
        $path = Join-Path $ArtifactDirectory ([string]$artifact.file)
        $info = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($info.Length -ne [long]$artifact.size -or $hash -ne [string]$artifact.sha256) { throw "Local release verification failed for $($artifact.file)." }
        $key = "opticon/releases/$version/$($artifact.file)"
        $contentType = if ($artifact.product -eq "OpticonBootstrap") { "application/vnd.microsoft.portable-executable" } else { "application/zip" }
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $existingHeadJson = aws s3api head-object --bucket $bucket --key $key --checksum-mode ENABLED --output json 2>$null
            $objectExists = $LASTEXITCODE -eq 0
        } finally { $ErrorActionPreference = $savedPreference }
        if ($objectExists) {
            $head = $existingHeadJson | ConvertFrom-Json
        } else {
            Invoke-Aws @("s3", "cp", $path, "s3://$bucket/$key", "--expected-size", "$($info.Length)", "--content-type", $contentType, "--cache-control", "public, max-age=31536000, immutable", "--sse", "AES256", "--checksum-algorithm", "SHA256", "--metadata", "sha256=$hash", "--only-show-errors")
            $head = aws s3api head-object --bucket $bucket --key $key --checksum-mode ENABLED --output json | ConvertFrom-Json
        }
        if ($LASTEXITCODE -ne 0 -or $head.ContentLength -ne $info.Length -or
            -not ([string]$head.Metadata.sha256).Equals($hash, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::IsNullOrWhiteSpace([string]$head.ChecksumSHA256) -or
            $head.ContentType -ne $contentType -or $head.CacheControl -ne "public, max-age=31536000, immutable" -or
            $head.ServerSideEncryption -ne "AES256") {
            if ($objectExists) { throw "Refusing to overwrite immutable release object s3://$bucket/$key because it does not match the local release." }
            throw "S3 verification failed for $key."
        }
        $url = "https://$($output.DistributionDomainName)/$key"
        $deadline = [DateTime]::UtcNow.AddMinutes(12)
        do {
            try {
                Invoke-CloudFrontVerification -Url $url -ExpectedHash $hash -ExpectedSize $info.Length -FullStream:($artifact.file -eq $fullStreamFile -or $artifact.product -eq 'OpticonSource')
                break
            } catch {
                if ([DateTime]::UtcNow -ge $deadline) { throw }
                Start-Sleep -Seconds 5
            }
        } while ($true)
        $artifact | Add-Member -NotePropertyName downloadUrl -NotePropertyValue $url -Force
    }

    $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllBytes("$manifestPath.new", $manifestBytes)
    Move-Item -LiteralPath "$manifestPath.new" -Destination $manifestPath -Force
} finally {
    $env:AWS_CONFIG_FILE = $previousConfig
    Remove-Item -LiteralPath $temporaryConfig -Force -ErrorAction SilentlyContinue
}

if (-not $SkipManifestPublish) {
    Publish-ManifestAtomically ([IO.File]::ReadAllBytes($manifestPath))
    $live = Invoke-RestMethod -Uri "$($ControlOrigin.TrimEnd('/'))/opticon/artifacts/v1/manifest.json" -Method Get
    $liveRelease = @($live.artifacts | Where-Object { $_.version -eq $version -and $_.product -in @("OpticonBundle", "OpticonBootstrap", "OpticonSource") })
    if ($liveRelease.Count -ne 4 -or @($liveRelease | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.downloadUrl) }).Count -ne 0) {
        throw "Fly accepted the manifest but did not serve the complete CloudFront release."
    }
}

[pscustomobject]@{
    Version = $version
    Bucket = $bucket
    Distribution = $output.DistributionDomainName
    FullStreamVerified = $fullStreamFile
    Artifacts = $releaseArtifacts | Select-Object product, file, size, sha256, downloadUrl
}
