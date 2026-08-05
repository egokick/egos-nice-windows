#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$source = 'C:\source\egos-nice-windows\Taildesk-source-1.0.0\artifacts\admin-hotfix-1.0.3\Opticon.exe'
$installDirectory = 'C:\Program Files\Taildesk\Admin'
$destination = Join-Path $installDirectory 'Opticon.exe'
$backup = Join-Path $installDirectory 'Opticon.exe.previous-1.0.2'
$expectedSigner = 'FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53'

if (-not (Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $destination)) {
    throw 'The signed hotfix or installed Opticon executable is missing.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $source
if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $expectedSigner -or
    $signature.Status -in @('NotSigned', 'HashMismatch')) {
    throw 'The Opticon hotfix did not match the pinned signing certificate.'
}

Get-Process -Name 'Opticon' -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not (Test-Path -LiteralPath $backup)) {
    Copy-Item -LiteralPath $destination -Destination $backup
}
Copy-Item -LiteralPath $source -Destination $destination -Force
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
    throw 'The installed Opticon hotfix did not match its signed source.'
}

Write-Host 'The signed Opticon coordinator hotfix was installed successfully.'
