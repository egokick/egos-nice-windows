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
    Write-Error "This operation must run as administrator."
    exit 1
}

$failed = $false
try {
    Clear-Content -LiteralPath $script:DockerWorkLog -ErrorAction SilentlyContinue
    Write-DockerWorkLog "Starting reliable Bluetooth return from Docker to the laptop."

    # The legacy VirtualBox return path acquires the same global mutex. Run it
    # before taking that mutex here so this button recovers Bluetooth no matter
    # whether the current owner is VirtualBox, WSL/Docker, or Windows.
    Release-VirtualBoxBluetooth

    Enter-DockerWorkBluetoothLock
    Assert-VirtualBoxDoesNotOwnBluetooth
    Detach-DockerWorkBluetooth
    Write-DockerWorkLog "Bluetooth return to the laptop completed."
}
catch {
    $failed = $true
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
}
finally {
    Exit-DockerWorkBluetoothLock
}

if ($failed) {
    exit 1
}
exit 0
