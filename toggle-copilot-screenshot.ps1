$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir "CopilotScreenshotRemap"
$projectFile = Join-Path $projectDir "CopilotScreenshotRemap.csproj"
$exePath = Join-Path $projectDir "bin\Release\net10.0-windows\CopilotScreenshotRemap.exe"
$pidFile = Join-Path $scriptDir "copilot-screenshot-helper.pid"
$logFile = Join-Path $scriptDir "copilot-screenshot-helper.log"

function Get-HelperProcess {
    if (-not (Test-Path $pidFile)) {
        return $null
    }

    $rawPid = Get-Content $pidFile -Raw
    $parsedPid = 0
    if (-not [int]::TryParse($rawPid.Trim(), [ref]$parsedPid)) {
        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
        return $null
    }

    $process = Get-Process -Id $parsedPid -ErrorAction SilentlyContinue
    if (-not $process) {
        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
        return $null
    }

    return $process
}

function Ensure-HelperBuilt {
    $sources = @(
        (Join-Path $projectDir "Program.cs"),
        $projectFile
    )

    $needsBuild = -not (Test-Path $exePath)
    if (-not $needsBuild) {
        $exeTime = (Get-Item $exePath).LastWriteTimeUtc
        foreach ($source in $sources) {
            if ((Get-Item $source).LastWriteTimeUtc -gt $exeTime) {
                $needsBuild = $true
                break
            }
        }
    }

    if ($needsBuild) {
        $env:DOTNET_CLI_HOME = Join-Path $scriptDir ".dotnet"
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
        dotnet build $projectFile -c Release | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE."
        }
    }
}

$runningProcess = Get-HelperProcess
if ($runningProcess) {
    Stop-Process -Id $runningProcess.Id -Force
    Start-Sleep -Milliseconds 300
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    Write-Output "Copilot screenshot remap disabled."
    exit 0
}

Ensure-HelperBuilt
Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $exePath -ArgumentList @($pidFile, $logFile) | Out-Null

for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 200
    $runningProcess = Get-HelperProcess
    if ($runningProcess) {
        Write-Output "Copilot screenshot remap enabled."
        exit 0
    }
}

throw "The Copilot screenshot helper did not stay running. Check $logFile for details."
