#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$ExpectedCodeSigningThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$ExpectedSourceReleaseKeyId,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)]
    [int]$BootstrapProcessId,
    [Parameter(Mandatory)][ValidateSet('0','1')]
    [string]$DevelopmentOnly,
    [string]$InstallDirectory = "$env:ProgramFiles\Taildesk\Admin",
    [switch]$ControllerOnlyRepair
)

$ErrorActionPreference = 'Stop'
$script:InvitationSigningThumbprint = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'
$script:RouteTaskName = 'Taildesk Fly Route'
$script:UiTaskName = 'Opticon Command Center'
$script:ControllerIPv4 = '213.188.217.227'
$script:IsDevelopmentBuild = $DevelopmentOnly -eq '1'
$ExpectedCodeSigningThumbprint = $ExpectedCodeSigningThumbprint.ToUpperInvariant()
$ExpectedSourceReleaseKeyId = $ExpectedSourceReleaseKeyId.ToUpperInvariant()
if ($script:IsDevelopmentBuild) {
    if ($ExpectedCodeSigningThumbprint -ceq $script:InvitationSigningThumbprint -or
        $ExpectedSourceReleaseKeyId -ceq $script:InvitationSigningThumbprint -or
        $ExpectedCodeSigningThumbprint -ceq $ExpectedSourceReleaseKeyId) {
        throw 'Developer artifacts must use explicit, separate nonpublishable product and source-release identities.'
    }
} elseif ($ExpectedCodeSigningThumbprint -ceq $script:InvitationSigningThumbprint -or
          $ExpectedSourceReleaseKeyId -ceq $script:InvitationSigningThumbprint -or
          $ExpectedCodeSigningThumbprint -ceq $ExpectedSourceReleaseKeyId) {
    throw 'Production code-signing, source-release, and invitation trust roots must be distinct.'
}

function Assert-ProtectedInstallerHandoff {
    $programData = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
    if (-not [IO.Path]::GetDirectoryName($root).Equals(
            $programData,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($root) -notmatch '^OpticonSecureInstall-[a-f0-9]{32}$') {
        throw 'The embedded installer is not running from its exact protected ProgramData handoff.'
    }
    foreach ($path in @($programData,$root)) {
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The protected installer handoff contains a reparse point: $path"
        }
    }
    $acl = (Get-Item -LiteralPath $root -Force).GetAccessControl(
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Access)
    $system = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,$null)
    $administrators = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,$null)
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    if (-not ($owner.Equals($system) -or $owner.Equals($administrators)) -or
        -not $acl.AreAccessRulesProtected -or $rules.Count -ne 2) {
        throw 'The protected installer handoff owner or ACL is invalid.'
    }
    foreach ($sid in @($system,$administrators)) {
        $matches = @($rules | Where-Object {
            -not $_.IsInherited -and $_.IdentityReference.Equals($sid) -and
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl -and
            $_.InheritanceFlags -eq $inheritance -and
            $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None
        })
        if ($matches.Count -ne 1) { throw 'The protected installer handoff ACL is not exact.' }
    }

    $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$BootstrapProcessId"
    if ($null -eq $self -or [int]$self.ParentProcessId -ne $BootstrapProcessId -or
        $null -eq $parent -or [string]::IsNullOrWhiteSpace([string]$parent.ExecutablePath) -or
        -not [IO.Path]::GetFileName([string]$parent.ExecutablePath).Equals(
            'Install-Opticon.exe',[StringComparison]::Ordinal)) {
        throw 'The signed Install-Opticon.exe wrapper is not the direct parent process.'
    }
    Assert-PinnedOpticonExecutable -Path ([string]$parent.ExecutablePath)
}

function New-ProtectedInstallerDirectory {
    param([Parameter(Mandatory)][string]$Prefix)
    if ($Prefix -notmatch '^[A-Za-z0-9_-]+$') { throw 'The protected installer prefix is invalid.' }
    $parentAcl = (Get-Item -LiteralPath $PSScriptRoot -Force).GetAccessControl(
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Access)
    for ($attempt=0;$attempt -lt 16;$attempt++) {
        $candidate = Join-Path $PSScriptRoot ($Prefix + [Guid]::NewGuid().ToString('N'))
        try {
            ([IO.DirectoryInfo]$candidate).Create($parentAcl)
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A protected installer child is a reparse point.'
            }
            return $candidate
        } catch [IO.IOException] {
            if (-not (Test-Path -LiteralPath $candidate)) { throw }
        }
    }
    throw 'Could not create a unique protected installer directory.'
}
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
        -not $signature.SignerCertificate.Thumbprint.Equals(
            $ExpectedCodeSigningThumbprint,[StringComparison]::OrdinalIgnoreCase)) {
        throw "The Opticon executable is unsigned, altered, or signed by an unexpected key: $Path"
    }
    $codeSigning = $false
    foreach ($extension in $signature.SignerCertificate.Extensions) {
        if ($extension -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $extension.EnhancedKeyUsages) {
                if ($oid.Value -ceq '1.3.6.1.5.5.7.3.3') { $codeSigning = $true }
            }
        }
    }
    if (-not $codeSigning) { throw "The Opticon signer lacks the Code Signing EKU: $Path" }
    if ($script:IsDevelopmentBuild) {
        if ($signature.Status -notin @(
                [Management.Automation.SignatureStatus]::Valid,
                [Management.Automation.SignatureStatus]::UnknownError)) {
            throw "The development Authenticode signature is invalid: $Path ($($signature.Status))"
        }
    } elseif ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
              $null -eq $signature.TimeStamperCertificate) {
        throw "The production executable lacks a trusted timestamped signature: $Path"
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

function Move-OpticonDirectoryWithRetry {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Description
    )
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Move-Item -LiteralPath $Source -Destination $Destination
            return
        } catch [IO.IOException] {
            if ($attempt -eq 20) {
                throw "$Description remained locked for 10 seconds. Close Opticon and any RustDesk, SSH, or command-prompt session opened from Opticon, then retry. $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 500
        }
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
            Move-OpticonDirectoryWithRetry -Source $destination -Destination $backup -Description 'The installed Opticon command center'
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
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')]
        [string]$ExpectedSignerThumbprint
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "Invalid Authenticode signature on $([IO.Path]::GetFileName($Path)): $($signature.Status)"
    }
    $actualText = (($signature.SignerCertificate.Thumbprint.ToUpperInvariant().ToCharArray() |
        Where-Object { [Uri]::IsHexDigit($_) }) -join '')
    $actual = [Convert]::FromHexString($actualText)
    $expected = [Convert]::FromHexString($ExpectedSignerThumbprint.ToUpperInvariant())
    if ($actual.Length -ne $expected.Length -or
        -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actual,$expected)) {
        throw "Unexpected publisher certificate on $([IO.Path]::GetFileName($Path))."
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigning = @($signature.SignerCertificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq $codeSigningOid }).Count -gt 0
    if (-not $hasCodeSigning) {
        throw "The pinned publisher lacks the Code Signing EKU on $([IO.Path]::GetFileName($Path))."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "The pinned publisher signature has no trusted timestamp on $([IO.Path]::GetFileName($Path))."
    }
    $timestampingOid = '1.3.6.1.5.5.7.3.8'
    $hasTimestamping = @($signature.TimeStamperCertificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq $timestampingOid }).Count -gt 0
    if (-not $hasTimestamping) {
        throw "The pinned publisher timestamp lacks the Time Stamping EKU on $([IO.Path]::GetFileName($Path))."
    }
}

function Get-PinnedArtifact {
    param([Parameter(Mandatory)][ValidateSet('Tailscale','RustDesk')][string]$Name)
    $arm64 = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64'
    if ($Name -eq 'Tailscale') {
        if ($arm64) { return [PSCustomObject]@{ Name='Tailscale'; Version='1.102.1'; FileName='tailscale-setup-1.102.1-arm64.msi'; Size=36000256L; Sha256='f81002c5b971fe2de197703606e81107eacc83c6ea40478976fe5de154aed177'; Vendor='https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-arm64.msi'; SignerThumbprint='108F172FDE945B21A5C0696731D6220D67D1C39E' } }
        return [PSCustomObject]@{ Name='Tailscale'; Version='1.102.1'; FileName='tailscale-setup-1.102.1-amd64.msi'; Size=38354432L; Sha256='988a38ab854ad176778955b0c92b27b1af14bf5e0146ea43076d829496d7ac77'; Vendor='https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-amd64.msi'; SignerThumbprint='108F172FDE945B21A5C0696731D6220D67D1C39E' }
    }
    if ($arm64) { return [PSCustomObject]@{ Name='RustDesk'; Version='1.4.9'; FileName='rustdesk-1.4.9-aarch64.msi'; Size=22855680L; Sha256='30bc8925e62c7ade52371758c2b944036ed2386f6c554e9e59f3bcfef06c7cd9'; Vendor='https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-aarch64.msi'; SignerThumbprint='4230334F8A7DD84E50D0273EF379E8B4A82F5DA5' } }
    return [PSCustomObject]@{ Name='RustDesk'; Version='1.4.9'; FileName='rustdesk-1.4.9-x86_64.msi'; Size=24825856L; Sha256='c87d2f4cef2a5acd6003b6507dcfbf5d5168a256db082cd90b54d35193224aaa'; Vendor='https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-x86_64.msi'; SignerThumbprint='4230334F8A7DD84E50D0273EF379E8B4A82F5DA5' }
}

function Get-VerifiedArtifact {
    param([Parameter(Mandatory)][object]$Artifact)
    $primary = "https://taildesk-egokick-control.fly.dev/opticon/artifacts/v1/$($Artifact.FileName)"
    $errors = @()
    foreach ($uri in @($primary, $Artifact.Vendor)) {
        if (([uri]$uri).Scheme -cne 'https') { throw 'Dependency download URLs must use HTTPS.' }
        $destination = Join-Path $script:DependencyStaging (
            [Guid]::NewGuid().ToString('N') + '-' + $Artifact.FileName)
        $handler = $null
        $client = $null
        $response = $null
        $input = $null
        $output = $null
        try {
            Write-Host "Downloading pinned $($Artifact.Name) $($Artifact.Version) from $(([uri]$uri).Host)..."
            $handler = New-Object Net.Http.HttpClientHandler
            $handler.AllowAutoRedirect = $false
            $handler.UseProxy = $false
            $handler.AutomaticDecompression = [Net.DecompressionMethods]::None
            $client = New-Object Net.Http.HttpClient($handler,$true)
            $client.Timeout = [TimeSpan]::FromMinutes(5)
            $request = New-Object Net.Http.HttpRequestMessage(
                [Net.Http.HttpMethod]::Get,[uri]$uri)
            [void]$request.Headers.TryAddWithoutValidation('Accept-Encoding','identity')
            try {
                $response = $client.SendAsync(
                    $request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            } finally { $request.Dispose() }
            if (-not $response.IsSuccessStatusCode) {
                throw "HTTP status $([int]$response.StatusCode)"
            }
            if ($response.Content.Headers.ContentEncoding.Count -ne 0) {
                throw 'encoded dependency responses are forbidden'
            }
            if ($response.Content.Headers.ContentLength.HasValue -and
                $response.Content.Headers.ContentLength.Value -ne $Artifact.Size) {
                throw "declared size $($response.Content.Headers.ContentLength.Value) does not match $($Artifact.Size)"
            }
            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = New-Object IO.FileStream(
                $destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,
                [IO.FileShare]::None,1048576,
                [IO.FileOptions]::WriteThrough -bor [IO.FileOptions]::SequentialScan)
            $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
                [Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = New-Object byte[] 1048576
                [long]$remaining = $Artifact.Size
                while ($remaining -gt 0) {
                    $read = $input.Read($buffer,0,[Math]::Min($buffer.Length,[int64]$remaining))
                    if ($read -eq 0) { throw 'dependency download ended before its pinned size' }
                    $hasher.AppendData($buffer,0,$read)
                    $output.Write($buffer,0,$read)
                    $remaining -= $read
                }
                if ($input.ReadByte() -ne -1) { throw 'dependency download exceeds its pinned size' }
                $output.Flush($true)
                $actualHash = (($hasher.GetHashAndReset() | ForEach-Object { $_.ToString('x2') }) -join '')
            } finally { $hasher.Dispose() }
            if ($actualHash -ne $Artifact.Sha256) { throw "SHA-256 $actualHash does not match the pinned hash" }
            $output.Dispose()
            $output = $null
            $input.Dispose()
            $input = $null
            Assert-ValidPublisher $destination $Artifact.SignerThumbprint
            return $destination
        } catch {
            $errors += "$uri : $($_.Exception.GetBaseException().Message)"
            Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        } finally {
            if ($null -ne $output) { $output.Dispose() }
            if ($null -ne $input) { $input.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
            if ($null -ne $client) { $client.Dispose() }
        }
    }
    throw "Both verified download sources failed for $($Artifact.Name): $($errors -join '; ')"
}

function Install-VerifiedMsi {
    param(
        [Parameter(Mandatory)][object]$Artifact,
        [Parameter(Mandatory)][string]$Path
    )
    $lease = New-Object IO.FileStream(
        $Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,
        1048576,[IO.FileOptions]::SequentialScan)
    try {
        if ($lease.Length -ne $Artifact.Size) { throw 'The held MSI size changed.' }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = (($sha.ComputeHash($lease) | ForEach-Object { $_.ToString('x2') }) -join '') }
        finally { $sha.Dispose() }
        if ($actualHash -ne $Artifact.Sha256) { throw 'The held MSI hash changed.' }
        Assert-ValidPublisher $Path $Artifact.SignerThumbprint
        $msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
        if (-not (Test-Path -LiteralPath $msiexec -PathType Leaf) -or
            ((Get-Item -LiteralPath $msiexec -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The exact System32 msiexec.exe is unavailable or is a reparse point.'
        }
        $process = Start-Process -FilePath $msiexec -ArgumentList @(
            '/i',$Path,'/qn','/norestart') -Wait -PassThru
        if ($process.ExitCode -notin @(0,3010)) {
            throw "$($Artifact.Name) installer returned $($process.ExitCode)."
        }
    } finally { $lease.Dispose() }
}

function Assert-FixedVendorExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Artifact
    )
    $programFiles = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith(
            $programFiles + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$($Artifact.Name) is not installed at its fixed Program Files path."
    }
    foreach ($candidate in @($programFiles,(Split-Path $full -Parent),$full)) {
        if (((Get-Item -LiteralPath $candidate -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A fixed $($Artifact.Name) path is a reparse point: $candidate"
        }
    }
    Assert-ValidPublisher -Path $full -ExpectedSignerThumbprint $Artifact.SignerThumbprint
}

function Invoke-FixedVendorExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Arguments,
        [TimeSpan]$Timeout = [TimeSpan]::FromSeconds(30)
    )
    $windows = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system32 = Join-Path $windows 'System32'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Path
    $start.WorkingDirectory = Split-Path $Path -Parent
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    $start.Environment['SystemRoot'] = $windows
    $start.Environment['WINDIR'] = $windows
    $start.Environment['ProgramFiles'] =
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $start.Environment['ProgramData'] =
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $start.Environment['PATH'] = $system32
    $start.Environment['PATHEXT'] = '.COM;.EXE'
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw "Windows could not start $([IO.Path]::GetFileName($Path))." }
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit([int]$Timeout.TotalMilliseconds)) {
            try { $process.Kill($true) } catch { }
            throw "$([IO.Path]::GetFileName($Path)) did not exit within $([int]$Timeout.TotalSeconds) seconds."
        }
        $standardOutput = $outputTask.GetAwaiter().GetResult()
        $standardError = $errorTask.GetAwaiter().GetResult()
        return [PSCustomObject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    } finally {
        $process.Dispose()
    }
}

function Get-NormalizedThreePartVersion {
    param([Parameter(Mandatory)][string]$Value)
    $match = [Regex]::Match($Value.Trim(),'^([0-9]+\.[0-9]+\.[0-9]+)(?:[.+-].*)?$')
    if (-not $match.Success) { return '' }
    return $match.Groups[1].Value
}

function Install-Tailscale {
    $artifact = Get-PinnedArtifact Tailscale
    $cli = "$env:ProgramFiles\Tailscale\tailscale.exe"
    if (Test-Path -LiteralPath $cli) {
        Assert-FixedVendorExecutable -Path $cli -Artifact $artifact
        $versionResult = Invoke-FixedVendorExecutable -Path $cli -Arguments @('version')
        if ($versionResult.ExitCode -ne 0) { throw 'The pinned Tailscale CLI could not report its version.' }
        $installed = Get-NormalizedThreePartVersion (
            ($versionResult.StandardOutput -split "`r?`n" | Select-Object -First 1))
        if ($installed -eq $artifact.Version) { return $cli }
    }
    $installer = Get-VerifiedArtifact $artifact
    try {
        Install-VerifiedMsi -Artifact $artifact -Path $installer
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    Assert-FixedVendorExecutable -Path $cli -Artifact $artifact
    $versionResult = Invoke-FixedVendorExecutable -Path $cli -Arguments @('version')
    if ($versionResult.ExitCode -ne 0) { throw 'The installed Tailscale CLI could not report its version.' }
    $installed = Get-NormalizedThreePartVersion (
        ($versionResult.StandardOutput -split "`r?`n" | Select-Object -First 1))
    if ($installed -ne $artifact.Version) { throw "Tailscale version $installed was installed instead of pinned version $($artifact.Version)." }
    return $cli
}

function Install-RustDesk {
    $artifact = Get-PinnedArtifact RustDesk
    $client = "$env:ProgramFiles\RustDesk\rustdesk.exe"
    if (Test-Path -LiteralPath $client) {
        Assert-FixedVendorExecutable -Path $client -Artifact $artifact
        $installed = Get-NormalizedThreePartVersion (
            [string](Get-Item -LiteralPath $client).VersionInfo.ProductVersion)
        if ($installed -eq $artifact.Version) { return $client }
    }
    $installer = Get-VerifiedArtifact $artifact
    try {
        Install-VerifiedMsi -Artifact $artifact -Path $installer
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    Assert-FixedVendorExecutable -Path $client -Artifact $artifact
    $installed = Get-NormalizedThreePartVersion (
        [string](Get-Item -LiteralPath $client).VersionInfo.ProductVersion)
    if ($installed -ne $artifact.Version) { throw "RustDesk version $installed was installed instead of pinned version $($artifact.Version)." }
    return $client
}

function Configure-PrivateRustDeskController {
    param([Parameter(Mandatory)][string]$Client)
    Write-Host 'Restricting the remote-session engine to Opticon and the private Tailscale mesh...'
    $options = @(@('direct-server','N'),@('custom-rendezvous-server','127.0.0.1'),@('relay-server','127.0.0.1'),@('enable-lan-discovery','N'),@('hide-tray','Y'),@('hide-stop-service','Y'),@('disable-discovery-panel','Y'),@('allow-auto-update','N'),@('enable-udp-punch','N'),@('enable-ipv6-punch','N'))
    foreach ($option in $options) {
        $result = Invoke-FixedVendorExecutable -Path $Client `
            -Arguments @('--option',$option[0],$option[1]) -Timeout ([TimeSpan]::FromSeconds(15))
        if ($result.ExitCode -ne 0) { throw "RustDesk rejected private option $($option[0])." }
    }
    Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue
    Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Set-Service -StartupType Disabled
    $netsh = Join-Path ([Environment]::SystemDirectory) 'netsh.exe'
    if (-not (Test-Path -LiteralPath $netsh -PathType Leaf)) { throw 'System32 netsh.exe is unavailable.' }
    & $netsh advfirewall firewall delete rule 'name=all' 'dir=in' "program=$Client" | Out-Null
    foreach($rule in @('RustDesk External IPv4 Block','RustDesk External IPv6 Block')){& $netsh advfirewall firewall delete rule "name=$rule" | Out-Null}
    & $netsh advfirewall firewall add rule 'name=RustDesk External IPv4 Block' 'dir=out' 'action=block' 'remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255' "program=$Client" 'profile=any' 'enable=yes' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not restrict RustDesk to Tailscale IPv4 destinations.'}
    & $netsh advfirewall firewall add rule 'name=RustDesk External IPv6 Block' 'dir=out' 'action=block' 'remoteip=::/1,8000::/1' "program=$Client" 'profile=any' 'enable=yes' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not block external RustDesk IPv6 destinations.'}
}

function New-Shortcut {
    param([string]$Target, [string]$Path)
    throw 'Elevated user-profile shortcut creation is intentionally disabled; Opticon uses a least-privilege interactive task.'
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

    if ($sid -notmatch '^S-1-(?:5|12)-(?:\d+-){1,14}\d+$') {
        throw 'The signed-in interactive Windows user SID is invalid.'
    }
    return [PSCustomObject]@{ AccountName = $accountName; Sid = $sid }

    <# Legacy profile discovery is deliberately unreachable. Elevated installation
       no longer reads user-controlled Shell Folders or writes to a user profile. #>
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
function Write-ProtectedTaskXml {
    param([Parameter(Mandatory)][string]$Xml)
    $path = Join-Path $PSScriptRoot ('.route-task-' + [Guid]::NewGuid().ToString('N') + '.xml')
    $stream = New-Object IO.FileStream(
        $path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,
        4096,[IO.FileOptions]::WriteThrough)
    try {
        $document = New-Object Xml.XmlDocument
        $document.PreserveWhitespace = $true
        $document.LoadXml($Xml)
        $declaration = $document.FirstChild -as [Xml.XmlDeclaration]
        if ($null -eq $declaration) {
            $declaration = $document.CreateXmlDeclaration('1.0','utf-8',$null)
            [void]$document.PrependChild($declaration)
        } else {
            $declaration.Encoding = 'utf-8'
        }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($document.OuterXml)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    return $path
}

function Get-RouteTaskXml {
    $task = Get-ScheduledTask -TaskName $script:RouteTaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) { return $null }
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    $output = @(& $schtasks /Query /TN $script:RouteTaskName /XML 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'The existing route task could not be snapshotted.' }
    $xml = $output -join "`r`n"
    if ($xml.Length -le 0 -or $xml.Length -gt 1048576) {
        throw 'The existing route task XML has an invalid size.'
    }
    return $xml
}

function Invoke-RegisterTaskXml {
    param([Parameter(Mandatory)][string]$Xml)
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    $path = Write-ProtectedTaskXml $Xml
    try {
        & $schtasks /Create /TN $script:RouteTaskName /XML $path /F | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Windows refused the protected RouteKeeper task XML.' }
    } finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function New-RouteKeeperTaskXml {
    param([Parameter(Mandatory)][string]$Helper)
    $command = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($Helper))
    $start = [Xml.XmlConvert]::ToString(
        [DateTime]::UtcNow.AddMinutes(1),[Xml.XmlDateTimeSerializationMode]::Utc)
    return @"
<?xml version="1.0" encoding="utf-8"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Maintains the fixed Opticon control-endpoint route.</Description></RegistrationInfo>
  <Triggers>
    <BootTrigger><Enabled>true</Enabled></BootTrigger>
    <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
    <TimeTrigger><Repetition><Interval>PT5M</Interval></Repetition><StartBoundary>$start</StartBoundary><Enabled>true</Enabled></TimeTrigger>
  </Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>
  <Actions Context="Author"><Exec><Command>$command</Command><Arguments>--controller-ip=$($script:ControllerIPv4)</Arguments></Exec></Actions>
</Task>
"@
}

function Assert-ExactRouteKeeperTask {
    param([Parameter(Mandatory)][string]$ExpectedHelper)
    $raw = Get-RouteTaskXml
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'The RouteKeeper task is missing.' }
    [xml]$xml = $raw
    $namespace = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $namespace.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    function NodeText([string]$xpath) {
        $node = $xml.SelectSingleNode($xpath,$namespace)
        if ($null -eq $node) { return '' }
        return [string]$node.InnerText
    }
    $expectedCommand = [IO.Path]::GetFullPath($ExpectedHelper)
    $triggerNodes = $xml.SelectNodes('/t:Task/t:Triggers/*',$namespace)
    $actionNodes = $xml.SelectNodes('/t:Task/t:Actions/*',$namespace)
    $principalNodes = $xml.SelectNodes('/t:Task/t:Principals/t:Principal',$namespace)
    if ($xml.DocumentElement.LocalName -cne 'Task' -or
        $xml.DocumentElement.NamespaceURI -cne 'http://schemas.microsoft.com/windows/2004/02/mit/task' -or
        $actionNodes.Count -ne 1 -or $actionNodes[0].LocalName -cne 'Exec' -or
        $principalNodes.Count -ne 1 -or $triggerNodes.Count -ne 3 -or
        -not (NodeText '/t:Task/t:Actions/t:Exec/t:Command').Equals(
            $expectedCommand,[StringComparison]::OrdinalIgnoreCase) -or
        (NodeText '/t:Task/t:Actions/t:Exec/t:Arguments') -cne "--controller-ip=$($script:ControllerIPv4)" -or
        (NodeText '/t:Task/t:Actions/@Context') -cne 'Author' -or
        (NodeText '/t:Task/t:Principals/t:Principal/t:UserId') -cne 'S-1-5-18' -or
        (NodeText '/t:Task/t:Principals/t:Principal/t:LogonType') -cne 'ServiceAccount' -or
        (NodeText '/t:Task/t:Principals/t:Principal/t:RunLevel') -cne 'HighestAvailable' -or
        (NodeText '/t:Task/t:Settings/t:MultipleInstancesPolicy') -cne 'IgnoreNew' -or
        (NodeText '/t:Task/t:Settings/t:DisallowStartIfOnBatteries') -cne 'false' -or
        (NodeText '/t:Task/t:Settings/t:StopIfGoingOnBatteries') -cne 'false' -or
        (NodeText '/t:Task/t:Settings/t:AllowStartOnDemand') -cne 'true' -or
        (NodeText '/t:Task/t:Settings/t:Enabled') -cne 'true' -or
        (NodeText '/t:Task/t:Settings/t:RunOnlyIfNetworkAvailable') -cne 'false' -or
        (NodeText '/t:Task/t:Settings/t:StartWhenAvailable') -cne 'true' -or
        (NodeText '/t:Task/t:Settings/t:ExecutionTimeLimit') -cne 'PT0S' -or
        (NodeText '/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Interval') -cne 'PT5M' -or
        $xml.SelectNodes('/t:Task/t:Triggers/t:TimeTrigger/t:Repetition/t:Duration',$namespace).Count -ne 0 -or
        $xml.SelectNodes('/t:Task/t:Triggers/t:BootTrigger',$namespace).Count -ne 1 -or
        $xml.SelectNodes('/t:Task/t:Triggers/t:LogonTrigger',$namespace).Count -ne 1 -or
        $xml.SelectNodes('/t:Task/t:Triggers/t:TimeTrigger',$namespace).Count -ne 1) {
        throw 'The installed RouteKeeper task does not match the exact signed task contract.'
    }
}

function Register-ExactRouteKeeperTask {
    $helper = Join-Path $script:CanonicalControllerInstallDirectory 'Tools\Taildesk.RouteKeeper.exe'
    Assert-PinnedOpticonExecutable $helper
    Invoke-RegisterTaskXml (New-RouteKeeperTaskXml $helper)
    Assert-ExactRouteKeeperTask $helper
}

function Restore-RouteTaskSnapshot {
    param([AllowNull()][string]$Snapshot)
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    if ([string]::IsNullOrWhiteSpace($Snapshot)) {
        & $schtasks /Delete /TN $script:RouteTaskName /F 2>$null | Out-Null
        return
    }
    Invoke-RegisterTaskXml $Snapshot
}

function Start-ExactRouteKeeperTask {
    $helper = Join-Path $script:CanonicalControllerInstallDirectory 'Tools\Taildesk.RouteKeeper.exe'
    Assert-ExactRouteKeeperTask $helper
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    & $schtasks /Run /TN $script:RouteTaskName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows could not start the exact RouteKeeper task.' }
}

function Get-UiTaskXml {
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    $output = @(& $schtasks /Query /TN $script:UiTaskName /XML 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    $xml = $output -join "`r`n"
    if ($xml.Length -le 0 -or $xml.Length -gt 1048576) { throw 'The command-center task XML has an invalid size.' }
    return $xml
}

function Invoke-RegisterUiTaskXml {
    param([Parameter(Mandatory)][string]$Xml)
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    $path = Write-ProtectedTaskXml $Xml
    try {
        & $schtasks /Create /TN $script:UiTaskName /XML $path /F | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Windows refused the protected command-center task XML.' }
    } finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function New-UiTaskXml {
    param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$Sid)
    if ($Sid -notmatch '^S-1-(?:5|12)-(?:\d+-){1,14}\d+$') { throw 'The interactive user SID is invalid.' }
    $command = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($Executable))
    $user = [Security.SecurityElement]::Escape($Sid)
    return @"
<?xml version="1.0" encoding="utf-8"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Runs the signed Opticon command center for its selected interactive user.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$user</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>
  <Actions Context="Author"><Exec><Command>$command</Command></Exec></Actions>
</Task>
"@
}

function Assert-ExactUiTask {
    param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$Sid)
    $raw = Get-UiTaskXml
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'The command-center task is missing.' }
    [xml]$xml = $raw
    $ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    function UiNode([string]$xpath) { $node=$xml.SelectSingleNode($xpath,$ns); if($null -eq $node){return ''}; return [string]$node.InnerText }
    $actions=$xml.SelectNodes('/t:Task/t:Actions/*',$ns)
    $principals=$xml.SelectNodes('/t:Task/t:Principals/t:Principal',$ns)
    $triggers=$xml.SelectNodes('/t:Task/t:Triggers/*',$ns)
    if($actions.Count -ne 1 -or $actions[0].LocalName -cne 'Exec' -or $principals.Count -ne 1 -or
       $triggers.Count -ne 1 -or $triggers[0].LocalName -cne 'LogonTrigger' -or
       -not (UiNode '/t:Task/t:Actions/t:Exec/t:Command').Equals([IO.Path]::GetFullPath($Executable),[StringComparison]::OrdinalIgnoreCase) -or
       -not [string]::IsNullOrWhiteSpace((UiNode '/t:Task/t:Actions/t:Exec/t:Arguments')) -or
       (UiNode '/t:Task/t:Principals/t:Principal/t:UserId') -cne $Sid -or
       (UiNode '/t:Task/t:Principals/t:Principal/t:LogonType') -cne 'InteractiveToken' -or
       (UiNode '/t:Task/t:Principals/t:Principal/t:RunLevel') -cne 'LeastPrivilege' -or
       (UiNode '/t:Task/t:Triggers/t:LogonTrigger/t:UserId') -cne $Sid -or
       (UiNode '/t:Task/t:Settings/t:MultipleInstancesPolicy') -cne 'IgnoreNew' -or
       (UiNode '/t:Task/t:Settings/t:DisallowStartIfOnBatteries') -cne 'false' -or
       (UiNode '/t:Task/t:Settings/t:StopIfGoingOnBatteries') -cne 'false' -or
       (UiNode '/t:Task/t:Settings/t:StartWhenAvailable') -cne 'true' -or
       (UiNode '/t:Task/t:Settings/t:ExecutionTimeLimit') -cne 'PT0S') { throw 'The command-center task does not match the exact least-privilege contract.' }
}

function Register-ExactUiTask {
    param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$Sid)
    Assert-PinnedOpticonExecutable $Executable
    Invoke-RegisterUiTaskXml (New-UiTaskXml -Executable $Executable -Sid $Sid)
    Assert-ExactUiTask -Executable $Executable -Sid $Sid
}

function Restore-UiTaskSnapshot {
    param([AllowNull()][string]$Snapshot)
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    if([string]::IsNullOrWhiteSpace($Snapshot)){
        & $schtasks /Delete /TN $script:UiTaskName /F 2>$null | Out-Null
    } else { Invoke-RegisterUiTaskXml $Snapshot }
}

function Start-ExactUiTask {
    param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$Sid)
    Assert-ExactUiTask -Executable $Executable -Sid $Sid
    $schtasks = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'
    & $schtasks /Run /TN $script:UiTaskName | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not start the least-privilege command-center task.'}
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
Assert-ProtectedInstallerHandoff
Assert-NoDirectoryReparsePoints -Directory $source
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
$tailscale = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not (Test-Path -LiteralPath $tailscale -PathType Leaf)) {
    throw 'This machine is not enrolled in the Opticon mesh. Install the command center through a source-build invitation; this elevated package will not open a browser or choose a control server.'
}
Assert-FixedVendorExecutable -Path $tailscale -Artifact (Get-PinnedArtifact -Name 'Tailscale')

$statusText = (& $tailscale status --json 2>$null) -join "`n"
$running = $false
if ($statusText) {
    try {
        $statusObject = $statusText | ConvertFrom-Json
        $running = $statusObject.BackendState -eq 'Running' -and @($statusObject.Self.TailscaleIPs | Where-Object { $_ -match '^100\.' }).Count -gt 0
    } catch { $running = $false }
}
if (-not $running) {
    throw 'The pinned Tailscale client is not already connected to the Opticon mesh. Use a source-build invitation; elevated browser login is intentionally disabled.'
}

# Validate every fallible input we can before swapping the controller payload.
$ipValue = & $tailscale ip -4 | Select-Object -First 1
if (-not $ipValue) { throw 'Tailscale did not assign an IPv4 address after login.' }
$ip = $ipValue.Trim()
if ($ip -notmatch '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.') {
    throw "Tailscale returned an address outside 100.64.0.0/10: $ip"
}
$profiles = @(Get-NetFirewallProfile)
if($profiles.Count -ne 3 -or @($profiles | Where-Object { -not $_.Enabled -or $_.DefaultInboundAction.ToString() -ne 'Block' }).Count -ne 0){
    throw 'All Windows Firewall profiles must be enabled with default inbound blocking before Opticon installs remote access.'
}
$netsh = Join-Path ([Environment]::SystemDirectory) 'netsh.exe'
$expectedRustDesk = Join-Path $env:ProgramFiles 'RustDesk\rustdesk.exe'
& $netsh advfirewall firewall delete rule 'name=all' 'dir=in' "program=$expectedRustDesk" | Out-Null
& $netsh advfirewall firewall add rule 'name=RustDesk Direct (Tailscale only)' 'dir=in' 'action=allow' 'protocol=TCP' 'localport=21118' "localip=$ip" 'remoteip=100.64.0.0/10' "program=$expectedRustDesk" 'profile=any' 'enable=yes' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Windows could not pre-isolate RustDesk before installation.'}
$rustDesk = Install-RustDesk
Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue
Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Set-Service -StartupType Disabled
Configure-PrivateRustDeskController $rustDesk
& $netsh advfirewall firewall add rule 'name=RustDesk Direct (Tailscale only)' 'dir=in' 'action=allow' 'protocol=TCP' 'localport=21118' "localip=$ip" 'remoteip=100.64.0.0/10' "program=$rustDesk" 'profile=any' 'enable=yes' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Windows could not retain the exact private RustDesk allow rule.'}
Get-Service -Name 'RustDesk' -ErrorAction Stop | Set-Service -StartupType Automatic
Get-Service -Name 'RustDesk' -ErrorAction Stop | Start-Service
# Network setup is a verified prerequisite before the directory swap. A
# failure here cannot activate a new controller payload.
$deleteRuleArguments = @('advfirewall', 'firewall', 'delete', 'rule', 'name=Opticon Coordinator (Tailscale only)')
& $netsh @deleteRuleArguments | Out-Null
$deleteLegacyRuleArguments = @('advfirewall', 'firewall', 'delete', 'rule', 'name=Taildesk Coordinator (Tailscale only)')
& $netsh @deleteLegacyRuleArguments | Out-Null
$addRuleArguments = @(
    'advfirewall', 'firewall', 'add', 'rule',
    'name=Opticon Coordinator (Tailscale only)', 'dir=in', 'action=allow',
    'protocol=TCP', 'localport=45830', "localip=$ip", 'remoteip=100.64.0.0/10',
    'profile=any', 'enable=yes'
)
& $netsh @addRuleArguments | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Windows could not create the Tailscale-only coordinator firewall rule.' }
}

$admin = Join-Path $InstallDirectory 'Opticon.exe'
$routeTaskSnapshot = Get-RouteTaskXml
$uiTaskSnapshot = Get-UiTaskXml
try {
    Install-OpticonPayloadTransaction -Source $source -Destination $InstallDirectory -ConfigureActivatedPayload {
        Register-ExactRouteKeeperTask
        Register-ExactUiTask -Executable $admin -Sid $interactiveProfile.Sid
    }
} catch {
    $installFailure = $_
    try {
        Restore-RouteTaskSnapshot -Snapshot $routeTaskSnapshot
        Restore-UiTaskSnapshot -Snapshot $uiTaskSnapshot
    } catch {
        throw "Opticon installation failed and its scheduled-task rollback also failed. Install error: $($installFailure.Exception.Message). Task rollback error: $($_.Exception.Message)"
    }
    throw $installFailure
}

Start-ExactRouteKeeperTask
Start-ExactUiTask -Executable $admin -Sid $interactiveProfile.Sid
Write-Host "Installed for $($interactiveProfile.AccountName)." -ForegroundColor Green
Write-Host 'The signed command center has been started through its least-privilege interactive task.' -ForegroundColor Green
Write-Host 'It starts again at sign-in and remains available while that user stays signed in; locking the screen is fine.' -ForegroundColor Yellow
} finally {
    $installLock.Dispose()
}
