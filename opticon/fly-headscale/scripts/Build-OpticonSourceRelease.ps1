[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('Production', 'OwnerManaged')]
    [string]$SigningProfile = 'Production',
    [string]$Version = '1.2.19',
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SourceReleaseCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ProductCertificateThumbprint,
    [Parameter(Mandatory)][string]$Rfc3161TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath
)

# This deliberately produces no OpticonBundle or OpticonBootstrap artifact.
# The signed launcher is an entry in the one source archive, not an S3 object.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.1') {
    throw 'The Opticon source-release builder requires PowerShell 7.1 or newer. Run this script with pwsh.exe, not Windows PowerShell.'
}

& (Join-Path $PSScriptRoot 'Build-OpticonBundles.ps1') @PSBoundParameters -SourceOnly
