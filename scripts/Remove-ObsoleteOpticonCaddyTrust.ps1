#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$thumbprints = @(
    'FD9ED287EF354B465915221973F417D16E3F2858',
    'C816A2B380DB7931406B6EC417BF876D2C60BEE0',
    '2B2A4E496E3E67048092DEEDD4AE2DC3913CBDCB'
)
$legacyState = 'C:\source\egos-nice-windows\Taildesk-source-1.0.0\local-headscale\state'
$quarantineRoot = 'C:\ProgramData\Opticon\SecurityQuarantine'
$quarantinePath = Join-Path $quarantineRoot ('LegacyLocalHeadscale-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

$removedTrustedRootCount = 0
foreach ($store in @('Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root')) {
    $matches = @(Get-ChildItem -Path $store | Where-Object { $_.Thumbprint -in $thumbprints })
    $removedTrustedRootCount += $matches.Count
    $matches | Remove-Item -Force
}

$moved = $false
if (Test-Path -LiteralPath $legacyState) {
    New-Item -ItemType Directory -Path $quarantineRoot -Force | Out-Null
    Move-Item -LiteralPath $legacyState -Destination $quarantinePath

    $userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $quarantineRoot '/inheritance:r' '/grant:r' "*$($userSid):(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not secure the Opticon quarantine root.' }
    & icacls.exe $quarantinePath '/inheritance:r' '/grant:r' "*$($userSid):(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '/T' '/C' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not secure the quarantined legacy Headscale state.' }
    $moved = $true
}

$remaining = foreach ($store in @('Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root')) {
    Get-ChildItem -Path $store |
        Where-Object { $_.Thumbprint -in $thumbprints } |
        Select-Object @{Name='Store';Expression={$store}}, Thumbprint, Subject
}
if (@($remaining).Count -ne 0) {
    throw 'One or more obsolete Caddy root certificates remain trusted.'
}

[pscustomobject]@{
    RemovedTrustedRootCount = $removedTrustedRootCount
    LegacyStateMoved = $moved
    QuarantinePath = if ($moved) { $quarantinePath } else { $null }
    RemainingTrustedRootCount = @($remaining).Count
} | ConvertTo-Json -Compress
