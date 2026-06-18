#requires -Version 5.1

param(
    [switch]$SkipChrome,
    [switch]$SkipOnePassword,
    [switch]$InstallKeePass,
    [switch]$InstallKeePassXC
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$winget = Get-Command winget -ErrorAction SilentlyContinue
if (-not $winget) {
    Write-Error "winget was not found in the guest. Install App Installer from Microsoft Store, then re-run this script."
}

$packages = New-Object System.Collections.Generic.List[object]

if (-not $SkipChrome) {
    $packages.Add([pscustomobject]@{ Id = "Google.Chrome"; Name = "Google Chrome" })
}

if (-not $SkipOnePassword) {
    $packages.Add([pscustomobject]@{ Id = "AgileBits.1Password"; Name = "1Password" })
}

if ($InstallKeePass) {
    $packages.Add([pscustomobject]@{ Id = "DominikReichl.KeePass"; Name = "KeePass" })
}

if ($InstallKeePassXC) {
    $packages.Add([pscustomobject]@{ Id = "KeePassXCTeam.KeePassXC"; Name = "KeePassXC" })
}

$packages.Add([pscustomobject]@{ Id = "AutoHotkey.AutoHotkey"; Name = "AutoHotkey v2" })

foreach ($package in $packages) {
    Write-Host "Installing $($package.Name)..."
    & $winget.Source install --id $package.Id --exact --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$($package.Name) install returned exit code $LASTEXITCODE."
    }
}

$chromePolicyPath = "HKLM:\Software\Policies\Google\Chrome\ExtensionInstallForcelist"
try {
    New-Item -Path $chromePolicyPath -Force | Out-Null
    New-ItemProperty -Path $chromePolicyPath -Name "1" -Value "aeblfdkhhhdcdjpifhhbdiojplfjncoa;https://clients2.google.com/service/update2/crx" -PropertyType String -Force | Out-Null
    Write-Host "Configured Chrome to install the 1Password extension."
}
catch {
    Write-Warning "Could not configure the Chrome 1Password extension policy: $($_.Exception.Message)"
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$keepAliveSource = Join-Path $scriptRoot "keep-rdp-alive.ahk"
$desktop = [Environment]::GetFolderPath("Desktop")
$keepAliveDest = Join-Path $desktop "WorkVM Keep RDP Alive.ahk"

if (Test-Path -LiteralPath $keepAliveSource) {
    Copy-Item -LiteralPath $keepAliveSource -Destination $keepAliveDest -Force
    Write-Host "Copied keepalive script to: $keepAliveDest"
}
else {
    Write-Warning "Could not find keep-rdp-alive.ahk next to this script."
}

Write-Host ""
Write-Host "Next inside the guest:"
Write-Host "  1. Confirm the USB Bluetooth dongle appears in Device Manager."
Write-Host "  2. Install the required browser extension(s), such as the 1Password extension."
Write-Host "  3. Test a phone passkey/auth flow before opening the browser RDP site."
Write-Host "  4. Open the RDP tab, keep it focused inside the VM, then start 'WorkVM Keep RDP Alive.ahk'."
