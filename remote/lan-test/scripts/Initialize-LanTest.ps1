[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LanIp,

    [switch]$SkipImagePull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $lanRoot '..\..')).Path
$environmentFile = Join-Path $lanRoot '.env'

function Get-DockerPath {
    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $desktopDocker = Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $desktopDocker) {
        return $desktopDocker
    }

    throw 'Docker Desktop is required. Start Docker Desktop and try again.'
}

function Assert-PrivateIpv4([string]$Value) {
    $address = $null
    if (
        -not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'LanIp must be an IPv4 address.'
    }

    $bytes = $address.GetAddressBytes()
    $isPrivate = $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $isPrivate) {
        throw 'LanIp must be an RFC1918 private IPv4 address; do not use a public address for this LAN test.'
    }

    return $address.IPAddressToString
}

function New-RandomBytes([int]$ByteCount) {
    [byte[]]$bytes = New-Object byte[] $ByteCount
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
        return ,$bytes
    }
    finally {
        $random.Dispose()
    }
}

function Clear-SensitiveBytes([byte[]]$Bytes) {
    if ($null -ne $Bytes) {
        [Array]::Clear($Bytes, 0, $Bytes.Length)
    }
}

function New-Base64Secret([int]$ByteCount) {
    [byte[]]$bytes = New-RandomBytes $ByteCount
    try {
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        Clear-SensitiveBytes $bytes
    }
}

function New-UrlSafePassword {
    [byte[]]$bytes = New-RandomBytes 32
    try {
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally {
        Clear-SensitiveBytes $bytes
    }
}

function Write-Utf8File([string]$Path, [string]$Content) {
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), $utf8NoBom)
}

function Protect-LocalSecret([string]$Path) {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $fileGrants = @(
        "${identity}:(F)",
        'SYSTEM:(F)',
        'BUILTIN\Administrators:(F)')
    $isDirectory = Test-Path -LiteralPath $Path -PathType Container

    if ($isDirectory) {
        # Existing descendants need access on the object itself. Apply plain
        # full-control ACEs recursively first, then put inheritable ACEs back
        # on the directory for future container-created files.
        & icacls $Path /inheritance:r /grant:r @fileGrants /T | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict existing local state below $Path."
        }

        $directoryGrants = @(
            "${identity}:(OI)(CI)F",
            'SYSTEM:(OI)(CI)F',
            'BUILTIN\Administrators:(OI)(CI)F')
        & icacls $Path /inheritance:r /grant:r @directoryGrants | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict future local state below $Path."
        }

        return
    }

    & icacls $Path /inheritance:r /grant:r @fileGrants | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to restrict access to $Path."
    }
}

function Render-Template([string]$TemplatePath, [string]$OutputPath, [hashtable]$Replacements) {
    $text = [System.IO.File]::ReadAllText($TemplatePath)
    foreach ($key in $Replacements.Keys) {
        $text = $text.Replace($key, [string]$Replacements[$key])
    }

    if ($text -match '__[A-Z0-9_]+__') {
        throw "Unrendered placeholder remains in $OutputPath."
    }

    Write-Utf8File $OutputPath $text
}

$lanIp = Assert-PrivateIpv4 $LanIp
if (Test-Path -LiteralPath $environmentFile) {
    throw "A LAN test is already initialized at $environmentFile. Do not overwrite its secrets or state."
}

foreach ($path in @(
    (Join-Path $lanRoot 'generated'),
    (Join-Path $lanRoot 'state'),
    (Join-Path $lanRoot 'certs'),
    (Join-Path $lanRoot 'secrets'))) {
    if (Test-Path -LiteralPath $path) {
        throw "Refusing to reuse pre-existing local state path: $path"
    }
}

$gitRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRevision)) {
    throw 'Unable to determine the reviewed Git revision.'
}

$workingTree = (& git -C $repoRoot status --porcelain) | Out-String
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine whether the reviewed Git revision has a clean working tree.'
}

if (-not [string]::IsNullOrWhiteSpace($workingTree)) {
    throw 'Commit the reviewed source before initializing the LAN test. The RemoteHub image label must identify an immutable revision.'
}

$docker = Get-DockerPath
$dockerDirectory = Split-Path -Parent $docker
if ($dockerDirectory -notin ($env:Path -split ';')) {
    # Docker Desktop stores docker-credential-desktop.exe beside docker.exe.
    # Calling the CLI by absolute path is not enough: registry pulls also need
    # the helper to be discoverable on PATH.
    $env:Path = $dockerDirectory + ';' + $env:Path
}

& $docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop is not ready.'
}

$imageInputs = [ordered]@{
    CADDY_IMAGE = 'caddy:2.10.2'
    HEADSCALE_IMAGE = 'headscale/headscale:0.29.2'
    MESHCENTRAL_IMAGE = 'ghcr.io/ylianst/meshcentral:1.1.59@sha256:7a619610e187fece8d3a15b3ac9448412d32baa1a9e63c1922b328eaeea17f10'
    KEYCLOAK_IMAGE = 'quay.io/keycloak/keycloak:26.6.3'
    POSTGRES_IMAGE = 'postgres:17.6'
    REMOTEHUB_SDK_IMAGE = 'mcr.microsoft.com/dotnet/sdk:10.0'
    REMOTEHUB_RUNTIME_IMAGE = 'mcr.microsoft.com/dotnet/aspnet:10.0'
}

$imagePins = [ordered]@{}
foreach ($entry in $imageInputs.GetEnumerator()) {
    if (-not $SkipImagePull) {
        & $docker pull $entry.Value
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to pull the reviewed image input $($entry.Value)."
        }
    }

    $digestOutput = & $docker image inspect $entry.Value --format '{{index .RepoDigests 0}}' 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker could not inspect the reviewed image input $($entry.Value)."
    }

    $digest = ($digestOutput | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($digest) -or -not $digest.Contains('@sha256:')) {
        if ($entry.Value.Contains('@sha256:')) {
            $digest = $entry.Value
        }
        else {
            throw "Docker did not provide a digest-pinned reference for $($entry.Value)."
        }
    }

    $imagePins[$entry.Key] = $digest
    Write-Host "$($entry.Key): $digest"
}

$paths = @{
    Generated = Join-Path $lanRoot 'generated'
    State = Join-Path $lanRoot 'state'
    Certificates = Join-Path $lanRoot 'certs'
    Secrets = Join-Path $lanRoot 'secrets'
}
foreach ($directory in @(
    (Join-Path $paths.Generated 'headscale'),
    (Join-Path $paths.Generated 'meshcentral'),
    (Join-Path $paths.Generated 'remotehub'),
    (Join-Path $paths.Generated 'keycloak'),
    (Join-Path $paths.State 'caddy\data'),
    (Join-Path $paths.State 'caddy\config'),
    (Join-Path $paths.State 'headscale'),
    (Join-Path $paths.State 'meshcentral\data'),
    (Join-Path $paths.State 'meshcentral\files'),
    (Join-Path $paths.State 'remotehub\journal'),
    $paths.Certificates,
    $paths.Secrets)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$operatorUsername = 'stayactive-operator'
$operatorPassword = New-UrlSafePassword
$postgresPassword = New-UrlSafePassword
$keycloakBootstrapPassword = New-UrlSafePassword
$meshSessionKey = New-Base64Secret 48
$meshRecordsKey = New-Base64Secret 48
$remoteHubJournalHmacKey = New-Base64Secret 48

Render-Template (Join-Path $lanRoot 'config\headscale\config.yaml.template') (Join-Path $paths.Generated 'headscale\config.yaml') @{ '__CADDY_CONTROL_IP__' = '172.30.60.10' }

# Headscale cannot validate tag ownership until the owner exists. Start with a
# deny-all bootstrap policy; Finalize-LanTest.ps1 creates the owner then applies
# the reviewed policy.
Write-Utf8File (Join-Path $paths.Generated 'headscale\policy.hujson') "{`n  `"grants`": []`n}`n"

Render-Template (Join-Path $lanRoot 'config\meshcentral\config.json.template') (Join-Path $paths.Generated 'meshcentral\config.json') @{
    '__CADDY_CONTROL_IP__' = '172.30.60.10'
    '__MESHCENTRAL_SESSION_KEY__' = $meshSessionKey
    '__MESHCENTRAL_RECORDS_KEY__' = $meshRecordsKey
    '__MESHCENTRAL_NEW_ACCOUNTS__' = 'true'
}

Render-Template (Join-Path $lanRoot 'config\remotehub\appsettings.Production.json.template') (Join-Path $paths.Generated 'remotehub\appsettings.Production.json') @{ '__REMOTEHUB_JOURNAL_HMAC_KEY_BASE64__' = $remoteHubJournalHmacKey }

Render-Template (Join-Path $lanRoot 'config\keycloak\stayactive-realm.json.template') (Join-Path $paths.Generated 'keycloak\stayactive-realm.json') @{
    '__OPERATOR_USERNAME__' = $operatorUsername
    '__OPERATOR_PASSWORD__' = $operatorPassword
}

$keycloakScopeMappings = [System.IO.File]::ReadAllText((Join-Path $lanRoot 'config\keycloak\configure-scope-mappings.sh.template')).Replace("`r`n", "`n").Replace("`r", "`n")
Write-Utf8File (Join-Path $paths.Generated 'keycloak\configure-scope-mappings.sh') $keycloakScopeMappings

$environmentLines = @(
    'COMPOSE_PROJECT_NAME=stayactive-remotes-lan-test',
    "LAN_IP=$lanIp",
    # HTTPS and STUN remain loopback-only until the initial MeshCentral
    # administrator exists and Finalize-LanTest.ps1 is explicitly confirmed.
    'CADDY_BIND_ADDRESS=127.0.0.1',
    'STUN_BIND_ADDRESS=127.0.0.1',
    'CONTROL_NETWORK_CIDR=172.30.60.0/24',
    'CADDY_CONTROL_IP=172.30.60.10',
    'HEADSCALE_CONTROL_IP=172.30.60.11',
    'MESHCENTRAL_CONTROL_IP=172.30.60.12',
    'REMOTEHUB_CONTROL_IP=172.30.60.13',
    'KEYCLOAK_CONTROL_IP=172.30.60.14',
    'POSTGRES_CONTROL_IP=172.30.60.15',
    "CADDY_IMAGE=$($imagePins.CADDY_IMAGE)",
    "HEADSCALE_IMAGE=$($imagePins.HEADSCALE_IMAGE)",
    "MESHCENTRAL_IMAGE=$($imagePins.MESHCENTRAL_IMAGE)",
    "KEYCLOAK_IMAGE=$($imagePins.KEYCLOAK_IMAGE)",
    "POSTGRES_IMAGE=$($imagePins.POSTGRES_IMAGE)",
    "REMOTEHUB_SDK_IMAGE=$($imagePins.REMOTEHUB_SDK_IMAGE)",
    "REMOTEHUB_RUNTIME_IMAGE=$($imagePins.REMOTEHUB_RUNTIME_IMAGE)",
    "REMOTEHUB_SOURCE_REVISION=$gitRevision",
    'POSTGRES_DB=keycloak',
    'POSTGRES_USER=keycloak',
    "POSTGRES_PASSWORD=$postgresPassword",
    'KC_BOOTSTRAP_ADMIN_USERNAME=stayactive-bootstrap',
    "KC_BOOTSTRAP_ADMIN_PASSWORD=$keycloakBootstrapPassword"
)
Write-Utf8File $environmentFile (($environmentLines -join "`n") + "`n")

$bootstrapSecrets = [ordered]@{
    OperatorUsername = $operatorUsername
    OperatorPassword = $operatorPassword
    CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json
$secretFile = Join-Path $paths.Secrets 'operator-bootstrap.json'
Write-Utf8File $secretFile $bootstrapSecrets

# Generated configuration contains keys and the one-time operator password.
# Restrict it to the current Windows user, SYSTEM, and local administrators.
foreach ($path in @($environmentFile, $paths.Generated, $paths.State, $paths.Certificates, $paths.Secrets)) {
    Protect-LocalSecret $path
}

Write-Host 'LAN test initialized with digest-pinned images and local-only secrets.'
Write-Host 'Caddy remains bound to 127.0.0.1 until MeshCentral bootstrap is explicitly finalized.'
Write-Host 'Next: run scripts/Start-LanTest.ps1, install the local root certificate, and create the single initial MeshCentral administrator.'