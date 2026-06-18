#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [ValidateSet("gui", "headless", "separate")]
    [string]$Type = "gui",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$BitsPerPixel = 32
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

$vbox = Get-VBoxManagePath
if (-not $vbox) {
    Write-Error "VirtualBox was not found. Run scripts\10-install-virtualbox.ps1 first."
}

$vmList = & $vbox list vms
$exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
if (-not $exists) {
    Write-Error "VM '$VMName' was not found. Run scripts\20-create-vm.ps1 first."
}

& $vbox setextradata $VMName CustomVideoMode1 "${Width}x${Height}x${BitsPerPixel}"
if ($LASTEXITCODE -ne 0) {
    throw "Could not set custom display mode for VM '$VMName'."
}

& $vbox setextradata $VMName GUI/LastGuestSizeHint "${Width},${Height}"
if ($LASTEXITCODE -ne 0) {
    throw "Could not set display size hint for VM '$VMName'."
}

& $vbox setextradata $VMName VBoxInternal2/EfiGraphicsResolution "${Width}x${Height}"
if ($LASTEXITCODE -ne 0) {
    throw "Could not set EFI display resolution for VM '$VMName'."
}

& $vbox startvm $VMName --type $Type
if ($LASTEXITCODE -ne 0) {
    throw "Could not start VM '$VMName'."
}

Start-Sleep -Seconds 5
& $vbox controlvm $VMName setvideomodehint $Width $Height $BitsPerPixel
