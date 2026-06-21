#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$BitsPerPixel = 32
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$Cache = Join-Path $Root ".cache"
$LogPath = Join-Path $Cache "force-bluetooth-passthrough-test.log"
$ScreenshotPath = Join-Path $Cache "force-bluetooth-passthrough-test.png"
$StartReadyScriptPath = Join-Path $PSScriptRoot "34-start-workvm-ready.ps1"
New-Item -ItemType Directory -Force -Path $Cache | Out-Null
Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue

function Write-Log {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $LogPath -Value $line
    Write-Host $line
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Join-ProcessArguments {
    param([string[]]$Arguments)

    return ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        } else {
            $_
        }
    }) -join " "
}

function Get-VBoxManagePath {
    $cmd = Get-Command VBoxManage -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    foreach ($path in @("$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe", "${env:ProgramFiles(x86)}\Oracle\VirtualBox\VBoxManage.exe")) {
        if (Test-Path -LiteralPath $path) { return $path }
    }

    throw "VirtualBox was not found."
}

function Invoke-Logged {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowFail
    )

    Write-Log "$FileName $($Arguments -join ' ')"
    $outputPath = Join-Path $Cache "force-process-output.txt"
    $errorPath = Join-Path $Cache "force-process-error.txt"
    Remove-Item -LiteralPath $outputPath,$errorPath -Force -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $FileName -ArgumentList (Join-ProcessArguments $Arguments) -NoNewWindow -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Log "  timed out after ${TimeoutSeconds}s; killing PID $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        if ($AllowFail) { return $false }
        throw "$FileName timed out."
    }

    $output = ""
    foreach ($path in @($outputPath, $errorPath)) {
        if (Test-Path -LiteralPath $path) {
            $output += Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        }
    }

    if ($output) {
        $output.TrimEnd() -split "\r?\n" | ForEach-Object { Write-Log "  $_" }
    }

    $process.Refresh()
    if (($null -ne $process.ExitCode -and $process.ExitCode -ne 0) -or
        $output -match "error:|Failed to|pending system reboot|not supported") {
        Write-Log "  exited with code $($process.ExitCode)"
        if ($AllowFail) { return $false }
        throw "$FileName failed with exit code $($process.ExitCode)."
    }

    return $true
}

function Get-VmState {
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
    if ($info -match 'VMState="([^"]+)"') { return $matches[1] }
    throw "Could not read VM state."
}

function Wait-VmState {
    param([string[]]$States, [int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = Get-VmState
        if ($state -in $States) { return $state }
        Start-Sleep -Seconds 2
    }
    return Get-VmState
}

function Remove-BluetoothFilters {
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
    $indexes = [regex]::Matches($info, 'USBFilterName(\d+)=') |
        ForEach-Object { [int]$_.Groups[1].Value - 1 } |
        Sort-Object -Descending -Unique
    foreach ($idx in $indexes) {
        [void](Invoke-Logged -FileName $script:VBoxManage -Arguments @("usbfilter", "remove", "$idx", "--target", $VMName) -AllowFail)
    }
}

function Add-BluetoothFilter {
    [void](Invoke-Logged -FileName $script:VBoxManage -Arguments @("usbfilter", "add", "0", "--target", $VMName, "--name", "Laptop MediaTek Bluetooth Adapter VIDPID", "--vendorid", "13d3", "--productid", "3602", "--active", "yes"))
    [void](Invoke-Logged -FileName $script:VBoxManage -Arguments @("modifyvm", $VMName, "--usb", "on", "--usbxhci", "on", "--boot1=disk", "--boot2=none", "--boot3=none", "--boot4=none"))
}

function Restart-BluetoothPnP {
    $devices = @(Get-PnpDevice | Where-Object { $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*") })
    foreach ($device in $devices) {
        Write-Log "PnP before: $($device.Status) $($device.Class) $($device.FriendlyName) $($device.InstanceId) problem=$($device.Problem)"
    }

    foreach ($device in $devices) {
        [void](Invoke-Logged -FileName "pnputil.exe" -Arguments @("/restart-device", $device.InstanceId) -TimeoutSeconds 30 -AllowFail)
    }

    Start-Sleep -Seconds 5
    foreach ($device in $devices) {
        [void](Invoke-Logged -FileName "pnputil.exe" -Arguments @("/disable-device", $device.InstanceId) -TimeoutSeconds 30 -AllowFail)
    }

    Start-Sleep -Seconds 5
    foreach ($device in $devices) {
        [void](Invoke-Logged -FileName "pnputil.exe" -Arguments @("/enable-device", $device.InstanceId) -TimeoutSeconds 30 -AllowFail)
    }

    [void](Invoke-Logged -FileName "pnputil.exe" -Arguments @("/scan-devices") -TimeoutSeconds 60 -AllowFail)
    Start-Sleep -Seconds 5

    Get-PnpDevice | Where-Object { $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*") } |
        ForEach-Object { Write-Log "PnP after: $($_.Status) $($_.Class) $($_.FriendlyName) $($_.InstanceId) problem=$($_.Problem)" }
}

function Restart-VirtualBoxUsbStack {
    foreach ($serviceName in @("VBoxSDS")) {
        [void](Invoke-Logged -FileName "sc.exe" -Arguments @("stop", $serviceName) -TimeoutSeconds 30 -AllowFail)
    }

    foreach ($driverName in @("VBoxUSBMon", "VBoxUSB")) {
        [void](Invoke-Logged -FileName "sc.exe" -Arguments @("stop", $driverName) -TimeoutSeconds 30 -AllowFail)
    }

    Start-Sleep -Seconds 5

    foreach ($driverName in @("VBoxUSBMon", "VBoxUSB")) {
        [void](Invoke-Logged -FileName "sc.exe" -Arguments @("start", $driverName) -TimeoutSeconds 30 -AllowFail)
    }

    foreach ($serviceName in @("VBoxSDS")) {
        [void](Invoke-Logged -FileName "sc.exe" -Arguments @("start", $serviceName) -TimeoutSeconds 30 -AllowFail)
    }

    Start-Sleep -Seconds 5
}

function Get-BluetoothUsbUuid {
    $usb = (& $script:VBoxManage list usbhost) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $usb
    foreach ($block in ($usb -split "(?:\r?\n){2,}")) {
        if ($block -match 'VendorId:\s+0x13d3' -and $block -match 'ProductId:\s+0x3602' -and $block -match 'UUID:\s+([0-9a-fA-F-]+)') {
            return $matches[1]
        }
    }
    throw "MediaTek Bluetooth USB device was not found in VirtualBox USB host list."
}

function Test-BluetoothAttached {
    $info = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $info
    $match = [regex]::Match($info, 'Currently attached USB devices:\s*(?<devices>[\s\S]*?)(?:\r?\nBandwidth groups:|\r?\nShared folders:|\r?\nVRDE:|\z)')
    return $match.Success -and $match.Groups["devices"].Value -notmatch "<none>" -and $match.Groups["devices"].Value -match "13d3|3602|IMC|MediaTek|Wireless"
}

function Ensure-Display {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $StartReadyScriptPath -VMName $VMName -Width $Width -Height $Height -BitsPerPixel $BitsPerPixel 2>&1 |
        ForEach-Object { Write-Log "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "Start-ready script failed with exit code $LASTEXITCODE." }
}

if (-not (Test-IsAdmin)) {
    throw "This script must run as administrator."
}

$script:VBoxManage = Get-VBoxManagePath
Write-Log "Starting force Bluetooth passthrough test."

$state = Get-VmState
Write-Log "Initial VM state: $state"
if ($state -eq "running") {
    [void](Invoke-Logged -FileName $script:VBoxManage -Arguments @("controlvm", $VMName, "savestate") -TimeoutSeconds 120)
    $state = Wait-VmState -States @("saved") -TimeoutSeconds 120
    Write-Log "VM state after save: $state"
}

Remove-BluetoothFilters

foreach ($name in @("VBoxManage", "VBoxSVC")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Log "Stopping $name PID $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 5

Restart-VirtualBoxUsbStack
Restart-BluetoothPnP
Add-BluetoothFilter
Ensure-Display

for ($attempt = 1; $attempt -le 6; $attempt++) {
    if (Test-BluetoothAttached) {
        Write-Log "Bluetooth is attached to WorkRDP."
        [void](Invoke-Logged -FileName $script:VBoxManage -Arguments @("controlvm", $VMName, "screenshotpng", $ScreenshotPath) -TimeoutSeconds 30 -AllowFail)
        Write-Log "Screenshot: $ScreenshotPath"
        exit 0
    }

    $uuid = Get-BluetoothUsbUuid
    Write-Log "Manual attach attempt ${attempt}: $uuid"
    [void](Invoke-Logged -FileName $script:VBoxManage -Arguments @("controlvm", $VMName, "usbattach", $uuid) -TimeoutSeconds 30 -AllowFail)
    Start-Sleep -Seconds 6
}

Ensure-Display
throw "Bluetooth did not attach to WorkRDP. See $LogPath."
