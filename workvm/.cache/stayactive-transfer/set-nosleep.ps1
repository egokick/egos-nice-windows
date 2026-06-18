$ErrorActionPreference = 'Continue'

Write-Host '=== StayActive process ==='
Get-Process stayactive -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, Path | Format-List

Write-Host '=== Startup shortcut ==='
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\StayActive.lnk'
Write-Host ("StartupShortcutExists={0}" -f (Test-Path $shortcut))

Write-Host '=== Edge bookmark ==='
$bookmarkPath = Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data\Default\Bookmarks'
if (Test-Path $bookmarkPath) {
  $bookmarks = Get-Content $bookmarkPath -Raw
  Write-Host ("Windows365BookmarkExists={0}" -f ($bookmarks.Contains('https://windows365.microsoft.com/ent#/devices')))
} else {
  Write-Host 'Windows365BookmarkExists=False'
}

Write-Host '=== Applying no-sleep display policy ==='
powercfg.exe /change standby-timeout-ac 0
powercfg.exe /change standby-timeout-dc 0
powercfg.exe /change monitor-timeout-ac 0
powercfg.exe /change monitor-timeout-dc 0
powercfg.exe /change hibernate-timeout-ac 0
powercfg.exe /change hibernate-timeout-dc 0

Write-Host '=== Standby idle ==='
powercfg.exe /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE

Write-Host '=== Hibernate idle ==='
powercfg.exe /query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE

Write-Host '=== Video idle ==='
powercfg.exe /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE

