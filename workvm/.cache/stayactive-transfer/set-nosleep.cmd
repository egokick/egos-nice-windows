@echo off
setlocal

echo === StayActive process ===
tasklist /fi "imagename eq stayactive.exe"

echo.
echo === Startup shortcut ===
if exist "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\StayActive.lnk" (
  echo StartupShortcutExists=True
) else (
  echo StartupShortcutExists=False
)

echo.
echo === Edge bookmark ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$p=Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data\Default\Bookmarks'; if (Test-Path $p) { 'Windows365BookmarkExists=' + ((Get-Content $p -Raw).Contains('https://windows365.microsoft.com/ent#/devices')) } else { 'Windows365BookmarkExists=False' }"

echo.
echo === Applying no-sleep display policy ===
powercfg.exe /change standby-timeout-ac 0
powercfg.exe /change standby-timeout-dc 0
powercfg.exe /change monitor-timeout-ac 0
powercfg.exe /change monitor-timeout-dc 0
powercfg.exe /change hibernate-timeout-ac 0
powercfg.exe /change hibernate-timeout-dc 0

echo.
echo === Standby idle ===
powercfg.exe /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE | findstr /i "Current"

echo.
echo === Hibernate idle ===
powercfg.exe /query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE | findstr /i "Current"

echo.
echo === Video idle ===
powercfg.exe /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE | findstr /i "Current"

echo.
echo Done.

