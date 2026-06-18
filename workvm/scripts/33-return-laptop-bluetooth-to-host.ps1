#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

    throw "VirtualBox was not found."
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
    throw "This script must run as administrator so it can re-enable the host Bluetooth adapter."
}

$vbox = Get-VBoxManagePath

$usbHost = (& $vbox list usbhost) -join "`n"
$bluetoothUuid = $null
$usbHost -split "(?:\r?\n){2,}" |
    Where-Object { $_ -match 'VendorId:\s+0x13d3' -and $_ -match 'ProductId:\s+0x3602' } |
    Select-Object -First 1 |
    ForEach-Object {
        if ($_ -match "UUID:\s+([0-9a-fA-F-]+)") {
            $bluetoothUuid = $matches[1]
        }
    }

if ($bluetoothUuid) {
    try {
        Write-Host "Detaching MediaTek Bluetooth adapter from VM: $bluetoothUuid"
        & $vbox controlvm $VMName usbdetach $bluetoothUuid
    }
    catch {
        Write-Warning "Could not detach the VM Bluetooth USB device: $($_.Exception.Message)"
    }
}

try {
    & $vbox usbfilter modify 0 --target $VMName --active no
}
catch {
    Write-Warning "Could not disable the VM USB filter: $($_.Exception.Message)"
}

$devices = Get-PnpDevice |
    Where-Object {
        $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*" -or
        ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "MediaTek Bluetooth Adapter")
    }

foreach ($device in $devices) {
    if ($device.Status -eq "Disabled") {
        Write-Host "Re-enabling host Bluetooth adapter: $($device.FriendlyName)"
        Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false
    }
}

pnputil /scan-devices | Out-Null

Write-Host "Host Bluetooth return step complete."
