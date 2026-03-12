$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$stageDir = Join-Path $PSScriptRoot "AppxStage"
$buildDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows"
$packagePath = Join-Path $PSScriptRoot "CopilotScreenshotProvider.msix"
$makeAppx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

dotnet build ".\CopilotScreenshotProvider.csproj" -c Release | Out-Null

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $buildDir "*") $stageDir -Recurse -Force
Copy-Item ".\Assets" $stageDir -Recurse -Force
Copy-Item ".\Public" $stageDir -Recurse -Force
Copy-Item ".\AppxManifest.xml" $stageDir -Force

if (Test-Path $packagePath) {
    Remove-Item $packagePath -Force
}

& $makeAppx pack /d $stageDir /p $packagePath /o | Out-Null
& $signtool sign /fd SHA256 /f ".\CopilotScreenshotProviderLeaf.pfx" /p "copilot-screenshot-provider" $packagePath | Out-Null

Import-Certificate -FilePath ".\CopilotScreenshotProviderRoot.cer" -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
Import-Certificate -FilePath ".\CopilotScreenshotProviderLeaf.cer" -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Add-AppxPackage -Path ".\CopilotScreenshotProvider.msix"

Write-Host "Installed Copilot Screenshot Provider."
