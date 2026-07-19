[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$')]
    [string]$NodeName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tailscale = 'C:\Program Files\Tailscale\tailscale.exe'
if (-not (Test-Path -LiteralPath $tailscale -PathType Leaf)) {
    throw 'The Tailscale client is not installed on this laptop.'
}
$deadline = [DateTime]::UtcNow.AddSeconds(45)
$matches = @()
do {
    $status = ((& $tailscale status --json 2>$null) -join [Environment]::NewLine) | ConvertFrom-Json
    if ($status.BackendState -ne 'Running') {
        throw 'The self-hosted VPN is not running.'
    }
    $peers = if ($null -ne $status.Peer) {
        @($status.Peer.PSObject.Properties | ForEach-Object { $_.Value })
    }
    else {
        @()
    }
    $matches = @($peers | Where-Object {
            ([string]$_.HostName -eq $NodeName -or ([string]$_.DNSName).TrimEnd('.') -eq $NodeName.TrimEnd('.')) -and
            [bool]$_.Online -and [bool]$_.ExitNodeOption
        })
    if ($matches.Count -eq 1) { break }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)
if ($matches.Count -ne 1) {
    throw "Expected one online approved exit node named '$NodeName'; found $($matches.Count)."
}
$exitIp = [string]@($matches[0].TailscaleIPs | Where-Object { $_ -match '^100\.' } | Select-Object -First 1)
if ($exitIp -notmatch '^100\.(?:[0-9]{1,3}\.){2}[0-9]{1,3}$') {
    throw 'The selected exit node has no valid IPv4 overlay address.'
}

$enabled = $false
try {
    & $tailscale set --exit-node $exitIp --exit-node-allow-lan-access=false --accept-dns=true 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Tailscale refused to enable the selected exit node.'
    }
    $enabled = $true
    $prefs = ((& $tailscale debug prefs 2>$null) -join [Environment]::NewLine) | ConvertFrom-Json
    if ([string]$prefs.ExitNodeIP -ne $exitIp -or [bool]$prefs.ExitNodeAllowLANAccess -or -not [bool]$prefs.CorpDNS) {
        throw 'The exit-node routing or DNS settings did not reach the required state.'
    }
    & $tailscale ping --timeout 10s $exitIp
    if ($LASTEXITCODE -ne 0) {
        throw 'The selected exit node did not answer over the self-hosted VPN.'
    }
    $null = Resolve-DnsName -Name 'example.com' -Type A -DnsOnly -ErrorAction Stop | Select-Object -First 1
    $egress = Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 20
    $egressAddress = $null
    if (-not [Net.IPAddress]::TryParse([string]$egress.ip, [ref]$egressAddress) -or
        $egressAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'Exit-node routing is enabled, but the public IPv4 verification response was invalid.'
    }
    Write-Host "Exit node '$NodeName' is enabled with local-LAN bypass disabled and exit-node DNS enabled."
    Write-Host "Verified public IPv4 through the exit node: $($egressAddress.IPAddressToString)"
}
catch {
    if ($enabled) {
        & $tailscale set --exit-node= 1>$null 2>$null
    }
    throw
}
