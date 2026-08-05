[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0",
    [string]$CertificateThumbprint = "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53"
)

$ErrorActionPreference = "Stop"
$flyRoot = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $flyRoot -Parent
$buildRoot = Join-Path $repo "artifacts\hosted-build"
$stageRoot = Join-Path $repo "artifacts\hosted-bundle-stage"
$artifactDirectory = Join-Path $flyRoot "artifacts"
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Thumbprint -eq $CertificateThumbprint -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $certificate) { throw "The pinned Opticon signing certificate is unavailable." }
$manifestPath = Join-Path $artifactDirectory "manifest.json"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$dependencies = @($manifest.artifacts | Where-Object { $_.product -in @("Tailscale", "RustDesk") })
if ($dependencies.Count -ne 4) { throw "The release manifest must declare four pinned dependency installers." }
foreach ($artifact in $dependencies) {
    $path = Join-Path $artifactDirectory ([string]$artifact.file)
    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($file.Length -ne [long]$artifact.size -or $hash -ne [string]$artifact.sha256 -or
        [string]::IsNullOrWhiteSpace([string]$artifact.signerThumbprint) -or
        $signature.Status -ne "Valid" -or -not $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne [string]$artifact.signerThumbprint) {
        throw "Release dependency verification failed for $($artifact.file)."
    }
}

foreach ($path in @($buildRoot, $stageRoot)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -Path $path -ItemType Directory | Out-Null
}

$publishArguments = @(
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:EnableWindowsTargeting=true"
)
$executables = @{
    Setup = "Taildesk.Setup.exe"
    Agent = "Taildesk.Agent.exe"
    Admin = "Opticon.exe"
}
foreach ($component in $executables.Keys) {
    $project = Join-Path $repo "src\Taildesk.$component\Taildesk.$component.csproj"
    $output = Join-Path $buildRoot $component
    dotnet publish $project @publishArguments -o $output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed." }
    $executable = Join-Path $output $executables[$component]
    $null = Set-AuthenticodeSignature -FilePath $executable -Certificate $certificate -HashAlgorithm SHA256
    $signature = Get-AuthenticodeSignature -FilePath $executable
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or
        $signature.Status -in @("NotSigned", "HashMismatch")) {
        throw "Authenticode verification failed for $executable."
    }
}

$definitions = @(
    @{ Role = "ManagedOnly"; Suffix = "managed"; IncludeAdmin = $false },
    @{ Role = "ControllerAndManaged"; Suffix = "controller"; IncludeAdmin = $true }
)
$records = @()
foreach ($definition in $definitions) {
    $stage = Join-Path $stageRoot $definition.Suffix
    New-Item -Path (Join-Path $stage "Payload\Agent") -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildRoot "Setup\Taildesk.Setup.exe") -Destination $stage
    Copy-Item -Path (Join-Path $buildRoot "Agent\*") -Destination (Join-Path $stage "Payload\Agent") -Recurse -Force
    if ($definition.IncludeAdmin) {
        New-Item -Path (Join-Path $stage "Payload\Admin") -ItemType Directory -Force | Out-Null
        Copy-Item -Path (Join-Path $buildRoot "Admin\*") -Destination (Join-Path $stage "Payload\Admin") -Recurse -Force
    }
    $fileName = "opticon-bundle-$Version-$($definition.Suffix)-$Runtime.zip"
    $destination = Join-Path $artifactDirectory $fileName
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $destination -CompressionLevel Optimal
    $file = Get-Item -LiteralPath $destination
    $records += [pscustomobject]@{
        product = "OpticonBundle"; version = $Version; role = $definition.Role
        architecture = "x64"; file = $fileName; size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifestPath = Join-Path $artifactDirectory "manifest.json"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$manifest.artifacts = @($manifest.artifacts | Where-Object { $_.product -ne "OpticonBundle" }) + $records
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
$records | Format-Table role, file, size, sha256 -AutoSize
Write-Host "Run .\scripts\Publish-OpticonBundles.ps1 after deploying the gateway manifest." -ForegroundColor Green
