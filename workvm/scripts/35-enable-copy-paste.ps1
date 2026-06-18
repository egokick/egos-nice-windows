#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP"
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

    throw "VirtualBox was not found. Run workvm\setup-workvm.ps1 first."
}

function Invoke-VBox {
    param([string[]]$Arguments)

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:VBoxManage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Get-VmInfo {
    return (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
}

function Get-VmState {
    $info = Get-VmInfo
    if ($info -match 'VMState="([^"]+)"') {
        return $matches[1]
    }

    throw "Could not read VM state for '$VMName'."
}

$script:VBoxManage = Get-VBoxManagePath

$vmList = & $script:VBoxManage list vms
$exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
if (-not $exists) {
    throw "VM '$VMName' does not exist. Run workvm\setup-workvm.ps1 first."
}

$state = Get-VmState
if ($state -eq "running") {
    Invoke-VBox @("controlvm", $VMName, "clipboard", "mode", "bidirectional")
    Invoke-VBox @("controlvm", $VMName, "clipboard", "filetransfers", "on")
    Invoke-VBox @("controlvm", $VMName, "draganddrop", "bidirectional")
}
else {
    Invoke-VBox @(
        "modifyvm", $VMName,
        "--clipboard-mode=bidirectional",
        "--clipboard-file-transfers=enabled",
        "--drag-and-drop=bidirectional"
    )
}

$info = Get-VmInfo
$runLevel = if ($info -match 'GuestAdditionsRunLevel=(\d+)') { [int]$matches[1] } else { 0 }

Write-Host ""
Write-Host "Copy/paste settings are enabled:"
Write-Host "  clipboard: bidirectional"
Write-Host "  file transfers: on"
Write-Host "  drag/drop: bidirectional"
Write-Host ""

if ($runLevel -le 0) {
    Write-Warning "Guest Additions are not running. Clipboard settings are enabled, but copy/paste will not work until Guest Additions are installed and the guest service starts."
}
else {
    Write-Host "Guest Additions are running. Copy/paste should work in both directions."
}
