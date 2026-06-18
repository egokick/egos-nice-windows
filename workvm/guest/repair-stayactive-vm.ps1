$ErrorActionPreference = 'Stop'

$source = 'W:\.cache\stayactive-vm-publish\stayactive.exe'
$targetDir = Join-Path $env:USERPROFILE 'Apps\StayActive\publish'
$target = Join-Path $targetDir 'stayactive.exe'
$settingsDir = Join-Path $env:LOCALAPPDATA 'StayActive'
$settingsPath = Join-Path $settingsDir 'settings.json'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\StayActive.lnk'
$log = Join-Path $env:USERPROFILE 'Desktop\stayactive-vm-repair.log'

function Write-Step {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -LiteralPath $log -Value $line
    Write-Host $line
}

Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
Write-Step "Starting StayActive VM repair."

if (-not (Test-Path -LiteralPath $source)) {
    throw "Updated StayActive executable was not found at $source"
}

Get-Process stayactive -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Step "Installed updated StayActive to $target"

New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
@{
    StartupInitialized = $true
    IsActive = $true
    JiggleMouseEnabled = $true
    TypeTextEnabled = $false
    DimScreenWhenActiveEnabled = $false
    EnableAfterInactivityEnabled = $false
    EnableAfterInactivitySeconds = 300
} | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
Write-Step "Wrote active jiggle settings to $settingsPath"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startup)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = $targetDir
$shortcut.Save()
Write-Step "Updated startup shortcut at $startup"

Start-Process -FilePath $target
Start-Sleep -Seconds 3

$proc = Get-Process stayactive -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    throw 'StayActive did not start.'
}

Add-Type -Namespace Native -Name Cursor -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetCursorPos(out POINT lpPoint);
'@

$before = New-Object Native.Cursor+POINT
[void][Native.Cursor]::GetCursorPos([ref]$before)
Start-Sleep -Seconds 25
$after = New-Object Native.Cursor+POINT
[void][Native.Cursor]::GetCursorPos([ref]$after)

Write-Step "StayActive PID: $($proc.Id)"
Write-Step "Cursor before: $($before.X),$($before.Y)"
Write-Step "Cursor after: $($after.X),$($after.Y)"
Write-Step "Repair complete."

