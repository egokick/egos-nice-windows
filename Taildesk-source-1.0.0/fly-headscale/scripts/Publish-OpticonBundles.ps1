[CmdletBinding()]
param(
    [string]$ControlOrigin = "https://taildesk-egokick-control.fly.dev",
    [string]$ArtifactDirectory = "",
    [string]$AdminConfig = "",
    [ValidateSet("", "ManagedOnly", "ControllerAndManaged")]
    [string]$Role = ""
)

$ErrorActionPreference = "Stop"
$ArtifactDirectory = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) { Join-Path $PSScriptRoot "..\artifacts" } else { $ArtifactDirectory }
$AdminConfig = if ([string]::IsNullOrWhiteSpace($AdminConfig)) { Join-Path $env:LOCALAPPDATA "Taildesk\Admin\admin.json" } else { $AdminConfig }
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Security

function ConvertTo-Hex([byte[]]$Bytes) {
    return ([BitConverter]::ToString($Bytes)).Replace("-", "").ToLowerInvariant()
}

function New-Nonce {
    $bytes = New-Object byte[] 24
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    return [Convert]::ToBase64String($bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

$config = Get-Content -Raw -LiteralPath $AdminConfig | ConvertFrom-Json
$protectedKey = [string]$config.headscaleApiKeyProtected
if ([string]::IsNullOrWhiteSpace($protectedKey)) { throw "The Opticon gateway key is not configured." }
$protectedBytes = [Convert]::FromBase64String($protectedKey)
$secretBytes = [Security.Cryptography.ProtectedData]::Unprotect(
    $protectedBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$manifestPath = Join-Path $ArtifactDirectory "manifest.json"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$bundles = @($manifest.artifacts | Where-Object {
    $_.product -eq "OpticonBundle" -and ([string]::IsNullOrWhiteSpace($Role) -or $_.role -eq $Role)
})
if ($bundles.Count -eq 0) { throw "The artifact manifest declares no Opticon bundles." }

$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(5)
$chunkSize = 4MB
try {
    foreach ($artifact in $bundles) {
        $path = Join-Path $ArtifactDirectory ([string]$artifact.file)
        $info = Get-Item -LiteralPath $path
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($info.Length -ne [long]$artifact.size -or $actualHash -ne [string]$artifact.sha256) {
            throw "Local bundle verification failed for $($artifact.file)."
        }

        $stream = [IO.File]::OpenRead($path)
        try {
            $offset = 0L
            $buffer = New-Object byte[] $chunkSize
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $chunk = if ($read -eq $buffer.Length) { $buffer } else {
                    $last = New-Object byte[] $read
                    [Array]::Copy($buffer, $last, $read)
                    $last
                }
                $relative = "/opticon/v1/bundles/$($artifact.file)?offset=$offset&total=$($info.Length)&sha256=$actualHash"
                $uri = [Uri]::new($ControlOrigin.TrimEnd("/") + $relative)
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $bodyHash = ConvertTo-Hex ($sha.ComputeHash($chunk, 0, $read)) }
                finally { $sha.Dispose() }
                $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
                $nonce = New-Nonce
                $canonical = "PUT`n$($uri.PathAndQuery)`n$timestamp`n$nonce`n$bodyHash"
                $hmac = [Security.Cryptography.HMACSHA256]::new($secretBytes)
                try { $signature = ConvertTo-Hex ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))) }
                finally { $hmac.Dispose() }
                $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put, $uri)
                $request.Content = [Net.Http.ByteArrayContent]::new($chunk, 0, $read)
                $null = $request.Headers.TryAddWithoutValidation("X-Opticon-Key-Id", "primary")
                $null = $request.Headers.TryAddWithoutValidation("X-Opticon-Timestamp", $timestamp)
                $null = $request.Headers.TryAddWithoutValidation("X-Opticon-Nonce", $nonce)
                $null = $request.Headers.TryAddWithoutValidation("X-Opticon-Content-SHA256", $bodyHash)
                $null = $request.Headers.TryAddWithoutValidation("X-Opticon-Signature", $signature)
                try {
                    $response = $client.SendAsync($request).GetAwaiter().GetResult()
                    try {
                        if (-not $response.IsSuccessStatusCode) {
                            $detail = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            throw "Bundle upload failed at offset $offset ($([int]$response.StatusCode)): $detail"
                        }
                    }
                    finally { $response.Dispose() }
                }
                finally { $request.Dispose() }
                $offset += $read
                Write-Progress -Activity "Publishing $($artifact.file)" -Status "$offset of $($info.Length) bytes" -PercentComplete (($offset * 100) / $info.Length)
            }
        }
        finally { $stream.Dispose() }
        Write-Progress -Activity "Publishing $($artifact.file)" -Completed
        Write-Host "Published and verified $($artifact.file) ($actualHash)"
    }
}
finally {
    $client.Dispose()
    [Array]::Clear($secretBytes, 0, $secretBytes.Length)
    [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
}
