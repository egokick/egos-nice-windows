[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Production", "OwnerManaged")]
    [string]$SigningProfile = "Production",
    [string]$Version = "1.2.3",
    [string]$MinimumGuardianVersion = "1.1.2",
    # SourceOnly is the v2 release path: it produces exactly one signed source
    # archive and deliberately emits no standalone bundle/bootstrap artifact.
    [switch]$SourceOnly,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SourceReleaseCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ProductCertificateThumbprint,
    [ValidatePattern('^$|^[A-Fa-f0-9]{40}$')][string]$LegacyMigrationSignerThumbprint = '',
    [Parameter(Mandatory)][string]$Rfc3161TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$invitationSigningThumbprint = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'
$legacyMigrationBridgeVersion = '1.1.41'
$obsoleteLegacyMigrationBridgeVersion = '1.1.40'
$SourceReleaseCertificateThumbprint = $SourceReleaseCertificateThumbprint.ToUpperInvariant()
$ProductCertificateThumbprint = $ProductCertificateThumbprint.ToUpperInvariant()
$LegacyMigrationSignerThumbprint = $LegacyMigrationSignerThumbprint.ToUpperInvariant()
$isLegacyMigration = -not [string]::IsNullOrWhiteSpace($LegacyMigrationSignerThumbprint)
if ($isLegacyMigration -and ($SigningProfile -ne 'OwnerManaged' -or
        $Version -cne $legacyMigrationBridgeVersion -or
        $LegacyMigrationSignerThumbprint -ne $invitationSigningThumbprint)) {
    throw 'A legacy Agent migration must be the exact OwnerManaged 1.1.41 release signed only with the exact retired invitation certificate.'
}
if ($SourceOnly -and $isLegacyMigration) {
    throw 'A source-only release cannot use the retired legacy migration signer.'
}
$script:git = $null
$script:trustedGitRoot = $null

function Assert-NoReparseTraversal {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $current = [IO.Path]::GetFullPath($Path)
    if ($current -ne $root -and -not $current.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A trusted build tool escaped its fixed root: $current"
    }
    while ($true) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "A trusted build-tool path is a reparse point: $current"
        }
        if ($current.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = Split-Path $current -Parent
    }
}

function Get-FixedGit {
    foreach ($root in @(
            [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
            [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        foreach ($relative in @('Git\cmd\git.exe', 'Git\bin\git.exe')) {
            $candidate = Join-Path $root $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Assert-NoReparseTraversal -Root $root -Path $candidate
                return $candidate
            }
        }
    }
    throw 'A production hosted build requires Git at a fixed Program Files path.'
}

function Invoke-FixedGit {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if ([string]::IsNullOrWhiteSpace($script:git)) { $script:git = Get-FixedGit }
    $windows = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:git
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Codex's workspace may be owned by its sandbox SID while this explicit
    # production publish runs as the interactive signing user. Scope Git's
    # ownership exception to this one validated repository and process; never
    # mutate the user's global safe.directory configuration.
    $null = $start.ArgumentList.Add('-c')
    if ([string]::IsNullOrWhiteSpace($script:trustedGitRoot)) { throw 'The exact trusted Git root has not been established.' }
    $null = $start.ArgumentList.Add("safe.directory=$($script:trustedGitRoot.Replace('\', '/'))")
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $start.Environment['SystemRoot'] = $windows
    $start.Environment['WINDIR'] = $windows
    $start.Environment['PATH'] = [string]::Join([IO.Path]::PathSeparator, @((Split-Path $script:git -Parent), $system32))
    $start.Environment['PATHEXT'] = '.COM;.EXE'
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start fixed Git.' }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Fixed Git failed: $($stderr.Trim())" }
        return $stdout
    } finally { $process.Dispose() }
}

function Get-ReleaseCertificate {
    param([Parameter(Mandatory)][string]$Thumbprint, [Parameter(Mandatory)][string]$Purpose)
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint.ToUpperInvariant() -eq $Thumbprint -and $_.HasPrivateKey } |
        Select-Object -First 1
    if (-not $certificate) { throw "$Purpose certificate $Thumbprint with an accessible private key is unavailable in CurrentUser\\My." }
    return $certificate
}

function Get-ArtifactString {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)][string]$Name)
    $property = $Artifact.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    return [string]$property.Value
}

function Test-ProductionArtifactTrust {
    param([Parameter(Mandatory)]$Artifact)
    return (Get-ArtifactString $Artifact 'signingProfile') -ceq $SigningProfile -and
        (Get-ArtifactString $Artifact 'sourceManifestKeyId') -eq $SourceReleaseCertificateThumbprint -and
        (Get-ArtifactString $Artifact 'productSignerThumbprint') -eq $ProductCertificateThumbprint
}

function Get-LegacyMigrationBundleFileName {
    param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Role)
    switch ($Role) {
        'ManagedOnly' { return "opticon-bundle-$Version-managed-win-x64.zip" }
        'ControllerAndManaged' { return "opticon-bundle-$Version-controller-win-x64.zip" }
        default { return '' }
    }
}

function Test-ExactLegacyMigrationArtifact {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)][string]$ExpectedVersion)

    $marker = Get-ArtifactString $Artifact 'legacyMigrationSignerThumbprint'
    $role = Get-ArtifactString $Artifact 'role'
    $expectedFile = Get-LegacyMigrationBundleFileName -Version $ExpectedVersion -Role $role
    $size = 0L
    try { $size = [long]$Artifact.size } catch { return $false }
    return $marker -ceq $invitationSigningThumbprint -and
        (Get-ArtifactString $Artifact 'product') -ceq 'OpticonBundle' -and
        (Get-ArtifactString $Artifact 'version') -ceq $ExpectedVersion -and
        (Get-ArtifactString $Artifact 'signingProfile') -ceq 'OwnerManaged' -and
        (Get-ArtifactString $Artifact 'sourceManifestKeyId') -ceq $SourceReleaseCertificateThumbprint -and
        (Get-ArtifactString $Artifact 'productSignerThumbprint') -ceq $ProductCertificateThumbprint -and
        (Get-ArtifactString $Artifact 'architecture') -ceq 'x64' -and
        -not [string]::IsNullOrWhiteSpace($expectedFile) -and
        (Get-ArtifactString $Artifact 'file') -ceq $expectedFile -and
        $size -ge 1024 -and $size -le 2GB -and
        (Get-ArtifactString $Artifact 'sha256') -match '^[A-Fa-f0-9]{64}$'
}

function Test-LegacyMigrationArtifact {
    param([Parameter(Mandatory)]$Artifact)

    $marker = Get-ArtifactString $Artifact 'legacyMigrationSignerThumbprint'
    if ([string]::IsNullOrWhiteSpace($marker)) { return $false }
    if (-not (Test-ExactLegacyMigrationArtifact -Artifact $Artifact -ExpectedVersion $legacyMigrationBridgeVersion)) {
        throw "A retained legacy migration bundle must be the exact OwnerManaged $legacyMigrationBridgeVersion bridge with the canonical retired signer: $($Artifact.file)."
    }
    return $true
}

function Get-LocalArtifactPath {
    param([Parameter(Mandatory)][string]$FileName)
    if ([string]::IsNullOrWhiteSpace($FileName) -or
        -not [IO.Path]::GetFileName($FileName).Equals($FileName, [StringComparison]::Ordinal) -or
        $FileName.Contains('/') -or $FileName.Contains('\')) {
        throw 'A release-manifest artifact filename is unsafe.'
    }
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($artifactDirectory))
    $path = [IO.Path]::GetFullPath((Join-Path $root $FileName))
    if (-not [IO.Path]::GetDirectoryName($path).Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The exact local artifact is missing or escaped its directory: $FileName"
    }
    Assert-NoReparseTraversal -Root $root -Path $path
    return $path
}

function Assert-ProductSigningCertificate {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][bool]$RequirePublicTrust)
    if ($RequirePublicTrust -and $Certificate.Subject -eq $Certificate.Issuer) {
        throw 'The production Authenticode certificate must not be self-signed.'
    }
    $ekuExtension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if ($null -eq $ekuExtension) { throw 'The production Authenticode certificate has no EKU restriction.' }
    $enhanced = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtension
    if (-not ($enhanced.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw 'The production Authenticode certificate lacks the Code Signing EKU.'
    }
    if (-not $RequirePublicTrust) { return }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        if (-not $chain.Build($Certificate)) {
            $detail = ($chain.ChainStatus | ForEach-Object { $_.Status.ToString() }) -join ', '
            throw "The production Authenticode certificate does not build to a trusted public Windows root: $detail"
        }
    } finally { $chain.Dispose() }
}

function Assert-ProductSignature {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ExpectedThumbprint = $script:payloadSignerThumbprint
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $allowedStatus = if ($SigningProfile -eq 'Production') {
        @([Management.Automation.SignatureStatus]::Valid)
    } else {
        @([Management.Automation.SignatureStatus]::Valid, [Management.Automation.SignatureStatus]::UnknownError)
    }
    if ($signature.Status -notin $allowedStatus -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Thumbprint.Equals($ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
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

function Assert-PinnedDependencySignature {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ExpectedThumbprint
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Thumbprint.Equals($ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Pinned dependency Authenticode or timestamp validation failed for $Path."
    }
    $codeEku = $signature.SignerCertificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if ($null -eq $codeEku -or -not (([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$codeEku).EnhancedKeyUsages |
            Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw "The pinned dependency signer for $Path lacks the Code Signing EKU."
    }
    $timestampEku = $signature.TimeStamperCertificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if ($null -eq $timestampEku -or -not (([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$timestampEku).EnhancedKeyUsages |
            Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.8' })) {
        throw "The dependency timestamp for $Path lacks the Time Stamping EKU."
    }
}

function Invoke-SignTool {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $windows = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $SignToolPath
    $start.WorkingDirectory = $system32
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $start.Environment['SystemRoot'] = $windows
    $start.Environment['WINDIR'] = $windows
    $start.Environment['ProgramFiles'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $start.Environment['ProgramFiles(x86)'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $start.Environment['ProgramData'] = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $start.Environment['PATH'] = [string]::Join([IO.Path]::PathSeparator, @((Split-Path $SignToolPath -Parent), $system32))
    $start.Environment['PATHEXT'] = '.COM;.EXE'
    $start.Environment['TEMP'] = $sdkAnchor
    $start.Environment['TMP'] = $sdkAnchor
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start the fixed Windows SDK signer.' }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            $diagnosticParts = @($stderr.Trim(), $stdout.Trim()) | Where-Object { $_ }
            $diagnostic = [string]::Join([Environment]::NewLine, $diagnosticParts)
            if ($diagnostic.Length -gt 8192) { $diagnostic = $diagnostic.Substring(0, 8192) + ' [truncated]' }
            throw "signtool failed with exit code $($process.ExitCode): $diagnostic"
        }
    } finally { $process.Dispose() }
}

function Invoke-ProductSigning {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Thumbprint = $script:payloadSignerThumbprint
    )
    Invoke-SignTool -Arguments @('sign', '/fd', 'SHA256', '/sha1', $Thumbprint,
        '/tr', $Rfc3161TimestampUrl, '/td', 'SHA256', $Path)
    Assert-ProductSignature -Path $Path -ExpectedThumbprint $Thumbprint
}

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
    # PowerShell enumerates an empty array-valued PSCustomObject property as
    # $null. Re-wrap it before strict-mode Count/index access.
    $leftPreRelease = @($leftParts.PreRelease)
    $rightPreRelease = @($rightParts.PreRelease)
    for ($index = 0; $index -lt 3; $index++) {
        $comparison = Compare-NumericIdentifier $leftParts.Core[$index] $rightParts.Core[$index]
        if ($comparison -ne 0) { return $comparison }
    }
    if ($leftPreRelease.Count -eq 0 -and $rightPreRelease.Count -eq 0) { return 0 }
    if ($leftPreRelease.Count -eq 0) { return 1 }
    if ($rightPreRelease.Count -eq 0) { return -1 }
    $count = [Math]::Min($leftPreRelease.Count, $rightPreRelease.Count)
    for ($index = 0; $index -lt $count; $index++) {
        $leftIdentifier = $leftPreRelease[$index]
        $rightIdentifier = $rightPreRelease[$index]
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
    return [Math]::Sign($leftPreRelease.Count - $rightPreRelease.Count)
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
$script:trustedGitRoot = [IO.Path]::GetFullPath((Split-Path $repo -Parent))
$gitMetadata = Join-Path $script:trustedGitRoot '.git'
if (-not (Test-Path -LiteralPath $gitMetadata -PathType Container)) {
    throw 'The expected production Git metadata directory is missing.'
}
Assert-NoReparseTraversal -Root $script:trustedGitRoot -Path $gitMetadata
$gitRoot = (Invoke-FixedGit -Arguments @('-C', $repo, 'rev-parse', '--show-toplevel')).Trim()
if ([string]::IsNullOrWhiteSpace($gitRoot) -or $gitRoot.Contains([Environment]::NewLine)) {
    throw 'A production hosted build must originate from one committed Git checkout.'
}
$gitRoot = [IO.Path]::GetFullPath($gitRoot)
if (-not $gitRoot.Equals($script:trustedGitRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The production hosted build resolved an unexpected Git root.'
}
$relativeRepo = [IO.Path]::GetRelativePath($gitRoot, $repo).Replace('\', '/')
$gitStatus = Invoke-FixedGit -Arguments @(
    '-C', $gitRoot, 'status', '--porcelain=v1', '--untracked-files=all', '--', $relativeRepo)
if (-not [string]::IsNullOrWhiteSpace($gitStatus)) {
    throw 'A production hosted build requires a clean committed Opticon source tree, including no untracked files.'
}
$dotnetRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
$timestampUri = $null
if (-not [Uri]::TryCreate($Rfc3161TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
    -not [string]::IsNullOrEmpty($timestampUri.UserInfo)) {
    throw 'Rfc3161TimestampUrl is invalid.'
}
$officialDigiCertRfc3161 = $timestampUri.Scheme -eq [Uri]::UriSchemeHttp -and
    $timestampUri.IsDefaultPort -and $timestampUri.Host.Equals('timestamp.digicert.com', [StringComparison]::OrdinalIgnoreCase) -and
    $timestampUri.AbsolutePath -eq '/' -and [string]::IsNullOrEmpty($timestampUri.Query) -and
    [string]::IsNullOrEmpty($timestampUri.Fragment)
if ($timestampUri.Scheme -ne [Uri]::UriSchemeHttps -and -not $officialDigiCertRfc3161) {
    throw 'Rfc3161TimestampUrl must use HTTPS or the exact Microsoft-documented DigiCert RFC3161 endpoint.'
}
$SignToolPath = [IO.Path]::GetFullPath($SignToolPath)
$windowsKitsRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) 'Windows Kits\10\bin'
if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $windowsKitsRoot -PathType Container) -or
    -not $SignToolPath.StartsWith([IO.Path]::GetFullPath($windowsKitsRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path $SignToolPath -Leaf) -ne 'signtool.exe' -or
    (Split-Path (Split-Path $SignToolPath -Parent) -Leaf) -ne 'x64') {
    throw 'SignToolPath must name the fixed x64 signtool.exe under Program Files (x86)\Windows Kits\10\bin\<version>\x64.'
}
Assert-NoReparseTraversal -Root ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) -Path $SignToolPath
if ($SourceReleaseCertificateThumbprint -eq $invitationSigningThumbprint -or
    $ProductCertificateThumbprint -eq $invitationSigningThumbprint -or
    $SourceReleaseCertificateThumbprint -eq $ProductCertificateThumbprint) {
    throw 'Production invitation, source-release, and Authenticode trust domains must be pairwise distinct.'
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'Release builds require exact .NET SDK 10.0.302 (the SDK carrying runtime 10.0.10).'
}
Assert-NoReparseTraversal -Root ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) -Path $dotnet
$artifactDirectory = Join-Path $flyRoot "artifacts"
Assert-NoReparseTraversal -Root $repo -Path $artifactDirectory
$sdkAnchor = Join-Path ([IO.Path]::GetTempPath()) ('opticon-sdk-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($sdkAnchor) | Out-Null
$buildRoot = Join-Path $sdkAnchor 'hosted-build'
$stageRoot = Join-Path $sdkAnchor 'hosted-bundle-stage'
[IO.File]::WriteAllText(
    (Join-Path $sdkAnchor 'global.json'),
    '{"sdk":{"version":"10.0.302","rollForward":"disable","allowPrerelease":false}}',
    [Text.UTF8Encoding]::new($false))
$packageCache = Join-Path $sdkAnchor 'packages'
$nugetHttpCache = Join-Path $sdkAnchor 'nuget-http-cache'
$cliHome = Join-Path $sdkAnchor 'dotnet-home'
$isolatedRoamingProfile = Join-Path $cliHome 'AppData\Roaming'
$isolatedLocalProfile = Join-Path $cliHome 'AppData\Local'
$buildTemp = Join-Path $sdkAnchor 'temp'
$userExtensions = Join-Path $sdkAnchor 'empty-msbuild-user-extensions'
$intermediateRoot = Join-Path $sdkAnchor 'obj'
foreach ($directory in @($packageCache, $nugetHttpCache, $cliHome, $isolatedRoamingProfile, $isolatedLocalProfile,
        $buildTemp, $userExtensions, $intermediateRoot)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
$emptyTargets = Join-Path $sdkAnchor 'Directory.Build.targets'
[IO.File]::WriteAllText($emptyTargets, '<Project />', [Text.UTF8Encoding]::new($false))
$nugetConfig = Join-Path $sdkAnchor 'NuGet.Config'
[IO.File]::WriteAllText($nugetConfig, @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <disabledPackageSources><clear /></disabledPackageSources>
  <packageSourceMapping><clear /><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
  <config>
    <add key="globalPackagesFolder" value="$([Security.SecurityElement]::Escape($packageCache))" />
    <add key="httpCachePath" value="$([Security.SecurityElement]::Escape($nugetHttpCache))" />
  </config>
</configuration>
"@, [Text.UTF8Encoding]::new($false))

function Invoke-ExactDotNet {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$CaptureOutput)
    $windows = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = [IO.Path]::GetFullPath([Environment]::SystemDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet
    $start.WorkingDirectory = $sdkAnchor
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $safeEnvironment = [ordered]@{
        SystemRoot = $windows; WINDIR = $windows; SystemDrive = [IO.Path]::GetPathRoot($windows).TrimEnd('\')
        ProgramFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        'ProgramFiles(x86)' = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
        ProgramW6432 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        ProgramData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        ComSpec = (Join-Path $system32 'cmd.exe'); PATH = [string]::Join([IO.Path]::PathSeparator, @($dotnetRoot, $system32)); PATHEXT = '.COM;.EXE'
        TEMP = $buildTemp; TMP = $buildTemp; DOTNET_ROOT = $dotnetRoot
        DOTNET_CLI_HOME = $cliHome; NUGET_PACKAGES = $packageCache; NUGET_HTTP_CACHE_PATH = $nugetHttpCache
        USERPROFILE = $cliHome; HOME = $cliHome; APPDATA = $isolatedRoamingProfile; LOCALAPPDATA = $isolatedLocalProfile
        HOMEDRIVE = [IO.Path]::GetPathRoot($cliHome).TrimEnd('\'); HOMEPATH = $cliHome.Substring([IO.Path]::GetPathRoot($cliHome).Length - 1)
        NUGET_XMLDOC_MODE = 'skip'; NUGET_CERT_REVOCATION_MODE = 'online'
        DOTNET_MULTILEVEL_LOOKUP = '0'; DOTNET_NOLOGO = '1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        # No Opticon project consumes SDK workloads and the resolver is also
        # disabled below. Do not let unrelated machine-wide workload metadata
        # make this otherwise hermetic restore depend on per-user CLI state.
        DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK = '1'
        MSBUILDDISABLENODEREUSE = '1'
    }
    foreach ($entry in $safeEnvironment.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) { $start.Environment[[string]$entry.Key] = [string]$entry.Value }
    }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start the fixed .NET SDK host.' }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            $diagnosticParts = @($stderr.Trim(), $stdout.Trim()) | Where-Object { $_ }
            $diagnostic = [string]::Join([Environment]::NewLine, $diagnosticParts)
            if ($diagnostic.Length -gt 8192) { $diagnostic = $diagnostic.Substring(0, 8192) + ' [truncated]' }
            throw "The exact .NET SDK command '$([string]::Join(' ', $Arguments))' failed ($($process.ExitCode)): $diagnostic"
        }
        if ($CaptureOutput) { return $stdout }
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout.TrimEnd() }
    } finally { $process.Dispose() }
}

$installedSdks = @((Invoke-ExactDotNet -Arguments @('--list-sdks') -CaptureOutput) -split "`r?`n")
if (-not ($installedSdks | Where-Object { $_ -match '^10\.0\.302\s' })) {
    throw 'Release builds require exact .NET SDK 10.0.302 (the SDK carrying runtime 10.0.10).'
}
$selectedSdk = (Invoke-ExactDotNet -Arguments @('--version') -CaptureOutput).Trim()
if ($selectedSdk -ne '10.0.302') { throw "global.json selected SDK '$selectedSdk', not exact SDK 10.0.302." }
$packageBuildLock = Enter-OpticonPackageBuildLock (Join-Path $repo "artifacts\.opticon-package-build.lock")
try {
$sourceReleaseCertificate = Get-ReleaseCertificate -Thumbprint $SourceReleaseCertificateThumbprint -Purpose 'Offline source-release signing'
$productCertificate = Get-ReleaseCertificate -Thumbprint $ProductCertificateThumbprint -Purpose "$SigningProfile Authenticode signing"
$script:payloadSignerThumbprint = $ProductCertificateThumbprint
$payloadCertificate = $productCertificate
$releaseManifestCertificate = $sourceReleaseCertificate
if ($isLegacyMigration) {
    $legacyMigrationCertificate = Get-ReleaseCertificate -Thumbprint $LegacyMigrationSignerThumbprint -Purpose 'One-time legacy Agent migration signing'
    Assert-ProductSigningCertificate -Certificate $legacyMigrationCertificate -RequirePublicTrust $false
    $script:payloadSignerThumbprint = $LegacyMigrationSignerThumbprint
    $payloadCertificate = $legacyMigrationCertificate
    # Only pre-trust-split Agents process this manifest. Their embedded verifier
    # pins the retired invitation certificate, and this exact bridge is the
    # final release allowed to use it.
    $releaseManifestCertificate = $legacyMigrationCertificate
}
$sourceRsaProbe = Get-OpticonManifestSigningKey -Certificate $sourceReleaseCertificate
try {
    if ($sourceRsaProbe.KeySize -lt 3072) { throw 'The production source-release RSA key must be at least 3072 bits.' }
} finally { $sourceRsaProbe.Dispose() }
Assert-ProductSigningCertificate -Certificate $productCertificate -RequirePublicTrust ($SigningProfile -eq 'Production')
$sourceReleaseCertificateBase64 = [Convert]::ToBase64String($sourceReleaseCertificate.RawData)
$productSigningCertificateBase64 = [Convert]::ToBase64String($productCertificate.RawData)
$manifestPath = Join-Path $artifactDirectory "manifest.json"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$retiredObsoleteLegacyMigrationArtifacts = @()
$normalizedArtifacts = [Collections.Generic.List[object]]::new()
foreach ($artifact in @($manifest.artifacts)) {
    $marker = Get-ArtifactString $artifact 'legacyMigrationSignerThumbprint'
    if ([string]::IsNullOrWhiteSpace($marker)) {
        $normalizedArtifacts.Add($artifact)
        continue
    }
    if (Test-ExactLegacyMigrationArtifact -Artifact $artifact -ExpectedVersion $obsoleteLegacyMigrationBridgeVersion) {
        $retiredObsoleteLegacyMigrationArtifacts += $artifact
        continue
    }
    # A marker is never ignored.  Only the known local 1.1.40 predecessor is
    # retired; malformed markers and the current bridge must remain fail-closed.
    $null = Test-LegacyMigrationArtifact -Artifact $artifact
    $normalizedArtifacts.Add($artifact)
}
if ($retiredObsoleteLegacyMigrationArtifacts.Count -gt 0) {
    $manifest.artifacts = @($normalizedArtifacts)
}
if (-not $SourceOnly) {
    $dependencies = @($manifest.artifacts | Where-Object { $_.product -in @("Tailscale", "RustDesk") })
    if ($dependencies.Count -ne 4) { throw "The release manifest must declare four pinned dependency installers." }
    $expectedDependencies = @{
        'Tailscale|x64' = $true; 'Tailscale|arm64' = $true
        'RustDesk|x64' = $true; 'RustDesk|arm64' = $true
    }
    $seenDependencies = @{}
    foreach ($artifact in $dependencies) {
        $dependencyKey = '{0}|{1}' -f ([string]$artifact.product), ([string]$artifact.architecture)
        if (-not $expectedDependencies.ContainsKey($dependencyKey) -or $seenDependencies.ContainsKey($dependencyKey)) {
            throw "The release manifest has a missing, duplicate, or unsupported dependency tuple: $dependencyKey"
        }
        $seenDependencies[$dependencyKey] = $true
        $path = Get-LocalArtifactPath ([string]$artifact.file)
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($file.Length -ne [long]$artifact.size -or $hash -ne [string]$artifact.sha256 -or
            [string]$artifact.sha256 -notmatch '^[a-f0-9]{64}$' -or
            [string]$artifact.signerThumbprint -notmatch '^[A-F0-9]{40}$') {
            throw "Release dependency verification failed for $($artifact.file)."
        }
        Assert-PinnedDependencySignature -Path $path -ExpectedThumbprint ([string]$artifact.signerThumbprint)
    }
    if ($seenDependencies.Count -ne $expectedDependencies.Count) { throw 'The release manifest omits a required dependency architecture.' }
}

$existingBundles = @($manifest.artifacts | Where-Object {
    $_.product -eq "OpticonBundle" -and (Test-ProductionArtifactTrust $_)
})
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
    "-c", "Release", "-r", $Runtime, "--self-contained", "true", "--no-restore", "-t:Rebuild", "--nologo",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:EnableWindowsTargeting=true",
    "-p:Version=$Version", "-p:InformationalVersion=$Version",
    "-p:IncludeSourceRevisionInInformationalVersion=false", "-p:ContinuousIntegrationBuild=true",
    "-p:OpticonSigningProfile=$SigningProfile",
    "-p:OpticonSourceReleaseKeyId=$SourceReleaseCertificateThumbprint",
    "-p:OpticonSourceReleaseCertificateBase64=$sourceReleaseCertificateBase64",
    "-p:OpticonProductSignerThumbprint=$ProductCertificateThumbprint",
    "-p:OpticonProductSigningCertificateBase64=$productSigningCertificateBase64",
    "-p:DirectoryBuildPropsPath=$(Join-Path $repo 'Directory.Build.props')",
    "-p:DirectoryBuildTargetsPath=$emptyTargets", "-p:MSBuildUserExtensionsPath=$userExtensions",
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false",
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false",
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false",
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false",
    "-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false",
    "-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false",
    "-p:ImportByWildcardBeforeMicrosoftCommonProps=false",
    "-p:ImportByWildcardAfterMicrosoftCommonProps=false",
    "-p:ImportByWildcardBeforeMicrosoftCommonTargets=false",
    "-p:ImportByWildcardAfterMicrosoftCommonTargets=false",
    "-p:ImportByWildcardBeforeMicrosoftCSharpTargets=false",
    "-p:ImportByWildcardAfterMicrosoftCSharpTargets=false",
    "-p:ImportByWildcardBeforeMicrosoftCommonCrossTargetingTargets=false",
    "-p:ImportByWildcardAfterMicrosoftCommonCrossTargetingTargets=false",
    "-p:UseSharedCompilation=false", "-p:MSBuildEnableWorkloadResolver=false", "-nodeReuse:false"
)
if ($isLegacyMigration) {
    $publishArguments += @(
        "-p:OpticonLegacyMigrationVersion=$Version",
        "-p:OpticonLegacyMigrationSignerThumbprint=$LegacyMigrationSignerThumbprint"
    )
}
$executables = if ($SourceOnly) {
    # The launcher is embedded in the signed archive; it is the fixed local
    # trust anchor for a first install, not a separately hosted release asset.
    [ordered]@{ Setup = "Taildesk.Setup.exe" }
} else {
    [ordered]@{
        Setup = "Taildesk.Setup.exe"
        Agent = "Taildesk.Agent.exe"
        Admin = "Opticon.exe"
        Cli = "opticon.exe"
        UpdateGuardian = "Taildesk.UpdateGuardian.exe"
        RouteKeeper = "Taildesk.RouteKeeper.exe"
    }
}
foreach ($component in $executables.Keys) {
    $project = Join-Path $repo "src\Taildesk.$component\Taildesk.$component.csproj"
    $output = Join-Path $buildRoot $component
    $componentArtifacts = Join-Path $intermediateRoot $component
    $trustArguments = @($publishArguments | Where-Object { $_ -like '-p:Opticon*' -or $_ -like '-p:DirectoryBuild*' -or
            $_ -like '-p:MSBuildUserExtensionsPath*' -or $_ -like '-p:Import*' -or
            $_ -eq '-p:UseSharedCompilation=false' -or $_ -eq '-p:MSBuildEnableWorkloadResolver=false' -or $_ -eq '-nodeReuse:false' })
    Invoke-ExactDotNet -Arguments (@(
        'restore', $project, '-r', $Runtime, '--configfile', $nugetConfig, '--packages', $packageCache,
        '--no-cache', '--force', '--force-evaluate', '--disable-parallel',
        '--artifacts-path', $componentArtifacts, '-p:EnableWindowsTargeting=true'
    ) + $trustArguments)
    Invoke-ExactDotNet -Arguments (@('publish', $project) + $publishArguments + @(
        '--artifacts-path', $componentArtifacts, '-o', $output))
    if ($component -eq "Cli") {
        $publishedCli = Join-Path $output "Taildesk.OpticonCli.exe"
        if (-not (Test-Path -LiteralPath $publishedCli -PathType Leaf)) { throw "The Opticon CLI apphost was not published." }
        Move-Item -LiteralPath $publishedCli -Destination (Join-Path $output "opticon.exe") -Force
        $referencedAdminRuntimeConfig = Join-Path $output "Opticon.runtimeconfig.json"
        if (Test-Path -LiteralPath $referencedAdminRuntimeConfig -PathType Leaf) {
            Remove-Item -LiteralPath $referencedAdminRuntimeConfig -Force
        }
    }
    $publishedFiles = @(Get-ChildItem -LiteralPath $output -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne $executables[$component] -or
        -not $publishedFiles[0].DirectoryName.Equals([IO.Path]::GetFullPath($output), [StringComparison]::OrdinalIgnoreCase)) {
        throw "The clean $component publish must contain only the declared single-file executable $($executables[$component])."
    }
    $executable = Join-Path $output $executables[$component]
    $productVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
    if ($productVersion -ne $Version) {
        throw "$component reported product version '$productVersion' instead of release version '$Version'."
    }
    Invoke-ProductSigning -Path $executable
}

$bootstrapRecord = $null
if (-not $SourceOnly) {
    $bootstrapFile = "opticon-bootstrap-$Version.exe"
    $bootstrapPath = Join-Path $artifactDirectory $bootstrapFile
    $bootstrapTemporary = "$bootstrapPath.new.exe"
    if (Test-Path -LiteralPath $bootstrapTemporary) { Remove-Item -LiteralPath $bootstrapTemporary -Force }
    Copy-Item -LiteralPath (Join-Path $buildRoot "Setup\Taildesk.Setup.exe") -Destination $bootstrapTemporary
    if ($isLegacyMigration) {
        # The bundled Setup remains legacy-signed for the pre-trust-split Agent,
        # but a hosted bootstrap is never an update payload. Strip that signature
        # and sign its separate immutable copy with the active product identity.
        Invoke-SignTool -Arguments @('remove', '/s', $bootstrapTemporary)
        Invoke-ProductSigning -Path $bootstrapTemporary -Thumbprint $ProductCertificateThumbprint
    }
    $bootstrapInfo = Get-Item -LiteralPath $bootstrapTemporary
    if ($bootstrapInfo.Length -gt 128MB) {
        Remove-Item -LiteralPath $bootstrapTemporary -Force
        throw 'The signed source bootstrap exceeds the 128 MiB invitation/download safety cap.'
    }
    $bootstrapRecord = [pscustomobject]@{
        product = "OpticonBootstrap"
        version = $Version
        architecture = "x64"
        file = $bootstrapFile
        size = $bootstrapInfo.Length
        sha256 = (Get-FileHash -LiteralPath $bootstrapTemporary -Algorithm SHA256).Hash.ToLowerInvariant()
        signerThumbprint = $ProductCertificateThumbprint
        signingProfile = $SigningProfile
        sourceManifestKeyId = $SourceReleaseCertificateThumbprint
        productSignerThumbprint = $ProductCertificateThumbprint
    }
    $existingBootstraps = @($manifest.artifacts | Where-Object { $_.product -eq 'OpticonBootstrap' -and $_.version -eq $Version })
    if ($existingBootstraps.Count -gt 1) { throw "The outer manifest declares bootstrap release $Version more than once." }
    if ($existingBootstraps.Count -eq 1 -and (([long]$existingBootstraps[0].size -ne [long]$bootstrapRecord.size) -or
        -not ([string]$existingBootstraps[0].sha256).Equals([string]$bootstrapRecord.sha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$existingBootstraps[0].signerThumbprint).Equals([string]$bootstrapRecord.signerThumbprint, [StringComparison]::OrdinalIgnoreCase))) {
        Remove-Item -LiteralPath $bootstrapTemporary -Force
        throw "Bootstrap release $Version is already declared with different bytes or publisher. Bump -Version."
    }
    Move-Item -LiteralPath $bootstrapTemporary -Destination $bootstrapPath -Force
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
                Assert-ProductSignature -Path $file.FullName
                $signerThumbprint = $script:payloadSignerThumbprint
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
        signingProfile = $SigningProfile
        sourceReleaseKeyId = $SourceReleaseCertificateThumbprint
        productSignerThumbprint = $script:payloadSignerThumbprint
        legacyMigration = $isLegacyMigration
        legacyMigrationSignerThumbprint = if ($isLegacyMigration) { $LegacyMigrationSignerThumbprint } else { '' }
        role = $Role
        architecture = $Architecture
        updateProtocolVersion = 1
        minimumGuardianVersion = $MinimumGuardianVersion
        files = $files
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    $manifestBytes = $utf8.GetBytes(($releaseManifest | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllBytes((Join-Path $Stage "release-manifest.json"), $manifestBytes)
    $rsa = Get-OpticonManifestSigningKey -Certificate $releaseManifestCertificate
    if ($null -eq $rsa) { throw "The offline source-release signing certificate has no RSA private key." }
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

$records = @()
if (-not $SourceOnly) {
    $definitions = @(
        @{ Role = "ManagedOnly"; Suffix = "managed"; IncludeAdmin = $false },
        @{ Role = "ControllerAndManaged"; Suffix = "controller"; IncludeAdmin = $true }
    )
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
        New-Item -Path (Join-Path $stage "Payload\Admin\Tools") -ItemType Directory -Force | Out-Null
        Copy-Item -Path (Join-Path $buildRoot "RouteKeeper\*") -Destination (Join-Path $stage "Payload\Admin\Tools") -Recurse -Force
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
         signingProfile = $SigningProfile; sourceManifestKeyId = $SourceReleaseCertificateThumbprint
         productSignerThumbprint = $ProductCertificateThumbprint
         legacyMigrationSignerThumbprint = if ($isLegacyMigration) { $LegacyMigrationSignerThumbprint } else { '' }
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
}

# Build a separate, immutable source archive.  Only this explicit allowlist is
# shipped; bin/obj/artifacts, local configuration, credentials, and repository
# metadata can never enter the source release by accident.
$sourceStage = Join-Path $stageRoot 'source'
New-Item -Path $sourceStage -ItemType Directory -Force | Out-Null
$sourceProps = [xml](Get-Content -Raw -LiteralPath (Join-Path $repo 'Directory.Build.props'))
$sourcePropertyValues = [ordered]@{
    Version = $Version
    OpticonSigningProfile = $SigningProfile
    OpticonSourceReleaseKeyId = $SourceReleaseCertificateThumbprint
    OpticonSourceReleaseCertificateBase64 = $sourceReleaseCertificateBase64
    OpticonProductSignerThumbprint = $ProductCertificateThumbprint
    OpticonProductSigningCertificateBase64 = $productSigningCertificateBase64
}
foreach ($property in $sourcePropertyValues.GetEnumerator()) {
    $node = $sourceProps.SelectSingleNode("/Project/PropertyGroup/$($property.Key)")
    if ($null -eq $node) { throw "Directory.Build.props lacks required production property $($property.Key)." }
    $node.RemoveAttribute('Condition')
    $node.InnerText = [string]$property.Value
}
$propsSettings = [Xml.XmlWriterSettings]::new()
$propsSettings.Encoding = [Text.UTF8Encoding]::new($false)
$propsSettings.Indent = $true
$propsWriter = [Xml.XmlWriter]::Create((Join-Path $sourceStage 'Directory.Build.props'), $propsSettings)
try { $sourceProps.Save($propsWriter) } finally { $propsWriter.Dispose() }
Copy-Item -LiteralPath (Join-Path $repo 'source-package\Directory.Build.targets') -Destination (Join-Path $sourceStage 'Directory.Build.targets')
$sourceGlobalJson = [ordered]@{ sdk = [ordered]@{ version = '10.0.302'; rollForward = 'disable'; allowPrerelease = $false } }
[IO.File]::WriteAllText((Join-Path $sourceStage 'global.json'), ($sourceGlobalJson | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repo 'source-package\Install-OpticonFromSource.ps1') -Destination (Join-Path $sourceStage 'Install-OpticonFromSource.ps1')
Copy-Item -LiteralPath (Join-Path $repo 'source-package\Build-OpticonUpdateFromSource.ps1') -Destination (Join-Path $sourceStage 'Build-OpticonUpdateFromSource.ps1')
Copy-Item -LiteralPath (Join-Path $repo 'source-package\NuGet.Config') -Destination (Join-Path $sourceStage 'NuGet.Config')
if ($SourceOnly) {
    $launcher = Join-Path $buildRoot 'Setup\Taildesk.Setup.exe'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw 'The source-only archive launcher was not produced.'
    }
    Assert-ProductSignature -Path $launcher
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $sourceStage 'OpticonSourceLauncher.exe')
}
$sourceRestoreTrustArguments = @($publishArguments | Where-Object {
        $_ -like '-p:Opticon*' -or $_ -like '-p:DirectoryBuild*' -or
        $_ -like '-p:MSBuildUserExtensionsPath*' -or $_ -like '-p:Import*' -or
        $_ -eq '-p:UseSharedCompilation=false' -or $_ -eq '-p:MSBuildEnableWorkloadResolver=false' -or $_ -eq '-nodeReuse:false'
    })
$sourceRestoreProject = Join-Path $repo 'src\Taildesk.Setup\Taildesk.Setup.csproj'
foreach ($sourceRuntime in @('win-x64', 'win-arm64')) {
    Invoke-ExactDotNet -Arguments (@(
        'restore', $sourceRestoreProject, '-r', $sourceRuntime,
        '--configfile', $nugetConfig, '--packages', $packageCache,
        '--no-cache', '--force', '--force-evaluate', '--disable-parallel',
        '--artifacts-path', (Join-Path $intermediateRoot "source-$sourceRuntime"),
        '-p:SelfContained=false', '-p:EnableWindowsTargeting=true'
    ) + $sourceRestoreTrustArguments)
}
$offlinePackageDirectory = Join-Path $sourceStage 'packages'
New-Item -Path $offlinePackageDirectory -ItemType Directory | Out-Null
$offlinePackages = @(
    @('microsoft.aspnetcore.app.runtime.win-x64', '10.0.10'),
    @('microsoft.netcore.app.runtime.win-x64', '10.0.10'),
    @('microsoft.windowsdesktop.app.runtime.win-x64', '10.0.10'),
    @('microsoft.aspnetcore.app.runtime.win-arm64', '10.0.10'),
    @('microsoft.netcore.app.host.win-arm64', '10.0.10'),
    @('microsoft.netcore.app.runtime.win-arm64', '10.0.10'),
    @('microsoft.windowsdesktop.app.runtime.win-arm64', '10.0.10'),
    @('microsoft.windows.sdk.net.ref', '10.0.19041.57')
)
foreach ($package in $offlinePackages) {
    $packageId = $package[0]
    $packageVersion = $package[1]
    $packageFile = "$packageId.$packageVersion.nupkg"
    $packagePath = Join-Path $packageCache "$packageId\$packageVersion\$packageFile"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "The authenticated source archive dependency is missing: $packageFile"
    }
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $offlinePackageDirectory $packageFile)
}
New-Item -Path (Join-Path $sourceStage 'assets') -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'assets\opticon.ico') -Destination (Join-Path $sourceStage 'assets\opticon.ico')
$sourceProjects = @('Taildesk.Shared', 'Taildesk.Setup', 'Taildesk.Agent', 'Taildesk.UpdateGuardian', 'Taildesk.Admin', 'Taildesk.Cli', 'Taildesk.RouteKeeper')
foreach ($projectName in $sourceProjects) {
    $sourceProject = Join-Path $repo "src\$projectName"
    $targetProject = Join-Path $sourceStage "src\$projectName"
    New-Item -Path $targetProject -ItemType Directory -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $sourceProject -File -Recurse |
             Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.csproj', '.xaml', '.ico', '.manifest') }) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source package input is a reparse point: $($file.FullName)" }
        $relative = $file.FullName.Substring($sourceProject.Length).TrimStart('\', '/')
        $destination = Join-Path $targetProject $relative
        New-Item -Path (Split-Path $destination -Parent) -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}

$sourcePrefix = [IO.Path]::GetFullPath($sourceStage).TrimEnd('\') + '\'
$sourceFiles = @()
foreach ($file in Get-ChildItem -LiteralPath $sourceStage -File -Recurse | Sort-Object FullName) {
    $full = [IO.Path]::GetFullPath($file.FullName)
    if (-not $full.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'A source package input escaped its stage.' }
    $sourceFiles += [pscustomobject][ordered]@{
        path = $full.Substring($sourcePrefix.Length).Replace('\', '/')
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$sourceManifest = [pscustomobject][ordered]@{
    schemaVersion = 1
    version = $Version
    signingProfile = $SigningProfile
    sourceReleaseKeyId = $SourceReleaseCertificateThumbprint
    sourceReleaseCertificateBase64 = $sourceReleaseCertificateBase64
    productSignerThumbprint = $ProductCertificateThumbprint
    productSigningCertificateBase64 = $productSigningCertificateBase64
    sdkVersion = '10.0.302'
    runtimeVersion = '10.0.10'
    targetRuntimes = @('win-x64', 'win-arm64')
    files = $sourceFiles
}
$utf8 = [Text.UTF8Encoding]::new($false)
$sourceManifestBytes = $utf8.GetBytes(($sourceManifest | ConvertTo-Json -Depth 8))
$sourceManifestPath = Join-Path $sourceStage 'source-manifest.json'
[IO.File]::WriteAllBytes($sourceManifestPath, $sourceManifestBytes)
$sourceManifestHash = (Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceRsa = Get-OpticonManifestSigningKey -Certificate $sourceReleaseCertificate
try {
    $sourceSignature = $sourceRsa.SignData($sourceManifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
} finally { $sourceRsa.Dispose() }
[IO.File]::WriteAllText((Join-Path $sourceStage 'source-manifest.sig'), [Convert]::ToBase64String($sourceSignature), $utf8)

$sourceFileName = "opticon-source-$Version.zip"
$sourceDestination = Join-Path $artifactDirectory $sourceFileName
$sourceTemporary = "$sourceDestination.new.zip"
if (Test-Path -LiteralPath $sourceTemporary) { Remove-Item -LiteralPath $sourceTemporary -Force }
Add-Type -AssemblyName System.IO.Compression
$sourceStream = [IO.File]::Open($sourceTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $sourceZip = [IO.Compression.ZipArchive]::new($sourceStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $sourceStage -File -Recurse | Sort-Object FullName) {
            $relative = ([IO.Path]::GetFullPath($file.FullName).Substring($sourcePrefix.Length)).Replace('\', '/')
            $entry = $sourceZip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try { $input.CopyTo($entryStream) } finally { $input.Dispose(); $entryStream.Dispose() }
        }
    } finally { $sourceZip.Dispose() }
} finally { $sourceStream.Dispose() }
$sourceInfo = Get-Item -LiteralPath $sourceTemporary
$sourceRecord = [pscustomobject][ordered]@{
    product = 'OpticonSource'
    version = $Version
    architecture = 'source'
    file = $sourceFileName
    size = $sourceInfo.Length
    sha256 = (Get-FileHash -LiteralPath $sourceTemporary -Algorithm SHA256).Hash.ToLowerInvariant()
    sdkVersion = '10.0.302'
    runtimeVersion = '10.0.10'
    targetRuntimes = @('win-x64', 'win-arm64')
    sourceManifestSha256 = $sourceManifestHash
    sourceManifestKeyId = $SourceReleaseCertificateThumbprint
    signingProfile = $SigningProfile
    productSignerThumbprint = $ProductCertificateThumbprint
    sourceLauncherFile = if ($SourceOnly) { "opticon-source-launcher-$Version.exe" } else { '' }
    sourceLauncherSize = if ($SourceOnly) { (Get-Item -LiteralPath (Join-Path $sourceStage 'OpticonSourceLauncher.exe')).Length } else { 0 }
    sourceLauncherSha256 = if ($SourceOnly) { (Get-FileHash -LiteralPath (Join-Path $sourceStage 'OpticonSourceLauncher.exe') -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
}
$existingSources = @($manifest.artifacts | Where-Object { $_.product -eq 'OpticonSource' -and $_.version -eq $Version })
if ($existingSources.Count -gt 1) { throw "The outer manifest declares source release $Version more than once." }
if ($existingSources.Count -eq 1 -and (([long]$existingSources[0].size -ne [long]$sourceRecord.size) -or
    -not ([string]$existingSources[0].sha256).Equals([string]$sourceRecord.sha256, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([string]$existingSources[0].sourceManifestSha256).Equals($sourceManifestHash, [StringComparison]::OrdinalIgnoreCase))) {
    Remove-Item -LiteralPath $sourceTemporary -Force
    throw "Source release $Version is already declared with different bytes. Bump -Version."
}
Move-Item -LiteralPath $sourceTemporary -Destination $sourceDestination -Force

if ($SourceOnly) {
    # S3 still receives only the source ZIP. Fly embeds these exact signed
    # launcher bytes so the invitation page can provide a one-click entry point.
    $sourceLauncherDestination = Join-Path $artifactDirectory $sourceRecord.sourceLauncherFile
    $sourceLauncherTemporary = "$sourceLauncherDestination.new"
    Copy-Item -LiteralPath (Join-Path $sourceStage 'OpticonSourceLauncher.exe') -Destination $sourceLauncherTemporary -Force
    if ((Get-Item -LiteralPath $sourceLauncherTemporary).Length -ne [long]$sourceRecord.sourceLauncherSize -or
        -not (Get-FileHash -LiteralPath $sourceLauncherTemporary -Algorithm SHA256).Hash.Equals(
            [string]$sourceRecord.sourceLauncherSha256, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $sourceLauncherTemporary -Force
        throw 'The staged one-click source launcher did not match the signed source archive.'
    }
    Move-Item -LiteralPath $sourceLauncherTemporary -Destination $sourceLauncherDestination -Force
}

if ($SourceOnly) {
    # Schema 2 is intentionally an all-source manifest. Existing binary,
    # bootstrap, and dependency records are not carried into its publication;
    # the authenticated gateway rejects the transition while an older active
    # invitation still depends on one of them.
    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 2
        artifacts = @($sourceRecord)
    }
} else {
$candidates = @($existingBundles | Where-Object {
    $existing = $_
    -not ($records | Where-Object {
        $_.role -eq $existing.role -and $_.architecture -eq $existing.architecture -and $_.version -eq $existing.version
    })
}) + @($records)
$retained = @()
$groups = @($candidates | Group-Object -Property { "$($_.role)|$($_.architecture)" } | Sort-Object -Property Name)
foreach ($group in $groups) {
    $ordinary = @()
    $legacyMigration = @()
    foreach ($candidate in @($group.Group)) {
        if (Test-LegacyMigrationArtifact $candidate) { $legacyMigration += $candidate }
        else { $ordinary += $candidate }
    }
    # Keep the newest ordinary bundle for normal clients, plus the newest exact
    # retired-signer bridge.  The latter must remain available after ordinary
    # releases so pre-trust-split Agents can migrate exactly once.
    foreach ($partition in @($ordinary, $legacyMigration)) {
        if ($partition.Count -eq 0) { continue }
        $best = $partition[0]
        foreach ($candidate in $partition | Select-Object -Skip 1) {
            $comparison = Compare-SemanticVersion ([string]$candidate.version) ([string]$best.version)
            if ($comparison -gt 0) { $best = $candidate }
            elseif ($comparison -eq 0 -and
                ([string]$candidate.version -ne [string]$best.version -or
                 [string]$candidate.file -ne [string]$best.file -or
                 -not ([string]$candidate.sha256).Equals([string]$best.sha256, [StringComparison]::OrdinalIgnoreCase))) {
                throw "The outer manifest contains ambiguous precedence-equivalent releases for $($group.Name)."
            }
        }
        $retained += $best
    }
}
foreach ($artifact in $retained) {
    $path = Get-LocalArtifactPath ([string]$artifact.file)
    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($file.Length -ne [long]$artifact.size -or -not $hash.Equals([string]$artifact.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Retained rollback bundle verification failed for $($artifact.file)."
    }
}
$retainedBootstraps = @($manifest.artifacts | Where-Object {
    $_.product -eq 'OpticonBootstrap' -and $_.version -ne $Version -and
    (Test-ProductionArtifactTrust $_) -and
    (Get-ArtifactString $_ 'signerThumbprint') -eq $ProductCertificateThumbprint
})
$retainedSources = @($manifest.artifacts | Where-Object {
    $_.product -eq 'OpticonSource' -and $_.version -ne $Version -and
    (Test-ProductionArtifactTrust $_)
})
foreach ($artifact in @($retainedBootstraps) + @($retainedSources)) {
    if ([string]::IsNullOrWhiteSpace([string]$artifact.downloadUrl)) {
        $path = Get-LocalArtifactPath ([string]$artifact.file)
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($file.Length -ne [long]$artifact.size -or -not $hash.Equals([string]$artifact.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Retained invitation artifact verification failed for $($artifact.file)."
        }
    }
}
$manifest.artifacts = @($manifest.artifacts | Where-Object { $_.product -notin @("OpticonBundle", "OpticonBootstrap", "OpticonSource") }) + @($retained) + @($retainedBootstraps) + @($bootstrapRecord) + @($retainedSources) + @($sourceRecord)
}
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
if ($retiredObsoleteLegacyMigrationArtifacts.Count -gt 0) {
    Write-Host "Retired $($retiredObsoleteLegacyMigrationArtifacts.Count) obsolete local 1.1.40 legacy migration artifact record(s); they were not retained or published." -ForegroundColor Yellow
}
if ($SourceOnly) {
    $sourceRecord | Format-Table version, file, size, sha256, sourceManifestSha256 -AutoSize
    Write-Host "Run .\scripts\Publish-OpticonSourceRelease.ps1 to upload the one signed source archive." -ForegroundColor Green
} else {
    $retained | Format-Table role, version, file, size, sha256 -AutoSize
    Write-Host "Run .\scripts\Publish-OpticonBundles.ps1 after deploying the gateway manifest." -ForegroundColor Green
}
} finally {
    if (Get-Variable -Name sourceReleaseCertificate -ErrorAction SilentlyContinue) { $sourceReleaseCertificate.Dispose() }
    if (Get-Variable -Name productCertificate -ErrorAction SilentlyContinue) { $productCertificate.Dispose() }
    if (Get-Variable -Name legacyMigrationCertificate -ErrorAction SilentlyContinue) { $legacyMigrationCertificate.Dispose() }
    $packageBuildLock.Dispose()
    if (Test-Path -LiteralPath $sdkAnchor) { Remove-Item -LiteralPath $sdkAnchor -Recurse -Force -ErrorAction SilentlyContinue }
}
