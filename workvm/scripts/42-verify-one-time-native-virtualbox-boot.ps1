#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-VBoxManagePath {
    foreach ($path in @(
        "$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe",
        "${env:ProgramFiles(x86)}\Oracle\VirtualBox\VBoxManage.exe"
    )) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }
    throw "VirtualBox was not found."
}

$computer = Get-CimInstance Win32_ComputerSystem
$deviceGuard = Get-CimInstance `
    -Namespace "root\Microsoft\Windows\DeviceGuard" `
    -ClassName Win32_DeviceGuard
$vboxManage = Get-VBoxManagePath
$vmInfo = (& $vboxManage showvminfo $VMName --machinereadable) -join "`n"
$vmRunning = $vmInfo -match '(?m)^VMState="running"\s*$'
$logFolderMatch = [regex]::Match(
    $vmInfo,
    '(?m)^LogFldr="(?<value>[^"]+)"\s*$'
)
$logPath = if ($logFolderMatch.Success) {
    Join-Path $logFolderMatch.Groups["value"].Value "VBox.log"
} else {
    $null
}
$logText = if ($null -ne $logPath -and (Test-Path -LiteralPath $logPath)) {
    Get-Content -LiteralPath $logPath -Raw
} else {
    ""
}

$hypervisorOff = -not [bool]$computer.HypervisorPresent
$vbsOff = [int]$deviceGuard.VirtualizationBasedSecurityStatus -ne 2
$nativeVirtualBox = $vmRunning -and
    $logText -notmatch 'NEMR3Init: Snail execution mode is active' -and
    $logText -notmatch 'WHvCapabilityCodeHypervisorPresent is TRUE'

$result = [ordered]@{
    HypervisorPresent = [bool]$computer.HypervisorPresent
    VirtualizationBasedSecurityStatus =
        [int]$deviceGuard.VirtualizationBasedSecurityStatus
    SecurityServicesRunning =
        @($deviceGuard.SecurityServicesRunning)
    VmRunning = $vmRunning
    NativeVirtualBoxLog = $nativeVirtualBox
    VBoxLog = $logPath
    Verified = $hypervisorOff -and $vbsOff -and $nativeVirtualBox
}
$result | ConvertTo-Json

if (-not $hypervisorOff) {
    throw "The Windows hypervisor is still running in this boot."
}
if (-not $vbsOff) {
    throw "Virtualization-based security is still running in this boot."
}
if (-not $vmRunning) {
    throw "Start '$VMName' before running the native-mode verification."
}
if (-not $nativeVirtualBox) {
    throw "The current VirtualBox log still reports the Windows Hyper-V/NEM engine."
}

Write-Host "STAYACTIVE_NATIVE_VBOX_TEST_VERIFIED"

