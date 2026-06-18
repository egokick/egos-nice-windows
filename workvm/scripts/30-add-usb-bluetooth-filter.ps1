#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [string]$VendorId,
    [string]$ProductId,
    [string]$FilterName = "Dedicated Bluetooth dongle"
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

function Normalize-HexId {
    param([string]$Value)
    $trimmed = $Value.Trim()
    if ($trimmed -match "^0x[0-9a-fA-F]{4}$") { return $trimmed.ToLowerInvariant() }
    if ($trimmed -match "^[0-9a-fA-F]{4}$") { return "0x$($trimmed.ToLowerInvariant())" }
    throw "Expected a 4-digit USB hex id like 0x0a12 or 0a12. Got '$Value'."
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

if (-not $VendorId -or -not $ProductId) {
    Write-Host "Attached USB devices from VirtualBox:"
    Write-Host ""
    & $script:VBoxManage list usbhost
    Write-Host ""
    Write-Host "Find the dedicated USB Bluetooth dongle block, then run:"
    Write-Host "  .\scripts\30-add-usb-bluetooth-filter.ps1 -VendorId 0x1234 -ProductId 0xabcd"
    Write-Host ""
    Write-Host "Tip: use a cheap external USB Bluetooth dongle for the VM. Do not use the laptop's built-in Bluetooth adapter unless you want the host to lose it."
    exit 0
}

$vendor = Normalize-HexId $VendorId
$product = Normalize-HexId $ProductId

Invoke-VBox @(
    "usbfilter", "add", "0",
    "--target", $VMName,
    "--name", $FilterName,
    "--action", "hold",
    "--active", "yes",
    "--vendorid", $vendor,
    "--productid", $product
)

Write-Host ""
Write-Host "USB filter added for $FilterName (${vendor}:${product})."
Write-Host "Unplug and replug the Bluetooth dongle. When the VM is running, the guest should capture it."
Write-Host "Verify inside the guest with Device Manager or guest\verify-bluetooth-passkeys.ps1."
