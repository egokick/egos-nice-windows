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
    Write-DockerWorkLog "Stopping the Docker work browser and returning Bluetooth."

    # Stay safe if this command is used while the legacy VM happens to own the
    # adapter. Its return script uses the same mutex, so release it first.
    Release-VirtualBoxBluetooth

    Enter-DockerWorkBluetoothLock
    Detach-DockerWorkBluetooth
    if (Test-DockerWorkDistroInstalled) {
        [void](Invoke-DockerWorkCompose `
            -Arguments @("down") `
            -TimeoutSeconds 120 `
            -AllowFailure)
    }
    Write-DockerWorkLog "Docker work browser stopped."
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
