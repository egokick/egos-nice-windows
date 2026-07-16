#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$CertificatePath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'certs\caddy-root.crt')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Caddy root certificate is missing: $CertificatePath. Start the LAN bootstrap first."
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
try {
    if ($certificate.Subject.IndexOf('Caddy Local Authority', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'The supplied certificate is not the expected Caddy local root authority.'
    }

    $existing = Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    if ($null -eq $existing -and $PSCmdlet.ShouldProcess('LocalMachine\Root', "Trust Caddy root certificate $($certificate.Thumbprint)")) {
        Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }

    $validated = Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    if ($null -eq $validated) {
        throw 'Caddy root certificate was not found in the Local Machine trusted root store after import.'
    }

    Write-Host "Caddy root certificate is trusted by LocalMachine: $($certificate.Thumbprint)"
}
finally {
    $certificate.Dispose()
}