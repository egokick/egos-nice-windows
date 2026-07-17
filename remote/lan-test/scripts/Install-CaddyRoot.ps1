#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$CertificatePath = (Join-Path (Join-Path $PSScriptRoot '..') 'certs\caddy-root.crt'),

    [string]$ExpectedCertificateSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Caddy root certificate is missing: $CertificatePath. Start the LAN bootstrap first."
}

$resolvedCertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
$defaultCertificatePath = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '..') 'certs\caddy-root.crt'))
$isDefaultLocalCertificate = [string]::Equals(
    $resolvedCertificatePath,
    $defaultCertificatePath,
    [System.StringComparison]::OrdinalIgnoreCase)

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedCertificatePath)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $certificateSha256 = ([System.BitConverter]::ToString($sha256.ComputeHash($certificate.RawData))).Replace('-', '')

    if ([string]::IsNullOrWhiteSpace($ExpectedCertificateSha256)) {
        if (-not $isDefaultLocalCertificate) {
            throw 'ExpectedCertificateSha256 is required when trusting a certificate copied from another machine.'
        }
    }
    else {
        $expected = ($ExpectedCertificateSha256 -replace '\s', '').ToUpperInvariant()
        if ($expected -notmatch '^[0-9A-F]{64}$') {
            throw 'ExpectedCertificateSha256 must be exactly 64 hexadecimal characters.'
        }

        if (-not [string]::Equals($expected, $certificateSha256, [System.StringComparison]::Ordinal)) {
            throw 'The copied Caddy root certificate does not match the separately verified SHA-256 fingerprint.'
        }
    }

    if ($certificate.Subject.IndexOf('Caddy Local Authority', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'The supplied certificate is not the expected Caddy local root authority.'
    }

    $basicConstraints = $certificate.Extensions |
        Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] } |
        Select-Object -First 1
    if ($null -eq $basicConstraints -or -not $basicConstraints.CertificateAuthority) {
        throw 'The supplied certificate is not a certificate-authority root.'
    }

    $existing = Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    if ($null -eq $existing -and $PSCmdlet.ShouldProcess('LocalMachine\Root', "Trust Caddy root certificate $certificateSha256")) {
        Import-Certificate -FilePath $resolvedCertificatePath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }

    $validated = Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    if ($null -eq $validated) {
        throw 'Caddy root certificate was not found in the Local Machine trusted root store after import.'
    }

    Write-Host "Caddy root certificate is trusted by LocalMachine. Certificate SHA-256: $certificateSha256"
}
finally {
    $sha256.Dispose()
    $certificate.Dispose()
}