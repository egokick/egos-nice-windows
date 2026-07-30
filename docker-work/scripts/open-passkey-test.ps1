#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

try {
    Assert-DockerWorkInstalled
    if (-not (Test-DockerWorkContainerRunning)) {
        throw "The Docker work browser is not running."
    }

    $health = Invoke-DockerWorkContainer `
        -Arguments @("/opt/stayactive/healthcheck.sh") `
        -TimeoutSeconds 45
    if ($health.Output -notmatch "STAYACTIVE_DOCKER_WORK_HEALTHY") {
        throw "The container does not have healthy Bluetooth and hybrid-passkey support."
    }
    Assert-DockerWorkWindowsViewerReachable

    $result = Invoke-DockerWorkContainer `
        -Arguments @(
            "curl",
            "--fail",
            "--silent",
            "--show-error",
            "--request", "PUT",
            "http://127.0.0.1:9222/json/new?http://localhost:8000/"
        ) `
        -TimeoutSeconds 15
    if ($result.Output -notmatch '"type"\s*:\s*"page"') {
        throw "Chrome did not confirm the passkey-test tab."
    }

    Start-Process "http://127.0.0.1:6080/vnc.html?autoconnect=1&resize=scale"
    Write-DockerWorkLog "The real WebAuthn hybrid-transport test is open inside container Chrome."
}
catch {
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
    exit 1
}
exit 0
