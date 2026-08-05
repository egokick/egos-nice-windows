[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$local = [Environment]::GetFolderPath('LocalApplicationData')
$exe = Join-Path $local 'Programs\Opticon\Opticon.exe'
$stage = Join-Path $PSScriptRoot '..\Taildesk-source-1.0.0\artifacts\Opticon-CommandCenter-win-x64\App\Opticon.exe'
$process = Get-CimInstance Win32_Process -Filter "Name='Opticon.exe'" | Select-Object -First 1
$legacyProcesses = @(Get-Process -Name 'Taildesk.Admin' -ErrorAction SilentlyContinue)
$configPath = Join-Path $local 'Taildesk\Admin\admin.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

$shell = New-Object -ComObject WScript.Shell
$linkPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Opticon.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Startup')) 'Opticon.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Opticon.lnk')
)
$links = foreach ($path in $linkPaths) {
    $shortcut = $shell.CreateShortcut($path)
    [pscustomobject]@{
        Path = $path
        Target = $shortcut.TargetPath
        Icon = $shortcut.IconLocation
    }
}

$tailscaleExe = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
$tailscaleStatus = & $tailscaleExe status --json | ConvertFrom-Json
$listener = Get-NetTCPConnection -State Listen -LocalPort $config.coordinatorPort -ErrorAction SilentlyContinue |
    Select-Object -First 1
$route = Get-NetRoute -DestinationPrefix '213.188.217.227/32' -ErrorAction SilentlyContinue |
    Sort-Object RouteMetric |
    Select-Object -First 1

$fly = (Invoke-WebRequest -UseBasicParsing 'https://taildesk-egokick-control.fly.dev/health' -TimeoutSec 20).StatusCode
$github = (Invoke-WebRequest -UseBasicParsing 'https://github.com' -TimeoutSec 20).StatusCode
$cloudflare = (Invoke-WebRequest -UseBasicParsing 'https://www.cloudflare.com/cdn-cgi/trace' -TimeoutSec 20).StatusCode

Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
$iconSize = '{0}x{1}' -f $icon.Width, $icon.Height
$icon.Dispose()

[pscustomobject]@{
    OpticonRunning = [bool]$process
    OpticonProcessPath = $process.ExecutablePath
    LegacyTaildeskProcessCount = $legacyProcesses.Count
    InstalledHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    PackageHash = (Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash
    EmbeddedIcon = $iconSize
    SetupComplete = $config.setupComplete
    HeadscaleControlUrl = $config.headscaleControlUrl
    CoordinatorPort = $config.coordinatorPort
    CoordinatorListening = [bool]$listener
    TailscaleBackendState = $tailscaleStatus.BackendState
    TailscaleSelfOnline = $tailscaleStatus.Self.Online
    TailscaleAddresses = ($tailscaleStatus.TailscaleIPs -join ',')
    FlyHealth = $fly
    Github = $github
    Cloudflare = $cloudflare
    FlyHostRoutePresent = [bool]$route
    FlyHostRouteNextHop = $route.NextHop
    FlyHostRouteInterface = $route.InterfaceAlias
    Shortcuts = @($links)
} | ConvertTo-Json -Depth 4
