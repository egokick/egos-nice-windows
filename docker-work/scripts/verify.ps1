#requires -Version 5.1

param(
    [ValidateRange(1, 25)][int]$CycleCount = 10,
    [switch]$NoElevate,
    [switch]$LeaveBluetoothOnLaptop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

if (-not $NoElevate -and -not (Test-DockerWorkAdministrator)) {
    $arguments = @("-NoElevate", "-CycleCount", [string]$CycleCount)
    if ($LeaveBluetoothOnLaptop) {
        $arguments += "-LeaveBluetoothOnLaptop"
    }
    exit (Invoke-DockerWorkElevatedScript `
        -ScriptPath $PSCommandPath `
        -Arguments $arguments)
}
if (-not (Test-DockerWorkAdministrator)) {
    Write-Error "Verification must run as administrator."
    exit 1
}

$results = [Collections.Generic.List[object]]::new()
$failed = $false
$lockHeld = $false

function Add-VerificationResult {
    param(
        [Parameter(Mandatory)][string]$Test,
        [Parameter(Mandatory)][string]$Result,
        [string]$Details = ""
    )

    $results.Add([ordered]@{
        test = $Test
        result = $Result
        details = $Details
        at = (Get-Date).ToString("o")
    })
    Write-DockerWorkLog "VERIFY ${Result}: $Test $Details"
}

function Get-ContainerChromeBrowserPid {
    $result = Invoke-DockerWorkContainer `
        -Arguments @(
            "sh",
            "-ceu",
            "pgrep -fo '/opt/google/chrome/[c]hrome.*--user-data-dir=/home/chrome/work-profile'"
        ) `
        -TimeoutSeconds 15
    $pidText = $result.StdOut.Trim()
    if ($pidText -notmatch '^\d+$') {
        throw "Could not identify the container Chrome browser process."
    }
    return $pidText
}

function Assert-ContainerRuntimeContract {
    param([Parameter(Mandatory)][string]$ExpectedChromeBrowserPid)

    $health = Invoke-DockerWorkContainer `
        -Arguments @("/opt/stayactive/healthcheck.sh") `
        -TimeoutSeconds 60
    if ($health.Output -notmatch "STAYACTIVE_DOCKER_WORK_HEALTHY") {
        throw "Full in-container health marker was absent."
    }

    $runtime = Invoke-DockerWorkWsl `
        -Command @'
set -Eeuo pipefail
memory="$(docker inspect -f '{{.HostConfig.Memory}}' stayactive-work-browser)"
cpus="$(docker inspect -f '{{.HostConfig.NanoCpus}}' stayactive-work-browser)"
test "$memory" = 6442450944
test "$cpus" = 4000000000

listeners="$(docker exec stayactive-work-browser ss -ltn)"
for port in 5900 6080 8000 9222; do
    grep -Eq "127[.]0[.]0[.]1:${port}[[:space:]]" <<<"$listeners"
    ! grep -Eq "(0[.]0[.]0[.]0|\*):${port}[[:space:]]" <<<"$listeners"
done

renderer_pid="$(docker exec stayactive-work-browser pgrep -f -- '--type=renderer' | head -n1)"
test -n "$renderer_pid"
browser_pid="$(
    docker exec stayactive-work-browser \
        pgrep -fo '/opt/google/chrome/[c]hrome.*--user-data-dir=/home/chrome/work-profile'
)"
test -n "$browser_pid"
docker exec stayactive-work-browser sh -ceu "
    test \"\$(awk '/^NoNewPrivs:/ {print \$2}' /proc/$renderer_pid/status)\" = 1
    test \"\$(awk '/^Seccomp:/ {print \$2}' /proc/$renderer_pid/status)\" = 2
    browser_namespace=\"\$(readlink /proc/$browser_pid/ns/pid)\"
    renderer_namespace=\"\$(readlink /proc/$renderer_pid/ns/pid)\"
    test \"\$browser_namespace\" != \"\$renderer_namespace\"
    browser_filters=\"\$(awk '/^Seccomp_filters:/ {print \$2}' /proc/$browser_pid/status)\"
    renderer_filters=\"\$(awk '/^Seccomp_filters:/ {print \$2}' /proc/$renderer_pid/status)\"
    test -n \"\$browser_filters\"
    test -n \"\$renderer_filters\"
    test \"\$renderer_filters\" -gt \"\$browser_filters\"
    ! grep -qi 'No usable sandbox' /var/log/stayactive/chrome.err.log
    printf 'BROWSER_PID=%s\n' '$browser_pid'
    printf 'CHROME_SANDBOX=pidns:%s->%s,seccomp_filters:%s->%s\n' \
        \"\$browser_namespace\" \"\$renderer_namespace\" \
        \"\$browser_filters\" \"\$renderer_filters\"
"
'@ `
        -TimeoutSeconds 45
    $actualChromePid = [regex]::Match(
        $runtime.StdOut,
        '(?m)^BROWSER_PID=(\d+)$'
    ).Groups[1].Value
    if ($actualChromePid -ne $ExpectedChromeBrowserPid) {
        throw "Chrome restarted during Bluetooth switching (expected PID $ExpectedChromeBrowserPid, actual $actualChromePid)."
    }
    Assert-DockerWorkWindowsViewerReachable
    Add-VerificationResult `
        -Test "container runtime, loopback listeners, limits, and sandbox process state" `
        -Result "PASS" `
        -Details ($runtime.StdOut -replace "\r?\n", "; ")
}

function Write-VerificationReport {
    $reportPath = Join-Path $script:DockerWorkCache "verification.json"
    $report = [ordered]@{
        completedAt = (Get-Date).ToString("o")
        success = (-not $failed)
        cyclesRequested = $CycleCount
        results = $results
    } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText(
        $reportPath,
        $report + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false)
    )
}

try {
    Clear-Content -LiteralPath $script:DockerWorkLog -ErrorAction SilentlyContinue
    Write-DockerWorkLog "Starting Docker work-browser reliability verification."
    Assert-DockerWorkInstalled
    Release-VirtualBoxBluetooth

    Enter-DockerWorkBluetoothLock
    $lockHeld = $true
    Assert-VirtualBoxDoesNotOwnBluetooth
    Detach-DockerWorkBluetooth
    [void](Invoke-DockerWorkWsl `
        -Command "systemctl start docker" `
        -TimeoutSeconds 90)
    [void](Invoke-DockerWorkCompose `
        -Arguments @("up", "-d") `
        -TimeoutSeconds 180)
    Wait-DockerWorkContainerBaseReady -TimeoutSeconds 120
    $chromeBrowserPid = Get-ContainerChromeBrowserPid

    $dockerProof = Invoke-DockerWorkWsl `
        -Command @'
set -Eeuo pipefail
docker run --rm hello-world | grep -q 'Hello from Docker'
uname -r
zgrep 'CONFIG_BT_HCIBTUSB_MTK=y' /proc/config.gz
test -r /lib/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin
test -r "/usr/lib/modules/$(uname -r)/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin"
'@ `
        -TimeoutSeconds 180
    Add-VerificationResult `
        -Test "native Docker, pinned kernel option, and MT7925 firmware" `
        -Result "PASS" `
        -Details ($dockerProof.StdOut -replace "\r?\n", "; ")

    for ($cycle = 1; $cycle -le $CycleCount; $cycle++) {
        Attach-DockerWorkBluetooth
        Assert-ContainerRuntimeContract -ExpectedChromeBrowserPid $chromeBrowserPid
        Add-VerificationResult `
            -Test "Bluetooth attach cycle $cycle" `
            -Result "PASS" `
            -Details "13d3:3602 -> hci0 -> one bluetoothd -> BlueZ powered/LE"

        $prepare = Invoke-DockerWorkContainer `
            -Arguments @("/opt/stayactive/prepare-detach.sh") `
            -TimeoutSeconds 30
        if ($prepare.Output -notmatch "STAYACTIVE_BLUETOOTH_READY_TO_DETACH") {
            throw "Cycle $cycle did not prove a clean Bluetooth daemon shutdown."
        }
        $zeroDaemons = Invoke-DockerWorkContainer `
            -Arguments @("sh", "-ceu", "sleep 2; ! pgrep -x bluetoothd") `
            -TimeoutSeconds 10
        Detach-DockerWorkBluetooth
        Add-VerificationResult `
            -Test "Bluetooth detach cycle $cycle" `
            -Result "PASS" `
            -Details "zero bluetoothd before detach; exact Windows parent and MI_00 healthy"
    }

    # Recovery proof 1: an abruptly killed container cannot retain the device.
    Attach-DockerWorkBluetooth
    [void](Invoke-DockerWorkWsl `
        -Command "docker kill $script:DockerWorkContainer >/dev/null" `
        -TimeoutSeconds 30)
    Detach-DockerWorkBluetooth
    Add-VerificationResult `
        -Test "forced container crash recovery" `
        -Result "PASS" `
        -Details "Windows Bluetooth recovered without a reboot"

    [void](Invoke-DockerWorkCompose `
        -Arguments @("up", "-d") `
        -TimeoutSeconds 180)
    Wait-DockerWorkContainerBaseReady -TimeoutSeconds 120

    # Recovery proof 2: terminating the dedicated distro releases USB/IP.
    Attach-DockerWorkBluetooth
    [void](Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--terminate", $script:DockerWorkDistro) `
        -TimeoutSeconds 60)
    Restore-DockerWorkHostBluetooth
    Add-VerificationResult `
        -Test "dedicated WSL shutdown recovery" `
        -Result "PASS" `
        -Details "Windows Bluetooth recovered without a reboot"

    if (-not $LeaveBluetoothOnLaptop) {
        [void](Invoke-DockerWorkWsl `
            -Command "systemctl start docker" `
            -TimeoutSeconds 90)
        [void](Invoke-DockerWorkCompose `
            -Arguments @("up", "-d") `
            -TimeoutSeconds 180)
        Wait-DockerWorkContainerBaseReady -TimeoutSeconds 120
        $chromeBrowserPid = Get-ContainerChromeBrowserPid
        Attach-DockerWorkBluetooth
        Assert-ContainerRuntimeContract -ExpectedChromeBrowserPid $chromeBrowserPid
        Add-VerificationResult `
            -Test "final work-browser state" `
            -Result "PASS" `
            -Details "container running and Bluetooth left on the work browser"
    }

    Write-DockerWorkLog "STAYACTIVE_DOCKER_VERIFICATION_COMPLETE"
}
catch {
    $failed = $true
    Add-VerificationResult `
        -Test "verification run" `
        -Result "FAIL" `
        -Details $_.Exception.Message
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
    try {
        if ($lockHeld) {
            Detach-DockerWorkBluetooth
        }
    }
    catch {
        Write-DockerWorkLog "ERROR: Verification rollback could not verify Windows Bluetooth: $($_.Exception.Message)"
    }
}
finally {
    if ($lockHeld) {
        Exit-DockerWorkBluetoothLock
    }
    Write-VerificationReport
}

if ($failed) {
    exit 1
}
exit 0
