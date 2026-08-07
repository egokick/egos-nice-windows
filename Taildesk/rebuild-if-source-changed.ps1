[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'

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
try {
    & (Join-Path $SourceRoot 'build.ps1')
} catch {
    throw "Opticon build failed before installation. $($_.Exception.Message)"
}
if ($LASTEXITCODE -ne 0) { throw "Opticon build failed with exit code $LASTEXITCODE." }

$stagedApp = Join-Path $SourceRoot 'artifacts\Opticon-CommandCenter-win-x64\App'
if (-not (Test-Path -LiteralPath (Join-Path $stagedApp 'Opticon.exe'))) {
    throw "The Opticon build did not produce its staged app at '$stagedApp'."
}
$stagedCli = Join-Path $stagedApp 'Payload\Admin\Cli\opticon.exe'
if (-not (Test-Path -LiteralPath $stagedCli -PathType Leaf)) {
    throw "The Opticon build did not produce its staged signed CLI at '$stagedCli'."
}

$transactionalInstaller = Join-Path $SourceRoot 'artifacts\Opticon-CommandCenter-win-x64\Install-Opticon.ps1'
if (-not (Test-Path -LiteralPath $transactionalInstaller -PathType Leaf)) {
    throw "The Opticon build completed without its transactional installer at '$transactionalInstaller'."
}
$repairLogDirectory = Join-Path $env:LOCALAPPDATA 'Opticon\Logs'
$repairLog = Join-Path $repairLogDirectory 'controller-repair.log'
$escapedInstaller = $transactionalInstaller.Replace("'", "''")
$escapedLogDirectory = $repairLogDirectory.Replace("'", "''")
$escapedLog = $repairLog.Replace("'", "''")
$command = @"
`$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path '$escapedLogDirectory' -Force | Out-Null
try {
    & '$escapedInstaller' -ControllerOnlyRepair *>&1 | Tee-Object -FilePath '$escapedLog' -Append
    exit 0
} catch {
    (`$_ | Format-List * -Force | Out-String) | Tee-Object -FilePath '$escapedLog' -Append | Out-Host
    exit 1
}
"@
$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$repairWorkingDirectory = Join-Path $env:SystemRoot 'Temp'
$updater = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCommand
) -WorkingDirectory $repairWorkingDirectory
if ($updater.ExitCode -ne 0) {
    $detail = if (Test-Path -LiteralPath $repairLog -PathType Leaf) {
        (@(Get-Content -LiteralPath $repairLog -Tail 35) -join [Environment]::NewLine).Trim()
    } else {
        'The elevated repair did not create its diagnostic log.'
    }
    throw "Opticon installation update failed with exit code $($updater.ExitCode).`n`n$detail"
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
