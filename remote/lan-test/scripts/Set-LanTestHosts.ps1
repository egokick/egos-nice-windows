#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Bootstrap', 'Lan')]
    [string]$Mode,

    [string]$ServerIp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PrivateIpv4([string]$Value) {
    $address = $null
    if (
        -not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'ServerIp must be an IPv4 address.'
    }

    $bytes = $address.GetAddressBytes()
    $isPrivate = $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $isPrivate) {
        throw 'ServerIp must be an RFC1918 private IPv4 address.'
    }

    return $address.IPAddressToString
}

$targetIp = if ($Mode -eq 'Bootstrap') {
    '127.0.0.1'
}
else {
    if ([string]::IsNullOrWhiteSpace($ServerIp)) {
        throw 'ServerIp is required in Lan mode.'
    }

    Assert-PrivateIpv4 $ServerIp
}

$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$beginMarker = '# StayActive Remotes LAN test BEGIN'
$endMarker = '# StayActive Remotes LAN test END'
$hostNames = @(
    'headscale.stayactive.test',
    'meshcentral.stayactive.test',
    'remotehub.stayactive.test',
    'id.stayactive.test'
)

$current = if (Test-Path -LiteralPath $hostsPath -PathType Leaf) {
    [System.IO.File]::ReadAllText($hostsPath)
}
else {
    ''
}

$managedBlockPattern = '(?ms)^' + [regex]::Escape($beginMarker) + '\r?\n.*?^' + [regex]::Escape($endMarker) + '\r?\n?'
$withoutManagedBlock = [regex]::Replace($current, $managedBlockPattern, '')
$managedBlock = @(
    $beginMarker,
    '# Managed by StayActive. Do not add unrelated hostnames inside this block.',
    "$targetIp $($hostNames -join ' ')",
    $endMarker
) -join [Environment]::NewLine

$withoutTrailingNewlines = $withoutManagedBlock.TrimEnd([char[]]@([char]13, [char]10))
$newContent = if ([string]::IsNullOrEmpty($withoutTrailingNewlines)) {
    $managedBlock + [Environment]::NewLine
}
else {
    $withoutTrailingNewlines + [Environment]::NewLine + [Environment]::NewLine + $managedBlock + [Environment]::NewLine
}

if ($PSCmdlet.ShouldProcess($hostsPath, "Map StayActive LAN service names to $targetIp")) {
    [System.IO.File]::WriteAllText($hostsPath, $newContent, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Updated the local hosts mapping for $Mode mode ($targetIp)."
}