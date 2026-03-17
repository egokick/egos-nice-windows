@echo off
setlocal
set "DOTNET_CLI_HOME=%~dp0.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DEMO_URL=http://127.0.0.1:48211/api/network-info"

powershell -NoProfile -Command ^
  "$ProgressPreference='SilentlyContinue';" ^
  "try {" ^
  "  $info = Invoke-RestMethod '%DEMO_URL%' -TimeoutSec 2;" ^
  "  Write-Host 'Hotspot Phone Demo is already running.';" ^
  "  if ($info.hotspotSsid) { Write-Host ('Hotspot SSID: ' + $info.hotspotSsid); }" ^
  "  if ($info.hotspotState) { Write-Host ('Hotspot state: ' + $info.hotspotState); }" ^
  "  if ($info.recommendedUrl) { Write-Host ('Phone URL: ' + $info.recommendedUrl); }" ^
  "  if ($info.instruction) { Write-Host ('Instruction: ' + $info.instruction); }" ^
  "  exit 0;" ^
  "} catch { exit 1 }"

if not errorlevel 1 exit /b 0

dotnet run --project "%~dp0HotspotPhoneDemo\HotspotPhoneDemo.csproj" %*
