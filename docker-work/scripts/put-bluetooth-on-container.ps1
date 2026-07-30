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
$lockHeld = $false
try {
    Clear-Content -LiteralPath $script:DockerWorkLog -ErrorAction SilentlyContinue
    Write-DockerWorkLog "Starting reliable Bluetooth handoff to the Docker work browser."
    Assert-DockerWorkInstalled

    # The VirtualBox return script uses the same mutex, so it must complete
    # before this process acquires that mutex.
    Release-VirtualBoxBluetooth

    Enter-DockerWorkBluetoothLock
    $lockHeld = $true
    Assert-VirtualBoxDoesNotOwnBluetooth
    [void](Invoke-DockerWorkWsl `
        -Command "systemctl start docker" `
        -TimeoutSeconds 90)
    [void](Invoke-DockerWorkCompose `
        -Arguments @("up", "-d") `
        -TimeoutSeconds 180)
    Wait-DockerWorkContainerBaseReady -TimeoutSeconds 120
    Attach-DockerWorkBluetooth
    Write-DockerWorkLog "Bluetooth handoff to the Docker work browser completed."
}
catch {
    $failed = $true
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
    if ($lockHeld) {
        try {
            Detach-DockerWorkBluetooth
        }
        catch {
            Write-DockerWorkLog "ERROR: Rollback could not verify Windows Bluetooth: $($_.Exception.Message)"
        }
    }
}
finally {
    if ($lockHeld) {
        Exit-DockerWorkBluetoothLock
    }
}

if ($failed) {
    exit 1
}
exit 0
