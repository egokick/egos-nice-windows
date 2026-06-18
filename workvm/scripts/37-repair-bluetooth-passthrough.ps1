#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$LogPath = Join-Path $Root ".cache\bluetooth-passthrough-repair.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null

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

function Invoke-VBox {
    param([string[]]$Arguments, [switch]$AllowFail)

    Write-Log "VBoxManage $($Arguments -join ' ')"
    & $script:VBoxManage @Arguments 2>&1 | ForEach-Object { Write-Log "  $_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Invoke-VBoxTimed {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 20
    )

    Write-Log "VBoxManage $($Arguments -join ' ')"
    $outputPath = Join-Path $Root ".cache\vboxmanage-timed-output.txt"
    $errorPath = Join-Path $Root ".cache\vboxmanage-timed-error.txt"
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $errorPath -Force -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $script:VBoxManage -ArgumentList $Arguments -NoNewWindow -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Log "  timed out after ${TimeoutSeconds}s; killing VBoxManage PID $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        return $false
    }

    if (Test-Path -LiteralPath $outputPath) {
        Get-Content -LiteralPath $outputPath | ForEach-Object { Write-Log "  $_" }
    }
    if (Test-Path -LiteralPath $errorPath) {
        Get-Content -LiteralPath $errorPath | ForEach-Object { Write-Log "  $_" }
    }

    if ($process.ExitCode -ne 0) {
        Write-Log "  exited with code $($process.ExitCode)"
        return $false
    }

    return $true
}

function Stop-VirtualBoxProcesses {
    Write-Log "Force-clearing VirtualBox helper processes."
    foreach ($name in @("VBoxManage", "VirtualBoxVM", "VBoxHeadless", "VBoxSVC")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Log "Stopping $name PID $($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Start-Sleep -Seconds 4
}

function Get-VmState {
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
    if ($info -match 'VMState="([^"]+)"') { return $matches[1] }
    throw "Could not read VM state."
}

function Get-WorkVmProcessIds {
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -match 'VirtualBoxVM|VBoxHeadless' -and (
                $_.CommandLine -match [regex]::Escape($VMName) -or
                -not $_.CommandLine
            )
        } |
        Select-Object -ExpandProperty ProcessId
}

function Stop-WorkVm {
    $state = Get-VmState
    Write-Log "VM state before stop: $state"
    if ($state -in @("running", "stuck", "stopping", "starting")) {
        [void](Invoke-VBoxTimed @("controlvm", $VMName, "poweroff") -TimeoutSeconds 20)
        Start-Sleep -Seconds 5
    }

    Stop-VirtualBoxProcesses

    $finalState = Get-VmState
    Write-Log "VM state after stop/clear: $finalState"
    if ($finalState -notin @("poweroff", "aborted", "saved")) {
        throw "Could not clear WorkVM state. Current state: $finalState"
    }
}

function Get-BluetoothUsbBlocks {
    $usb = (& $script:VBoxManage list usbhost) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $usb
    $usb -split "(?:\r?\n){2,}" |
        Where-Object { $_ -match 'VendorId:\s+0x13d3' -and $_ -match 'ProductId:\s+0x3602' }
}

function Get-BluetoothUsbUuid {
    $blocks = @(Get-BluetoothUsbBlocks)
    foreach ($block in $blocks) {
        if ($block -match "UUID:\s+([0-9a-fA-F-]+)") {
            return $matches[1]
        }
    }

    throw "Could not find MediaTek Bluetooth USB device 13d3:3602 in VirtualBox USB host list."
}

function Test-BluetoothUsbAttached {
    $vmInfo = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $vmInfo
    return ($vmInfo -match 'Currently attached USB devices:[\s\S]*13d3' -or
        $vmInfo -match 'Currently attached USB devices:[\s\S]*3602' -or
        $vmInfo -match 'Currently attached USB devices:[\s\S]*Wireless_Device' -or
        $vmInfo -match 'Currently attached USB devices:[\s\S]*MediaTek')
}

function Restart-HostBluetoothUsb {
    $devices = Get-PnpDevice |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or
            ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*")
        }

    foreach ($device in $devices) {
        Write-Log "PnP before: $($device.Status) $($device.Class) $($device.FriendlyName) $($device.InstanceId)"
    }

    foreach ($device in $devices) {
        Write-Log "Disabling $($device.InstanceId)"
        Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 4

    foreach ($device in $devices) {
        Write-Log "Enabling $($device.InstanceId)"
        Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 8

    Get-PnpDevice |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or
            ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*")
        } |
        ForEach-Object { Write-Log "PnP after: $($_.Status) $($_.Class) $($_.FriendlyName) $($_.InstanceId)" }
}

function Set-BroadBluetoothFilter {
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
    $indexes = [regex]::Matches($info, 'USBFilterName(\d+)=') |
        ForEach-Object { [int]$_.Groups[1].Value - 1 } |
        Sort-Object -Descending -Unique

    foreach ($idx in $indexes) {
        Invoke-VBox @("usbfilter", "remove", "$idx", "--target", $VMName) -AllowFail
    }

    Invoke-VBox @("usbfilter", "add", "0", "--target", $VMName, "--name", "Laptop MediaTek Bluetooth Adapter VIDPID", "--vendorid", "13d3", "--productid", "3602", "--active", "yes")
    Invoke-VBox @("modifyvm", $VMName, "--usb", "on", "--usbxhci", "on", "--boot1=disk", "--boot2=none", "--boot3=none", "--boot4=none")
}

function Start-And-AttachBluetooth {
    $state = Get-VmState
    if ($state -ne "running") {
        Invoke-VBox @("startvm", $VMName, "--type", "gui")
        Start-Sleep -Seconds 25
    } else {
        Write-Log "VM is already running."
    }

    $uuid = Get-BluetoothUsbUuid

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Write-Log "Attaching MediaTek Bluetooth USB device attempt ${attempt}: $uuid"
        Invoke-VBox @("controlvm", $VMName, "usbdetach", $uuid) -AllowFail
        Start-Sleep -Seconds 2
        Invoke-VBox @("controlvm", $VMName, "usbattach", $uuid) -AllowFail
        Start-Sleep -Seconds 5

        if (Test-BluetoothUsbAttached) {
            Write-Log "MediaTek Bluetooth USB device is attached to VM."
            return
        }

        $usbBlocks = @(Get-BluetoothUsbBlocks) -join "`n"
        if ($usbBlocks -match "busy with a previous request" -or $usbBlocks -match "Current State:\s+Captured") {
            Write-Log "Bluetooth USB still appears captured/busy; restarting VirtualBox backend before retry."
            Stop-WorkVm
            Invoke-VBox @("startvm", $VMName, "--type", "gui")
            Start-Sleep -Seconds 25
            $uuid = Get-BluetoothUsbUuid
        }
    }

    $vmInfo = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $vmInfo
    Write-Log "Final attached USB excerpt:"
    ($vmInfo -split "`n" | Select-String -Pattern "Currently attached USB devices|13d3|3602|MediaTek|Wireless|IMC" -Context 0,6) |
        ForEach-Object { Write-Log $_.ToString() }

    throw "MediaTek Bluetooth USB device was not attached to VM. See $LogPath."
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-NoElevate"
    )
    Write-Host "Requesting administrator elevation for Bluetooth USB repair..."
    Start-Process -FilePath "powershell.exe" -ArgumentList $args -Verb RunAs
    exit 0
}

if (-not (Test-IsAdmin)) {
    throw "This script must run as administrator."
}

Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
Write-Log "Starting Bluetooth passthrough repair."
$script:VBoxManage = Get-VBoxManagePath
Stop-WorkVm
Restart-HostBluetoothUsb
Set-BroadBluetoothFilter
Start-And-AttachBluetooth
Write-Log "Bluetooth passthrough repair complete."
