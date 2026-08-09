[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Production', 'OwnerManaged', 'Developer')]
    [string]$BuildProfile = 'Production',
    [string]$CodeSigningCertificateThumbprint,
    [string]$SourceReleaseSigningCertificateThumbprint,
    [ValidateSet('http://timestamp.digicert.com')]
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$SkipTargetReleaseDeployment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RequiredSdkVersion = '10.0.302'
$InvitationSigningThumbprint = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'
$repo = [IO.Path]::GetFullPath($PSScriptRoot)
$artifacts = Join-Path $repo 'artifacts'
$dist = Join-Path $repo 'dist'
$propsPath = Join-Path $repo 'Directory.Build.props'
$solutionPath = Join-Path $repo 'Taildesk.sln'
$packageLockPath = Join-Path $artifacts '.opticon-package-build.lock'
$sourceRsa = $null
$git = $null

function Normalize-Thumbprint {
    param([Parameter(Mandatory)][string]$Value)
    return -join ($Value.ToUpperInvariant().ToCharArray() | Where-Object { [Uri]::IsHexDigit($_) })
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
            return [IO.File]::Open(
                $Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for another Opticon package build to release $Path."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Remove-OpticonBuildDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $fullArtifacts = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($artifacts))
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $fullArtifacts + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a build directory outside the Opticon artifacts root: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Assert-NoReparseTraversal {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )
    $fullRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath -ne $fullRoot -and -not $fullPath.StartsWith(
            $fullRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "A trusted build-tool path escaped its fixed root: $fullPath"
    }
    $current = $fullPath
    while ($true) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "A trusted build-tool path is a reparse point: $current"
        }
        if ($current.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = Split-Path $current -Parent
        if ([string]::IsNullOrWhiteSpace($current)) {
            throw "A trusted build-tool path has no fixed parent: $fullPath"
        }
    }
}

function Get-RequiredDotNet {
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $dotnet = Join-Path $programFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw ".NET SDK $RequiredSdkVersion is required at the fixed Program Files dotnet location."
    }
    Assert-NoReparseTraversal -Root $programFiles -Path $dotnet
    $sdkDirectory = Join-Path (Split-Path $dotnet -Parent) "sdk\$RequiredSdkVersion"
    if (-not (Test-Path -LiteralPath $sdkDirectory -PathType Container)) {
        throw ".NET SDK $RequiredSdkVersion is required exactly. Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
    }
    Assert-NoReparseTraversal -Root $programFiles -Path $sdkDirectory
    return $dotnet
}

function Get-SigningCertificate {
    param(
        [Parameter(Mandatory)][string]$Thumbprint,
        [Parameter(Mandatory)][string]$Purpose
    )
    $normalized = Normalize-Thumbprint $Thumbprint
    if ($normalized -notmatch '^[A-F0-9]{40}$') {
        throw "$Purpose thumbprint must be exactly 40 hexadecimal characters."
    }
    $matches = @()
    foreach ($storeLocation in @(
            [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::My, $storeLocation)
        try {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            foreach ($certificate in $store.Certificates) {
                if ((Normalize-Thumbprint $certificate.Thumbprint) -eq $normalized -and
                    $certificate.HasPrivateKey) {
                    $matches += [pscustomobject]@{
                        Certificate = $certificate
                        StoreLocation = $storeLocation
                    }
                }
            }
        } finally {
            $store.Dispose()
        }
    }
    if ($matches.Count -eq 0) {
        throw "$Purpose certificate $normalized with a private key was not found."
    }
    $distinct = @($matches | Group-Object { [Convert]::ToBase64String($_.Certificate.RawData) })
    if ($distinct.Count -ne 1) {
        throw "More than one different $Purpose certificate matched $normalized."
    }
    return $matches[0]
}

function Assert-CodeSigningCertificate {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][bool]$RequirePublicTrust
    )
    $now = [DateTime]::UtcNow
    if ($now -lt $Certificate.NotBefore.ToUniversalTime() -or
        $now -gt $Certificate.NotAfter.ToUniversalTime()) {
        throw 'The Opticon product code-signing certificate is outside its validity period.'
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigning = @($Certificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq $codeSigningOid }).Count -gt 0
    if (-not $hasCodeSigning) {
        throw 'The Opticon product signer lacks the Code Signing EKU.'
    }
    if ($RequirePublicTrust) {
        $subject = [Convert]::ToBase64String($Certificate.SubjectName.RawData)
        $issuer = [Convert]::ToBase64String($Certificate.IssuerName.RawData)
        if ($subject -ceq $issuer) {
            throw 'The production product signer must not be self-signed.'
        }
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.RevocationMode =
                [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
            $chain.ChainPolicy.RevocationFlag =
                [Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
            $chain.ChainPolicy.VerificationFlags =
                [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
            if (-not $chain.Build($Certificate)) {
                $errors = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; '
                throw "The production product signer does not build to a trusted, non-revoked Windows chain: $errors"
            }
        } finally {
            $chain.Dispose()
        }
    }
}

function Assert-ProductionGitState {
    $gitRoot = (Invoke-FixedGit -Arguments @('-C', $repo, 'rev-parse', '--show-toplevel')).Trim()
    if ([string]::IsNullOrWhiteSpace($gitRoot) -or $gitRoot.Contains([Environment]::NewLine)) {
        throw 'A production build must originate from a committed Git checkout.'
    }
    $gitRoot = [IO.Path]::GetFullPath($gitRoot.Trim())
    $relativeRepo = [IO.Path]::GetRelativePath($gitRoot, $repo).Replace('\', '/')
    $status = Invoke-FixedGit -Arguments @(
        '-C', $gitRoot, 'status', '--porcelain=v1', '--untracked-files=all', '--', $relativeRepo)
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw 'A production build requires a clean committed Opticon source tree, including no untracked files.'
    }
}

function Get-FixedGit {
    $roots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
    foreach ($root in $roots) {
        foreach ($relative in @('Git\cmd\git.exe', 'Git\bin\git.exe')) {
            $candidate = Join-Path $root $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Assert-NoReparseTraversal -Root $root -Path $candidate
                return $candidate
            }
        }
    }
    throw 'A production build requires Git at its fixed Program Files location.'
}

function Invoke-FixedGit {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if ([string]::IsNullOrWhiteSpace($script:git)) { $script:git = Get-FixedGit }
    $windows = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:git
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $start.Environment['SystemRoot'] = $windows
    $start.Environment['WINDIR'] = $windows
    $start.Environment['PATH'] = [string]::Join(
        [IO.Path]::PathSeparator, @((Split-Path $script:git -Parent), $system32))
    $start.Environment['PATHEXT'] = '.COM;.EXE'
    $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $start.Environment['GIT_CONFIG_GLOBAL'] = 'NUL'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start the fixed Git executable.' }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Fixed Git failed with exit code $($process.ExitCode): $($stderr.Trim())"
        }
        return $stdout
    } finally {
        $process.Dispose()
    }
}

function Get-SignTool {
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $binRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $binRoot -PathType Container)) {
        throw 'The Windows SDK signing tools are not installed.'
    }
    $candidates = @(Get-ChildItem -LiteralPath $binRoot -Directory |
        Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count -eq 0) {
        throw 'The fixed x64 Windows SDK signtool.exe was not found.'
    }
    Assert-NoReparseTraversal -Root $programFilesX86 -Path $candidates[0]
    return $candidates[0]
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $windows = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:dotnet
    $start.WorkingDirectory = $script:sdkRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $false
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $environment = [ordered]@{
            SystemRoot = $windows
            WINDIR = $windows
            SystemDrive = [IO.Path]::GetPathRoot($windows).TrimEnd('\')
            ProgramFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            'ProgramFiles(x86)' = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
            ProgramW6432 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            ProgramData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
            ComSpec = (Join-Path $system32 'cmd.exe')
            PATH = [string]::Join([IO.Path]::PathSeparator, @((Split-Path $script:dotnet -Parent), $system32))
            PATHEXT = '.COM;.EXE'
            TEMP = $script:buildTemp
            TMP = $script:buildTemp
            DOTNET_ROOT = (Split-Path $script:dotnet -Parent)
            DOTNET_CLI_HOME = $script:cliHome
            USERPROFILE = $script:buildUserProfile
            HOME = $script:buildUserProfile
            APPDATA = $script:buildAppData
            LOCALAPPDATA = $script:buildLocalAppData
            DOTNET_MULTILEVEL_LOOKUP = '0'
            DOTNET_NOLOGO = '1'
            DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            NUGET_PACKAGES = $script:packageCache
            NUGET_HTTP_CACHE_PATH = $script:nugetHttpCache
            NUGET_PLUGINS_CACHE_PATH = $script:nugetPluginsCache
            NUGET_XMLDOC_MODE = 'skip'
            NUGET_CERT_REVOCATION_MODE = 'online'
            MSBUILDDISABLENODEREUSE = '1'
        }
    foreach ($entry in $environment.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            $start.Environment[[string]$entry.Key] = [string]$entry.Value
        }
    }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start the fixed .NET SDK host.' }
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "dotnet failed with exit code $($process.ExitCode)."
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-SignTool {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $windows = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:signTool
    $start.WorkingDirectory = $system32
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $false
    foreach ($argument in $Arguments) { $null = $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $start.Environment['SystemRoot'] = $windows
    $start.Environment['WINDIR'] = $windows
    $start.Environment['ProgramFiles'] =
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $start.Environment['ProgramFiles(x86)'] =
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $start.Environment['ProgramData'] =
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $start.Environment['PATH'] = [string]::Join(
        [IO.Path]::PathSeparator, @((Split-Path $script:signTool -Parent), $system32))
    $start.Environment['PATHEXT'] = '.COM;.EXE'
    $start.Environment['TEMP'] = $script:buildTemp
    $start.Environment['TMP'] = $script:buildTemp
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'Windows could not start the fixed Windows SDK signer.' }
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "signtool failed with exit code $($process.ExitCode)."
        }
    } finally {
        $process.Dispose()
    }
}

function Publish-OpticonProject {
    param(
        [Parameter(Mandatory)][string]$ProjectName,
        [Parameter(Mandatory)][string]$ExpectedExecutable,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    $projectFile = Join-Path $repo "src\$ProjectName\$ProjectName.csproj"
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "The declared Opticon project is missing: $ProjectName"
    }
    $null = [IO.Directory]::CreateDirectory($OutputDirectory)
    $componentArtifacts = Join-Path $script:workspace "component-artifacts\$ProjectName"
    Invoke-DotNet -Arguments (@(
        'restore', $projectFile,
        '-r', $Runtime,
        '--configfile', $script:nugetConfig,
        '--packages', $script:packageCache,
        '--no-cache',
        '--force',
        '--force-evaluate',
        '--disable-parallel',
        '--artifacts-path', $componentArtifacts
    ) + $script:msbuildTrustArguments)
    Invoke-DotNet -Arguments (@(
        'publish', $projectFile,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', 'true',
        '--no-restore',
        '-t:Rebuild',
        '--nologo',
        '-o', $OutputDirectory,
        '--artifacts-path', $componentArtifacts,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:EnableWindowsTargeting=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:ContinuousIntegrationBuild=true'
    ) + $script:msbuildTrustArguments)
    if ($ProjectName -eq 'Taildesk.Cli') {
        $referencedAdminRuntimeConfig = Join-Path $OutputDirectory 'Opticon.runtimeconfig.json'
        if (Test-Path -LiteralPath $referencedAdminRuntimeConfig -PathType Leaf) {
            [IO.File]::Delete($referencedAdminRuntimeConfig)
        }
    }
    $files = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse)
    if ($files.Count -ne 1 -or -not $files[0].Name.Equals(
            $ExpectedExecutable, [StringComparison]::Ordinal)) {
        throw "The clean $ProjectName publish must contain only the declared single-file $ExpectedExecutable."
    }
    return $files[0].FullName
}

function Sign-OpticonExecutable {
    param([Parameter(Mandatory)][string]$Path)
    $arguments = @('sign', '/fd', 'SHA256')
    if ($BuildProfile -in @('Production', 'OwnerManaged')) {
        $arguments += @('/tr', $TimestampServer, '/td', 'SHA256')
    }
    $arguments += @('/sha1', $script:productThumbprint, '/s', 'My')
    if ($script:productSigner.StoreLocation -eq
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine) {
        $arguments += '/sm'
    }
    $arguments += $Path
    Invoke-SignTool -Arguments $arguments
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not $signature.SignerCertificate -or
        (Normalize-Thumbprint $signature.SignerCertificate.Thumbprint) -ne $script:productThumbprint) {
        throw "The signed executable lacks the exact product signer: $Path"
    }
    if ($BuildProfile -eq 'Production' -and
        ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate)) {
        throw "Windows did not validate the production Authenticode chain for $Path ($($signature.Status))."
    }
    if ($BuildProfile -eq 'OwnerManaged' -and
        ($signature.Status -notin @('Valid', 'UnknownError') -or -not $signature.TimeStamperCertificate)) {
        throw "Windows did not validate the exact timestamped owner-managed signature for $Path ($($signature.Status))."
    }
    if ($BuildProfile -eq 'Developer' -and
        ($signature.Status -in @('NotSigned', 'HashMismatch') -or $signature.TimeStamperCertificate)) {
        throw "Developer Authenticode signing failed for $Path ($($signature.Status))."
    }
}

function Copy-ExactFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    $null = [IO.Directory]::CreateDirectory((Split-Path $Destination -Parent))
    [IO.File]::Copy($Source, $Destination, $false)
}

function New-ReproducibleZip {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$Destination
    )
    Add-Type -AssemblyName System.IO.Compression
    $temporary = $Destination + '.new-' + [Guid]::NewGuid().ToString('N')
    $stream = [IO.File]::Open(
        $temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse |
                     Sort-Object { [IO.Path]::GetRelativePath($SourceDirectory, $_.FullName) }) {
                if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "A package input is a reparse point: $($file.FullName)"
                }
                $relative = [IO.Path]::GetRelativePath(
                    $SourceDirectory, $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::Open(
                    $file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $output = $entry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                } finally {
                    $input.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
        $stream.Flush($true)
    } catch {
        try { [IO.File]::Delete($temporary) } catch { }
        throw
    } finally {
        $stream.Dispose()
    }
    [IO.File]::Move($temporary, $Destination, $true)
}

$packageBuildLock = Enter-OpticonPackageBuildLock -Path $packageLockPath
$workspace = Join-Path $artifacts ('b-' + [Guid]::NewGuid().ToString('N'))
try {
    if ($BuildProfile -eq 'Developer' -and -not $SkipTargetReleaseDeployment) {
        throw 'Developer artifacts are intentionally non-publishable; pass -SkipTargetReleaseDeployment explicitly.'
    }
    if ($BuildProfile -in @('Production', 'OwnerManaged')) {
        if ([string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint) -or
            [string]::IsNullOrWhiteSpace($SourceReleaseSigningCertificateThumbprint)) {
            throw 'Publishable builds require separate code-signing and source-release signing certificate thumbprints.'
        }
        Assert-ProductionGitState
    }
    if ([string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint) -or
        [string]::IsNullOrWhiteSpace($SourceReleaseSigningCertificateThumbprint)) {
        throw 'Every package build requires explicit code-signing and source-release signing certificate thumbprints.'
    }

    $productThumbprint = Normalize-Thumbprint $CodeSigningCertificateThumbprint
    $sourceThumbprint = Normalize-Thumbprint $SourceReleaseSigningCertificateThumbprint
    if ($productThumbprint -eq (Normalize-Thumbprint $InvitationSigningThumbprint) -or
        $sourceThumbprint -eq (Normalize-Thumbprint $InvitationSigningThumbprint) -or
        $productThumbprint -eq $sourceThumbprint) {
        throw 'Invitation, source-release, and product code-signing trust domains must remain separate.'
    }
    $productSigner = Get-SigningCertificate -Thumbprint $productThumbprint -Purpose 'product code-signing'
    $sourceSigner = Get-SigningCertificate -Thumbprint $sourceThumbprint -Purpose 'source-release signing'
    Assert-CodeSigningCertificate -Certificate $productSigner.Certificate -RequirePublicTrust ($BuildProfile -eq 'Production')
    if ($sourceSigner.Certificate.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow -or
        $sourceSigner.Certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow) {
        throw 'The source-release signing certificate is outside its validity period.'
    }
    $props = [xml][IO.File]::ReadAllText($propsPath)
    $version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $requiredRuntime = $props.SelectSingleNode('/Project/PropertyGroup/OpticonRuntimeVersion').InnerText
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $requiredRuntime -notmatch '^10\.0\.[0-9]+$') {
        throw 'Directory.Build.props does not contain exact release/runtime versions.'
    }

    $dotnet = Get-RequiredDotNet
    $signTool = Get-SignTool
    $null = [IO.Directory]::CreateDirectory($workspace)
    $sdkRoot = Join-Path $workspace 'sdk-pin'
    $stage = Join-Path $workspace 'package'
    $publishRoot = Join-Path $workspace 'publish'
    $packageCache = Join-Path $workspace 'nuget-packages'
    $nugetHttpCache = Join-Path $workspace 'nuget-http-cache'
    $cliHome = Join-Path $workspace 'dotnet-home'
    $buildTemp = Join-Path $workspace 'temp'
    $buildUserProfile = Join-Path $workspace 'user-profile'
    $buildAppData = Join-Path $buildUserProfile 'AppData\Roaming'
    $buildLocalAppData = Join-Path $buildUserProfile 'AppData\Local'
    $nugetPluginsCache = Join-Path $workspace 'nuget-plugins-cache'
    $userExtensions = Join-Path $workspace 'empty-msbuild-user-extensions'
    foreach ($directory in @(
            $sdkRoot, $stage, $publishRoot, $packageCache, $nugetHttpCache,
            $cliHome, $buildTemp, $userExtensions, $buildUserProfile,
            $buildAppData, $buildLocalAppData, $nugetPluginsCache)) {
        $null = [IO.Directory]::CreateDirectory($directory)
    }
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    $sdkPin = [ordered]@{
        sdk = [ordered]@{
            version = $RequiredSdkVersion
            rollForward = 'disable'
            allowPrerelease = $false
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $sdkRoot 'global.json'), ($sdkPin | ConvertTo-Json -Depth 3), $utf8NoBom)
    $emptyTargets = Join-Path $sdkRoot 'Directory.Build.targets'
    [IO.File]::WriteAllText($emptyTargets, '<Project />', $utf8NoBom)
    $nugetConfig = Join-Path $sdkRoot 'NuGet.Config'
    $escapedPackages = [System.Security.SecurityElement]::Escape($packageCache)
    $escapedHttpCache = [System.Security.SecurityElement]::Escape($nugetHttpCache)
    $nugetConfiguration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$escapedPackages" />
    <add key="httpCachePath" value="$escapedHttpCache" />
  </config>
</configuration>
"@
    [IO.File]::WriteAllText($nugetConfig, $nugetConfiguration, $utf8NoBom)

    $sourcePublicBase64 = [Convert]::ToBase64String($sourceSigner.Certificate.RawData)
    $productPublicBase64 = [Convert]::ToBase64String($productSigner.Certificate.RawData)
    $msbuildTrustArguments = @(
        "-p:OpticonSigningProfile=$BuildProfile",
        "-p:OpticonSourceReleaseKeyId=$sourceThumbprint",
        "-p:OpticonSourceReleaseCertificateBase64=$sourcePublicBase64",
        "-p:OpticonProductSignerThumbprint=$productThumbprint",
        "-p:OpticonProductSigningCertificateBase64=$productPublicBase64",
        "-p:DirectoryBuildPropsPath=$propsPath",
        "-p:DirectoryBuildTargetsPath=$emptyTargets",
        "-p:MSBuildUserExtensionsPath=$userExtensions",
        '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false',
        '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false',
        '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false',
        '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false',
        '-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false',
        '-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false',
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false'
    )

    Push-Location $sdkRoot
    try {
        Invoke-DotNet -Arguments @('--version')
        Invoke-DotNet -Arguments (@(
            'restore', $solutionPath,
            '--configfile', $nugetConfig,
            '--packages', $packageCache,
            '--no-cache',
            '--force',
            '--force-evaluate',
            '--disable-parallel'
        ) + $msbuildTrustArguments)
        try {
            try {
                Invoke-DotNet -Arguments (@(
                    'build', $solutionPath, '-c', 'Release', '-t:Rebuild', '--nologo',
                    '--no-restore',
                    '-p:EnableWindowsTargeting=true',
                    '-p:IncludeSourceRevisionInInformationalVersion=false',
                    '-p:ContinuousIntegrationBuild=true'
                ) + $msbuildTrustArguments)
            } catch {
                throw "The Opticon solution build failed. $($_.Exception.Message)"
            }
            $selfTestDll = Join-Path $repo (
                'tests\Taildesk.SelfTest\bin\Release\net10.0-windows10.0.19041.0\Taildesk.SelfTest.dll')
            if (-not (Test-Path -LiteralPath $selfTestDll -PathType Leaf)) {
                throw 'The Opticon self-test executable was not built.'
            }
            try {
                Invoke-DotNet -Arguments @($selfTestDll)
            } catch {
                throw 'The Opticon self-tests failed.'
            }

            $published = @{}
            foreach ($declaration in @(
                    @('Taildesk.Agent', 'Taildesk.Agent.exe', 'Agent'),
                    @('Taildesk.Admin', 'Opticon.exe', 'Admin'),
                    @('Taildesk.Cli', 'Taildesk.OpticonCli.exe', 'Cli'),
                    @('Taildesk.Setup', 'Taildesk.Setup.exe', 'Setup'),
                    @('Taildesk.UpdateGuardian', 'Taildesk.UpdateGuardian.exe', 'UpdateGuardian'),
                    @('Taildesk.RouteKeeper', 'Taildesk.RouteKeeper.exe', 'RouteKeeper'),
                    @('Taildesk.CommandCenterInstaller', 'Install-Opticon.exe', 'CommandCenterInstaller'))) {
                $outputDirectory = Join-Path $publishRoot $declaration[2]
                $published[$declaration[2]] = Publish-OpticonProject -ProjectName $declaration[0] -ExpectedExecutable $declaration[1] -OutputDirectory $outputDirectory
            }
        } finally {
            # All dotnet children run in the generated exact-SDK directory.
        }
    } finally {
        Pop-Location
    }

    $cliCommand = Join-Path (Split-Path $published.Cli -Parent) 'opticon.exe'
    [IO.File]::Move($published.Cli, $cliCommand)
    $published.Cli = $cliCommand
    $cliFiles = @(Get-ChildItem -LiteralPath (Split-Path $cliCommand -Parent) -File)
    if ($cliFiles.Count -ne 1 -or $cliFiles[0].Name -ne 'opticon.exe') {
        throw 'The clean CLI publish must contain only the signed opticon.exe single-file app.'
    }

    $payload = [ordered]@{
        'App/Opticon.exe' = $published.Admin
        'App/Cli/opticon.exe' = $published.Cli
        'App/Tools/Taildesk.RouteKeeper.exe' = $published.RouteKeeper
        'App/Payload/Setup/Taildesk.Setup.exe' = $published.Setup
        'App/Payload/Agent/Taildesk.Agent.exe' = $published.Agent
        'App/Payload/Admin/Opticon.exe' = $published.Admin
        'App/Payload/Admin/Cli/opticon.exe' = $published.Cli
        'App/Payload/Admin/Tools/Taildesk.RouteKeeper.exe' = $published.RouteKeeper
        'App/Payload/UpdateGuardian/Taildesk.UpdateGuardian.exe' = $published.UpdateGuardian
    }
    foreach ($entry in $payload.GetEnumerator()) {
        Copy-ExactFile -Source $entry.Value -Destination (Join-Path $stage $entry.Key.Replace('/', '\'))
    }
    foreach ($executable in Get-ChildItem -LiteralPath (Join-Path $stage 'App') -Filter '*.exe' -File -Recurse) {
        Sign-OpticonExecutable -Path $executable.FullName
    }

    # The offline release private key is opened only after every build, test,
    # publish, and Authenticode operation has completed.
    $sourceRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $sourceSigner.Certificate)
    if (-not $sourceRsa) {
        throw 'The source-release signing certificate has no RSA private key.'
    }

    $manifestFiles = @($payload.Keys | Sort-Object | ForEach-Object {
        $path = Join-Path $stage $_.Replace('/', '\')
        [ordered]@{
            path = $_
            size = [IO.FileInfo]::new($path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $version
        signingProfile = $BuildProfile
        sourceReleaseKeyId = $sourceThumbprint
        productSignerThumbprint = $productThumbprint
        developmentOnly = ($BuildProfile -eq 'Developer')
        files = $manifestFiles
    }
    $manifestBytes = $utf8NoBom.GetBytes(
        ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
    $manifestPath = Join-Path $stage 'command-center.manifest.json'
    $signaturePath = Join-Path $stage 'command-center.manifest.sig'
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    $manifestSignature = $sourceRsa.SignData(
        $manifestBytes,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes($signaturePath, $manifestSignature)
    if (-not $sourceRsa.VerifyData(
            $manifestBytes,
            $manifestSignature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'The command-center manifest signature did not verify after creation.'
    }

    $wrapper = Join-Path $stage 'Install-Opticon.exe'
    Copy-ExactFile -Source $published.CommandCenterInstaller -Destination $wrapper
    Sign-OpticonExecutable -Path $wrapper
    $expectedRoot = @(
        'App',
        'Install-Opticon.exe',
        'command-center.manifest.json',
        'command-center.manifest.sig')
    $actualRoot = @(Get-ChildItem -LiteralPath $stage | Select-Object -ExpandProperty Name | Sort-Object)
    if ([string]::Join('|', $actualRoot) -ne [string]::Join('|', ($expectedRoot | Sort-Object))) {
        throw 'The command-center package root is not the exact signed-wrapper allowlist.'
    }

    $null = [IO.Directory]::CreateDirectory($dist)
    $artifactName = if ($BuildProfile -eq 'Production') {
        "Opticon-CommandCenter-$Runtime.zip"
    } elseif ($BuildProfile -eq 'OwnerManaged') {
        "Opticon-CommandCenter-OWNER-MANAGED-$Runtime.zip"
    } else {
        "Opticon-CommandCenter-DEV-UNTRUSTED-$Runtime.zip"
    }
    $zip = Join-Path $dist $artifactName
    New-ReproducibleZip -SourceDirectory $stage -Destination $zip
    Write-Host "Built $zip" -ForegroundColor Green
} finally {
    if ($null -ne $sourceRsa) { $sourceRsa.Dispose() }
    Remove-OpticonBuildDirectory -Path $workspace
    $packageBuildLock.Dispose()
}

if ($BuildProfile -eq 'Production' -and -not $SkipTargetReleaseDeployment) {
    & (Join-Path $repo 'scripts\Ensure-OpticonTargetRelease.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'The Opticon target release check or deployment failed.'
    }
}
