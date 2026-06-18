#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$Downloads = Join-Path $Root "downloads"
$ExtPackPath = Join-Path $Downloads "Oracle_VirtualBox_Extension_Pack-7.2.8.vbox-extpack"
$ExtPackUrl = "https://download.virtualbox.org/virtualbox/7.2.8/Oracle_VirtualBox_Extension_Pack-7.2.8.vbox-extpack"
$ExtPackLicenseHash = "eb31505e56e9b4d0fbca139104da41ac6f6b98f8e78968bdf01b1f3da3c4f9ae"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Get-VBoxManagePath {
    $cmd = Get-Command VBoxManage -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe",
        "${env:ProgramFiles(x86)}\Oracle\VirtualBox\VBoxManage.exe"
    )

    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) { return $path }
    }

    throw "VirtualBox was not found. Run scripts\10-install-virtualbox.ps1 first."
}

function Invoke-VBox {
    param([string[]]$Arguments)

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:VBoxManage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Ensure-ExtensionPack {
    $extpacks = & $script:VBoxManage list extpacks
    if ($extpacks -match "Oracle VirtualBox Extension Pack" -and $extpacks -match "Version:\s+7\.2\.8") {
        Write-Host "VirtualBox Extension Pack 7.2.8 is already installed."
        return
    }

    New-Item -ItemType Directory -Force -Path $Downloads | Out-Null
    if (-not (Test-Path -LiteralPath $ExtPackPath)) {
        Write-Host "Downloading VirtualBox Extension Pack 7.2.8..."
        Invoke-WebRequest -Uri $ExtPackUrl -OutFile $ExtPackPath
    }

    Invoke-VBox @("extpack", "install", "--replace", "--accept-license=$ExtPackLicenseHash", $ExtPackPath)
}

function Get-UsbHostBlocks {
    $raw = (& $script:VBoxManage list usbhost) -join [Environment]::NewLine
    return ($raw -split "(?:\r?\n){2,}") | Where-Object { $_ -match "UUID:" }
}

function Get-MediaTekUsbUuid {
    $blocks = Get-UsbHostBlocks
    foreach ($block in $blocks) {
        if ($block -match "VendorId:\s+0x13d3" -and
            $block -match "ProductId:\s+0x3602" -and
            ($block -match "Manufacturer:\s+MediaTek" -or $block -match "Product:\s+Wireless_Device")) {
            $match = [regex]::Match($block, "UUID:\s+([0-9a-fA-F-]+)")
            if ($match.Success) { return $match.Groups[1].Value }
        }
    }

    throw "Could not find the host MediaTek Bluetooth USB device 13d3:3602 in VBoxManage list usbhost."
}

function Ensure-SpecificUsbFilter {
    $info = & $script:VBoxManage showvminfo $VMName --machinereadable
    $existing = [regex]::Match(($info -join "`n"), 'USBFilterName(\d+)="Laptop MediaTek Bluetooth Adapter"')

    if ($existing.Success) {
        $index = [int]$existing.Groups[1].Value - 1
        Invoke-VBox @(
            "usbfilter", "modify", "$index",
            "--target", $VMName,
            "--name", "Laptop MediaTek Bluetooth Adapter",
            "--active", "yes",
            "--vendorid", "13d3",
            "--productid", "3602",
            "--manufacturer", "MediaTek Inc.",
            "--product", "Wireless_Device",
            "--serialnumber", "000000000"
        )
    }
    else {
        Invoke-VBox @(
            "usbfilter", "add", "0",
            "--target", $VMName,
            "--name", "Laptop MediaTek Bluetooth Adapter",
            "--action", "hold",
            "--active", "yes",
            "--vendorid", "13d3",
            "--productid", "3602",
            "--manufacturer", "MediaTek Inc.",
            "--product", "Wireless_Device",
            "--serialnumber", "000000000"
        )
    }
}

function Disable-HostBluetoothAdapter {
    $targets = Get-PnpDevice |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*" -or
            ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "MediaTek Bluetooth Adapter")
        }

    if (-not $targets) {
        Write-Warning "Could not find the host MediaTek Bluetooth adapter PnP interface to disable."
        return
    }

    foreach ($device in $targets) {
        if ($device.Status -ne "Disabled") {
            Write-Host "Disabling host Bluetooth adapter: $($device.FriendlyName)"
            Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false
        }
        else {
            Write-Host "Host Bluetooth adapter is already disabled: $($device.FriendlyName)"
        }
    }
}

function Ensure-VmRunning {
    $stateLine = (& $script:VBoxManage showvminfo $VMName --machinereadable | Select-String '^VMState=').ToString()
    if ($stateLine -match 'VMState="running"') {
        return
    }

    Write-Host "Starting VM '$VMName'..."
    Invoke-VBox @("startvm", $VMName, "--type", "gui")
    Start-Sleep -Seconds 10
}

function Attach-BluetoothUsb {
    $uuid = Get-MediaTekUsbUuid
    Write-Host "Attaching MediaTek Bluetooth USB device to VM '$VMName': $uuid"
    & $script:VBoxManage controlvm $VMName usbattach $uuid
    if ($LASTEXITCODE -ne 0) {
        throw @"
VirtualBox still could not attach the laptop Bluetooth adapter.

Try rebooting the host once, then run this script again. The host adapter has been targeted correctly, but VirtualBox is still reporting the radio as busy.
"@
    }

    Start-Sleep -Seconds 3
    & $script:VBoxManage showvminfo $VMName | Select-String -Pattern "Currently attached USB devices|13d3|3602|MediaTek|Wireless" -Context 0,4
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-NoElevate"
    )
    Write-Host "Requesting administrator elevation..."
    Start-Process -FilePath "powershell.exe" -ArgumentList $args -Verb RunAs
    exit 0
}

if (-not (Test-IsAdmin)) {
    throw "This script must run as administrator so it can disable the host Bluetooth adapter."
}

$script:VBoxManage = Get-VBoxManagePath

Ensure-ExtensionPack
Ensure-SpecificUsbFilter
Disable-HostBluetoothAdapter
Start-Sleep -Seconds 3
Ensure-VmRunning
Attach-BluetoothUsb

Write-Host ""
Write-Host "Laptop Bluetooth handoff attempted. Inside the guest, run or open workvm-bt-deep-check.txt again after Windows finishes detecting devices."
Write-Host "Host Bluetooth will remain unavailable while the adapter is disabled or attached to the VM."
