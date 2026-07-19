[CmdletBinding()]
param(
    [switch]$ForceReenroll,

    [switch]$PauseOnCompletion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

trap {
    Write-Host "StayActive exit-node setup failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($PauseOnCompletion) { $null = Read-Host 'Press Enter to close this window' }
    exit 1
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    if ($ForceReenroll) { $arguments += '-ForceReenroll' }
    $arguments += '-PauseOnCompletion'
    try {
        $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    }
    catch {
        throw 'Administrator approval was cancelled. No enrollment changes were made.'
    }
    if ($elevated.ExitCode -ne 0) {
        throw 'The elevated StayActive exit-node setup did not complete.'
    }
    return
}

$joinScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\lan-test\scripts\Join-LanTestDevice.ps1'))
$certificate = Join-Path $PSScriptRoot 'caddy-root.crt'
if (-not (Test-Path -LiteralPath $joinScript -PathType Leaf)) {
    throw 'The StayActive device-enrollment script is missing. Pull the latest main branch and retry.'
}

& $joinScript `
    -ServerIp '137.66.29.4' `
    -ExpectedCertificateSha256 'DBE921E0D15D821B4F6B5AE08EAF730E8B51D22F833DF9075186C72B5648AA84' `
    -CertificatePath $certificate `
    -AdvertiseExitNode `
    -InstallTailscale `
    -PublicPinned `
    -ForceReenroll:$ForceReenroll

try {
    $egress = Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 15
    $address = $null
    if ([Net.IPAddress]::TryParse([string]$egress.ip, [ref]$address) -and
        $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
        Write-Host "Expected exit-node public IPv4 after approval: $($address.IPAddressToString)"
    }
}
catch {
    Write-Warning 'The exit node joined, but its current public IPv4 could not be recorded automatically.'
}

if ($PauseOnCompletion) { $null = Read-Host 'Setup is complete. Press Enter to close this window' }
