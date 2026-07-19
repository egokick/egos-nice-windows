#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$EnvironmentFile = 'C:\source\babelfish\.env',

    [switch]$ForceReenroll
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FlyAdmin.Common.ps1')

$joinScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\lan-test\scripts\Join-LanTestDevice.ps1'))
$certificate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\client\caddy-root.crt'))
$ticket = $null
$secureKey = $null
$joined = $false
try {
    $ticket = New-StayActiveEnrollmentKey $false $EnvironmentFile
    $secureKey = ConvertTo-SecureString -String $ticket.Key -AsPlainText -Force
    $ticket.Key = $null
    & $joinScript `
        -ServerIp '137.66.29.4' `
        -ExpectedCertificateSha256 'DBE921E0D15D821B4F6B5AE08EAF730E8B51D22F833DF9075186C72B5648AA84' `
        -CertificatePath $certificate `
        -InstallTailscale `
        -PublicPinned `
        -ForceReenroll:$ForceReenroll `
        -EnrollmentKey $secureKey
    $joined = $true
}
catch {
    if ($null -ne $ticket -and -not $joined) {
        try { Expire-StayActiveEnrollmentKey $ticket.Id $EnvironmentFile } catch { }
    }
    throw
}
finally {
    if ($null -ne $ticket) { $ticket.Key = $null }
    if ($null -ne $secureKey) { $secureKey.Dispose() }
}
