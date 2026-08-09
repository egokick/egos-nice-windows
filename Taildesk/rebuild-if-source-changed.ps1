[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'

$OwnerManagedProductSigner = '820179B968ADC9C289A56B52025292BDCBFF3A74'
$OwnerManagedSourceSigner = 'EF6907F6706FB68CB4743F0781AFF631391FCDD2'
$Rfc3161TimestampUrl = 'http://timestamp.digicert.com'

function Get-PowerShell7Path {
    $command = Get-Command pwsh.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command -or [string]::IsNullOrWhiteSpace($command.Source)) {
        throw 'PowerShell 7 or newer is required to rebuild Opticon. Install Microsoft.PowerShell, then try again.'
    }
    return [IO.Path]::GetFullPath($command.Source)
}

function Assert-OwnerManagedInstaller {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The signed Opticon installer is missing: $Path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $actual = if ($null -eq $signature.SignerCertificate) {
        ''
    } else {
        $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
    }
    if ($actual -cne $OwnerManagedProductSigner -or
        $signature.Status -in @('NotSigned', 'HashMismatch') -or
        $null -eq $signature.TimeStamperCertificate) {
        throw 'The rebuilt Opticon installer does not have the exact OwnerManaged product signature and RFC 3161 timestamp.'
    }
}

function Expand-OwnerManagedPackage {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        throw "The random package extraction directory already exists: $Destination"
    }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    Expand-Archive -LiteralPath $Archive -DestinationPath $Destination
}

function Remove-ExtractedOwnerManagedPackage {
    param([Parameter(Mandatory)][string]$Directory)

    $files = @(
        'Install-Opticon.exe',
        'command-center.manifest.json',
        'command-center.manifest.sig',
        'App\Opticon.exe',
        'App\Cli\opticon.exe',
        'App\Tools\Taildesk.RouteKeeper.exe',
        'App\Payload\Setup\Taildesk.Setup.exe',
        'App\Payload\Agent\Taildesk.Agent.exe',
        'App\Payload\Admin\Opticon.exe',
        'App\Payload\Admin\Cli\opticon.exe',
        'App\Payload\Admin\Tools\Taildesk.RouteKeeper.exe',
        'App\Payload\UpdateGuardian\Taildesk.UpdateGuardian.exe'
    )
    foreach ($relative in $files) {
        $path = Join-Path $Directory $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::Delete($path) }
    }
    foreach ($relative in @(
            'App\Payload\Admin\Tools', 'App\Payload\Admin\Cli', 'App\Payload\Admin',
            'App\Payload\UpdateGuardian', 'App\Payload\Agent', 'App\Payload\Setup',
            'App\Payload', 'App\Tools', 'App\Cli', 'App', '.')) {
        $path = if ($relative -eq '.') { $Directory } else { Join-Path $Directory $relative }
        if (Test-Path -LiteralPath $path -PathType Container) {
            try { [IO.Directory]::Delete($path, $false) } catch { }
        }
    }
}

function Get-InstalledOpticonPath {
    foreach ($path in @(
        (Join-Path $env:ProgramFiles 'Taildesk\Admin\Opticon.exe'),
        (Join-Path $env:LocalAppData 'Programs\Opticon\Opticon.exe')
    )) {
        if (Test-Path -LiteralPath $path) { return $path }
    }

    return $null
}

function Test-ControllerInstallationReady {
    $controllerRoot = Join-Path $env:ProgramFiles 'Taildesk'
    $requiredFiles = @(
        (Join-Path $controllerRoot '.controller-install.lock'),
        (Join-Path $controllerRoot 'Admin\.opticon-controller-owned'),
        (Join-Path $controllerRoot 'Admin\.opticon-controller-ready')
    )

    return ($requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0
}

function Get-InstalledOpticonProcessIds {
    param([Parameter(Mandatory)][string]$InstalledPath)

    $expected = [IO.Path]::GetFullPath($InstalledPath)
    $ids = @()
    foreach ($process in @(Get-Process -Name 'Opticon','Taildesk.Admin' -ErrorAction SilentlyContinue)) {
        try {
            if ([IO.Path]::GetFullPath($process.MainModule.FileName).Equals(
                    $expected, [StringComparison]::OrdinalIgnoreCase)) {
                $ids += $process.Id
            }
        } catch {
            throw "Opticon could not verify running process $($process.ProcessName) ($($process.Id)); exit Opticon from its notification-area icon and try again."
        } finally {
            $process.Dispose()
        }
    }
    return @($ids)
}

function Request-InstalledOpticonShutdown {
    param([Parameter(Mandatory)][string]$InstalledPath)

    $processIds = @(Get-InstalledOpticonProcessIds -InstalledPath $InstalledPath)
    if ($processIds.Count -eq 0) { return }

    try {
        $shutdownEvent = [Threading.EventWaitHandle]::OpenExisting(
            'Local\Taildesk.Admin.ShutdownForUpdate')
    } catch [Threading.WaitHandleCannotBeOpenedException] {
        throw "The running Opticon command center predates automatic source-update handoff. Exit Opticon once from its notification-area icon, then start it from the Admin Panel again."
    }
    try { [void]$shutdownEvent.Set() }
    finally { $shutdownEvent.Dispose() }

    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        $remaining = @($processIds | Where-Object {
            Get-Process -Id $_ -ErrorAction SilentlyContinue
        })
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Opticon did not finish its bounded SSH-session cleanup before the local update. Exit it from its notification-area icon, then try again."
}

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$fingerprintHelper = Join-Path $PSScriptRoot 'Get-OpticonSourceFingerprint.ps1'
if (-not (Test-Path -LiteralPath $fingerprintHelper -PathType Leaf)) {
    throw "Opticon's source fingerprint helper is missing: $fingerprintHelper"
}
. $fingerprintHelper
$installedOpticon = Get-InstalledOpticonPath
$sourceFingerprint = Get-OpticonSourceFingerprint -SourceRoot $SourceRoot
$controllerInstallationReady = Test-ControllerInstallationReady
$cacheDirectory = Join-Path $env:LOCALAPPDATA 'Opticon\BuildCache'
$cachePath = Join-Path $cacheDirectory 'controller-source-v1.json'
$installedHash = if ($null -ne $installedOpticon) {
    (Get-FileHash -LiteralPath $installedOpticon -Algorithm SHA256).Hash.ToLowerInvariant()
} else { '' }
$cached = if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
    try { Get-Content -Raw -LiteralPath $cachePath | ConvertFrom-Json } catch { $null }
} else { $null }
if ($null -ne $installedOpticon -and
    $null -ne $cached -and
    [int]$cached.schemaVersion -eq 1 -and
    [string]$cached.sourceFingerprint -eq $sourceFingerprint -and
    [string]$cached.installedSha256 -eq $installedHash -and
    [string]$cached.installedPath -eq [IO.Path]::GetFullPath($installedOpticon) -and
    $controllerInstallationReady) {
    Write-Host 'Opticon source and installed command center are unchanged; using the installed build.' -ForegroundColor DarkGray
    exit 0
}

if ($controllerInstallationReady) {
    Write-Host 'Opticon source changes detected; rebuilding and installing the updated command center...' -ForegroundColor Cyan
} else {
    Write-Host 'Opticon installation integrity files are missing; rebuilding and repairing the command center...' -ForegroundColor Yellow
}
$buildScript = Join-Path $SourceRoot 'build.ps1'
$powershell7 = Get-PowerShell7Path
try {
    & $powershell7 -NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $buildScript `
        -BuildProfile OwnerManaged `
        -Runtime win-x64 `
        -CodeSigningCertificateThumbprint $OwnerManagedProductSigner `
        -SourceReleaseSigningCertificateThumbprint $OwnerManagedSourceSigner `
        -TimestampServer $Rfc3161TimestampUrl `
        -SkipTargetReleaseDeployment
} catch {
    throw "Opticon build failed before installation. $($_.Exception.Message)"
}
if ($LASTEXITCODE -ne 0) { throw "Opticon build failed with exit code $LASTEXITCODE." }

$package = Join-Path $SourceRoot 'dist\Opticon-CommandCenter-OWNER-MANAGED-win-x64.zip'
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw "The Opticon build completed without its OwnerManaged package at '$package'."
}
$packageCacheRoot = Join-Path $env:LOCALAPPDATA 'Opticon\BuildCache\Packages'
[IO.Directory]::CreateDirectory($packageCacheRoot) | Out-Null
$packageDirectory = Join-Path $packageCacheRoot ([Guid]::NewGuid().ToString('N'))
try {
    Expand-OwnerManagedPackage -Archive $package -Destination $packageDirectory
    $transactionalInstaller = Join-Path $packageDirectory 'Install-Opticon.exe'
    Assert-OwnerManagedInstaller -Path $transactionalInstaller

    if ($null -ne $installedOpticon) {
        Request-InstalledOpticonShutdown -InstalledPath $installedOpticon
    }
    $updater = Start-Process -FilePath $transactionalInstaller -Verb RunAs -Wait -PassThru `
        -ArgumentList '--controller-only-repair' -WorkingDirectory $packageDirectory
    if ($updater.ExitCode -ne 0) {
        throw "The signed Opticon command-center repair returned exit code $($updater.ExitCode)."
    }
} finally {
    Remove-ExtractedOwnerManagedPackage -Directory $packageDirectory
}

$installedOpticon = Get-InstalledOpticonPath
if ($null -eq $installedOpticon -or -not (Test-ControllerInstallationReady)) {
    throw 'Opticon installation completed without a ready command-center payload.'
}
$installedHash = (Get-FileHash -LiteralPath $installedOpticon -Algorithm SHA256).Hash.ToLowerInvariant()
$cache = [pscustomobject][ordered]@{
    schemaVersion = 1
    sourceFingerprint = $sourceFingerprint
    installedPath = [IO.Path]::GetFullPath($installedOpticon)
    installedSha256 = $installedHash
    installedVersion = (Get-Item -LiteralPath $installedOpticon).VersionInfo.ProductVersion
    installedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
[IO.Directory]::CreateDirectory($cacheDirectory) | Out-Null
$temporaryCache = "$cachePath.new"
[IO.File]::WriteAllText(
    $temporaryCache,
    ($cache | ConvertTo-Json),
    [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryCache -Destination $cachePath -Force

Write-Host 'Opticon was rebuilt and updated successfully.' -ForegroundColor Green
