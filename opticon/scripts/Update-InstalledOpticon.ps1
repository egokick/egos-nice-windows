#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$InstallDirectory = "$env:ProgramFiles\Taildesk\Admin"
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
$releaseRoot = [IO.Path]::GetDirectoryName($source)
if ([string]::IsNullOrWhiteSpace($releaseRoot)) {
    throw 'The staged Opticon source directory has no release root.'
}
$expectedSource = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'App')).TrimEnd('\')
if (-not $source.Equals($expectedSource, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing a controller update from a source outside an extracted Opticon release App directory.'
}

# Do not copy over a live controller. The adjacent release installer owns the
# exclusive lock, validates the signed payload, preserves .previous, updates
# shortcuts/PATH, and writes the durable readiness marker after configuration.
$transactionalInstaller = Join-Path $releaseRoot 'Install-Opticon.ps1'
if (-not (Test-Path -LiteralPath $transactionalInstaller -PathType Leaf)) {
    throw 'The extracted Opticon release has no transactional Install-Opticon.ps1. Rebuild the command center package before repairing it.'
}
& $transactionalInstaller -InstallDirectory $InstallDirectory -ControllerOnlyRepair