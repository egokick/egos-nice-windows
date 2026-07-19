$ErrorActionPreference = 'Stop'

function Test-DockerReady {
    param([string]$Executable)

    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        & $Executable info *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
}

$knownDockerPaths = @(
    (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Docker\Docker\resources\bin\docker.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe')
)

& (Join-Path $PSScriptRoot 'ensure-winget-package.ps1') `
    -PackageId 'Docker.DockerDesktop' `
    -DisplayName 'Docker Desktop' `
    -CommandName 'docker.exe' `
    -KnownPaths $knownDockerPaths

$dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
$docker = if ($dockerCommand) {
    $dockerCommand.Source
} else {
    $knownDockerPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $docker) {
    throw 'Docker Desktop was installed, but docker.exe could not be found.'
}

if (Test-DockerReady -Executable $docker) {
    Write-Host 'Docker Desktop is ready.'
    exit 0
}

$desktopCandidates = @(
    (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Docker\Docker\Docker Desktop.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\Docker Desktop.exe')
)
$desktop = $desktopCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $desktop) {
    throw 'Docker Desktop is installed, but its desktop executable could not be found.'
}

Write-Host 'Starting Docker Desktop...'
Start-Process -FilePath $desktop -ArgumentList '--minimized' -WindowStyle Hidden
$deadline = (Get-Date).AddMinutes(2)
do {
    Start-Sleep -Seconds 3
    if (Test-DockerReady -Executable $docker) {
        Write-Host 'Docker Desktop is ready.'
        exit 0
    }
} while ((Get-Date) -lt $deadline)

throw 'Docker Desktop did not become ready within two minutes.'
