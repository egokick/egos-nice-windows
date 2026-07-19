[CmdletBinding()]
param(
    [switch]$ExitCapable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    if ($ExitCapable) { $arguments += '-ExitCapable' }
    try {
        $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    }
    catch {
        throw 'Administrator approval was cancelled. No policy changes were made.'
    }
    if ($elevated.ExitCode -ne 0) {
        throw 'The elevated StayActive client-policy setup did not complete.'
    }
    return
}

$policyPath = 'HKLM:\SOFTWARE\Policies\Tailscale'
$null = New-Item -Path $policyPath -Force
$values = @{
    LoginURL = 'https://headscale.stayactive.test'
    UseTailscaleDNSSettings = 'always'
    UnattendedMode = 'always'
    InstallUpdates = 'never'
    AdminConsole = 'hide'
    OnboardingFlow = 'hide'
    AdvertiseExitNode = if ($ExitCapable) { 'always' } else { 'never' }
}
foreach ($entry in $values.GetEnumerator()) {
    $null = New-ItemProperty -Path $policyPath -Name $entry.Key -Value $entry.Value -PropertyType String -Force
}
[Environment]::SetEnvironmentVariable('TS_NO_LOGS_NO_SUPPORT', 'true', [EnvironmentVariableTarget]::Machine)

$tailscale = 'C:\Program Files\Tailscale\tailscale.exe'
if (Test-Path -LiteralPath $tailscale -PathType Leaf) {
    Restart-Service -Name Tailscale -Force
    (Get-Service -Name Tailscale).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    & $tailscale set --accept-dns=true --update-check=false --auto-update=false --report-posture=false --ssh=false --webclient=false 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Tailscale did not accept the pinned client privacy preferences.'
    }
}

$policy = Get-ItemProperty -Path $policyPath
$expectedRole = if ($ExitCapable) { 'always' } else { 'never' }
if (
    [string]$policy.LoginURL -ne 'https://headscale.stayactive.test' -or
    [string]$policy.UseTailscaleDNSSettings -ne 'always' -or
    [string]$policy.AdvertiseExitNode -ne $expectedRole) {
    throw 'The Tailscale machine policy was not applied exactly.'
}
Write-Host 'Tailscale is pinned to the self-hosted StayActive controller and assigned device role.'
