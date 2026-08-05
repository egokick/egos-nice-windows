[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$publish = Join-Path $artifacts "publish-$Runtime"
$stage = Join-Path $artifacts "Opticon-CommandCenter-$Runtime"
$dist = Join-Path $repo 'dist'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0'
}

dotnet build (Join-Path $repo 'Taildesk.sln') -c Release -p:EnableWindowsTargeting=true
dotnet run --project (Join-Path $repo 'tests/Taildesk.SelfTest/Taildesk.SelfTest.csproj') -c Release -p:EnableWindowsTargeting=true

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $publish -ItemType Directory | Out-Null
New-Item (Join-Path $stage 'App/Payload') -ItemType Directory -Force | Out-Null
New-Item $dist -ItemType Directory -Force | Out-Null

$publishArgs = @(
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $selfContained,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:EnableWindowsTargeting=true'
)

foreach ($project in @('Taildesk.Agent', 'Taildesk.Admin', 'Taildesk.Setup', 'Taildesk.InviteLauncher')) {
    $projectFile = Join-Path $repo "src/$project/$project.csproj"
    $output = Join-Path $publish $project.Replace('Taildesk.', '')
    dotnet publish $projectFile @publishArgs -o $output
}

$app = Join-Path $stage 'App'
Copy-Item (Join-Path $publish 'Admin/*') $app -Recurse -Force
Copy-Item (Join-Path $publish 'Setup') (Join-Path $app 'Payload/Setup') -Recurse -Force
Copy-Item (Join-Path $publish 'Agent') (Join-Path $app 'Payload/Agent') -Recurse -Force
Copy-Item (Join-Path $publish 'Admin') (Join-Path $app 'Payload/Admin') -Recurse -Force
Copy-Item (Join-Path $publish 'InviteLauncher') (Join-Path $app 'Payload/InviteLauncher') -Recurse -Force

$signingThumbprint = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'
$signingCertificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Thumbprint -eq $signingThumbprint -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $signingCertificate) { throw "Opticon signing certificate $signingThumbprint with private key was not found." }
foreach ($executable in Get-ChildItem $app -Filter '*.exe' -Recurse) {
    $null = Set-AuthenticodeSignature -LiteralPath $executable.FullName -Certificate $signingCertificate -HashAlgorithm SHA256
    $signature = Get-AuthenticodeSignature -LiteralPath $executable.FullName
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $signingThumbprint -or
        $signature.Status -in @('NotSigned', 'HashMismatch')) {
        throw "Authenticode signing failed for $($executable.FullName)."
    }
}
Copy-Item (Join-Path $repo 'installer/Install-CommandCenter.ps1') (Join-Path $stage 'Install-Opticon.ps1') -Force
New-Item (Join-Path $stage 'Tools') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo 'scripts/Install-TaildeskFlyRouteTask.ps1') (Join-Path $stage 'Tools') -Force
Copy-Item (Join-Path $repo 'scripts/Set-TaildeskFlyBypassRoute.ps1') (Join-Path $stage 'Tools') -Force
Copy-Item (Join-Path $repo 'installer/README-OPTICON-START-HERE.txt') (Join-Path $stage 'README-START-HERE.txt') -Force
Copy-Item (Join-Path $repo 'README-OPTICON.md') (Join-Path $stage 'README.md') -Force
Copy-Item (Join-Path $repo 'docs') (Join-Path $stage 'docs') -Recurse -Force
New-Item (Join-Path $stage 'config') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $repo 'config/headscale-policy.hujson') (Join-Path $stage 'config') -Force

$zip = Join-Path $dist "Opticon-CommandCenter-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Built $zip" -ForegroundColor Green
