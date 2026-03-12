$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $scriptDir "CopilotScreenshotProvider.csproj"
$buildDir = Join-Path $scriptDir "bin\Release\net10.0-windows"
$stageDir = Join-Path $scriptDir "AppxStage"
$packagePath = Join-Path $scriptDir "CopilotScreenshotProvider.msix"
$cerPath = Join-Path $scriptDir "CopilotScreenshotProvider.cer"
$pfxPath = Join-Path $scriptDir "CopilotScreenshotProvider.pfx"
$publisher = "CN=Egos Nice Windows"
$passwordText = "copilot-screenshot-provider"
$makeAppx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

$env:DOTNET_CLI_HOME = Join-Path $scriptDir ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

dotnet build $projectFile -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item (Join-Path $buildDir '*') $stageDir -Recurse -Force
Copy-Item (Join-Path $scriptDir 'Assets') $stageDir -Recurse -Force
Copy-Item (Join-Path $scriptDir 'Public') $stageDir -Recurse -Force
Copy-Item (Join-Path $scriptDir 'AppxManifest.xml') $stageDir -Force

Add-Type -AssemblyName System.Security
$myStore = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
$myStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$existingCert = $myStore.Certificates | Where-Object { $_.Subject -eq $publisher } | Sort-Object NotAfter -Descending | Select-Object -First 1
$myStore.Close()

if (-not $existingCert) {
    $existingCert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName "Copilot Screenshot Provider" -CertStoreLocation "Cert:\CurrentUser\My"
}

[IO.File]::WriteAllBytes($cerPath, $existingCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
[IO.File]::WriteAllBytes($pfxPath, $existingCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $passwordText))

$certToImport = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)

$trustedPeople = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'CurrentUser')
$trustedPeople.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$trustedPeople.Add($certToImport)
$trustedPeople.Close()

$rootStore = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
$rootStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$rootStore.Add($certToImport)
$rootStore.Close()

if (Test-Path $packagePath) {
    Remove-Item $packagePath -Force
}

& $makeAppx pack /d $stageDir /p $packagePath /o | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed with exit code $LASTEXITCODE."
}

& $signtool sign /fd SHA256 /f $pfxPath /p $passwordText $packagePath | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE."
}

$packageName = 'EgosNiceWindows.CopilotScreenshotProvider'
Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $packagePath -ForceApplicationShutdown

Write-Output "Installed package: $packagePath"
Write-Output "App AUMID: EgosNiceWindows.CopilotScreenshotProvider!App"
Write-Output "Open Settings > Personalization > Text input > Customize Copilot key on keyboard > Custom and pick Copilot Screenshot Provider."
