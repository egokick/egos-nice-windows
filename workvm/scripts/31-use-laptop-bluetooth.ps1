#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [string]$VendorId = "0x13d3",
    [string]$ProductId = "0x3602",
    [string]$FilterName = "Laptop MediaTek Bluetooth Adapter"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

    return $null
}

function Invoke-VBox {
    param([string[]]$Arguments)

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:VBoxManage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

$script:VBoxManage = Get-VBoxManagePath
if (-not $script:VBoxManage) {
    Write-Error "VirtualBox was not found. Run scripts\10-install-virtualbox.ps1 first."
}

$vmList = & $script:VBoxManage list vms
$exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
if (-not $exists) {
    Write-Error "VM '$VMName' does not exist yet. Create it first with scripts\20-create-vm.ps1 -IsoPath <real ISO path>."
}

$info = & $script:VBoxManage showvminfo $VMName --machinereadable
if ($info -match ('USBFilterName\d+="' + [regex]::Escape($FilterName) + '"')) {
    Write-Host "USB filter '$FilterName' already exists on VM '$VMName'."
    exit 0
}

Write-Host "VirtualBox USB devices currently visible on the host:"
Write-Host ""
& $script:VBoxManage list usbhost
Write-Host ""

Write-Host "Adding a VM USB filter for the laptop Bluetooth radio (${VendorId}:${ProductId})."
Write-Host "This is the no-dongle path: when the VM captures it, host Windows Bluetooth will stop working until the VM releases it."
Write-Host ""

Invoke-VBox @(
    "usbfilter", "add", "0",
    "--target", $VMName,
    "--name", $FilterName,
    "--action", "hold",
    "--active", "yes",
    "--vendorid", $VendorId,
    "--productid", $ProductId
)

Write-Host ""
Write-Host "Added Bluetooth filter to VM '$VMName'."
Write-Host "Next:"
Write-Host "  1. Fully power off the VM if it is running."
Write-Host "  2. Start the VM."
Write-Host "  3. In the VM window, check Devices > USB and confirm the MediaTek/IMC Networks Bluetooth device is selected."
Write-Host "  4. Inside the guest, run W:\guest\verify-bluetooth-passkeys.ps1 after Guest Additions/shared folder is working."
Write-Host ""
Write-Host "If capture fails, temporarily turn off Bluetooth on the host or disable the MediaTek Bluetooth Adapter in host Device Manager, then start the VM again."
