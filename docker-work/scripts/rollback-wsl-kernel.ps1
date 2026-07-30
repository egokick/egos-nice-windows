#requires -Version 5.1

param([switch]$NoElevate)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

if (-not $NoElevate -and -not (Test-DockerWorkAdministrator)) {
    exit (Invoke-DockerWorkElevatedScript `
        -ScriptPath $PSCommandPath `
        -Arguments @("-NoElevate"))
}
if (-not (Test-DockerWorkAdministrator)) {
    Write-Error "Kernel rollback must run as administrator."
    exit 1
}

$backup = Join-Path $script:DockerWorkState "wslconfig.before-stayactive"
$absentMarker = Join-Path $script:DockerWorkState "wslconfig.was-absent"
$wslConfig = Join-Path $env:USERPROFILE ".wslconfig"

try {
    Enter-DockerWorkBluetoothLock
    if (Test-DockerWorkUsbipdInstalled) {
        [void](Invoke-DockerWorkUsbipd `
            -Arguments @("detach", "--hardware-id", $script:BluetoothHardwareId) `
            -TimeoutSeconds 45 `
            -AllowFailure)
    }

    if (Test-Path -LiteralPath $backup) {
        Copy-Item -LiteralPath $backup -Destination $wslConfig -Force
    }
    elseif (Test-Path -LiteralPath $absentMarker) {
        Remove-Item -LiteralPath $wslConfig -Force -ErrorAction SilentlyContinue
    }
    else {
        throw "No pre-StayActive .wslconfig backup or absence marker exists."
    }

    [void](Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--shutdown") `
        -TimeoutSeconds 90)
    Restore-DockerWorkHostBluetooth
    Remove-Item `
        -LiteralPath (Join-Path $script:DockerWorkState "setup-complete.json") `
        -Force `
        -ErrorAction SilentlyContinue
    Write-DockerWorkLog "The prior WSL kernel configuration was restored."
}
catch {
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
    exit 1
}
finally {
    Exit-DockerWorkBluetoothLock
}
exit 0
