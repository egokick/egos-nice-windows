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
$LogPath = Join-Path $Cache "admin-openvm-bluetooth-test.log"
$ScreenshotPath = Join-Path $Cache "admin-openvm-bluetooth-test.png"
$RepairScriptPath = Join-Path $PSScriptRoot "37-repair-bluetooth-passthrough.ps1"
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

function Invoke-ProcessTimed {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowFail
    )

    Write-Log "$FileName $($Arguments -join ' ')"
    $outputPath = Join-Path $Cache "admin-test-process-output.txt"
    $errorPath = Join-Path $Cache "admin-test-process-error.txt"
    Remove-Item -LiteralPath $outputPath,$errorPath -Force -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $FileName -ArgumentList $Arguments -NoNewWindow -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Log "  timed out after ${TimeoutSeconds}s; killing PID $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        if ($AllowFail) { return $false }
        throw "$FileName timed out."
    }

    foreach ($path in @($outputPath, $errorPath)) {
        if (Test-Path -LiteralPath $path) {
            Get-Content -LiteralPath $path | ForEach-Object { Write-Log "  $_" }
        }
    }

    if ($process.ExitCode -ne 0) {
        Write-Log "  exited with code $($process.ExitCode)"
        if ($AllowFail) { return $false }
        throw "$FileName failed with exit code $($process.ExitCode)."
    }

    return $true
}

if (-not (Test-IsAdmin)) {
    throw "This script must run as administrator."
}

$vbox = Get-VBoxManagePath
Write-Log "Starting elevated Open VM Bluetooth test."
Write-Log "VBoxManage: $vbox"

[void](Invoke-ProcessTimed -FileName $vbox -Arguments @("controlvm", $VMName, "poweroff") -TimeoutSeconds 30 -AllowFail)
Start-Sleep -Seconds 3

foreach ($name in @("VirtualBoxVM", "VBoxHeadless", "VBoxManage", "VBoxSVC")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Log "Stopping $name PID $($_.Id)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

Start-Sleep -Seconds 5

Write-Log "Running Bluetooth-ready VM launch as admin."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $RepairScriptPath -VMName $VMName -Width $Width -Height $Height -BitsPerPixel $BitsPerPixel -NoElevate 2>&1 |
    ForEach-Object { Write-Log "  $_" }
if ($LASTEXITCODE -ne 0) {
    throw "Bluetooth-ready launch failed with exit code $LASTEXITCODE."
}

Write-Log "Waiting for Windows desktop graphics to become active."
for ($attempt = 1; $attempt -le 60; $attempt++) {
    $info = (& $vbox showvminfo $VMName) -join "`n"
    if ($info -match "State:\s+running" -and $info -match 'Facility "Graphics Mode": active/running') {
        Write-Log "VM display is active."
        break
    }

    Start-Sleep -Seconds 5
}

[void](Invoke-ProcessTimed -FileName $vbox -Arguments @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel") -TimeoutSeconds 15 -AllowFail)
Start-Sleep -Seconds 5
[void](Invoke-ProcessTimed -FileName $vbox -Arguments @("controlvm", $VMName, "screenshotpng", $ScreenshotPath) -TimeoutSeconds 30)

Write-Log "Final VM state:"
(& $vbox showvminfo $VMName) | Select-String -Pattern "State:|Video mode:|Currently attached USB devices|VendorId|ProductId|Current State:|IMC|MediaTek|Graphics Mode" -Context 0,4 |
    ForEach-Object { Add-Content -LiteralPath $LogPath -Value $_.ToString(); Write-Host $_.ToString() }

Write-Log "Final USB host state:"
(& $vbox list usbhost) | Select-String -Pattern "0x13d3|0x3602|Current State|Manufacturer|Product|UUID" -Context 0,2 |
    ForEach-Object { Add-Content -LiteralPath $LogPath -Value $_.ToString(); Write-Host $_.ToString() }

Write-Log "Screenshot: $ScreenshotPath"
Write-Log "Elevated Open VM Bluetooth test complete."
