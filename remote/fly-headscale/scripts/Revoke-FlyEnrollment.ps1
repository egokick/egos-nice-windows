[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, [uint64]::MaxValue)]
    [uint64]$EnrollmentId,

    [string]$EnvironmentFile = 'C:\source\babelfish\.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FlyAdmin.Common.ps1')

Expire-StayActiveEnrollmentKey $EnrollmentId $EnvironmentFile
Write-Host "Enrollment ticket $EnrollmentId was revoked."
