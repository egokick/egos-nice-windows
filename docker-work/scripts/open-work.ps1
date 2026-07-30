#requires -Version 5.1

param(
    [switch]$NoElevate,
    [switch]$NoOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

if (-not $NoElevate -and -not (Test-DockerWorkAdministrator)) {
    $arguments = @("-NoElevate")
    if ($NoOpen) {
        $arguments += "-NoOpen"
    }
    exit (Invoke-DockerWorkElevatedScript `
        -ScriptPath $PSCommandPath `
        -Arguments $arguments)
}
if (-not (Test-DockerWorkAdministrator)) {
    Write-Error "This operation must run as administrator."
    exit 1
}

$failed = $false
$lockHeld = $false
try {
    Clear-Content -LiteralPath $script:DockerWorkLog -ErrorAction SilentlyContinue
    Write-DockerWorkLog "Opening the Docker work browser with Bluetooth."
    Assert-DockerWorkInstalled

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

    $health = Invoke-DockerWorkContainer `
        -Arguments @("/opt/stayactive/healthcheck.sh") `
        -TimeoutSeconds 30
    if ($health.Output -notmatch 'STAYACTIVE_DOCKER_WORK_HEALTHY') {
        throw "The container did not emit its healthy marker."
    }
    Assert-DockerWorkWindowsViewerReachable

    if (-not $NoOpen) {
        Start-Process "http://127.0.0.1:6080/vnc.html?autoconnect=1&resize=scale"
    }
    Write-DockerWorkLog "STAYACTIVE_DOCKER_WORK_READY"
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
