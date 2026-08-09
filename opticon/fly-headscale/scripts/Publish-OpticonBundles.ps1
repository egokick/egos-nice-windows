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
    [Parameter(Mandatory)][string]$Rfc3161TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$AwsProfile = 'default',
    [switch]$SkipBuild,
    [Alias("SkipFlyDeploy")]
    [switch]$SkipManifestPublish
)

# AWS authority comes only from the operator's authenticated CLI. The tiny
# manifest uses the existing DPAPI-protected Opticon admin HMAC credential.
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
$invitationSigningThumbprint = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'
$SourceReleaseCertificateThumbprint = $SourceReleaseCertificateThumbprint.ToUpperInvariant()
$ProductCertificateThumbprint = $ProductCertificateThumbprint.ToUpperInvariant()
if ($SourceReleaseCertificateThumbprint -eq $invitationSigningThumbprint -or
    $ProductCertificateThumbprint -eq $invitationSigningThumbprint -or
    $SourceReleaseCertificateThumbprint -eq $ProductCertificateThumbprint) {
    throw 'Production invitation, source-release, and Authenticode trust domains must be pairwise distinct.'
}
$timestampUri = $null
if (-not [Uri]::TryCreate($Rfc3161TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
    $timestampUri.Scheme -ne [Uri]::UriSchemeHttps -or -not [string]::IsNullOrEmpty($timestampUri.UserInfo)) {
    throw 'Rfc3161TimestampUrl must be an absolute HTTPS URL without user information.'
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

function Invoke-AwsCli {
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

function Get-LocalArtifactPath {
    param([Parameter(Mandatory)][string]$FileName)
    if ([string]::IsNullOrWhiteSpace($FileName) -or
        -not [IO.Path]::GetFileName($FileName).Equals($FileName, [StringComparison]::Ordinal) -or
        $FileName.Contains('/') -or $FileName.Contains('\')) {
        throw 'An Opticon artifact filename is unsafe.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $ArtifactDirectory $FileName))
    if (-not [IO.Path]::GetDirectoryName($path).Equals([IO.Path]::GetFullPath($ArtifactDirectory), [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The exact local artifact is missing or escaped its directory: $FileName"
    }
    $current = $path
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
        [IO.DirectoryInfo]::new($path).Create($security)
    } finally { $identity.Dispose() }
    if (-not (Test-Path -LiteralPath $path -PathType Container) -or
        ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The private publisher directory could not be created safely.'
    }
    return $path
}

function Assert-ProductSignature {
    param([Parameter(Mandatory)][string]$Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $allowedStatus = if ($SigningProfile -eq 'Production') {
        @([Management.Automation.SignatureStatus]::Valid)
    } else {
        @([Management.Automation.SignatureStatus]::Valid, [Management.Automation.SignatureStatus]::UnknownError)
    }
    if ($signature.Status -notin $allowedStatus -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Thumbprint.Equals($ProductCertificateThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
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

function Get-NextReleaseVersion {
    $listResult = Invoke-AwsCli -Arguments @('s3api', 'list-objects-v2', '--bucket', $bucket,
        '--prefix', 'opticon/releases/', '--query', 'Contents[].Key', '--output', 'json')
    if ($listResult.ExitCode -ne 0) { throw "Could not list published Opticon releases: $($listResult.Error.Trim())" }
    $keys = @($listResult.Output | ConvertFrom-Json)
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
        [Parameter(Mandatory)]$Record
    )
    Assert-ProductionArtifactTrust -Artifact $Record
    if ([long]$Record.size -lt 1024 -or [long]$Record.size -gt 256MB -or
        (Get-Item -LiteralPath $Path).Length -ne [long]$Record.size -or
        [string]$Record.sdkVersion -ne '10.0.302' -or [string]$Record.runtimeVersion -ne '10.0.10' -or
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
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [byte[]]$script:VerifiedSourceReleaseCertificateRawData)
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try {
            if (-not $rsa.VerifyData($manifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.RSASignaturePadding]::Pss)) {
                throw 'The bundle release-manifest RSA-PSS signature is invalid.'
            }
        } finally { $rsa.Dispose(); $certificate.Dispose() }
        $inner = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
        if ([int]$inner.schemaVersion -ne 1 -or [string]$inner.version -ne [string]$Record.version -or
            [string]$inner.role -ne [string]$Record.role -or [string]$inner.architecture -ne [string]$Record.architecture -or
            [string]$inner.signingProfile -cne $SigningProfile -or
            [string]$inner.sourceReleaseKeyId -ne $SourceReleaseCertificateThumbprint -or
            [string]$inner.productSignerThumbprint -ne $ProductCertificateThumbprint) {
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
                if ([string]$file.signerThumbprint -ne $ProductCertificateThumbprint) {
                    throw "Bundle executable $name has the wrong signed publisher declaration."
                }
                Assert-ProductSignature -Path $destination
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

$version = if ([string]::IsNullOrWhiteSpace($Version)) { Get-NextReleaseVersion } else { $Version }
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw "Version must be a stable major.minor.patch release." }
if ($SkipBuild -and [string]::IsNullOrWhiteSpace($Version)) { throw "-SkipBuild requires an explicit -Version so an existing build is never misidentified." }
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "Build-OpticonBundles.ps1") -Version $version `
        -SigningProfile $SigningProfile `
        -SourceReleaseCertificateThumbprint $SourceReleaseCertificateThumbprint `
        -ProductCertificateThumbprint $ProductCertificateThumbprint `
        -Rfc3161TimestampUrl $Rfc3161TimestampUrl `
        -SignToolPath $SignToolPath
    if ($LASTEXITCODE -ne 0) { throw "Opticon bundle build failed." }
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$allOpticonArtifacts = @($manifest.artifacts | Where-Object { $_.product -in @('OpticonBundle', 'OpticonBootstrap', 'OpticonSource') })
if ($allOpticonArtifacts.Count -eq 0) { throw 'The release manifest has no Opticon artifacts.' }
foreach ($artifact in $allOpticonArtifacts) { Assert-ProductionArtifactTrust -Artifact $artifact }
$releaseArtifacts = @($manifest.artifacts | Where-Object { $_.version -eq $version -and $_.product -in @("OpticonBundle", "OpticonBootstrap", "OpticonSource") })
$bundles = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonBundle" })
$bootstraps = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonBootstrap" })
$sources = @($releaseArtifacts | Where-Object { $_.product -eq "OpticonSource" })
if ($bundles.Count -ne 2 -or $bootstraps.Count -ne 1 -or $sources.Count -ne 1) { throw "Build did not produce two bundles, one bootstrap, and one source archive for $version." }
$bootstrapPath = Get-LocalArtifactPath ([string]$bootstraps[0].file)
if ([string]$bootstraps[0].signerThumbprint -ne $ProductCertificateThumbprint) {
    throw 'The source bootstrap outer signer pin does not match the production product signer.'
}
Assert-ProductSignature -Path $bootstrapPath
Assert-OpticonSourceArchive -Path (Get-LocalArtifactPath ([string]$sources[0].file)) -Record $sources[0]
foreach ($bundle in @($allOpticonArtifacts | Where-Object { $_.product -eq 'OpticonBundle' })) {
    Assert-OpticonBundleArchive -Path (Get-LocalArtifactPath ([string]$bundle.file)) -Record $bundle
}
$fullStreamFiles = @($releaseArtifacts | ForEach-Object { [string]$_.file })

$temporaryConfig = Join-Path $script:AwsScratchDirectory 'aws.config'
@("[default]", "s3 =", "    max_concurrent_requests = 20", "    multipart_threshold = 64MB", "    multipart_chunksize = 16MB") | Set-Content -LiteralPath $temporaryConfig -Encoding ascii
$previousConfig = $script:AwsConfigFile
$script:AwsConfigFile = $temporaryConfig
try {
    foreach ($artifact in $releaseArtifacts) {
        $path = Get-LocalArtifactPath ([string]$artifact.file)
        $info = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedChecksum = [Convert]::ToBase64String([Convert]::FromHexString($hash))
        if ($info.Length -ne [long]$artifact.size -or $hash -ne [string]$artifact.sha256) { throw "Local release verification failed for $($artifact.file)." }
        $key = "opticon/releases/$version/$($artifact.file)"
        $contentType = if ($artifact.product -eq "OpticonBootstrap") { "application/vnd.microsoft.portable-executable" } else { "application/zip" }
        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $existingHeadResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket,
                '--key', $key, '--checksum-mode', 'ENABLED', '--output', 'json')
            $existingHeadJson = $existingHeadResult.Output
            $objectExists = $existingHeadResult.ExitCode -eq 0
        } finally { $ErrorActionPreference = $savedPreference }
        if ($objectExists) {
            $head = $existingHeadJson | ConvertFrom-Json
        } else {
            Invoke-Aws @("s3", "cp", $path, "s3://$bucket/$key", "--expected-size", "$($info.Length)", "--content-type", $contentType, "--cache-control", "public, max-age=31536000, immutable", "--sse", "AES256", "--checksum-algorithm", "SHA256", "--metadata", "sha256=$hash", "--only-show-errors")
            $headResult = Invoke-AwsCli -Arguments @('s3api', 'head-object', '--bucket', $bucket,
                '--key', $key, '--checksum-mode', 'ENABLED', '--output', 'json')
            if ($headResult.ExitCode -ne 0) { throw "S3 head-object verification failed: $($headResult.Error.Trim())" }
            $head = $headResult.Output | ConvertFrom-Json
        }
        if ($head.ContentLength -ne $info.Length -or
            -not ([string]$head.Metadata.sha256).Equals($hash, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$head.ChecksumSHA256).Equals($expectedChecksum, [StringComparison]::Ordinal) -or
            $head.ContentType -ne $contentType -or $head.CacheControl -ne "public, max-age=31536000, immutable" -or
            $head.ServerSideEncryption -ne "AES256") {
            if ($objectExists) { throw "Refusing to overwrite immutable release object s3://$bucket/$key because it does not match the local release." }
            throw "S3 verification failed for $key."
        }
        $url = "https://$($output.DistributionDomainName)/$key"
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
        $artifact | Add-Member -NotePropertyName downloadUrl -NotePropertyValue $url -Force
    }

    $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllBytes("$manifestPath.new", $manifestBytes)
    Move-Item -LiteralPath "$manifestPath.new" -Destination $manifestPath -Force
} finally {
    $script:AwsConfigFile = $previousConfig
    Remove-Item -LiteralPath $temporaryConfig -Force -ErrorAction SilentlyContinue
}

if (-not $SkipManifestPublish) {
    Publish-ManifestAtomically ([IO.File]::ReadAllBytes($manifestPath))
    $live = Read-PublicManifestBounded
    $liveRelease = @($live.artifacts | Where-Object { $_.version -eq $version -and $_.product -in @("OpticonBundle", "OpticonBootstrap", "OpticonSource") })
    if ($liveRelease.Count -ne 4 -or @($liveRelease | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.downloadUrl) }).Count -ne 0) {
        throw "Fly accepted the manifest but did not serve the complete CloudFront release."
    }
    foreach ($expected in $releaseArtifacts) {
        $actual = @($liveRelease | Where-Object { [string]$_.file -ceq [string]$expected.file })
        if ($actual.Count -ne 1 -or [string]$actual[0].product -cne [string]$expected.product -or
            [string]$actual[0].version -cne [string]$expected.version -or [long]$actual[0].size -ne [long]$expected.size -or
            [string]$actual[0].sha256 -cne [string]$expected.sha256 -or [string]$actual[0].downloadUrl -cne [string]$expected.downloadUrl -or
            [string]$actual[0].signingProfile -cne $SigningProfile -or
            [string]$actual[0].sourceManifestKeyId -cne $SourceReleaseCertificateThumbprint -or
            [string]$actual[0].productSignerThumbprint -cne $ProductCertificateThumbprint) {
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
}
} finally {
    if (-not [string]::IsNullOrWhiteSpace($script:AwsScratchDirectory) -and
        (Test-Path -LiteralPath $script:AwsScratchDirectory -PathType Container)) {
        Remove-Item -LiteralPath $script:AwsScratchDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
