[CmdletBinding()]
param(
    [switch]$ForceReenroll,

    [switch]$PauseOnCompletion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

trap {
    Write-Host "StayActive client setup failed: $($_.Exception.Message)" -ForegroundColor Red
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
        throw 'The elevated StayActive client setup did not complete.'
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
    -InstallTailscale `
    -PublicPinned `
    -ForceReenroll:$ForceReenroll

if ($PauseOnCompletion) { $null = Read-Host 'Setup is complete. Press Enter to close this window' }
