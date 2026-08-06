#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\Taildesk\Admin",
    [switch]$ControllerOnlyRepair
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'App'
if (-not (Test-Path (Join-Path $source 'Opticon.exe')) -or
    -not (Test-Path (Join-Path $source 'Cli\opticon.exe'))) {
    throw 'The App folder or signed Opticon CLI is missing. Extract the complete command-center ZIP first.'
}


$script:ControllerOwnershipMarkerName = '.opticon-controller-owned'
$script:ControllerOwnershipMarkerValue = 'Opticon command-center controller payload v1'
$script:ControllerReadyMarkerName = '.opticon-controller-ready'
$script:ControllerReadyMarkerValue = 'Opticon command-center controller payload ready v1'
$script:ControllerInstallDirectoryValueName = 'InstallDirectory'
$script:ControllerInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Taildesk')).TrimEnd('\')
$script:CanonicalControllerInstallDirectory = [IO.Path]::GetFullPath((Join-Path $script:ControllerInstallRoot 'Admin')).TrimEnd('\')
$script:ControllerInstallLockPath = Join-Path $script:ControllerInstallRoot '.controller-install.lock'
function Test-SameFullPath {
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)
    return [IO.Path]::GetFullPath($Left).TrimEnd('\').Equals(
        [IO.Path]::GetFullPath($Right).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathWithinDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Directory)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    return $fullPath.Equals($fullDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullDirectory + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-InstalledOpticonClosed {
    param([Parameter(Mandatory)][string[]]$Directories)
    $roots = @($Directories | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') } |
        Select-Object -Unique)
    if ($roots.Count -eq 0) { return }

    $running = @()
    foreach ($process in @(Get-Process -Name 'Opticon','Taildesk.Admin','Taildesk.OpticonCli' -ErrorAction SilentlyContinue)) {
        try {
            $processPath = [IO.Path]::GetFullPath($process.MainModule.FileName)
            if (@($roots | Where-Object { Test-PathWithinDirectory -Path $processPath -Directory $_ }).Count -gt 0) {
                $running += "$($process.ProcessName) ($($process.Id))"
            }
        } catch {
            throw "Opticon could not verify running process $($process.ProcessName) ($($process.Id)); close it before installation."
        } finally {
            $process.Dispose()
        }
    }
    if ($running.Count -gt 0) {
        throw "Close the installed or retained Opticon UI and CLI normally before upgrading ($($running -join ', ')). This lets active SSH sessions revoke their leases and erase ephemeral keys."
    }
}

function Assert-PinnedOpticonExecutable {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The signed Opticon executable is missing: $Path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53' -or
        $signature.Status -in @('NotSigned','HashMismatch')) {
        throw "The Opticon executable is unsigned, altered, or signed by an unexpected key: $Path"
    }
}

function Get-MatchingOpticonUiCliVersion {
    param([Parameter(Mandatory)][string]$Directory)
    $uiPath = Join-Path $Directory 'Opticon.exe'
    $cliPath = Join-Path $Directory 'Cli\opticon.exe'
    try {
        $uiVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($uiPath).FileVersion)
        $cliVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($cliPath).FileVersion)
    } catch {
        throw "The Opticon UI or CLI has no valid file version in '$Directory'."
    }
    if ($uiVersion -ne $cliVersion) {
        throw "The Opticon UI ($uiVersion) and CLI ($cliVersion) versions do not match in '$Directory'."
    }
    return $uiVersion
}

function Assert-MatchingOpticonUiCliVersion {
    param([Parameter(Mandatory)][string]$Directory)
    [void](Get-MatchingOpticonUiCliVersion -Directory $Directory)
}

function Assert-SafeInstallSibling {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent, [Parameter(Mandatory)][string]$LeafPrefix)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    if (-not [IO.Path]::GetDirectoryName($fullPath).Equals($fullParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($fullPath).StartsWith($LeafPrefix, [StringComparison]::Ordinal)) {
        throw "Unsafe Opticon installation transaction path: $fullPath"
    }
}

function Assert-NoDirectoryReparsePoints {
    param([Parameter(Mandatory)][string]$Directory)
    $root = Get-Item -LiteralPath $Directory -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The Opticon controller directory is a reparse point: $Directory"
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $Directory -Force -Recurse)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The Opticon controller directory contains a reparse point: $($item.FullName)"
        }
    }
}

function Write-ControllerOwnershipMarker {
    param([Parameter(Mandatory)][string]$Directory)
    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        (Join-Path $Directory $script:ControllerOwnershipMarkerName),
        $script:ControllerOwnershipMarkerValue,
        $encoding)
}

function Write-ControllerReadyMarker {
    param([Parameter(Mandatory)][string]$Directory)
    $path = Join-Path $Directory $script:ControllerReadyMarkerName
    $encoding = New-Object Text.UTF8Encoding($false)
    $stream = New-Object IO.FileStream(
        $path, [IO.FileMode]::Create, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $writer = New-Object IO.StreamWriter($stream, $encoding, 4096, $true)
        try {
            $version = Get-MatchingOpticonUiCliVersion -Directory $Directory
            $writer.Write("$($script:ControllerReadyMarkerValue)|$version")
            $writer.Flush()
            $stream.Flush($true)
        } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

function Test-ControllerReadyMarker {
    param([Parameter(Mandatory)][string]$Directory)
    $marker = Join-Path $Directory $script:ControllerReadyMarkerName
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    try { $version = Get-MatchingOpticonUiCliVersion -Directory $Directory }
    catch { return $false }
    return [IO.File]::ReadAllText($marker).Equals(
        "$($script:ControllerReadyMarkerValue)|$version",
        [StringComparison]::Ordinal)
}

function Assert-CommittedOrLegacyOpticonDirectory {
    param([Parameter(Mandatory)][string]$Directory)
    $ownershipMarker = Join-Path $Directory $script:ControllerOwnershipMarkerName
    Assert-OwnedOpticonDirectory -Directory $Directory -AllowLegacyCanonical
    if ((Test-Path -LiteralPath $ownershipMarker -PathType Leaf) -and
        -not (Test-ControllerReadyMarker -Directory $Directory)) {
        throw "The Opticon controller payload is owned but was never durably committed: $Directory"
    }
}

function Assert-VerifiedOpticonDirectory {
    param([Parameter(Mandatory)][string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "The Opticon controller directory is missing: $Directory"
    }
    Assert-NoDirectoryReparsePoints -Directory $Directory
    $marker = Join-Path $Directory $script:ControllerOwnershipMarkerName
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        -not [IO.File]::ReadAllText($marker).Equals($script:ControllerOwnershipMarkerValue, [StringComparison]::Ordinal)) {
        throw "The Opticon controller ownership marker is missing or invalid: $Directory"
    }
    Assert-PinnedOpticonExecutable -Path (Join-Path $Directory 'Opticon.exe')
    Assert-PinnedOpticonExecutable -Path (Join-Path $Directory 'Cli\opticon.exe')
    $executables = @(Get-ChildItem -LiteralPath $Directory -Filter '*.exe' -File -Recurse)
    if ($executables.Count -lt 2) { throw "The Opticon controller payload is incomplete: $Directory" }
    foreach ($executable in $executables) { Assert-PinnedOpticonExecutable -Path $executable.FullName }
    Assert-MatchingOpticonUiCliVersion -Directory $Directory
}

function Assert-OwnedOpticonDirectory {
    param([Parameter(Mandatory)][string]$Directory, [switch]$AllowLegacyCanonical)
    $full = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "The Opticon controller directory is missing: $full"
    }
    Assert-NoDirectoryReparsePoints -Directory $full
    $marker = Join-Path $full $script:ControllerOwnershipMarkerName
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        Assert-VerifiedOpticonDirectory -Directory $full
        return
    }

    $legacyAllowed = $AllowLegacyCanonical -and
        ((Test-SameFullPath -Left $full -Right $script:CanonicalControllerInstallDirectory) -or
         (Test-SameFullPath -Left $full -Right ($script:CanonicalControllerInstallDirectory + '.previous')))
    if (-not $legacyAllowed) {
        throw "Refusing to replace or delete an unowned Opticon controller directory: $full"
    }
    $legacyExecutable = @(
        (Join-Path $full 'Opticon.exe'),
        (Join-Path $full 'Taildesk.Admin.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $legacyExecutable) {
        throw "The legacy canonical controller directory is not recognizably Opticon-owned: $full"
    }
    $legacyExecutables = @(Get-ChildItem -LiteralPath $full -Filter '*.exe' -File -Recurse)
    if ($legacyExecutables.Count -eq 0) {
        throw "The legacy canonical controller directory has no executable payload: $full"
    }
    foreach ($executable in $legacyExecutables) {
        Assert-PinnedOpticonExecutable -Path $executable.FullName
    }
}

function Remove-OwnedOpticonDirectory {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$LeafPrefix,
        [switch]$AllowLegacyCanonical
    )
    if (-not (Test-Path -LiteralPath $Directory)) { return }
    Assert-SafeInstallSibling -Path $Directory -Parent $Parent -LeafPrefix $LeafPrefix
    Assert-OwnedOpticonDirectory -Directory $Directory -AllowLegacyCanonical:$AllowLegacyCanonical
    Remove-Item -LiteralPath $Directory -Recurse -Force
}

function Assert-InstallDestinationPreflight {
    param([Parameter(Mandatory)][string]$Destination)
    $destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if (-not (Test-SameFullPath -Left $destination -Right $script:CanonicalControllerInstallDirectory)) {
        throw "Opticon controller installation is restricted to the canonical directory '$($script:CanonicalControllerInstallDirectory)'."
    }
    $parent = [IO.Path]::GetDirectoryName($destination)
    $leaf = [IO.Path]::GetFileName($destination)
    if ([string]::IsNullOrWhiteSpace($parent) -or [string]::IsNullOrWhiteSpace($leaf)) {
        throw 'The Opticon installation directory is unsafe.'
    }
    $backup = $destination + '.previous'
    Assert-SafeInstallSibling -Path $backup -Parent $parent -LeafPrefix "$leaf.previous"
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        throw "The Opticon installation path is a file: $destination"
    }
    if (Test-Path -LiteralPath $destination -PathType Container) {
        Assert-OwnedOpticonDirectory -Directory $destination -AllowLegacyCanonical
    }
    if (Test-Path -LiteralPath $backup -PathType Leaf) {
        throw "The Opticon retained payload path is a file: $backup"
    }
    if (Test-Path -LiteralPath $backup -PathType Container) {
        Assert-OwnedOpticonDirectory -Directory $backup -AllowLegacyCanonical
    }
}

function Enter-ControllerInstallLock {
    [IO.Directory]::CreateDirectory($script:ControllerInstallRoot) | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    while ($true) {
        try {
            return New-Object IO.FileStream(
                $script:ControllerInstallLockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None,
                1,
                [IO.FileOptions]::WriteThrough)
        } catch [IO.IOException] {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw 'Another Opticon controller installer, UI, or CLI still owns the installation lock after two minutes.'
            }
            Start-Sleep -Milliseconds 250
        } catch [UnauthorizedAccessException] {
            throw "The Opticon controller installation lock cannot be opened: $($script:ControllerInstallLockPath)"
        }
    }
}

function Restore-InterruptedOpticonInstall {
    param([Parameter(Mandatory)][string]$Destination)
    $destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $parent = [IO.Path]::GetDirectoryName($destination)
    $leaf = [IO.Path]::GetFileName($destination)
    $backup = $destination + '.previous'
    Assert-SafeInstallSibling -Path $backup -Parent $parent -LeafPrefix "$leaf.previous"
    if (-not (Test-Path -LiteralPath $backup)) { return }
    if (-not (Test-Path -LiteralPath $backup -PathType Container)) {
        throw "The retained Opticon payload path is not a directory: $backup"
    }

    Assert-InstalledOpticonClosed -Directories @($destination, $backup)
    Assert-OwnedOpticonDirectory -Directory $backup -AllowLegacyCanonical
    if (-not (Test-Path -LiteralPath $destination)) {
        Assert-CommittedOrLegacyOpticonDirectory -Directory $backup
        Move-Item -LiteralPath $backup -Destination $destination
        return
    }
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "The live Opticon path is not a directory; the retained payload was preserved at '$backup'."
    }
    try {
        Assert-OwnedOpticonDirectory -Directory $destination -AllowLegacyCanonical
    } catch {
        throw "Both live and retained Opticon payloads exist, but the live directory is not safely owned. The prior payload was preserved at '$backup'. $($_.Exception.Message)"
    }
    Assert-InstalledOpticonClosed -Directories @($destination, $backup)
    if (Test-ControllerReadyMarker -Directory $destination) {
        Remove-OwnedOpticonDirectory -Directory $backup -Parent $parent -LeafPrefix "$leaf.previous" -AllowLegacyCanonical
        return
    }

    # A signed/owned live candidate without the durable ready marker may have
    # crashed before shortcuts or PATH committed. Restore the known-good prior.
    Assert-CommittedOrLegacyOpticonDirectory -Directory $backup
    $failed = Join-Path $parent "$leaf.failed-$([Guid]::NewGuid().ToString('N'))"
    Assert-SafeInstallSibling -Path $failed -Parent $parent -LeafPrefix "$leaf.failed-"
    Move-Item -LiteralPath $destination -Destination $failed
    try {
        Move-Item -LiteralPath $backup -Destination $destination
        Remove-OwnedOpticonDirectory -Directory $failed -Parent $parent -LeafPrefix "$leaf.failed-" -AllowLegacyCanonical
    } catch {
        throw "Opticon found an uncommitted live payload but could not restore '$backup'. The uncommitted payload remains at '$failed'. $($_.Exception.Message)"
    }
}

function Install-OpticonPayloadTransaction {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][scriptblock]$ConfigureActivatedPayload
    )
    $destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $parent = [IO.Path]::GetDirectoryName($destination)
    $leaf = [IO.Path]::GetFileName($destination)
    if ([string]::IsNullOrWhiteSpace($parent) -or [string]::IsNullOrWhiteSpace($leaf)) {
        throw 'The Opticon installation directory is unsafe.'
    }
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $staging = Join-Path $parent "$leaf.installing-$([Guid]::NewGuid().ToString('N'))"
    $backup = $destination + '.previous'
    $failed = Join-Path $parent "$leaf.failed-$([Guid]::NewGuid().ToString('N'))"
    Assert-SafeInstallSibling -Path $staging -Parent $parent -LeafPrefix "$leaf.installing-"
    Assert-SafeInstallSibling -Path $backup -Parent $parent -LeafPrefix "$leaf.previous"
    Assert-SafeInstallSibling -Path $failed -Parent $parent -LeafPrefix "$leaf.failed-"

    Restore-InterruptedOpticonInstall -Destination $destination
    $previousMoved = $false
    $candidateActivated = $false
    try {
        New-Item -Path $staging -ItemType Directory | Out-Null
        Copy-Item -Path (Join-Path $Source '*') -Destination $staging -Recurse -Force
        Remove-Item -LiteralPath (Join-Path $staging $script:ControllerReadyMarkerName) -Force -ErrorAction SilentlyContinue
        Write-ControllerOwnershipMarker -Directory $staging
        Assert-VerifiedOpticonDirectory -Directory $staging

        if (Test-Path -LiteralPath $destination -PathType Container) {
            Assert-OwnedOpticonDirectory -Directory $destination -AllowLegacyCanonical
        } elseif (Test-Path -LiteralPath $destination) {
            throw "The Opticon installation path is not a directory: $destination"
        }
        if (Test-Path -LiteralPath $backup) {
            throw "An unrecovered Opticon payload is still present; refusing the swap: $backup"
        }

        Assert-InstalledOpticonClosed -Directories @($destination, $backup)
        if (Test-Path -LiteralPath $destination -PathType Container) {
            Move-Item -LiteralPath $destination -Destination $backup
            $previousMoved = $true
            Assert-InstalledOpticonClosed -Directories @($destination, $backup)
        }
        Move-Item -LiteralPath $staging -Destination $destination
        $candidateActivated = $true
        Assert-VerifiedOpticonDirectory -Directory $destination
        & $ConfigureActivatedPayload
        # This flushed marker is the commit point, written only after all
        # rollback-managed configuration succeeds.
        Write-ControllerReadyMarker -Directory $destination
        # Keep one verified .previous payload until the next locked run so an
        # interrupted activation remains recoverable.
    } catch {
        $installFailure = $_
        try {
            if ($candidateActivated -and (Test-Path -LiteralPath $destination -PathType Container)) {
                Assert-OwnedOpticonDirectory -Directory $destination
                Move-Item -LiteralPath $destination -Destination $failed
            }
            if ($previousMoved -and (Test-Path -LiteralPath $backup -PathType Container)) {
                Move-Item -LiteralPath $backup -Destination $destination
            }
            if (Test-Path -LiteralPath $failed -PathType Container) {
                Remove-OwnedOpticonDirectory -Directory $failed -Parent $parent -LeafPrefix "$leaf.failed-"
            }
        } catch {
            throw "Opticon payload installation failed and rollback also failed. The prior payload remains at '$backup'. Install error: $($installFailure.Exception.Message). Rollback error: $($_.Exception.Message)"
        }
        throw $installFailure
    } finally {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            Assert-SafeInstallSibling -Path $staging -Parent $parent -LeafPrefix "$leaf.installing-"
            Assert-NoDirectoryReparsePoints -Directory $staging
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
function Assert-ValidPublisher {
    param([string]$Path, [string[]]$PublisherTerms)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "Invalid Authenticode signature on $([IO.Path]::GetFileName($Path)): $($signature.Status)"
    }
    $subject = $signature.SignerCertificate.Subject
    if (-not ($PublisherTerms | Where-Object { $subject -match [regex]::Escape($_) })) {
        throw "Unexpected publisher on $([IO.Path]::GetFileName($Path)): $subject"
    }
}

function Get-PinnedArtifact {
    param([Parameter(Mandatory)][ValidateSet('Tailscale','RustDesk')][string]$Name)
    $arm64 = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64'
    if ($Name -eq 'Tailscale') {
        if ($arm64) { return [PSCustomObject]@{ Name='Tailscale'; Version='1.102.1'; FileName='tailscale-setup-1.102.1-arm64.msi'; Size=36000256L; Sha256='f81002c5b971fe2de197703606e81107eacc83c6ea40478976fe5de154aed177'; Vendor='https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-arm64.msi'; Publishers=@('Tailscale') } }
        return [PSCustomObject]@{ Name='Tailscale'; Version='1.102.1'; FileName='tailscale-setup-1.102.1-amd64.msi'; Size=38354432L; Sha256='988a38ab854ad176778955b0c92b27b1af14bf5e0146ea43076d829496d7ac77'; Vendor='https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-amd64.msi'; Publishers=@('Tailscale') }
    }
    if ($arm64) { return [PSCustomObject]@{ Name='RustDesk'; Version='1.4.9'; FileName='rustdesk-1.4.9-aarch64.msi'; Size=22855680L; Sha256='30bc8925e62c7ade52371758c2b944036ed2386f6c554e9e59f3bcfef06c7cd9'; Vendor='https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-aarch64.msi'; Publishers=@('RustDesk','PURSLANE') } }
    return [PSCustomObject]@{ Name='RustDesk'; Version='1.4.9'; FileName='rustdesk-1.4.9-x86_64.msi'; Size=24825856L; Sha256='c87d2f4cef2a5acd6003b6507dcfbf5d5168a256db082cd90b54d35193224aaa'; Vendor='https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-x86_64.msi'; Publishers=@('RustDesk','PURSLANE') }
}

function Get-VerifiedArtifact {
    param([Parameter(Mandatory)][object]$Artifact)
    $destination = Join-Path $env:TEMP ("opticon-" + $Artifact.FileName)
    $primary = "https://taildesk-egokick-control.fly.dev/opticon/artifacts/v1/$($Artifact.FileName)"
    $errors = @()
    foreach ($uri in @($primary, $Artifact.Vendor)) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        try {
            Write-Host "Downloading pinned $($Artifact.Name) $($Artifact.Version) from $(([uri]$uri).Host)..."
            Invoke-WebRequest $uri -OutFile $destination -UseBasicParsing
            $actualSize = (Get-Item -LiteralPath $destination).Length
            if ($actualSize -ne $Artifact.Size) { throw "size $actualSize does not match $($Artifact.Size)" }
            $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $Artifact.Sha256) { throw "SHA-256 $actualHash does not match the pinned hash" }
            Assert-ValidPublisher $destination $Artifact.Publishers
            return $destination
        } catch { $errors += "$uri : $($_.Exception.Message)" }
    }
    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
    throw "Both verified download sources failed for $($Artifact.Name): $($errors -join '; ')"
}
function Install-Tailscale {
    $artifact = Get-PinnedArtifact Tailscale
    $cli = "$env:ProgramFiles\Tailscale\tailscale.exe"
    if (Test-Path $cli) {
        $installed = ((& $cli version 2>$null | Select-Object -First 1) -as [string]).Trim()
        if ($installed -eq $artifact.Version) { return $cli }
    }
    $installer = Get-VerifiedArtifact $artifact
    try {
        $process = Start-Process msiexec.exe -ArgumentList @('/i', $installer, '/qn', '/norestart') -Wait -PassThru
        if ($process.ExitCode -notin @(0, 3010)) { throw "Tailscale installer returned $($process.ExitCode)." }
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $cli)) { throw 'Tailscale installed, but tailscale.exe was not found.' }
    $installed = ((& $cli version 2>$null | Select-Object -First 1) -as [string]).Trim()
    if ($installed -ne $artifact.Version) { throw "Tailscale version $installed was installed instead of pinned version $($artifact.Version)." }
    return $cli
}

function Install-RustDesk {
    $artifact = Get-PinnedArtifact RustDesk
    $client = "$env:ProgramFiles\RustDesk\rustdesk.exe"
    if (Test-Path $client) {
        $installed = (Get-Item -LiteralPath $client).VersionInfo.ProductVersion
        if ($installed -like "$($artifact.Version)*") { return $client }
    }
    $installer = Get-VerifiedArtifact $artifact
    try {
        $process = Start-Process msiexec.exe -ArgumentList @('/i', $installer, '/qn', '/norestart') -Wait -PassThru
        if ($process.ExitCode -notin @(0, 3010)) { throw "RustDesk installer returned $($process.ExitCode)." }
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $client)) { throw 'RustDesk installed, but rustdesk.exe was not found.' }
    $installed = (Get-Item -LiteralPath $client).VersionInfo.ProductVersion
    if ($installed -notlike "$($artifact.Version)*") { throw "RustDesk version $installed was installed instead of pinned version $($artifact.Version)." }
    return $client
}

function Configure-PrivateRustDeskController {
    param([Parameter(Mandatory)][string]$Client, [Parameter(Mandatory)][string]$ProfilePath)
    Write-Host 'Restricting the remote-session engine to Opticon and the private Tailscale mesh...'
    $options = @(@('direct-server','N'),@('custom-rendezvous-server','127.0.0.1'),@('relay-server','127.0.0.1'),@('enable-lan-discovery','N'),@('hide-tray','Y'),@('hide-stop-service','Y'),@('disable-discovery-panel','Y'),@('allow-auto-update','N'),@('enable-udp-punch','N'),@('enable-ipv6-punch','N'))
    foreach ($option in $options) {
        $process = Start-Process $Client -ArgumentList @('--option',$option[0],$option[1]) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            throw "RustDesk timed out applying private option $($option[0])."
        }
        if ($process.ExitCode -ne 0) { throw "RustDesk rejected private option $($option[0])." }
    }
    Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue
    Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Set-Service -StartupType Disabled
    Get-Process -Name 'RustDesk' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $configRoots = @(
        (Join-Path $ProfilePath 'AppData\Roaming\RustDesk\config'),
        (Join-Path $env:APPDATA 'RustDesk\config'),
        (Join-Path $env:WINDIR 'ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config'),
        (Join-Path $env:WINDIR 'ServiceProfiles\NetworkService\AppData\Roaming\RustDesk\config'),
        (Join-Path $env:WINDIR 'System32\config\systemprofile\AppData\Roaming\RustDesk\config')
    ) | Select-Object -Unique
    foreach ($configRoot in $configRoots) {
        if (-not (Test-Path -LiteralPath $configRoot)) { continue }
        foreach ($configFile in Get-ChildItem -LiteralPath $configRoot -File -Filter '*.toml' -ErrorAction SilentlyContinue) {
            $content = Get-Content -LiteralPath $configFile.FullName -Raw
            $content = [regex]::Replace($content,'(?m)^\s*rendezvous-server\s*=.*(?:\r?\n)?','')
            if ($content -match '(?m)^\s*rendezvous_server\s*=') { $content = [regex]::Replace($content,'(?m)^\s*rendezvous_server\s*=.*$',"rendezvous_server = '127.0.0.1:21116'") } else { $content = "rendezvous_server = '127.0.0.1:21116'`r`n" + $content }
            [IO.File]::WriteAllText($configFile.FullName,$content,[Text.UTF8Encoding]::new($false))
        }
    }
    $commonStartup=[Environment]::GetFolderPath('CommonStartup');$commonDesktop=[Environment]::GetFolderPath('CommonDesktopDirectory');$commonPrograms=[Environment]::GetFolderPath('CommonPrograms')
    foreach($shortcut in @((Join-Path $commonStartup 'RustDesk Tray.lnk'),(Join-Path $commonDesktop 'RustDesk.lnk'))){Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue}
    $rustDeskPrograms=Join-Path $commonPrograms 'RustDesk';if(Test-Path -LiteralPath $rustDeskPrograms){Remove-Item -LiteralPath $rustDeskPrograms -Recurse -Force}
    & netsh.exe advfirewall firewall delete rule 'name=all' 'dir=in' "program=$Client" | Out-Null
    foreach($rule in @('RustDesk External IPv4 Block','RustDesk External IPv6 Block')){& netsh.exe advfirewall firewall delete rule "name=$rule" | Out-Null}
    & netsh.exe advfirewall firewall add rule 'name=RustDesk External IPv4 Block' 'dir=out' 'action=block' 'remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255' "program=$Client" 'profile=any' 'enable=yes' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not restrict RustDesk to Tailscale IPv4 destinations.'}
    & netsh.exe advfirewall firewall add rule 'name=RustDesk External IPv6 Block' 'dir=out' 'action=block' 'remoteip=::/1,8000::/1' "program=$Client" 'profile=any' 'enable=yes' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not block external RustDesk IPv6 destinations.'}
}

function New-Shortcut {
    param([string]$Target, [string]$Path)
    $directory = Split-Path $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target
    $shortcut.Description = 'Opticon command center'
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Save()
}

function Expand-InteractivePath {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$ProfilePath,
        [Parameter(Mandatory)][string]$FallbackRelativePath,
        [Parameter(Mandatory)][hashtable]$Variables
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return Join-Path $ProfilePath $FallbackRelativePath
    }

    $expanded = [string]$Value
    for ($pass = 0; $pass -lt 4; $pass++) {
        $before = $expanded
        foreach ($entry in $Variables.GetEnumerator()) {
            $pattern = [regex]::Escape("%$($entry.Key)%")
            $replacement = [string]$entry.Value
            $expanded = [regex]::Replace(
                $expanded,
                $pattern,
                [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement },
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
        $expanded = [Environment]::ExpandEnvironmentVariables($expanded)
        if ($expanded -eq $before) { break }
    }

    if ($expanded.Contains('%')) {
        return Join-Path $ProfilePath $FallbackRelativePath
    }
    return $expanded
}

function Resolve-InteractiveUserProfile {
    # With over-the-shoulder UAC, WindowsIdentity and the process environment
    # describe the administrator whose credentials were entered, not the user
    # who launched Setup. Resolve the Explorer owner in this session instead.
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $accountName = $null
    $sid = $null
    try {
        $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe' AND SessionId=$sessionId" |
            Select-Object -First 1
        if ($null -ne $explorer) {
            $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner
            $ownerSid = Invoke-CimMethod -InputObject $explorer -MethodName GetOwnerSid
            if ($owner.ReturnValue -eq 0 -and -not [string]::IsNullOrWhiteSpace($owner.User)) {
                $accountName = if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
                    $owner.User
                } else {
                    "$($owner.Domain)\$($owner.User)"
                }
            }
            if ($ownerSid.ReturnValue -eq 0) { $sid = $ownerSid.Sid }
        }
    } catch {
        # The Win32_ComputerSystem fallback below covers systems where CIM is
        # unavailable, while still preferring the same signed-in user.
    }

    if ([string]::IsNullOrWhiteSpace($accountName)) {
        try { $accountName = (Get-CimInstance Win32_ComputerSystem).UserName } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($accountName)) {
        throw 'No signed-in interactive Windows user was found. Run this installer from the desktop session that will use Opticon.'
    }
    if ([string]::IsNullOrWhiteSpace($sid)) {
        $account = New-Object -TypeName System.Security.Principal.NTAccount -ArgumentList $accountName
        $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
    }

    $profileKeyPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
    $profileValue = (Get-ItemProperty -LiteralPath $profileKeyPath -ErrorAction Stop).ProfileImagePath
    if ([string]::IsNullOrWhiteSpace($profileValue)) {
        throw "The Windows profile for $accountName could not be found."
    }
    $profilePath = [Environment]::ExpandEnvironmentVariables([string]$profileValue)

    $profileRoot = [IO.Path]::GetPathRoot($profilePath).TrimEnd('\')
    $variables = @{
        USERPROFILE = $profilePath
        HOMEDRIVE = $profileRoot
        HOMEPATH = $profilePath.Substring($profileRoot.Length)
    }
    $accountParts = $accountName -split '\\', 2
    if ($accountParts.Length -eq 2) {
        $variables['USERDOMAIN'] = $accountParts[0]
        $variables['USERNAME'] = $accountParts[1]
    } else {
        $variables['USERNAME'] = $accountName
    }

    $environmentKey = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\Environment")
    $shellKey = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders")
    try {
        if ($null -ne $environmentKey) {
            foreach ($name in $environmentKey.GetValueNames()) {
                $value = $environmentKey.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($null -ne $value) { $variables[$name] = [string]$value }
            }
        }

        $appDataValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('AppData', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }
        $localAppDataValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('Local AppData', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }
        $variables['APPDATA'] = Expand-InteractivePath $appDataValue $profilePath 'AppData\Roaming' $variables
        $variables['LOCALAPPDATA'] = Expand-InteractivePath $localAppDataValue $profilePath 'AppData\Local' $variables

        $desktopValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('Desktop', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }
        $startupValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('Startup', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }

        return [PSCustomObject]@{
            AccountName = $accountName
            ProfilePath = $profilePath
            Sid = $sid
            Desktop = Expand-InteractivePath $desktopValue $profilePath 'Desktop' $variables
            Startup = Expand-InteractivePath $startupValue $profilePath 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup' $variables
            Programs = Join-Path $variables['APPDATA'] 'Microsoft\Windows\Start Menu\Programs'
        }
    } finally {
        if ($null -ne $environmentKey) { $environmentKey.Dispose() }
        if ($null -ne $shellKey) { $shellKey.Dispose() }
    }
}

function Publish-InteractiveEnvironmentChange {
    if (-not ('Opticon.NativeEnvironment' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Opticon {
    public static class NativeEnvironment {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam,
            string lParam, uint flags, uint timeout, out UIntPtr result);
        public static void Broadcast() {
            UIntPtr result;
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero,
                "Environment", 0x0002, 5000, out result);
        }
    }
}
"@
    }
    [Opticon.NativeEnvironment]::Broadcast()
}

function ConvertTo-NormalizedPathEntry {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    try {
        return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Value.Trim().Trim('"'))).TrimEnd('\')
    } catch { return '' }
}

function Test-TrustedRecordedOpticonCliPath {
    param([string]$CliPath, [string]$InstallDirectory)
    try {
        $cli = ConvertTo-NormalizedPathEntry $CliPath
        $install = ConvertTo-NormalizedPathEntry $InstallDirectory
        if ([string]::IsNullOrWhiteSpace($cli) -or [string]::IsNullOrWhiteSpace($install)) { return $false }
        if (-not (Test-SameFullPath -Left $install -Right $script:CanonicalControllerInstallDirectory)) { return $false }
        if (-not (Test-SameFullPath -Left $cli -Right (Join-Path $install 'Cli'))) { return $false }
        Assert-OwnedOpticonDirectory -Directory $install -AllowLegacyCanonical
        Assert-MatchingOpticonUiCliVersion -Directory $install
        return $true
    } catch { return $false }
}

function Add-InteractiveUserPathEntry {
    param([Parameter(Mandatory)][string]$Sid, [Parameter(Mandatory)][string]$Directory)
    $target = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $installDirectory = [IO.Path]::GetDirectoryName($target)
    if ([string]::IsNullOrWhiteSpace($installDirectory) -or
        -not (Test-SameFullPath -Left $installDirectory -Right $script:CanonicalControllerInstallDirectory) -or
        -not [IO.Path]::GetFileName($target).Equals('Cli', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Opticon CLI installation directory is invalid.'
    }
    Assert-VerifiedOpticonDirectory -Directory $installDirectory
    $key = [Microsoft.Win32.Registry]::Users.CreateSubKey("$Sid\Environment", $true)
    $stateKey = [Microsoft.Win32.Registry]::Users.CreateSubKey("$Sid\Software\Taildesk\Opticon", $true)
    if ($null -eq $key -or $null -eq $stateKey) {
        if ($null -ne $key) { $key.Dispose() }
        if ($null -ne $stateKey) { $stateKey.Dispose() }
        throw 'The signed-in user Opticon environment registry keys could not be opened.'
    }
    try {
        $current = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $current = if ($null -eq $current) { '' } else { [string]$current }
        $previousRaw = [string]$stateKey.GetValue('CliPath', '')
        $previousInstallRaw = [string]$stateKey.GetValue($script:ControllerInstallDirectoryValueName, '')
        $previous = ConvertTo-NormalizedPathEntry $previousRaw
        $previousTrusted = (-not [string]::IsNullOrWhiteSpace($previous)) -and
            ((Test-SameFullPath -Left $previous -Right $target) -or
             (Test-TrustedRecordedOpticonCliPath -CliPath $previousRaw -InstallDirectory $previousInstallRaw))
        $retained = @()
        foreach ($entry in @($current -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
            $normalized = ConvertTo-NormalizedPathEntry $entry
            if (-not [string]::IsNullOrWhiteSpace($normalized) -and
                (Test-SameFullPath -Left $normalized -Right $target)) { continue }
            if ($previousTrusted -and -not [string]::IsNullOrWhiteSpace($normalized) -and
                (Test-SameFullPath -Left $normalized -Right $previous)) { continue }
            $retained += $entry.Trim()
        }
        $updated = (@($target) + $retained) -join ';'
        if ($updated.Length -gt 32767) { throw 'The signed-in user PATH is too long to add the Opticon CLI safely.' }
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        try { $kind = $key.GetValueKind('Path') } catch { }
        if ($kind -notin @([Microsoft.Win32.RegistryValueKind]::String, [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
            throw 'The signed-in user PATH registry value has an unexpected type.'
        }
        if (-not $current.Equals($updated, [StringComparison]::Ordinal)) { $key.SetValue('Path', $updated, $kind) }
        $stateKey.SetValue('CliPath', $target, [Microsoft.Win32.RegistryValueKind]::String)
        $stateKey.SetValue($script:ControllerInstallDirectoryValueName, $installDirectory, [Microsoft.Win32.RegistryValueKind]::String)
    } finally {
        $key.Dispose()
        $stateKey.Dispose()
    }
    Publish-InteractiveEnvironmentChange
}

function Get-FileSnapshot {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path -PathType Container) {
        throw "An expected Opticon configuration file path is a directory: $Path"
    }
    $exists = Test-Path -LiteralPath $Path -PathType Leaf
    return [PSCustomObject]@{
        Path = $Path
        Existed = $exists
        Content = if ($exists) { [IO.File]::ReadAllBytes($Path) } else { $null }
    }
}

function Get-RegistryValueSnapshot {
    param($Key, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Key -or -not (@($Key.GetValueNames()) -contains $Name)) {
        return [PSCustomObject]@{ Existed = $false; Value = $null; Kind = $null }
    }
    return [PSCustomObject]@{
        Existed = $true
        Value = $Key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind = $Key.GetValueKind($Name)
    }
}

function Restore-RegistryValueSnapshot {
    param($Key, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Snapshot)
    if ($Snapshot.Existed) {
        $Key.SetValue($Name, $Snapshot.Value, $Snapshot.Kind)
    } else {
        $Key.DeleteValue($Name, $false)
    }
}

function Get-ControllerConfigurationSnapshot {
    param([Parameter(Mandatory)]$InteractiveProfile)
    $paths = @(
        (Join-Path $InteractiveProfile.Desktop 'Opticon.lnk'),
        (Join-Path $InteractiveProfile.Startup 'Opticon.lnk'),
        (Join-Path $InteractiveProfile.Programs 'Opticon.lnk'),
        (Join-Path $InteractiveProfile.Desktop 'Taildesk.lnk'),
        (Join-Path $InteractiveProfile.Startup 'Taildesk.lnk'),
        (Join-Path $InteractiveProfile.Programs 'Taildesk.lnk')
    )
    $environmentKey = [Microsoft.Win32.Registry]::Users.OpenSubKey("$($InteractiveProfile.Sid)\Environment", $false)
    $stateKey = [Microsoft.Win32.Registry]::Users.OpenSubKey("$($InteractiveProfile.Sid)\Software\Taildesk\Opticon", $false)
    try {
        return [PSCustomObject]@{
            Files = @($paths | ForEach-Object { Get-FileSnapshot -Path $_ })
            Path = Get-RegistryValueSnapshot -Key $environmentKey -Name 'Path'
            CliPath = Get-RegistryValueSnapshot -Key $stateKey -Name 'CliPath'
            InstallDirectory = Get-RegistryValueSnapshot -Key $stateKey -Name $script:ControllerInstallDirectoryValueName
        }
    } finally {
        if ($null -ne $environmentKey) { $environmentKey.Dispose() }
        if ($null -ne $stateKey) { $stateKey.Dispose() }
    }
}

function Restore-ControllerConfigurationSnapshot {
    param([Parameter(Mandatory)][string]$Sid, [Parameter(Mandatory)]$Snapshot)
    foreach ($file in $Snapshot.Files) {
        if ($file.Existed) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.Path)) | Out-Null
            [IO.File]::WriteAllBytes($file.Path, $file.Content)
        } elseif (Test-Path -LiteralPath $file.Path -PathType Leaf) {
            Remove-Item -LiteralPath $file.Path -Force
        }
    }
    $environmentKey = [Microsoft.Win32.Registry]::Users.CreateSubKey("$Sid\Environment", $true)
    $stateKey = [Microsoft.Win32.Registry]::Users.CreateSubKey("$Sid\Software\Taildesk\Opticon", $true)
    if ($null -eq $environmentKey -or $null -eq $stateKey) {
        if ($null -ne $environmentKey) { $environmentKey.Dispose() }
        if ($null -ne $stateKey) { $stateKey.Dispose() }
        throw 'The signed-in user Opticon configuration could not be reopened for rollback.'
    }
    try {
        Restore-RegistryValueSnapshot -Key $environmentKey -Name 'Path' -Snapshot $Snapshot.Path
        Restore-RegistryValueSnapshot -Key $stateKey -Name 'CliPath' -Snapshot $Snapshot.CliPath
        Restore-RegistryValueSnapshot -Key $stateKey -Name $script:ControllerInstallDirectoryValueName -Snapshot $Snapshot.InstallDirectory
    } finally {
        $environmentKey.Dispose()
        $stateKey.Dispose()
    }
    Publish-InteractiveEnvironmentChange
}
function Test-ExactOpenSshClient {
    $openSshDirectory = Join-Path $env:WINDIR 'System32\OpenSSH'
    $ssh = Join-Path $openSshDirectory 'ssh.exe'
    $sshKeygen = Join-Path $openSshDirectory 'ssh-keygen.exe'
    foreach ($executable in @($ssh, $sshKeygen)) {
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { return $false }
        if (((Get-Item -LiteralPath $executable -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The exact System32 OpenSSH prerequisite is a reparse point: $executable"
        }
    }
    return $true
}

function Ensure-OpenSshClientCapability {
    if (Test-ExactOpenSshClient) {
        Write-Host 'Windows OpenSSH Client is already available at the exact System32 paths.'
        return
    }

    $capabilityName = 'OpenSSH.Client~~~~0.0.1.0'
    $capability = Get-WindowsCapability -Online -Name $capabilityName -ErrorAction Stop
    if ($capability.State -eq 'Installed') {
        throw 'Windows reports OpenSSH Client installed, but its exact System32 ssh.exe and ssh-keygen.exe are unavailable. Repair Windows or finish the pending reboot before installing Opticon.'
    }
    if ($capability.State -ne 'NotPresent') {
        throw "Windows OpenSSH Client is in state '$($capability.State)'. Finish the pending servicing/reboot before installing Opticon."
    }

    Write-Host 'Installing the Windows OpenSSH Client prerequisite before changing Opticon...'
    $installed = Add-WindowsCapability -Online -Name $capabilityName -ErrorAction Stop
    if ($installed.RestartNeeded) {
        throw 'Windows installed OpenSSH Client but requires a reboot. Reboot, then rerun this installer; Opticon has not been changed.'
    }

    $verified = Get-WindowsCapability -Online -Name $capabilityName -ErrorAction Stop
    if ($verified.State -ne 'Installed' -or -not (Test-ExactOpenSshClient)) {
        throw 'Windows did not make the exact System32 OpenSSH Client binaries ready. Finish servicing or reboot, then rerun this installer; Opticon has not been changed.'
    }
}
$sourceExecutable = Join-Path $source 'Opticon.exe'
$sourceCli = Join-Path $source 'Cli\opticon.exe'
Assert-PinnedOpticonExecutable -Path $sourceExecutable
Assert-PinnedOpticonExecutable -Path $sourceCli
foreach ($executable in @(Get-ChildItem -LiteralPath $source -Filter '*.exe' -File -Recurse)) {
    Assert-PinnedOpticonExecutable -Path $executable.FullName
}
Assert-MatchingOpticonUiCliVersion -Directory $source

$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$installBackup = $InstallDirectory + '.previous'
# Reject an arbitrary existing/custom target before OpenSSH or dependency setup
# performs the first mutation.
Assert-InstallDestinationPreflight -Destination $InstallDirectory
Assert-InstalledOpticonClosed -Directories @($InstallDirectory, $installBackup)
$installLock = Enter-ControllerInstallLock
try {
    # Recheck under the exclusive lease before any mutation. The same lease is
    # held through dependencies, network setup, snapshot, swap, and commit.
    Assert-InstalledOpticonClosed -Directories @($InstallDirectory, $installBackup)

$interactiveProfile = Resolve-InteractiveUserProfile
if ($ControllerOnlyRepair) {
    # Source-triggered UI repairs must not re-enroll Tailscale, reinstall
    # RustDesk, or modify recovery networking. Existing control stays up
    # while the signed controller payload is safely swapped.
    Write-Host "Repairing the Opticon command center for $($interactiveProfile.AccountName)..." -ForegroundColor Cyan
} else {
# This prerequisite is deliberately the first mutation after signed payload and
# destination verification. Failure leaves Tailscale, RustDesk, enrollment, and
# the Opticon payload/configuration untouched.
Ensure-OpenSshClientCapability

Write-Host 'Installing Opticon command center...' -ForegroundColor Cyan
Write-Host "Installing for signed-in user $($interactiveProfile.AccountName)."
$tailscale = Install-Tailscale
$rustDesk = Install-RustDesk
Configure-PrivateRustDeskController $rustDesk $interactiveProfile.ProfilePath

$statusText = (& $tailscale status --json 2>$null) -join "`n"
$running = $false
if ($statusText) {
    try {
        $statusObject = $statusText | ConvertFrom-Json
        $running = $statusObject.BackendState -eq 'Running' -and @($statusObject.Self.TailscaleIPs | Where-Object { $_ -match '^100\.' }).Count -gt 0
    } catch { $running = $false }
}
if (-not $running) {
    Write-Host 'A browser window will open so you can sign this laptop into Tailscale.' -ForegroundColor Yellow
    & $tailscale login
}

# Validate every fallible input we can before swapping the controller payload.
$ipValue = & $tailscale ip -4 | Select-Object -First 1
if (-not $ipValue) { throw 'Tailscale did not assign an IPv4 address after login.' }
$ip = $ipValue.Trim()
if ($ip -notmatch '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.') {
    throw "Tailscale returned an address outside 100.64.0.0/10: $ip"
}
$routeTaskInstaller = Join-Path $PSScriptRoot 'Tools\Install-TaildeskFlyRouteTask.ps1'
if (-not (Test-Path -LiteralPath $routeTaskInstaller -PathType Leaf)) {
    throw 'The Opticon roaming-route task installer is missing from the extracted package.'
}
# Network setup is a verified prerequisite before the directory swap. A
# failure here cannot activate a new controller payload.
$deleteRuleArguments = @('advfirewall', 'firewall', 'delete', 'rule', 'name=Opticon Coordinator (Tailscale only)')
& netsh.exe @deleteRuleArguments | Out-Null
$deleteLegacyRuleArguments = @('advfirewall', 'firewall', 'delete', 'rule', 'name=Taildesk Coordinator (Tailscale only)')
& netsh.exe @deleteLegacyRuleArguments | Out-Null
$addRuleArguments = @(
    'advfirewall', 'firewall', 'add', 'rule',
    'name=Opticon Coordinator (Tailscale only)', 'dir=in', 'action=allow',
    'protocol=TCP', 'localport=45830', "localip=$ip", 'remoteip=100.64.0.0/10',
    'profile=any', 'enable=yes'
)
& netsh.exe @addRuleArguments | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Windows could not create the Tailscale-only coordinator firewall rule.' }
& $routeTaskInstaller -ControllerIPv4 '213.188.217.227' | Out-Null
}

$admin = Join-Path $InstallDirectory 'Opticon.exe'
$cliDirectory = Join-Path $InstallDirectory 'Cli'
$desktopLink = Join-Path $interactiveProfile.Desktop 'Opticon.lnk'
$startupLink = Join-Path $interactiveProfile.Startup 'Opticon.lnk'
$startMenuLink = Join-Path $interactiveProfile.Programs 'Opticon.lnk'
$legacyLinks = @(
    (Join-Path $interactiveProfile.Desktop 'Taildesk.lnk'),
    (Join-Path $interactiveProfile.Startup 'Taildesk.lnk'),
    (Join-Path $interactiveProfile.Programs 'Taildesk.lnk')
)
$configurationSnapshot = Get-ControllerConfigurationSnapshot -InteractiveProfile $interactiveProfile
try {
    Install-OpticonPayloadTransaction -Source $source -Destination $InstallDirectory -ConfigureActivatedPayload {

        foreach ($legacyLink in $legacyLinks) { Remove-Item -LiteralPath $legacyLink -Force -ErrorAction SilentlyContinue }
        New-Shortcut $admin $desktopLink
        New-Shortcut $admin $startupLink
        New-Shortcut $admin $startMenuLink
        Add-InteractiveUserPathEntry -Sid $interactiveProfile.Sid -Directory $cliDirectory
    }
} catch {
    $installFailure = $_
    try {
        Restore-ControllerConfigurationSnapshot -Sid $interactiveProfile.Sid -Snapshot $configurationSnapshot
    } catch {
        throw "Opticon installation failed and its user configuration rollback also failed. Install error: $($installFailure.Exception.Message). Configuration rollback error: $($_.Exception.Message)"
    }
    throw $installFailure
}

Write-Host "Installed for $($interactiveProfile.AccountName)." -ForegroundColor Green
Write-Host 'Close this elevated installer, then open Opticon from that user''s desktop shortcut.' -ForegroundColor Green
Write-Host 'The command center starts at sign-in and remains available while that user stays signed in; locking the screen is fine.' -ForegroundColor Yellow
} finally {
    $installLock.Dispose()
}
