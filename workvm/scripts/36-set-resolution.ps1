#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
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

$script:VBoxManage = Get-VBoxManagePath

$vmList = & $script:VBoxManage list vms
$exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
if (-not $exists) {
    throw "VM '$VMName' does not exist. Run workvm\setup-workvm.ps1 first."
}

Invoke-VBox @("setextradata", $VMName, "CustomVideoMode1", "${Width}x${Height}x${BitsPerPixel}")
Invoke-VBox @("setextradata", $VMName, "GUI/LastGuestSizeHint", "${Width},${Height}")
Invoke-VBox @("setextradata", $VMName, "VBoxInternal2/EfiGraphicsResolution", "${Width}x${Height}")

$info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
if ($info -match 'VMState="running"') {
    Invoke-VBox @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel")
}
else {
    Invoke-VBox @("modifyvm", $VMName, "--vram=128")
}

Write-Host ""
Write-Host "Resolution preference set to ${Width}x${Height}."
Write-Host "If the guest stays at a lower resolution, repair VirtualBox Guest Additions inside the VM."
