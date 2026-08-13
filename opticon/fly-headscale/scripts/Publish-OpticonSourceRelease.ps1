[CmdletBinding()]
param(
    [string]$StackName = 'opticon-release-distribution',
    [string]$Region = 'us-east-1',
    [string]$ArtifactDirectory = '',
    [string]$Version = '1.2.21',
    [string]$ControlOrigin = 'https://taildesk-egokick-control.fly.dev',
    [ValidateSet('Production', 'OwnerManaged')]
    [string]$SigningProfile = 'Production',
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SourceReleaseCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ProductCertificateThumbprint,
    [Parameter(Mandatory)][string]$Rfc3161TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath,
    [string]$ClientInstallValidationBase64 = '',
    [switch]$ForceRedeploy,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$AwsProfile = 'default',
    [switch]$CheckOnly,
    [switch]$SkipBuild,
    [Alias('SkipFlyDeploy')]
    [switch]$SkipManifestPublish,
    # Upload and verify the immutable source archive, preserving a local and
    # S3-durable receipt whose unique ID is bound into the archive metadata for
    # a later lease-bound manifest commit/recovery.
    [switch]$StageOnly,
    # Commit the exact previously staged source artifact without building or
    # uploading a replacement; missing local archive/launcher files are
    # rehydrated from the receipt-selected immutable S3 archive.
    [switch]$CommitStaged
)

# The public release channel has one immutable object per version:
# opticon-source-<version>.zip. The archive contains the signed source
# manifest, exact SDK/dependency pins, and the fixed signed local launcher;
# bundles and release-specific bootstraps are intentionally never uploaded.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.1') {
    throw 'The Opticon source-release publisher requires PowerShell 7.1 or newer. Run this script with pwsh.exe, not Windows PowerShell.'
}

$arguments = @{
    StackName = $StackName
    Region = $Region
    ArtifactDirectory = $ArtifactDirectory
    Version = $Version
    ControlOrigin = $ControlOrigin
    SigningProfile = $SigningProfile
    SourceReleaseCertificateThumbprint = $SourceReleaseCertificateThumbprint
    ProductCertificateThumbprint = $ProductCertificateThumbprint
    Rfc3161TimestampUrl = $Rfc3161TimestampUrl
    SignToolPath = $SignToolPath
    AwsProfile = $AwsProfile
    SourceOnly = $true
    CheckOnly = $CheckOnly
    SkipBuild = $SkipBuild
    SkipManifestPublish = $SkipManifestPublish
    StageOnly = $StageOnly
    CommitStaged = $CommitStaged
    ClientInstallValidationBase64 = $ClientInstallValidationBase64
    ForceRedeploy = $ForceRedeploy
}

& (Join-Path $PSScriptRoot 'Publish-OpticonBundles.ps1') @arguments
