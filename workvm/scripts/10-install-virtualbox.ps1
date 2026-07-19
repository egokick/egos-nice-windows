#requires -Version 5.1

param(
    [switch]$Silent
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

    return $null
}

$existing = Get-VBoxManagePath
if ($existing) {
    Write-Host "VirtualBox is already available: $existing"
    & $existing --version
    exit 0
}

if (-not (Test-IsAdmin)) {
    Write-Host "VirtualBox installs host drivers and requires administrator approval."
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Silent) {
        $arguments += " -Silent"
    }

    $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
}

$winget = Get-Command winget -ErrorAction SilentlyContinue
$choco = Get-Command choco -ErrorAction SilentlyContinue

if ($winget) {
    $args = @(
        "install",
        "--id", "Oracle.VirtualBox",
        "--exact",
        "--source", "winget",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )

    if ($Silent) {
        $args += "--silent"
    }

    Write-Host "Installing VirtualBox with winget..."
    & $winget.Source @args
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "winget returned exit code $LASTEXITCODE."
    }
}
elseif ($choco) {
    Write-Host "Installing VirtualBox with Chocolatey..."
    & $choco.Source install virtualbox -y
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Chocolatey returned exit code $LASTEXITCODE."
    }
}
else {
    Write-Error "Neither winget nor Chocolatey is available. Install VirtualBox manually, then run scripts\20-create-vm.ps1."
}

$installed = Get-VBoxManagePath
if (-not $installed) {
    Write-Warning "VirtualBox was not found after install. Restart PowerShell or reboot if the installer requested it."
    exit 1
}

Write-Host "VirtualBox installed: $installed"
& $installed --version
Write-Host "Next: run scripts\20-create-vm.ps1 -IsoPath C:\path\to\Win11.iso"
