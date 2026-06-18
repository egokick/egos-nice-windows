#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "== $Title ==" -ForegroundColor Cyan
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

    return $null
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

Write-Section "Identity"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
[pscustomobject]@{
    User    = $identity.Name
    IsAdmin = Test-IsAdmin
} | Format-List

Write-Section "Host"
$computer = Get-ComputerInfo -Property CsTotalPhysicalMemory,CsNumberOfLogicalProcessors,OsName,OsVersion,HyperVisorPresent
[pscustomobject]@{
    OS                = $computer.OsName
    Version           = $computer.OsVersion
    RAMGB             = [math]::Round($computer.CsTotalPhysicalMemory / 1GB, 1)
    LogicalProcessors = $computer.CsNumberOfLogicalProcessors
    HyperVisorPresent = $computer.HyperVisorPresent
} | Format-List

Write-Section "Package managers"
$packageManagers = foreach ($name in "winget", "choco", "scoop") {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Name  = $name
        Found = [bool]$cmd
        Path  = if ($cmd) { $cmd.Source } else { $null }
    }
}
$packageManagers | Format-Table -AutoSize

Write-Section "Virtualization tools"
$vbox = Get-VBoxManagePath
$virtualizationTools = @(
    [pscustomobject]@{
    Tool  = "VirtualBox VBoxManage"
    Found = [bool]$vbox
    Path  = $vbox
    }
)

foreach ($name in "vmrun", "vmware", "qemu-system-x86_64") {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    $virtualizationTools += [pscustomobject]@{
        Tool  = $name
        Found = [bool]$cmd
        Path  = if ($cmd) { $cmd.Source } else { $null }
    }
}
$virtualizationTools | Format-Table -AutoSize

Write-Section "Relevant installed apps"
$uninstallRoots = @(
    "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
    "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall"
)

$apps = foreach ($root in $uninstallRoots) {
    if (Test-Path $root) {
        Get-ChildItem -Path $root -ErrorAction SilentlyContinue |
            Get-ItemProperty -ErrorAction SilentlyContinue |
            Where-Object {
                $_.PSObject.Properties["DisplayName"] -and
                $_.DisplayName -match "VirtualBox|VMware|QEMU|Chrome|1Password|KeePass"
            } |
            Select-Object DisplayName, DisplayVersion, Publisher, InstallLocation
    }
}

if ($apps) {
    $apps | Sort-Object DisplayName | Format-Table -AutoSize
} else {
    Write-Host "No relevant installed apps found in uninstall registry."
}

Write-Section "Bluetooth-visible devices"
$btDevices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Class -eq "Bluetooth" -or
        $_.FriendlyName -match "Bluetooth|BLE" -or
        $_.InstanceId -match "^USB\\VID_.*PID_.*"
    } |
    Select-Object Status, Class, FriendlyName, InstanceId

if ($btDevices) {
    $btDevices | Format-Table -AutoSize
} else {
    Write-Host "No Bluetooth/USB Bluetooth-looking devices found."
}

Write-Section "Next"
if (-not $vbox) {
    Write-Host "VirtualBox is not installed or not on PATH. Run scripts\10-install-virtualbox.ps1 from an elevated PowerShell."
} else {
    Write-Host "VirtualBox is available. Run scripts\20-create-vm.ps1 with your Windows ISO path."
}
