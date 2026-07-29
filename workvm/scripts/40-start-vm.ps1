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

function Invoke-VBox {
    param([string[]]$Arguments)

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
    Write-Error "VM '$VMName' was not found. Run scripts\20-create-vm.ps1 first."
}

$info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
$stateMatch = [regex]::Match($info, '(?m)^VMState="([^"]+)"')
if (-not $stateMatch.Success) {
    throw "Could not read VM state for '$VMName'."
}
$state = $stateMatch.Groups[1].Value

if ($state -in @("saved", "aborted-saved")) {
    Write-Host "Discarding saved memory state so '$VMName' starts cleanly from disk."
    Invoke-VBox @("discardstate", $VMName)
    $state = "poweroff"
}

if ($state -eq "running") {
    Write-Host "VM '$VMName' is already running."
    exit 0
}

if ($state -notin @("poweroff", "aborted")) {
    throw "VM '$VMName' is '$state'; a clean start requires it to be powered off."
}

Invoke-VBox @(
    "modifyvm", $VMName,
    "--cpus=4",
    "--mouse=ps2",
    "--keyboard=ps2",
    "--usb-ohci=off",
    "--usb-xhci=on"
)
Invoke-VBox @("setextradata", $VMName, "GUI/DefaultCloseAction", "Shutdown")
Invoke-VBox @("setextradata", $VMName, "GUI/LastCloseAction", "Shutdown")
Invoke-VBox @("setextradata", $VMName, "GUI/RestrictedCloseActions", "SaveState")
Invoke-VBox @("setextradata", $VMName, "GUI/RestrictedRuntimeMachineMenuActions", "SaveState")
Invoke-VBox @("setextradata", $VMName, "CustomVideoMode1", "${Width}x${Height}x${BitsPerPixel}")
Invoke-VBox @("setextradata", $VMName, "GUI/LastGuestSizeHint", "${Width},${Height}")
Invoke-VBox @("setextradata", $VMName, "VBoxInternal2/EfiGraphicsResolution", "${Width}x${Height}")
Invoke-VBox @("startvm", $VMName, "--type", $Type)

Start-Sleep -Seconds 5
Invoke-VBox @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel")
