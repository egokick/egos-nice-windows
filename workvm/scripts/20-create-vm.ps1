#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [Parameter(Mandatory = $true)]
    [string]$IsoPath,
    [string]$BaseFolder = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path,
    [int]$MemoryMB = 8192,
    [int]$CPUs = 4,
    [int]$DiskGB = 96,
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

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:VBoxManage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

$script:VBoxManage = Get-VBoxManagePath
if (-not $script:VBoxManage) {
    Write-Error "VirtualBox was not found. Run scripts\10-install-virtualbox.ps1 first from elevated PowerShell."
}

if (-not (Test-Path -LiteralPath $IsoPath)) {
    Write-Error @"
ISO path not found: $IsoPath

That argument must be the real path to a Windows installer ISO, not the placeholder path.

Try:
  .\scripts\05-find-windows-iso.ps1

If no ISO is found, open Microsoft's official Windows 11 ISO page:
  .\scripts\05-find-windows-iso.ps1 -OpenMicrosoftDownloadPage

Then re-run this command with the downloaded ISO path, for example:
  .\scripts\20-create-vm.ps1 -IsoPath "$env:USERPROFILE\Downloads\Win11_25H2_English_x64.iso"
"@
}

$iso = Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop
$isoFull = $iso.Path
$base = Resolve-Path -LiteralPath $BaseFolder -ErrorAction Stop
$baseFull = $base.Path

$vmList = & $script:VBoxManage list vms
$exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
if ($exists) {
    Write-Host "VM '$VMName' already exists. Leaving existing storage untouched."
    Write-Host "Use scripts\40-start-vm.ps1 -VMName $VMName to start it."
    exit 0
}

$osTypes = & $script:VBoxManage list ostypes
$osType = if ($osTypes -match "Windows11_64") { "Windows11_64" } else { "Windows10_64" }

New-Item -ItemType Directory -Force -Path $baseFull | Out-Null

Invoke-VBox @("createvm", "--name", $VMName, "--basefolder", $baseFull, "--ostype", $osType, "--register")

Invoke-VBox @(
    "modifyvm", $VMName,
    "--memory=$MemoryMB",
    "--cpus=$CPUs",
    "--vram=128",
    "--graphicscontroller=vboxsvga",
    "--firmware=efi",
    "--chipset=ich9",
    "--ioapic=on",
    "--rtc-use-utc=on",
    "--boot1=dvd",
    "--boot2=disk",
    "--boot3=none",
    "--boot4=none",
    "--nic1=nat",
    "--mouse=usbtablet",
    "--keyboard=usb",
    "--audio-driver=default",
    "--audio-controller=hda",
    "--clipboard-mode=bidirectional",
    "--clipboard-file-transfers=enabled",
    "--drag-and-drop=bidirectional",
    "--usb-xhci=on"
)

Invoke-VBox @("setextradata", $VMName, "CustomVideoMode1", "${Width}x${Height}x${BitsPerPixel}")
Invoke-VBox @("setextradata", $VMName, "GUI/LastGuestSizeHint", "${Width},${Height}")
Invoke-VBox @("setextradata", $VMName, "VBoxInternal2/EfiGraphicsResolution", "${Width}x${Height}")

try {
    Invoke-VBox @("modifyvm", $VMName, "--tpm-type=2.0")
}
catch {
    Write-Warning "Could not enable VirtualBox TPM 2.0. Windows 11 setup may complain. Update VirtualBox if needed."
}

$vmFolder = Join-Path $baseFull $VMName
$diskPath = Join-Path $vmFolder "$VMName.vdi"
$diskMB = $DiskGB * 1024

Invoke-VBox @("storagectl", $VMName, "--name", "SATA", "--add", "sata", "--controller", "IntelAhci", "--portcount", "4")
Invoke-VBox @("createmedium", "disk", "--filename", $diskPath, "--size", "$diskMB", "--format", "VDI")
Invoke-VBox @("storageattach", $VMName, "--storagectl", "SATA", "--port", "0", "--device", "0", "--type", "hdd", "--medium", $diskPath)
Invoke-VBox @("storageattach", $VMName, "--storagectl", "SATA", "--port", "1", "--device", "0", "--type", "dvddrive", "--medium", $isoFull)

try {
    Invoke-VBox @("sharedfolder", "add", $VMName, "--name", "workvm", "--hostpath", $baseFull, "--automount", "--auto-mount-point", "W:")
}
catch {
    Write-Warning "Could not add shared folder now. You can add it later in VirtualBox UI: name workvm, path $baseFull."
}

Write-Host ""
Write-Host "VM created: $VMName"
Write-Host "Base folder: $baseFull"
Write-Host "Disk: $diskPath"
Write-Host "ISO: $isoFull"
Write-Host ""
Write-Host "Next:"
Write-Host "  .\scripts\40-start-vm.ps1 -VMName $VMName"
Write-Host "  Install Windows, then install VirtualBox Guest Additions."
Write-Host "  After that, pass through the dedicated USB Bluetooth dongle with scripts\30-add-usb-bluetooth-filter.ps1."
