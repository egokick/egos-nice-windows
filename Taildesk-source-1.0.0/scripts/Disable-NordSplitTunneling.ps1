#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$nordRoot = 'C:\ProgramData\NordVPN'
$database = Join-Path $nordRoot 'settings.db'
$helper = 'C:\source\egos-nice-windows\Taildesk-source-1.0.0\scripts\Disable-NordSplitTunneling.py'
$backup = Join-Path $nordRoot ('OpticonBackups\split-tunneling-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

if (-not (Test-Path -LiteralPath $database) -or -not (Test-Path -LiteralPath $helper)) {
    throw 'The NordVPN settings database or Opticon helper is missing.'
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
    $settings = Get-Content -Raw -LiteralPath $settingsFile.FullName | ConvertFrom-Json
    if ($settings.PSObject.Properties.Name -contains 'IsSplitTunnelingEnabled') {
        $settings.IsSplitTunnelingEnabled = $false
        $json = $settings | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($settingsFile.FullName, $json, [Text.UTF8Encoding]::new($false))
    }
}

Start-Service -Name 'nordvpn-service'
Start-Process -FilePath 'C:\Program Files\NordVPN\NordVPN.exe'
Start-Sleep -Seconds 10
Clear-DnsClientCache
Write-Host "NordVPN split tunneling is disabled. Backup: $backup"
