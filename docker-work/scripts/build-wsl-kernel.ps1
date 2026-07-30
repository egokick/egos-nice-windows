#requires -Version 5.1

param(
    [switch]$NoElevate,
    [switch]$UseExistingArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

$kernelTag = "linux-msft-wsl-6.18.33.2"
$kernelCommit = "c21a03b2943d147c280bdf32530d4fe6badfd6bd"
$kernelRelease = "6.18.33.2-microsoft-standard-WSL2-mtk"
$artifactRoot = Join-Path $env:LOCALAPPDATA "StayActive\wsl-kernel\$kernelRelease"
$kernelPath = Join-Path $artifactRoot "bzImage"
$modulesPath = Join-Path $artifactRoot "modules.vhdx"
$wslConfigPath = Join-Path $env:USERPROFILE ".wslconfig"
$wslConfigBackup = Join-Path $script:DockerWorkState "wslconfig.before-stayactive"
$wslConfigAbsentMarker = Join-Path $script:DockerWorkState "wslconfig.was-absent"

function Set-StayActiveWslKernelConfiguration {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

    if (-not (Test-Path -LiteralPath $wslConfigBackup) -and
        -not (Test-Path -LiteralPath $wslConfigAbsentMarker)) {
        if (Test-Path -LiteralPath $wslConfigPath) {
            Copy-Item -LiteralPath $wslConfigPath -Destination $wslConfigBackup
        }
        else {
            New-Item -ItemType File -Path $wslConfigAbsentMarker | Out-Null
        }
    }

    # Do not assign an empty generic list through PowerShell's output pipeline:
    # it enumerates to no objects and becomes $null under StrictMode.
    $lines = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $wslConfigPath) {
        foreach ($existingLine in Get-Content -LiteralPath $wslConfigPath) {
            $lines.Add([string]$existingLine)
        }
    }

    $sectionStart = -1
    $sectionEnd = $lines.Count
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s*\[wsl2\]\s*$') {
            $sectionStart = $index
            for ($next = $index + 1; $next -lt $lines.Count; $next++) {
                if ($lines[$next] -match '^\s*\[[^\]]+\]\s*$') {
                    $sectionEnd = $next
                    break
                }
            }
            break
        }
    }

    if ($sectionStart -lt 0) {
        if ($lines.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1])) {
            $lines.Add("")
        }
        $sectionStart = $lines.Count
        $lines.Add("[wsl2]")
        $sectionEnd = $lines.Count
    }

    for ($index = $sectionEnd - 1; $index -gt $sectionStart; $index--) {
        if ($lines[$index] -match '^\s*(kernel|kernelModules)\s*=') {
            $lines.RemoveAt($index)
        }
    }

    $escapedKernel = $kernelPath.Replace('\', '\\')
    $escapedModules = $modulesPath.Replace('\', '\\')
    $lines.Insert($sectionStart + 1, "kernelModules=$escapedModules")
    $lines.Insert($sectionStart + 1, "kernel=$escapedKernel")
    [IO.File]::WriteAllLines($wslConfigPath, $lines, [Text.UTF8Encoding]::new($false))
}

function Restore-PreStayActiveWslKernelConfiguration {
    if (Test-Path -LiteralPath $wslConfigBackup) {
        Copy-Item -LiteralPath $wslConfigBackup -Destination $wslConfigPath -Force
    }
    elseif (Test-Path -LiteralPath $wslConfigAbsentMarker) {
        Remove-Item -LiteralPath $wslConfigPath -Force -ErrorAction SilentlyContinue
    }
    else {
        return
    }

    [void](Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--shutdown") `
        -TimeoutSeconds 90 `
        -AllowFailure)
    Write-DockerWorkLog "Rolled back the WSL kernel configuration after validation failed."
}

if (-not $NoElevate -and -not (Test-DockerWorkAdministrator)) {
    $arguments = @("-NoElevate")
    if ($UseExistingArtifacts) {
        $arguments += "-UseExistingArtifacts"
    }
    exit (Invoke-DockerWorkElevatedScript `
        -ScriptPath $PSCommandPath `
        -Arguments $arguments)
}
if (-not (Test-DockerWorkAdministrator)) {
    Write-Error "Kernel setup must run as administrator."
    exit 1
}
if (-not (Test-DockerWorkDistroInstalled)) {
    Write-Error "Install the StayActiveDocker distribution before building its WSL kernel."
    exit 1
}

$failed = $false
$configurationApplied = $false
try {
    Write-DockerWorkLog "Building pinned Microsoft WSL kernel $kernelTag with MediaTek Bluetooth support."
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $artifactWslPath = (Invoke-DockerWorkWsl `
        -Command "wslpath -a $(ConvertTo-BashSingleQuoted -Value $artifactRoot)" `
        -TimeoutSeconds 15 `
        -Quiet).StdOut.Trim()

    $buildScript = @'
set -Eeuo pipefail

tag='__KERNEL_TAG__'
expected_commit='__KERNEL_COMMIT__'
expected_release='__KERNEL_RELEASE__'
artifact_dir='__ARTIFACT_DIR__'
source_root='/opt/stayactive-wsl-kernel'
source_dir="$source_root/source"

export DEBIAN_FRONTEND=noninteractive
# Microsoft's release tag is lightweight. An explicitly empty build-time
# LOCALVERSION prevents Linux's setlocalversion helper from adding a misleading
# "+" while CONFIG_LOCALVERSION supplies our stable suffix.
export LOCALVERSION=''
apt-get update
apt-get install -y --no-install-recommends \
    git ca-certificates build-essential flex bison dwarves libssl-dev \
    libelf-dev cpio qemu-utils e2fsprogs util-linux

install -d -m 0755 "$source_root" "$artifact_dir"
if [[ ! -d "$source_dir/.git" ]]; then
    git clone --depth 1 --branch "$tag" \
        https://github.com/microsoft/WSL2-Linux-Kernel.git "$source_dir"
fi

cd "$source_dir"
test "$(git rev-parse HEAD)" = "$expected_commit"
test "$(git describe --tags --exact-match)" = "$tag"

cp arch/x86/configs/config-wsl .config
./scripts/config --file .config --enable BT_HCIBTUSB_MTK
./scripts/config --file .config --enable FW_LOADER_COMPRESS_ZSTD
./scripts/config --file .config --disable LOCALVERSION_AUTO
./scripts/config --file .config \
    --set-str LOCALVERSION '-microsoft-standard-WSL2-mtk'
make KCONFIG_CONFIG=.config olddefconfig
test "$(./scripts/config --file .config --state BT_HCIBTUSB_MTK)" = y
test "$(./scripts/config --file .config --state FW_LOADER_COMPRESS_ZSTD)" = y
test "$(./scripts/config --file .config --state LOCALVERSION_AUTO)" = n
test "$(./scripts/config --file .config --state USBIP_VHCI_HCD)" = m
release="$(make -s KCONFIG_CONFIG=.config kernelrelease)"
test "$release" = "$expected_release"

make -j"$(nproc)" KCONFIG_CONFIG=.config

build_root="$(mktemp -d /opt/stayactive-wsl-build.XXXXXX)"
stage="$build_root/modules"
raw_image="$build_root/modules.raw"
mount_dir="$build_root/mount"
loop_device=''
mounted=0
modules_tmp="$artifact_dir/.modules.vhdx.new.$$"
kernel_tmp="$artifact_dir/.bzImage.new.$$"

cleanup() {
    set +e
    if [[ "$mounted" = 1 ]]; then
        umount "$mount_dir"
    fi
    if [[ -n "$loop_device" ]]; then
        losetup -d "$loop_device"
    fi
    [[ -z "$modules_tmp" ]] || rm -f "$modules_tmp"
    [[ -z "$kernel_tmp" ]] || rm -f "$kernel_tmp"
    rm -rf "$build_root"
}
trap cleanup EXIT

install -d -m 0755 "$stage" "$mount_dir"
make KCONFIG_CONFIG=.config INSTALL_MOD_PATH="$stage" modules_install
test -r "$stage/lib/modules/$release/kernel/drivers/bluetooth/btusb.ko"
test -r "$stage/lib/modules/$release/kernel/drivers/bluetooth/btmtk.ko"
test -r "$stage/lib/modules/$release/kernel/drivers/usb/usbip/vhci-hcd.ko"
firmware_relative='mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin'
test -r "/lib/firmware/$firmware_relative"
install -d -m 0755 "$stage/firmware/mediatek/mt7925"
install -m 0644 \
    "/lib/firmware/$firmware_relative" \
    "$stage/firmware/$firmware_relative"

module_bytes="$(du -bs "$stage" | awk '{print $1}')"
image_bytes=$((((module_bytes + 268435456 + 511) / 512) * 512))
truncate -s "$image_bytes" "$raw_image"
loop_device="$(losetup --find --show "$raw_image")"
mkfs.ext4 -q "$loop_device"
mount "$loop_device" "$mount_dir"
mounted=1
cp -a "$stage/lib/modules/$release/." "$mount_dir/"
cp -a "$stage/firmware" "$mount_dir/"
sync
umount "$mount_dir"
mounted=0
losetup -d "$loop_device"
loop_device=''

rm -f "$modules_tmp" "$kernel_tmp"
qemu-img convert -f raw -O vhdx "$raw_image" "$modules_tmp"
qemu-img check -f vhdx "$modules_tmp"
cp arch/x86/boot/bzImage "$kernel_tmp"
mv -f "$modules_tmp" "$artifact_dir/modules.vhdx"
modules_tmp=''
mv -f "$kernel_tmp" "$artifact_dir/bzImage"
kernel_tmp=''
cp .config "$artifact_dir/config"
cp System.map "$artifact_dir/System.map"
printf '%s\n%s\n' "$tag" "$expected_commit" > "$artifact_dir/source.txt"
(
    cd "$artifact_dir"
    sha256sum bzImage modules.vhdx config System.map source.txt > SHA256SUMS
)
test -s "$artifact_dir/bzImage"
test -s "$artifact_dir/modules.vhdx"
# Keep the pinned checkout for provenance and future rebuilds, but discard
# several gigabytes of reproducible object files after the artifacts are safe.
make KCONFIG_CONFIG=.config clean
'@
    $buildScript = $buildScript.
        Replace("__KERNEL_TAG__", $kernelTag).
        Replace("__KERNEL_COMMIT__", $kernelCommit).
        Replace("__KERNEL_RELEASE__", $kernelRelease).
        Replace("__ARTIFACT_DIR__", $artifactWslPath.Replace("'", "'""'""'"))

    if ($UseExistingArtifacts) {
        foreach ($requiredArtifact in @(
            $kernelPath,
            $modulesPath,
            (Join-Path $artifactRoot "config"),
            (Join-Path $artifactRoot "System.map"),
            (Join-Path $artifactRoot "source.txt"),
            (Join-Path $artifactRoot "SHA256SUMS")
        )) {
            if (-not (Test-Path -LiteralPath $requiredArtifact)) {
                throw "Existing kernel artifact is missing: $requiredArtifact"
            }
        }

        $sourceInfo = @(Get-Content -LiteralPath (Join-Path $artifactRoot "source.txt"))
        if ($sourceInfo.Count -ne 2 -or
            $sourceInfo[0] -ne $kernelTag -or
            $sourceInfo[1] -ne $kernelCommit) {
            throw "Existing kernel artifacts do not match the pinned source."
        }

        $artifactConfig = Get-Content `
            -LiteralPath (Join-Path $artifactRoot "config") `
            -Raw
        foreach ($requiredConfig in @(
            'CONFIG_BT_HCIBTUSB_MTK=y',
            'CONFIG_FW_LOADER_COMPRESS_ZSTD=y',
            'CONFIG_USBIP_VHCI_HCD=m',
            'CONFIG_LOCALVERSION="-microsoft-standard-WSL2-mtk"',
            '# CONFIG_LOCALVERSION_AUTO is not set'
        )) {
            if ($artifactConfig -notmatch "(?m)^$([regex]::Escape($requiredConfig))`$") {
                throw "Existing kernel config is missing '$requiredConfig'."
            }
        }

        $artifactVerification = Invoke-DockerWorkWsl `
            -Command @"
set -Eeuo pipefail
cd $(ConvertTo-BashSingleQuoted -Value $artifactWslPath)
sha256sum --check SHA256SUMS
qemu-img check -f vhdx modules.vhdx
"@ `
            -TimeoutSeconds 300 `
            -AllowFailure `
            -Quiet
        if (-not $artifactVerification.Success) {
            throw "Existing kernel artifact verification failed: $($artifactVerification.Output)"
        }
        Write-DockerWorkLog "Reusing verified pinned kernel artifacts."
        [void](Invoke-DockerWorkWsl `
            -Command "if test -f /opt/stayactive-wsl-kernel/source/Makefile; then make -C /opt/stayactive-wsl-kernel/source KCONFIG_CONFIG=.config clean; fi" `
            -TimeoutSeconds 600 `
            -Quiet)
    }
    else {
        [void](Invoke-DockerWorkWsl `
            -Command $buildScript `
            -TimeoutSeconds 7200 `
            -Quiet)
    }

    if (-not (Test-Path -LiteralPath $kernelPath) -or
        -not (Test-Path -LiteralPath $modulesPath)) {
        throw "The custom-kernel artifacts were not copied to Windows."
    }

    Set-StayActiveWslKernelConfiguration
    $configurationApplied = $true

    if (Test-DockerWorkUsbipdInstalled) {
        [void](Invoke-DockerWorkUsbipd `
            -Arguments @("detach", "--hardware-id", $script:BluetoothHardwareId) `
            -TimeoutSeconds 45 `
            -AllowFailure)
    }
    [void](Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--shutdown") `
        -TimeoutSeconds 90)

    $validation = Invoke-DockerWorkWsl `
        -Command @"
set -Eeuo pipefail
test "`$(uname -r)" = '$kernelRelease'
zgrep -qx 'CONFIG_BT_HCIBTUSB_MTK=y' /proc/config.gz
modinfo -F filename btusb
modinfo -F filename btmtk
modinfo -F filename vhci-hcd
modprobe vhci-hcd
modprobe btusb
test -r "/usr/lib/modules/$kernelRelease/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin"
"@ `
        -TimeoutSeconds 90
    Write-DockerWorkLog "Custom WSL kernel validated: $($validation.StdOut -replace '\r?\n', ', ')."
}
catch {
    $failed = $true
    Write-DockerWorkLog "ERROR: $($_.Exception.Message)"
    if ($configurationApplied) {
        try {
            Restore-PreStayActiveWslKernelConfiguration
        }
        catch {
            Write-DockerWorkLog "ERROR: WSL kernel rollback failed: $($_.Exception.Message)"
        }
    }
}

if ($failed) {
    exit 1
}
exit 0
