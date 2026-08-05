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

function Get-LatestSourceWriteTime {
    $inputs = @(
        (Join-Path $SourceRoot 'src'),
        (Join-Path $SourceRoot 'assets'),
        (Join-Path $SourceRoot 'Taildesk.sln'),
        (Join-Path $SourceRoot 'Directory.Build.props'),
        (Join-Path $SourceRoot 'build.ps1')
    )

    $newest = [DateTime]::MinValue
    foreach ($input in $inputs) {
        if (-not (Test-Path -LiteralPath $input)) { continue }

        $items = if ((Get-Item -LiteralPath $input).PSIsContainer) {
            Get-ChildItem -LiteralPath $input -File -Recurse -Force |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
        } else {
            Get-Item -LiteralPath $input
        }

        foreach ($item in $items) {
            if ($item.LastWriteTimeUtc -gt $newest) { $newest = $item.LastWriteTimeUtc }
        }
    }

    return $newest
}

$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$installedOpticon = Get-InstalledOpticonPath
$latestSourceWriteTime = Get-LatestSourceWriteTime
if ($null -ne $installedOpticon -and
    $latestSourceWriteTime -le (Get-Item -LiteralPath $installedOpticon).LastWriteTimeUtc) {
    exit 0
}

Write-Host 'Opticon source changes detected; rebuilding and installing the updated command center...' -ForegroundColor Cyan
& (Join-Path $SourceRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw "Opticon build failed with exit code $LASTEXITCODE." }

$stagedApp = Join-Path $SourceRoot 'artifacts\Opticon-CommandCenter-win-x64\App'
if (-not (Test-Path -LiteralPath (Join-Path $stagedApp 'Opticon.exe'))) {
    throw "The Opticon build did not produce its staged app at '$stagedApp'."
}

$updateScript = Join-Path $SourceRoot 'scripts\Update-InstalledOpticon.ps1'
$command = "& '$($updateScript.Replace("'", "''"))' -SourceDirectory '$($stagedApp.Replace("'", "''"))'"
$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$updater = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCommand
)
if ($updater.ExitCode -ne 0) {
    throw "Opticon installation update failed with exit code $($updater.ExitCode)."
}

Write-Host 'Opticon was rebuilt and updated successfully.' -ForegroundColor Green
