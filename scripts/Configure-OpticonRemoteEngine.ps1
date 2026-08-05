#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$RustDeskPath = "$env:ProgramFiles\RustDesk\rustdesk.exe"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $RustDeskPath)) {
    throw "RustDesk was not found at $RustDeskPath"
}

$options = @(
    @('direct-server', 'N'),
    @('custom-rendezvous-server', '127.0.0.1'),
    @('relay-server', '127.0.0.1'),
    @('enable-lan-discovery', 'N'),
    @('hide-tray', 'Y'),
    @('hide-stop-service', 'Y'),
    @('disable-discovery-panel', 'Y'),
    @('allow-auto-update', 'N'),
    @('enable-udp-punch', 'N'),
    @('enable-ipv6-punch', 'N')
)

foreach ($option in $options) {
    $process = Start-Process -FilePath $RustDeskPath `
        -ArgumentList @('--option', $option[0], $option[1]) `
        -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "RustDesk rejected private option $($option[0])."
    }
}

$userConfigPath = Join-Path $env:APPDATA 'RustDesk\config\RustDesk2.toml'
if (Test-Path -LiteralPath $userConfigPath) {
    $configText = [System.IO.File]::ReadAllText($userConfigPath)
    $configText = [regex]::Replace($configText, '(?m)^rendezvous_server\s*=.*$', "rendezvous_server = '127.0.0.1:21116'")
    $configText = [regex]::Replace($configText, '(?m)^rendezvous-server\s*=.*\r?\n?', '')
    [System.IO.File]::WriteAllText($userConfigPath, $configText, [System.Text.UTF8Encoding]::new($false))
}

$commonStartup = [Environment]::GetFolderPath('CommonStartup')
$commonDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$commonPrograms = [Environment]::GetFolderPath('CommonPrograms')
foreach ($shortcut in @(
    (Join-Path $commonStartup 'RustDesk Tray.lnk'),
    (Join-Path $commonDesktop 'RustDesk.lnk')
)) {
    Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
}
$rustDeskPrograms = Join-Path $commonPrograms 'RustDesk'
if (Test-Path -LiteralPath $rustDeskPrograms) {
    Remove-Item -LiteralPath $rustDeskPrograms -Recurse -Force
}

$service = Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue
if ($service) {
    $service | Stop-Service -Force -ErrorAction SilentlyContinue
    $service | Set-Service -StartupType Disabled
}
Get-Process -Name 'RustDesk' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

& netsh.exe advfirewall firewall delete rule 'name=all' 'dir=in' "program=$RustDeskPath" | Out-Null
foreach ($rule in @('RustDesk External IPv4 Block', 'RustDesk External IPv6 Block')) {
    & netsh.exe advfirewall firewall delete rule "name=$rule" | Out-Null
}

& netsh.exe advfirewall firewall add rule `
    'name=RustDesk External IPv4 Block' 'dir=out' 'action=block' `
    'remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255' `
    "program=$RustDeskPath" 'profile=any' 'enable=yes' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Windows could not restrict RustDesk to Tailscale IPv4 destinations.'
}

& netsh.exe advfirewall firewall add rule `
    'name=RustDesk External IPv6 Block' 'dir=out' 'action=block' `
    'remoteip=::/1,8000::/1' "program=$RustDeskPath" 'profile=any' 'enable=yes' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Windows could not block external RustDesk IPv6 destinations.'
}

[pscustomobject]@{
    RustDeskPath = $RustDeskPath
    ServiceStatus = (Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue).Status
    ServiceStartType = (Get-CimInstance Win32_Service -Filter "Name='RustDesk'" -ErrorAction SilentlyContinue).StartMode
    RunningProcessCount = @(Get-Process -Name 'RustDesk' -ErrorAction SilentlyContinue).Count
    ExternalIPv4Block = [bool](Get-NetFirewallRule -DisplayName 'RustDesk External IPv4 Block' -ErrorAction SilentlyContinue)
    ExternalIPv6Block = [bool](Get-NetFirewallRule -DisplayName 'RustDesk External IPv6 Block' -ErrorAction SilentlyContinue)
} | ConvertTo-Json -Compress
