#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ServerIp,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCertificateSha256,

    [string]$CertificatePath = (Join-Path (Join-Path $PSScriptRoot '..') 'certs\caddy-root.crt'),

    [switch]$AdvertiseExitNode,

    [switch]$InstallTailscale
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $principal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an Administrator PowerShell window.'
    }
}

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

    return $null
}

function Install-TailscaleIfRequested {
    param([switch]$Requested)

    $existing = Resolve-TailscaleExecutable
    if ($null -ne $existing) {
        return $existing
    }

    if (-not $Requested) {
        throw 'The Tailscale client is not installed. Install it with winget (or re-run this script with -InstallTailscale), then retry.'
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        throw 'winget is unavailable. Install the supported Tailscale Windows client, then rerun this script.'
    }

    & $winget.Source install --exact --id Tailscale.Tailscale --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to install the Tailscale client.'
    }

    $installed = Resolve-TailscaleExecutable
    if ($null -eq $installed) {
        throw 'Tailscale was installed but its executable is not available yet. Open a new Administrator PowerShell window and rerun this script.'
    }

    return $installed
}

function Read-OneTimeJoinCommand {
    Write-Host 'Paste the one-time command shown by StayActive Remotes > Add device. It will not be echoed or written to PowerShell history by this script.'
    $secureCommand = $null
    $pointer = [IntPtr]::Zero
    try {
        $secureCommand = Read-Host -AsSecureString 'One-time join command'
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureCommand)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        if ($null -ne $secureCommand) {
            $secureCommand.Dispose()
        }
    }
}

function Parse-OneTimeJoinCommand([string]$JoinCommand) {
    # Accept only the exact command emitted by StayActive. Do not use
    # Invoke-Expression or run any pasted shell text.
    $pattern = '^\s*tailscale(?:\.exe)?\s+up\s+--login-server\s+(?<server>https://headscale\.stayactive\.test)\s+--auth-key\s+(?<key>[A-Za-z0-9_-]{16,4096})\s*$'
    $match = [regex]::Match($JoinCommand, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw 'The pasted command is not the exact one-time self-hosted StayActive enrollment command.'
    }

    $server = $match.Groups['server'].Value
    $key = $match.Groups['key'].Value
    if ($key.StartsWith('-', [StringComparison]::Ordinal)) {
        throw 'The one-time enrollment key is invalid.'
    }

    return [pscustomobject]@{
        Server = $server
        Key = $key
    }
}

function Invoke-TailscaleJoin(
    [string]$Executable,
    [string]$LoginServer,
    [string]$AuthKey,
    [bool]$ShouldAdvertiseExitNode) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('up')
    $startInfo.ArgumentList.Add('--login-server')
    $startInfo.ArgumentList.Add($LoginServer)
    $startInfo.ArgumentList.Add('--auth-key')
    $startInfo.ArgumentList.Add($AuthKey)
    if ($ShouldAdvertiseExitNode) {
        $startInfo.ArgumentList.Add('--advertise-exit-node')
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
        # Do not display process output: it is not needed for the happy path and
        # a future client must not be able to reflect a supplied one-time key.
        $null = $standardOutput.GetAwaiter().GetResult()
        $null = $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw 'Tailscale did not accept the one-time enrollment command. The key may have expired, been revoked, or already been used.'
        }
    }
    finally {
        $process.Dispose()
    }
}

Assert-Administrator

$hostsScript = Join-Path $PSScriptRoot 'Set-LanTestHosts.ps1'
$certificateScript = Join-Path $PSScriptRoot 'Install-CaddyRoot.ps1'
if (-not (Test-Path -LiteralPath $hostsScript -PathType Leaf) -or -not (Test-Path -LiteralPath $certificateScript -PathType Leaf)) {
    throw 'This checkout is missing the required StayActive LAN setup scripts.'
}

& $hostsScript -Mode Lan -ServerIp $ServerIp -Confirm:$false
& $certificateScript -CertificatePath $CertificatePath -ExpectedCertificateSha256 $ExpectedCertificateSha256 -Confirm:$false

$tailscale = Install-TailscaleIfRequested -Requested:$InstallTailscale
$joinCommand = $null
$join = $null
try {
    $joinCommand = Read-OneTimeJoinCommand
    $join = Parse-OneTimeJoinCommand $joinCommand
    Invoke-TailscaleJoin $tailscale $join.Server $join.Key $AdvertiseExitNode
}
finally {
    if ($null -ne $join) {
        $join.Key = $null
    }
    $joinCommand = $null
}

if ($AdvertiseExitNode) {
    Write-Host 'This laptop joined the self-hosted Headscale network and is advertising its default route. On the controller laptop, approve that route before selecting it as an exit node.'
}
else {
    Write-Host 'This laptop joined the self-hosted Headscale network. It is ready to select an approved exit node.'
}
