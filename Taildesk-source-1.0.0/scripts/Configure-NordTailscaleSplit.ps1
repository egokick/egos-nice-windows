#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$nordRoot = 'C:\ProgramData\NordVPN'
$database = Join-Path $nordRoot 'settings.db'
$helper = 'C:\source\egos-nice-windows\Taildesk-source-1.0.0\scripts\Configure-NordTailscaleSplit.py'
$backup = Join-Path $nordRoot ('OpticonBackups\tailscale-exclusion-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$tailscaleRoot = 'C:\Program Files\Tailscale'
$tailscaleApps = @(
    @{ Name = 'tailscaled'; DisplayName = 'Tailscale Service'; Path = (Join-Path $tailscaleRoot 'tailscaled.exe') },
    @{ Name = 'tailscale-ipn'; DisplayName = 'Tailscale'; Path = (Join-Path $tailscaleRoot 'tailscale-ipn.exe') },
    @{ Name = 'tailscale'; DisplayName = 'Tailscale CLI'; Path = (Join-Path $tailscaleRoot 'tailscale.exe') }
)

if (-not (Test-Path -LiteralPath $database) -or -not (Test-Path -LiteralPath $helper)) {
    throw 'The NordVPN settings database or Opticon helper is missing.'
}
foreach ($app in $tailscaleApps) {
    if (-not (Test-Path -LiteralPath $app.Path)) { throw "Required Tailscale executable is missing: $($app.Path)" }
}

Get-Process -Name 'NordVPN' -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service -Name 'nordvpn-service' -Force
New-Item -Path $backup -ItemType Directory -Force | Out-Null
foreach ($name in @('settings.db', 'settings.db-wal', 'settings.db-shm')) {
    $path = Join-Path $nordRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $backup }
}
foreach ($settingsFile in Get-ChildItem -LiteralPath (Join-Path $nordRoot 'settings') -Filter '*.json' -File) {
    Copy-Item -LiteralPath $settingsFile.FullName -Destination $backup
}

& python $helper
if ($LASTEXITCODE -ne 0) { throw 'The NordVPN settings update failed.' }

foreach ($settingsFile in Get-ChildItem -LiteralPath (Join-Path $nordRoot 'settings') -Filter '*.json' -File) {
    $root = Get-Content -Raw -LiteralPath $settingsFile.FullName | ConvertFrom-Json
    if (-not $root.SettingsDto) { continue }
    $root.SettingsDto.IsSplitTunnelingEnabled = $true
    $root.SettingsDto.SplitTunnelingMode = 'vpnDisabledForApps'
    $root.SettingsDto.SplitTunnelingApps = @($tailscaleApps | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            DisplayName = $_.DisplayName
            Path = $_.Path
            StartupArgs = ''
            AppType = 'native'
            IconPath = $_.Path
        }
    })
    $json = $root | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($settingsFile.FullName, $json, [Text.UTF8Encoding]::new($false))
}

Start-Service -Name 'nordvpn-service'
Start-Process -FilePath 'C:\Program Files\NordVPN\NordVPN.exe'
Start-Sleep -Seconds 10
& 'C:\Program Files\NordVPN\NordVPN.exe' -c
Start-Sleep -Seconds 15
Restart-Service -Name 'Tailscale'
Start-Sleep -Seconds 8
Clear-DnsClientCache
Write-Host "NordVPN now excludes only Tailscale. Backup: $backup"
