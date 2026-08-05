[CmdletBinding()]
param(
    [string]$LoginUrl = 'https://taildesk-egokick-control.fly.dev'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this command-center policy script as administrator.'
}

$uri = $null
if (-not [Uri]::TryCreate($LoginUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
    throw 'LoginUrl must be an absolute HTTPS URL.'
}

$policyPath = 'HKLM:\SOFTWARE\Policies\Tailscale'
New-Item -Path $policyPath -Force | Out-Null
New-ItemProperty -Path $policyPath -Name 'LoginURL' -Value $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/') -PropertyType String -Force | Out-Null
New-ItemProperty -Path $policyPath -Name 'UseTailscaleDNSSettings' -Value 'never' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $policyPath -Name 'AdvertiseExitNode' -Value 'never' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $policyPath -Name 'UnattendedMode' -Value 'always' -PropertyType String -Force | Out-Null

Restart-Service -Name 'Tailscale' -Force

