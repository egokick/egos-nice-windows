[CmdletBinding()]
param(
    [string]$StackName = 'opticon-release-distribution',
    [string]$Region = 'us-east-1',
    [string]$ArtifactDirectory = '',
    [string]$Version = '1.2.5',
    [string]$ControlOrigin = 'https://taildesk-egokick-control.fly.dev',
    [ValidateSet('Production', 'OwnerManaged')]
    [string]$SigningProfile = 'Production',
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SourceReleaseCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ProductCertificateThumbprint,
    [Parameter(Mandatory)][string]$Rfc3161TimestampUrl,
    [Parameter(Mandatory)][string]$SignToolPath,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$AwsProfile = 'default',
    [switch]$SkipBuild,
    [Alias('SkipFlyDeploy')]
    [switch]$SkipManifestPublish
)

# The public release channel has one immutable object per version:
# opticon-source-<version>.zip. The archive contains the signed source
# manifest, exact SDK/dependency pins, and the fixed signed local launcher;
# bundles and release-specific bootstraps are intentionally never uploaded.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    SkipBuild = $SkipBuild
    SkipManifestPublish = $SkipManifestPublish
}

& (Join-Path $PSScriptRoot 'Publish-OpticonBundles.ps1') @arguments
