#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Use')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Use')]
    [string]$ExitNodeIp,

    [Parameter(Mandatory, ParameterSetName = 'Clear')]
    [switch]$Clear,

    [switch]$AllowLocalNetworkAccess
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-TailscaleExecutable {
    $command = Get-Command tailscale.exe -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    foreach ($path in @(
            (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'),
            (Join-Path $env:LOCALAPPDATA 'Tailscale\tailscale.exe'))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return $path
        }
    }

    throw 'The Tailscale client is not installed.'
}

function Assert-Ipv4([string]$Value) {
    $address = $null
    if ((-not [System.Net.IPAddress]::TryParse($Value, [ref]$address)) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'ExitNodeIp must be an IPv4 Tailscale address.'
    }

    return $address.IPAddressToString
}

$tailscale = Resolve-TailscaleExecutable
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $tailscale
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add('set')

if ($Clear) {
    $startInfo.ArgumentList.Add('--exit-node=')
}
else {
    $address = Assert-Ipv4 $ExitNodeIp
    $startInfo.ArgumentList.Add('--exit-node=' + $address)
    $startInfo.ArgumentList.Add('--exit-node-allow-lan-access=' + $(if ($AllowLocalNetworkAccess) { 'true' } else { 'false' }))
}

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw 'The Tailscale client could not be started.'
    }

    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $null = $standardOutput.GetAwaiter().GetResult()
    $null = $standardError.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw 'Tailscale rejected the exit-route change. Confirm the remote route is approved by Headscale.'
    }
}
finally {
    $process.Dispose()
}

if ($Clear) {
    Write-Host 'Direct internet routing has been restored.'
}
else {
    Write-Host "Internet traffic now routes through the approved exit node at $address."
}
