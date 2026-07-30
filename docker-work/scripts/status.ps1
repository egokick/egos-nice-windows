#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

try {
    if (-not (Test-DockerWorkDistroInstalled)) {
        Write-Output "STAYACTIVE_DOCKER_NOT_INSTALLED"
        exit 0
    }

    $distroRunning = Test-DockerWorkDistroRunning
    if (Test-VirtualBoxOwnsBluetooth) {
        Write-Output "STAYACTIVE_BLUETOOTH_OWNER=VM"
    }
    elseif ($distroRunning -and (Test-DockerWorkWslUsbPresent)) {
        Write-Output "STAYACTIVE_BLUETOOTH_OWNER=CONTAINER"
    }
    elseif ((Get-DockerWorkHostBluetoothState).Healthy) {
        Write-Output "STAYACTIVE_BLUETOOTH_OWNER=LAPTOP"
    }
    else {
        Write-Output "STAYACTIVE_BLUETOOTH_OWNER=UNKNOWN"
    }

    if ($distroRunning -and (Test-DockerWorkContainerRunning)) {
        $health = Invoke-DockerWorkContainer `
            -Arguments @("/opt/stayactive/healthcheck.sh", "--container") `
            -TimeoutSeconds 10 `
            -AllowFailure `
            -Quiet
        if ($health.Success) {
            Write-Output "STAYACTIVE_DOCKER_CONTAINER=RUNNING"
        }
        else {
            Write-Output "STAYACTIVE_DOCKER_CONTAINER=UNHEALTHY"
        }
    }
    else {
        Write-Output "STAYACTIVE_DOCKER_CONTAINER=STOPPED"
    }
}
catch {
    Write-Output "STAYACTIVE_DOCKER_STATUS_ERROR=$($_.Exception.Message)"
    exit 1
}
