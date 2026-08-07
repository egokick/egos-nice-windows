[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [string]$Version = "1.1.12",
    [string]$MinimumGuardianVersion = "1.1.2",
    [string]$CertificateThumbprint = "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53"
)

$ErrorActionPreference = "Stop"

function Get-OpticonManifestSigningKey {
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    try {
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
        if ($null -ne $rsa) { return $rsa }
        throw "The pinned Opticon signing certificate has no RSA private key."
    } catch {
        if ($_.Exception.Message -notmatch '(?i)ephemeral') { throw }
    }

    if ($null -eq ('OpticonBundleSigning.CngKeyReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpticonBundleSigning
{
    public static class CngKeyReader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CryptKeyProviderInfo
        {
            public IntPtr ContainerName;
            public IntPtr ProviderName;
            public uint ProviderType;
            public uint Flags;
            public uint ParameterCount;
            public IntPtr Parameters;
            public uint KeySpec;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CertGetCertificateContextProperty(
            IntPtr certificateContext, uint propertyId, IntPtr data, ref uint dataSize);

        public static RSA Open(X509Certificate2 certificate)
        {
            const uint KeyProviderInfoProperty = 2;
            uint byteCount = 0;
            if (!CertGetCertificateContextProperty(certificate.Handle, KeyProviderInfoProperty, IntPtr.Zero, ref byteCount) || byteCount == 0)
                throw new CryptographicException("The Opticon signing key provider information is unavailable.");

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)byteCount));
            try
            {
                if (!CertGetCertificateContextProperty(certificate.Handle, KeyProviderInfoProperty, buffer, ref byteCount))
                    throw new CryptographicException("Windows could not read the Opticon signing key provider information.");

                var keyInfo = (CryptKeyProviderInfo)Marshal.PtrToStructure(buffer, typeof(CryptKeyProviderInfo));
                var keyName = Marshal.PtrToStringUni(keyInfo.ContainerName);
                var providerName = Marshal.PtrToStringUni(keyInfo.ProviderName);
                if (String.IsNullOrWhiteSpace(keyName) || String.IsNullOrWhiteSpace(providerName) || keyInfo.ProviderType != 0)
                    throw new CryptographicException("The Opticon signing key is not a supported CNG key.");

                return new RSACng(CngKey.Open(keyName, new CngProvider(providerName)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
'@
    }

    return [OpticonBundleSigning.CngKeyReader]::Open($Certificate)
}

function Get-SemanticVersionParts {
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match($Value,
        '^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')
    if (-not $match.Success) { throw "'$Value' is not a semantic version." }
    $core = @($match.Groups['major'].Value, $match.Groups['minor'].Value, $match.Groups['patch'].Value)
    foreach ($identifier in $core) {
        if ($identifier.Length -gt 1 -and $identifier.StartsWith('0', [StringComparison]::Ordinal)) {
            throw "'$Value' is not a semantic version because a numeric identifier has a leading zero."
        }
    }
    $preRelease = if ($match.Groups['pre'].Success) { @($match.Groups['pre'].Value.Split('.')) } else { @() }
    foreach ($identifier in $preRelease) {
        if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier.StartsWith('0', [StringComparison]::Ordinal)) {
            throw "'$Value' is not a semantic version because a prerelease numeric identifier has a leading zero."
        }
    }
    return [pscustomobject]@{ Core = $core; PreRelease = $preRelease }
}

function Compare-NumericIdentifier {
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)
    if ($Left.Length -ne $Right.Length) { return [Math]::Sign($Left.Length - $Right.Length) }
    return [Math]::Sign([string]::CompareOrdinal($Left, $Right))
}

function Compare-SemanticVersion {
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)
    $leftParts = Get-SemanticVersionParts $Left
    $rightParts = Get-SemanticVersionParts $Right
    for ($index = 0; $index -lt 3; $index++) {
        $comparison = Compare-NumericIdentifier $leftParts.Core[$index] $rightParts.Core[$index]
        if ($comparison -ne 0) { return $comparison }
    }
    if ($leftParts.PreRelease.Count -eq 0 -and $rightParts.PreRelease.Count -eq 0) { return 0 }
    if ($leftParts.PreRelease.Count -eq 0) { return 1 }
    if ($rightParts.PreRelease.Count -eq 0) { return -1 }
    $count = [Math]::Min($leftParts.PreRelease.Count, $rightParts.PreRelease.Count)
    for ($index = 0; $index -lt $count; $index++) {
        $leftIdentifier = $leftParts.PreRelease[$index]
        $rightIdentifier = $rightParts.PreRelease[$index]
        $leftNumeric = $leftIdentifier -match '^[0-9]+$'
        $rightNumeric = $rightIdentifier -match '^[0-9]+$'
        if ($leftNumeric -and $rightNumeric) {
            $comparison = Compare-NumericIdentifier $leftIdentifier $rightIdentifier
        } elseif ($leftNumeric) {
            $comparison = -1
        } elseif ($rightNumeric) {
            $comparison = 1
        } else {
            $comparison = [Math]::Sign([string]::CompareOrdinal($leftIdentifier, $rightIdentifier))
        }
        if ($comparison -ne 0) { return $comparison }
    }
    return [Math]::Sign($leftParts.PreRelease.Count - $rightParts.PreRelease.Count)
}

function Enter-OpticonPackageBuildLock {
    param(
        [Parameter(Mandatory)][string]$Path,
        [TimeSpan]$Timeout = [TimeSpan]::FromMinutes(30)
    )

    $null = [IO.Directory]::CreateDirectory((Split-Path $Path -Parent))
    $deadline = [DateTime]::UtcNow.Add($Timeout)
    while ($true) {
        try {
            return [IO.File]::Open($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for another Opticon package build to release $Path."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

$null = Get-SemanticVersionParts $Version
$null = Get-SemanticVersionParts $MinimumGuardianVersion
# Agent file versions intentionally use three numeric components. Rejecting
# prerelease/build labels prevents a release from reporting the same installed
# version as a later stable build and accidentally suppressing that upgrade.
if ($Version.Contains('-') -or $Version.Contains('+') -or $MinimumGuardianVersion.Contains('-') -or $MinimumGuardianVersion.Contains('+')) {
    throw "Remote Opticon releases and guardian requirements must use stable major.minor.patch versions."
}
if ((Compare-SemanticVersion $MinimumGuardianVersion $Version) -gt 0) {
    throw "MinimumGuardianVersion $MinimumGuardianVersion cannot be newer than release $Version."
}

$flyRoot = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $flyRoot -Parent
$buildRoot = Join-Path $repo "artifacts\hosted-build"
$stageRoot = Join-Path $repo "artifacts\hosted-bundle-stage"
$artifactDirectory = Join-Path $flyRoot "artifacts"
$packageBuildLock = Enter-OpticonPackageBuildLock (Join-Path $repo "artifacts\.opticon-package-build.lock")
try {
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

$existingBundles = @($manifest.artifacts | Where-Object { $_.product -eq "OpticonBundle" })
foreach ($role in @("ManagedOnly", "ControllerAndManaged")) {
    $published = @($existingBundles | Where-Object { $_.role -eq $role -and $_.architecture -eq "x64" })
    if ($published.Count -eq 0) { continue }
    $highest = $published[0]
    foreach ($candidate in $published | Select-Object -Skip 1) {
        $comparison = Compare-SemanticVersion ([string]$candidate.version) ([string]$highest.version)
        if ($comparison -gt 0) { $highest = $candidate }
        elseif ($comparison -eq 0 -and [string]$candidate.version -ne [string]$highest.version) {
            throw "The release manifest contains precedence-equivalent versions '$($candidate.version)' and '$($highest.version)' for $role."
        }
    }
    $requestedComparison = Compare-SemanticVersion $Version ([string]$highest.version)
    if ($requestedComparison -lt 0) {
        throw "Release $Version is older than the published $role release $($highest.version). Refusing an accidental downgrade."
    }
    if ($requestedComparison -eq 0 -and $Version -ne [string]$highest.version) {
        throw "Release $Version has the same semantic precedence as published release $($highest.version). Use a distinct higher version."
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
    "-p:EnableWindowsTargeting=true", "-p:Version=$Version", "-p:InformationalVersion=$Version",
    "-p:IncludeSourceRevisionInInformationalVersion=false"
)
$executables = [ordered]@{
    Setup = "Taildesk.Setup.exe"
    Agent = "Taildesk.Agent.exe"
    Admin = "Opticon.exe"
    Cli = "opticon.exe"
    UpdateGuardian = "Taildesk.UpdateGuardian.exe"
}
$commandCenterPublish = Join-Path $repo "artifacts\publish-$Runtime"
$buildInputs = @(Get-ChildItem -LiteralPath (Join-Path $repo "src") -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
$buildInputs += @(Get-ChildItem -LiteralPath $repo -File |
    Where-Object { $_.Name -like "Directory.Build.*" -or $_.Name -like "Directory.Packages.*" })
$latestSourceWrite = ($buildInputs | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
$reuseCommandCenterPublish = $true
foreach ($component in $executables.Keys) {
    $candidateDirectory = Join-Path $commandCenterPublish $component
    $candidateExecutable = Join-Path $candidateDirectory $executables[$component]
    if (-not (Test-Path -LiteralPath $candidateExecutable -PathType Leaf) -or
        (Get-Item -LiteralPath $candidateExecutable).VersionInfo.ProductVersion -ne $Version -or
        (Get-Item -LiteralPath $candidateExecutable).LastWriteTimeUtc -lt $latestSourceWrite) {
        $reuseCommandCenterPublish = $false
        break
    }
}
if ($reuseCommandCenterPublish) {
    Write-Host "Reusing the current $Version command-center component publish outputs." -ForegroundColor Cyan
    foreach ($component in $executables.Keys) {
        $output = Join-Path $buildRoot $component
        New-Item -Path $output -ItemType Directory -Force | Out-Null
        Copy-Item -Path (Join-Path $commandCenterPublish "$component\*") -Destination $output -Recurse -Force
    }
}
foreach ($component in $executables.Keys) {
    $project = Join-Path $repo "src\Taildesk.$component\Taildesk.$component.csproj"
    $output = Join-Path $buildRoot $component
    if (-not $reuseCommandCenterPublish) {
        dotnet publish $project @publishArguments -o $output
        if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed." }
    }
    if ($component -eq "Cli" -and -not $reuseCommandCenterPublish) {
        $publishedCli = Join-Path $output "Taildesk.OpticonCli.exe"
        if (-not (Test-Path -LiteralPath $publishedCli -PathType Leaf)) { throw "The Opticon CLI apphost was not published." }
        Move-Item -LiteralPath $publishedCli -Destination (Join-Path $output "opticon.exe") -Force
        $referencedAdminRuntimeConfig = Join-Path $output "Opticon.runtimeconfig.json"
        if (Test-Path -LiteralPath $referencedAdminRuntimeConfig -PathType Leaf) {
            Remove-Item -LiteralPath $referencedAdminRuntimeConfig -Force
        }
        $cliFiles = @(Get-ChildItem -LiteralPath $output -File)
        if ($cliFiles.Count -ne 1 -or $cliFiles[0].Name -ne "opticon.exe") {
            throw "The hosted CLI directory must contain only the signed opticon.exe single-file app."
        }
    }
    $executable = Join-Path $output $executables[$component]
    $productVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
    if ($productVersion -ne $Version) {
        throw "$component reported product version '$productVersion' instead of release version '$Version'."
    }
    $null = Set-AuthenticodeSignature -FilePath $executable -Certificate $certificate -HashAlgorithm SHA256
    $signature = Get-AuthenticodeSignature -FilePath $executable
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or
        $signature.Status -in @("NotSigned", "HashMismatch")) {
        throw "Authenticode verification failed for $executable."
    }
}

$bootstrapFile = "opticon-bootstrap-$Version.exe"
$bootstrapPath = Join-Path $artifactDirectory $bootstrapFile
Copy-Item -LiteralPath (Join-Path $buildRoot "Setup\Taildesk.Setup.exe") -Destination $bootstrapPath -Force
$bootstrapInfo = Get-Item -LiteralPath $bootstrapPath
$bootstrapRecord = [pscustomobject]@{
    product = "OpticonBootstrap"
    version = $Version
    architecture = "x64"
    file = $bootstrapFile
    size = $bootstrapInfo.Length
    sha256 = (Get-FileHash -LiteralPath $bootstrapPath -Algorithm SHA256).Hash.ToLowerInvariant()
    signerThumbprint = $CertificateThumbprint.ToUpperInvariant()
}

function Write-SignedReleaseManifest {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Role,
        [Parameter(Mandatory)][string]$Architecture,
        [Parameter(Mandatory)][bool]$IncludeAdmin
    )

    $stagePrefix = [IO.Path]::GetFullPath($Stage).TrimEnd(
        [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) +
        [IO.Path]::DirectorySeparatorChar
    $setupPath = Join-Path $Stage "Taildesk.Setup.exe"
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "The signed release Setup executable is missing."
    }
    $candidates = @((Get-Item -LiteralPath $setupPath))
    $roots = @(
        (Join-Path $Stage "Payload\Agent"),
        (Join-Path $Stage "Payload\UpdateGuardian")
    )
    if ($IncludeAdmin) { $roots += (Join-Path $Stage "Payload\Admin") }
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            throw "The release payload directory $root is missing."
        }
        $candidates += @(Get-ChildItem -LiteralPath $root -File -Recurse)
    }
    $files = @()
    foreach ($file in $candidates) {
            $fullPath = [IO.Path]::GetFullPath($file.FullName)
            if (-not $fullPath.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Release payload file escaped the signed stage: $fullPath"
            }
            # Use a substring for Windows PowerShell 5 compatibility after the strict prefix check.
            $relativePath = $fullPath.Substring($stagePrefix.Length).Replace('\', '/')
            $signerThumbprint = ""
            if ($file.Extension.Equals('.exe', [StringComparison]::OrdinalIgnoreCase)) {
                $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
                if (-not $signature.SignerCertificate -or
                    -not $signature.SignerCertificate.Thumbprint.Equals($CertificateThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
                    $signature.Status -in @("NotSigned", "HashMismatch")) {
                    throw "Release executable $relativePath is unsigned, altered, or signed by an unexpected certificate."
                }
                $signerThumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
            }
            $files += [pscustomobject][ordered]@{
                path = $relativePath
                size = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                signerThumbprint = $signerThumbprint
            }
        }
    $files = @($files | Sort-Object -Property path)
    $releaseManifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        version = $Version
        role = $Role
        architecture = $Architecture
        updateProtocolVersion = 1
        minimumGuardianVersion = $MinimumGuardianVersion
        files = $files
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    $manifestBytes = $utf8.GetBytes(($releaseManifest | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllBytes((Join-Path $Stage "release-manifest.json"), $manifestBytes)
    $rsa = Get-OpticonManifestSigningKey -Certificate $certificate
    if ($null -eq $rsa) { throw "The pinned Opticon signing certificate has no RSA private key." }
    try {
        $signatureBytes = $rsa.SignData(
            $manifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    } finally {
        $rsa.Dispose()
    }
    [IO.File]::WriteAllText(
        (Join-Path $Stage "release-manifest.sig"),
        [Convert]::ToBase64String($signatureBytes),
        $utf8)
}

$definitions = @(
    @{ Role = "ManagedOnly"; Suffix = "managed"; IncludeAdmin = $false },
    @{ Role = "ControllerAndManaged"; Suffix = "controller"; IncludeAdmin = $true }
)
$records = @()
foreach ($definition in $definitions) {
    $stage = Join-Path $stageRoot $definition.Suffix
    New-Item -Path (Join-Path $stage "Payload\Agent") -ItemType Directory -Force | Out-Null
    New-Item -Path (Join-Path $stage "Payload\UpdateGuardian") -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildRoot "Setup\Taildesk.Setup.exe") -Destination $stage
    Copy-Item -Path (Join-Path $buildRoot "Agent\*") -Destination (Join-Path $stage "Payload\Agent") -Recurse -Force
    Copy-Item -Path (Join-Path $buildRoot "UpdateGuardian\*") -Destination (Join-Path $stage "Payload\UpdateGuardian") -Recurse -Force
    if ($definition.IncludeAdmin) {
        New-Item -Path (Join-Path $stage "Payload\Admin") -ItemType Directory -Force | Out-Null
        Copy-Item -Path (Join-Path $buildRoot "Admin\*") -Destination (Join-Path $stage "Payload\Admin") -Recurse -Force
        New-Item -Path (Join-Path $stage "Payload\Admin\Cli") -ItemType Directory -Force | Out-Null
        Copy-Item -Path (Join-Path $buildRoot "Cli\*") -Destination (Join-Path $stage "Payload\Admin\Cli") -Recurse -Force
    }
    Write-SignedReleaseManifest -Stage $stage -Role $definition.Role -Architecture "x64" -IncludeAdmin $definition.IncludeAdmin
    $fileName = "opticon-bundle-$Version-$($definition.Suffix)-$Runtime.zip"
    $destination = Join-Path $artifactDirectory $fileName
    $temporaryDestination = "$destination.new.zip"
    if (Test-Path -LiteralPath $temporaryDestination) { Remove-Item -LiteralPath $temporaryDestination -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $temporaryDestination -CompressionLevel Optimal
    $file = Get-Item -LiteralPath $temporaryDestination
    $record = [pscustomobject]@{
        product = "OpticonBundle"; version = $Version; role = $definition.Role
        architecture = "x64"; file = $fileName; size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $temporaryDestination -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $sameRelease = @($existingBundles | Where-Object {
        $_.role -eq $record.role -and $_.architecture -eq $record.architecture -and $_.version -eq $record.version
    })
    if ($sameRelease.Count -gt 1) { throw "The outer manifest declares $($record.role) $Version more than once." }
    if ($sameRelease.Count -eq 1 -and
        (([long]$sameRelease[0].size -ne [long]$record.size) -or
         -not ([string]$sameRelease[0].sha256).Equals([string]$record.sha256, [StringComparison]::OrdinalIgnoreCase))) {
        Remove-Item -LiteralPath $temporaryDestination -Force
        throw "$($record.role) $Version is already declared with different bytes. Bump -Version; published release filenames are immutable."
    }
    Move-Item -LiteralPath $temporaryDestination -Destination $destination -Force
    $records += $record
}

$candidates = @($existingBundles | Where-Object {
    $existing = $_
    -not ($records | Where-Object {
        $_.role -eq $existing.role -and $_.architecture -eq $existing.architecture -and $_.version -eq $existing.version
    })
}) + @($records)
$retained = @()
$groups = @($candidates | Group-Object -Property { "$($_.role)|$($_.architecture)" } | Sort-Object -Property Name)
foreach ($group in $groups) {
    $remaining = @($group.Group)
    $keptForGroup = 0
    while ($remaining.Count -gt 0 -and $keptForGroup -lt 1) {
        $bestIndex = 0
        for ($index = 1; $index -lt $remaining.Count; $index++) {
            $comparison = Compare-SemanticVersion ([string]$remaining[$index].version) ([string]$remaining[$bestIndex].version)
            if ($comparison -gt 0) { $bestIndex = $index }
            elseif ($comparison -eq 0) {
                if ([string]$remaining[$index].version -ne [string]$remaining[$bestIndex].version -or
                    -not ([string]$remaining[$index].sha256).Equals([string]$remaining[$bestIndex].sha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "The outer manifest contains ambiguous precedence-equivalent releases for $($group.Name)."
                }
            }
        }
        $retained += $remaining[$bestIndex]
        $keptForGroup++
        $remaining = @(for ($index = 0; $index -lt $remaining.Count; $index++) {
            if ($index -ne $bestIndex) { $remaining[$index] }
        })
    }
}
foreach ($artifact in $retained) {
    $path = Join-Path $artifactDirectory ([string]$artifact.file)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Retained rollback bundle $($artifact.file) is missing." }
    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($file.Length -ne [long]$artifact.size -or -not $hash.Equals([string]$artifact.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Retained rollback bundle verification failed for $($artifact.file)."
    }
}
$manifest.artifacts = @($manifest.artifacts | Where-Object { $_.product -notin @("OpticonBundle", "OpticonBootstrap") }) + @($retained) + @($bootstrapRecord)
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
$retained | Format-Table role, version, file, size, sha256 -AutoSize
Write-Host "Run .\scripts\Publish-OpticonBundles.ps1 after deploying the gateway manifest." -ForegroundColor Green
} finally {
    $packageBuildLock.Dispose()
}
