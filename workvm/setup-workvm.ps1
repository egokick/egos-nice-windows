#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [switch]$SkipUnattendedInstall,
    [switch]$NoStart,
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Downloads = Join-Path $Root "downloads"
$IsoPath = Join-Path $Downloads "Win11_25H2_English_x64_v2.iso"
$CredentialPath = Join-Path $Root "vm-credentials.txt"

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

function Invoke-VBox {
    param([string[]]$Arguments)

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:VBoxManage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Wait-ForIsoDownload {
    Import-Module BitsTransfer -ErrorAction SilentlyContinue

    if ((Test-Path -LiteralPath $IsoPath) -and (Get-Item -LiteralPath $IsoPath).Length -gt 4GB) {
        Write-Host "ISO is already downloaded: $IsoPath"
        return
    }

    $job = Get-BitsTransfer -Name "WorkVM Windows 11 ISO" -ErrorAction SilentlyContinue
    if (-not $job) {
        $urlPath = Join-Path $Downloads "windows11-iso-url.txt"
        if (-not (Test-Path -LiteralPath $urlPath)) {
            throw "No ISO file or active ISO download was found. Ask Codex to refresh the Microsoft ISO link."
        }

        $url = (Get-Content -Raw -LiteralPath $urlPath).Trim()
        Write-Host "Starting ISO download..."
        Start-BitsTransfer -Source $url -Destination $IsoPath -DisplayName "WorkVM Windows 11 ISO" -Description "Official Microsoft Windows 11 ISO for WorkVM" -Asynchronous
        $job = Get-BitsTransfer -Name "WorkVM Windows 11 ISO" -ErrorAction Stop
    }

    Write-Host "Waiting for ISO download to finish. This can take a while."
    while ($true) {
        $job = Get-BitsTransfer -Name "WorkVM Windows 11 ISO" -ErrorAction SilentlyContinue
        if (-not $job) {
            if ((Test-Path -LiteralPath $IsoPath) -and (Get-Item -LiteralPath $IsoPath).Length -gt 4GB) {
                return
            }
            throw "The ISO download job disappeared before the ISO was completed."
        }

        $totalKnown = $job.BytesTotal -gt 0 -and $job.BytesTotal -lt [uint64]::MaxValue
        $pct = if ($totalKnown) { [math]::Round(($job.BytesTransferred / $job.BytesTotal) * 100, 1) } else { $null }
        $gbDone = [math]::Round($job.BytesTransferred / 1GB, 2)
        $gbTotal = if ($totalKnown) { [math]::Round($job.BytesTotal / 1GB, 2) } else { "unknown" }

        Write-Host "Download state: $($job.JobState) $gbDone GB / $gbTotal GB $(if ($pct -ne $null) { "($pct%)" })"

        switch ($job.JobState) {
            "Transferred" {
                Complete-BitsTransfer -BitsJob $job
                if (-not (Test-Path -LiteralPath $IsoPath)) {
                    throw "BITS reported completion, but the ISO is missing: $IsoPath"
                }
                Write-Host "ISO download completed: $IsoPath"
                return
            }
            "Error" {
                $message = $job.ErrorDescription
                Remove-BitsTransfer -BitsJob $job -Confirm:$false -ErrorAction SilentlyContinue
                throw "ISO download failed: $message"
            }
            "TransientError" {
                Resume-BitsTransfer -BitsJob $job -ErrorAction SilentlyContinue
            }
            "Suspended" {
                Resume-BitsTransfer -BitsJob $job
            }
        }

        Start-Sleep -Seconds 30
    }
}

function Ensure-VirtualBox {
    $script:VBoxManage = Get-VBoxManagePath
    if ($script:VBoxManage) {
        Write-Host "VirtualBox found: $script:VBoxManage"
        return
    }

    Write-Host "VirtualBox not found. Installing it now..."
    & (Join-Path $Root "scripts\10-install-virtualbox.ps1") -Silent
    $script:VBoxManage = Get-VBoxManagePath
    if (-not $script:VBoxManage) {
        throw "VirtualBox still was not found after installation. Reboot and run this script again."
    }
}

function Ensure-Vm {
    $vmList = & $script:VBoxManage list vms
    $exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
    if ($exists) {
        Write-Host "VM '$VMName' already exists."
        return
    }

    & (Join-Path $Root "scripts\20-create-vm.ps1") -VMName $VMName -IsoPath $IsoPath
}

function Ensure-BluetoothFilter {
    $info = & $script:VBoxManage showvminfo $VMName --machinereadable
    if ($info -match 'USBFilterName\d+="Laptop MediaTek Bluetooth Adapter"') {
        Write-Host "Laptop Bluetooth USB filter already exists."
        return
    }

    & (Join-Path $Root "scripts\31-use-laptop-bluetooth.ps1") -VMName $VMName
}

function Get-OrCreateGuestCredential {
    if (Test-Path -LiteralPath $CredentialPath) {
        return Get-Content -Raw -LiteralPath $CredentialPath
    }

    $bytes = New-Object byte[] 18
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }
    $password = [Convert]::ToBase64String($bytes).TrimEnd("=") + "!"
    $text = @"
VM name: $VMName
Guest username: workvm
Guest password: $password

This account is created by VirtualBox unattended install if you let setup-workvm.ps1 start the unattended install.
"@
    Set-Content -LiteralPath $CredentialPath -Value $text -Encoding UTF8
    return $text
}

function Get-GuestPassword {
    $text = Get-OrCreateGuestCredential
    $match = [regex]::Match($text, "Guest password:\s*(.+)")
    if (-not $match.Success) {
        throw "Could not parse guest password from $CredentialPath"
    }
    return $match.Groups[1].Value.Trim()
}

function Start-UnattendedInstall {
    if ($SkipUnattendedInstall) {
        Write-Host "Skipping unattended Windows install because -SkipUnattendedInstall was specified."
        return
    }

    $running = & $script:VBoxManage list runningvms
    if ($running -match ('^"' + [regex]::Escape($VMName) + '"')) {
        Write-Host "VM '$VMName' is already running. Leaving it alone."
        return
    }

    $machineInfo = & $script:VBoxManage showvminfo $VMName --machinereadable
    if ($machineInfo -match 'Unattended') {
        Write-Host "VM appears to have unattended install media configured already."
    }

    $password = Get-GuestPassword
    Write-Host "Starting unattended Windows install. Credentials are saved at: $CredentialPath"
    Invoke-VBox @(
        "unattended", "install", $VMName,
        "--iso=$IsoPath",
        "--user=workvm",
        "--user-password=$password",
        "--full-user-name=WorkVM",
        "--install-additions",
        "--locale=en_US",
        "--country=US",
        "--time-zone=Eastern Standard Time",
        "--hostname=workrdp.local",
        "--image-index=1",
        "--start-vm=gui"
    )
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    $argList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`""
    )
    if ($SkipUnattendedInstall) { $argList += "-SkipUnattendedInstall" }
    if ($NoStart) { $argList += "-NoStart" }
    $argList += "-NoElevate"

    Write-Host "Requesting administrator elevation..."
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

New-Item -ItemType Directory -Force -Path $Downloads | Out-Null

Wait-ForIsoDownload
Ensure-VirtualBox
Ensure-Vm
Ensure-BluetoothFilter

if ($NoStart) {
    Write-Host "Setup complete. VM was not started because -NoStart was specified."
} else {
    Start-UnattendedInstall
}

Write-Host ""
Write-Host "WorkVM setup is configured."
Write-Host "Built-in laptop Bluetooth is filtered into the VM. Host Bluetooth will stop working while the VM owns it."
Write-Host "Guest credentials, if unattended install is used: $CredentialPath"
