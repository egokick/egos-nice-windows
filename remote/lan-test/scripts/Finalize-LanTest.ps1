#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$MeshCentralAdministratorCreated,

    [Parameter(Mandatory)]
    [switch]$EnableLan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $MeshCentralAdministratorCreated -or -not $EnableLan) {
    throw 'Refusing to publish the LAN endpoint. Re-run only after creating the single MeshCentral administrator, with both confirmation switches.'
}

$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$environmentFile = Join-Path $lanRoot '.env'
$composeFile = Join-Path $lanRoot 'compose.yaml'
$policySource = Join-Path $lanRoot 'config\headscale\policy.hujson'
$policyDestination = Join-Path $lanRoot 'generated\headscale\policy.hujson'
$rootCertificatePath = Join-Path $lanRoot 'certs\caddy-root.crt'
$firewallScript = Join-Path $PSScriptRoot 'Enable-LanTestFirewall.ps1'
$hostsScript = Join-Path $PSScriptRoot 'Set-LanTestHosts.ps1'

function Invoke-Docker([string[]]$Arguments, [string]$FailureMessage) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-EnvironmentValue([string[]]$Lines, [string]$Name) {
    $line = $Lines | Where-Object { $_ -like "$Name=*" } | Select-Object -First 1
    if ($null -eq $line) {
        throw "Missing $Name in the LAN test environment file."
    }

    return $line.Substring($Name.Length + 1)
}

function Assert-PrivateIpv4([string]$Value) {
    $address = $null
    if (
        -not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'LAN_IP must be an IPv4 address.'
    }

    $bytes = $address.GetAddressBytes()
    if (-not (
            $bytes[0] -eq 10 -or
            ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
            ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) {
        throw 'LAN_IP must remain an RFC1918 private IPv4 address.'
    }

    return $address.IPAddressToString
}

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw 'LAN test is not initialized. Run Initialize-LanTest.ps1 first.'
}

foreach ($path in @($composeFile, $policySource, $policyDestination, $rootCertificatePath, $firewallScript, $hostsScript)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required LAN test file is missing: $path"
    }
}

$environmentLines = [System.IO.File]::ReadAllLines($environmentFile)
$lanIp = Assert-PrivateIpv4 (Get-EnvironmentValue $environmentLines 'LAN_IP')
$caddyBindAddress = Get-EnvironmentValue $environmentLines 'CADDY_BIND_ADDRESS'
if ($caddyBindAddress -ne '127.0.0.1' -and $caddyBindAddress -ne $lanIp) {
    throw 'CADDY_BIND_ADDRESS is neither the guarded bootstrap loopback address nor the configured LAN address.'
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
try {
    if ($null -eq (Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue)) {
        throw 'Install the Caddy root certificate into LocalMachine\Root before exposing the LAN endpoint.'
    }
}
finally {
    $certificate.Dispose()
}

$composePrefix = @('compose', '--env-file', $environmentFile, '-f', $composeFile)
$userListOutput = & docker @($composePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'list', '--output', 'json'))
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to list Headscale users. Verify the bootstrap stack is running.'
}

try {
    $headscaleUsers = @($userListOutput | Out-String | ConvertFrom-Json)
}
catch {
    throw 'Headscale did not return a JSON user list; refusing to change the policy.'
}

if (-not ($headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' })) {
    Invoke-Docker ($composePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'create', 'stayactive-admin')) 'Unable to create the Headscale policy owner.'
}

Copy-Item -LiteralPath $policySource -Destination $policyDestination -Force
Invoke-Docker ($composePrefix + @('restart', 'headscale')) 'Unable to apply the reviewed Headscale policy.'

# Open the two narrowly scoped firewall rules before rebinding Caddy. Until
# Caddy is restarted, its listener is still loopback-only.
& $firewallScript -Confirm:$false
& $hostsScript -Mode Lan -ServerIp $lanIp -Confirm:$false

$updatedEnvironmentLines = $environmentLines | ForEach-Object {
    if ($_ -like 'CADDY_BIND_ADDRESS=*') {
        "CADDY_BIND_ADDRESS=$lanIp"
    }
    else {
        $_
    }
}
$tempEnvironmentFile = "$environmentFile.pending"
[System.IO.File]::WriteAllLines($tempEnvironmentFile, $updatedEnvironmentLines, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempEnvironmentFile -Destination $environmentFile -Force

Invoke-Docker ($composePrefix + @('up', '-d', 'caddy')) 'Unable to rebind Caddy to the configured LAN address.'

Write-Host "The self-hosted control plane is now available to the local subnet at $lanIp on HTTPS and STUN only."
Write-Host 'Do not distribute enrollment keys or the operator password through chat or email.'