#requires -Version 5.1

param(
    [switch]$NoElevate,
    [switch]$SkipKernelBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

function Install-DockerWorkUsbipd {
    try {
        [void](Get-UsbipdPath)
        return
    }
    catch {
    }

    # usbipd-win and VirtualBox both maintain USB monitor filter drivers. The
    # signed usbipd installer must briefly stop VBoxUSBMon while it updates the
    # filter stack. Preserve WorkRDP with a supported saved state first, then
    # require that no VM is still using the monitor driver.
    $vboxManage = Join-Path $env:ProgramFiles "Oracle\VirtualBox\VBoxManage.exe"
    if (Test-Path -LiteralPath $vboxManage) {
        $running = Invoke-DockerWorkProcess `
            -FileName $vboxManage `
            -Arguments @("list", "runningvms") `
            -TimeoutSeconds 20 `
            -AllowFailure
        if ($running.Success -and $running.Output) {
            $runningLines = @($running.Output -split "\r?\n" | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_)
            })
            $workVmLines = @($runningLines | Where-Object {
                $_ -match '^"WorkRDP"\s+\{'
            })
            if ($runningLines.Count -ne $workVmLines.Count) {
                throw "Another VirtualBox VM is running. Stop it before installing usbipd-win."
            }

            Write-DockerWorkLog "Saving WorkRDP cleanly so usbipd-win can update the USB monitor stack."
            [void](Invoke-DockerWorkProcess `
                -FileName $vboxManage `
                -Arguments @("controlvm", "WorkRDP", "savestate") `
                -TimeoutSeconds 300)
        }

        $stillRunning = Invoke-DockerWorkProcess `
            -FileName $vboxManage `
            -Arguments @("list", "runningvms") `
            -TimeoutSeconds 20 `
            -AllowFailure
        if ($stillRunning.Output) {
            throw "A VirtualBox VM remained running; usbipd-win installation was not attempted."
        }

        $vboxService = Get-Process -Name "VBoxSVC" -ErrorAction SilentlyContinue
        if ($vboxService) {
            Stop-Process -InputObject $vboxService -Force
            $vboxService | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        }
    }

    Write-DockerWorkLog "Installing official usbipd-win."
    [void](Invoke-DockerWorkProcess `
        -FileName "winget.exe" `
        -Arguments @(
            "install",
            "--id", "dorssel.usbipd-win",
            "--exact",
            "--source", "winget",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--silent",
            "--disable-interactivity"
        ) `
        -TimeoutSeconds 600)
    [void](Get-UsbipdPath)
}

function Install-DockerWorkDistro {
    if (Test-DockerWorkDistroInstalled) {
        return
    }

    Write-DockerWorkLog "Installing dedicated Ubuntu 24.04 WSL distribution."
    [void](Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @(
            "--install",
            "Ubuntu-24.04",
            "--name", $script:DockerWorkDistro,
            "--version", "2",
            "--no-launch",
            "--web-download"
        ) `
        -TimeoutSeconds 1800)

    if (-not (Test-DockerWorkDistroInstalled)) {
        throw "WSL did not register $script:DockerWorkDistro."
    }
}

function Install-DockerWorkLinuxPackages {
    Write-DockerWorkLog "Configuring systemd, firmware, and native Docker Engine."
    [void](Invoke-DockerWorkWsl `
        -Command @'
set -Eeuo pipefail
install -d -m 0755 /etc
printf '%s\n' \
    '[boot]' \
    'systemd=true' \
    '' \
    '[user]' \
    'default=root' > /etc/wsl.conf
'@ `
        -TimeoutSeconds 30)

    [void](Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--terminate", $script:DockerWorkDistro) `
        -TimeoutSeconds 60 `
        -AllowFailure)

    [void](Invoke-DockerWorkWsl `
        -Command @'
set -Eeuo pipefail
export DEBIAN_FRONTEND=noninteractive
rm -f /etc/apt/sources.list.d/docker.list
apt-get update
apt-get install -y --no-install-recommends \
    ca-certificates curl gnupg linux-firmware usbutils kmod udev zstd
firmware_dir=/lib/firmware/mediatek/mt7925
firmware_name=BT_RAM_CODE_MT7925_1_1_hdr.bin
if [[ ! -r "$firmware_dir/$firmware_name" \
    && -r "$firmware_dir/$firmware_name.zst" ]]; then
    zstd --decompress --keep \
        "$firmware_dir/$firmware_name.zst" \
        -o "$firmware_dir/$firmware_name"
fi
test -r "$firmware_dir/$firmware_name"
systemctl mask --now bluetooth.service 2>/dev/null || true
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
    -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
printf '%s\n' \
    "deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" \
    > /etc/apt/sources.list.d/docker.list
apt-get update
apt-get install -y --no-install-recommends \
    docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
docker version
docker compose version
'@ `
        -TimeoutSeconds 1800)
}

if (-not $NoElevate -and -not (Test-DockerWorkAdministrator)) {
    $arguments = @("-NoElevate")
    if ($SkipKernelBuild) {
        $arguments += "-SkipKernelBuild"
    }
    exit (Invoke-DockerWorkElevatedScript `
        -ScriptPath $PSCommandPath `
        -Arguments $arguments)
}
if (-not (Test-DockerWorkAdministrator)) {
    Write-Error "Setup must run as administrator."
    exit 1
}

$setupMarker = Join-Path $script:DockerWorkState "setup-complete.json"
$setupMarkerTemp = "$setupMarker.tmp"
$failed = $false
try {
    Clear-Content -LiteralPath $script:DockerWorkLog -ErrorAction SilentlyContinue
    Write-DockerWorkLog "Starting StayActive Docker work-browser setup."
    Remove-Item -LiteralPath $setupMarker, $setupMarkerTemp `
        -Force `
        -ErrorAction SilentlyContinue

    # The legacy return script uses the shared handoff mutex internally.
    # Complete it first, then normalize any prior Docker/WSL ownership back to
    # Windows so setup is safe to rerun from every steady or interrupted state.
    Release-VirtualBoxBluetooth
    Enter-DockerWorkBluetoothLock
    try {
        Detach-DockerWorkBluetooth
    }
    finally {
        Exit-DockerWorkBluetoothLock
    }

    Install-DockerWorkUsbipd
    Install-DockerWorkDistro
    Install-DockerWorkLinuxPackages

    if (-not $SkipKernelBuild) {
        $kernelCheck = Invoke-DockerWorkWsl `
            -Command "zgrep -qx 'CONFIG_BT_HCIBTUSB_MTK=y' /proc/config.gz" `
            -TimeoutSeconds 15 `
            -AllowFailure `
            -Quiet
        if (-not $kernelCheck.Success) {
            $kernelScript = Join-Path $PSScriptRoot "build-wsl-kernel.ps1"
            $kernelResult = Invoke-DockerWorkProcess `
                -FileName "powershell.exe" `
                -Arguments @(
                    "-NoProfile",
                    "-ExecutionPolicy", "Bypass",
                    "-File", $kernelScript,
                    "-NoElevate"
                ) `
                -TimeoutSeconds 7500 `
                -DisplayCommand "Build the pinned WSL MediaTek kernel" `
                -AllowFailure
            if (-not $kernelResult.Success) {
                throw "The required WSL MediaTek kernel build failed: $($kernelResult.Output)"
            }
        }
    }

    $kernelValidation = Invoke-DockerWorkWsl `
        -Command @'
set -Eeuo pipefail
zgrep -qx 'CONFIG_BT_HCIBTUSB_MTK=y' /proc/config.gz
modprobe vhci-hcd
modprobe btusb
test -r /lib/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin
test -r "/usr/lib/modules/$(uname -r)/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin"
'@ `
        -TimeoutSeconds 60

    [void](Invoke-DockerWorkWsl `
        -Command "systemctl enable --now docker" `
        -TimeoutSeconds 90)
    [void](Invoke-DockerWorkCompose `
        -Arguments @("build", "--pull") `
        -TimeoutSeconds 1800)

    [void](Invoke-DockerWorkWsl `
        -Command "docker run --rm hello-world >/tmp/stayactive-hello-world.txt && grep -q 'Hello from Docker' /tmp/stayactive-hello-world.txt" `
        -TimeoutSeconds 180)

    Enter-DockerWorkBluetoothLock
    try {
        Assert-VirtualBoxDoesNotOwnBluetooth
        Restore-DockerWorkHostBluetooth
        $bind = Invoke-DockerWorkUsbipd `
            -Arguments @(
                "bind",
                "--hardware-id", $script:BluetoothHardwareId
            ) `
            -TimeoutSeconds 90 `
            -AllowFailure
        if (-not $bind.Success -and
            $bind.Output -notmatch '(?i)already (?:shared|bound)') {
            throw "usbipd could not share 13d3:3602 without force mode. Force mode is intentionally forbidden because Windows could not reclaim Bluetooth after detach. $($bind.Output)"
        }
        Restore-DockerWorkHostBluetooth
    }
    finally {
        Exit-DockerWorkBluetoothLock
    }

    $setupState = [ordered]@{
        completedAt = (Get-Date).ToString("o")
        distro = $script:DockerWorkDistro
        hardwareId = $script:BluetoothHardwareId
        kernel = $kernelValidation.StdOut.Trim()
        image = "stayactive-work-browser:local"
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        $setupMarkerTemp,
        $setupState + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false)
    )
    Move-Item -LiteralPath $setupMarkerTemp `
        -Destination $setupMarker `
        -Force
    Write-DockerWorkLog "STAYACTIVE_DOCKER_SETUP_COMPLETE"
}
catch {
    $failed = $true
    Remove-Item -LiteralPath $setupMarker, $setupMarkerTemp `
        -Force `
        -ErrorAction SilentlyContinue
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
    try {
        if (Test-DockerWorkBluetoothLockHeld) {
            Detach-DockerWorkBluetooth
        }
    }
    catch {
        Write-DockerWorkLog "ERROR: Setup rollback could not verify Windows Bluetooth: $($_.Exception.Message)"
    }
}
finally {
    Exit-DockerWorkBluetoothLock
}

if ($failed) {
    exit 1
}
exit 0
