[CmdletBinding()]
param(
    [switch]$SkipReleaseBuild,
    [switch]$KeepEnvironment
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Opticon.LocalE2E.Docker\compose.yaml'
$acceptanceContext = Join-Path $PSScriptRoot 'Opticon.InviteAcceptance.Docker'
$integrationProject = Join-Path $PSScriptRoot 'Taildesk.HostedInviteIntegration\Taildesk.HostedInviteIntegration.csproj'
$artifactDirectory = Join-Path $repo 'fly-headscale\artifacts'
$manifestPath = Join-Path $artifactDirectory 'manifest.json'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpticonLocalE2E-' + [Guid]::NewGuid().ToString('N'))
$inputDirectory = Join-Path $tempRoot 'input'
$outputDirectory = Join-Path $tempRoot 'output'
$configPath = Join-Path $tempRoot 'admin.json'
$inputPath = Join-Path $inputDirectory 'input.json'
$resultPath = Join-Path $outputDirectory 'result.json'
$rootCertificatePath = Join-Path $tempRoot 'root.crt'
$deviceContainer = 'opticon-e2e-device'
$hubContainer = 'opticon-e2e-hub'
$tailscaleImage = 'tailscale/tailscale:v1.98.9@sha256:f15d5d3f4a68773a853180b72496f70ba614b64de0878c43fe3da39fe0afba47'
$adminHmac = 'local-e2e-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$apiKey = 'bootstrap-placeholder'
$importedRootThumbprint = ''
$rootWasAlreadyTrusted = $false
$startedDocker = $false

function Invoke-Docker {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: docker $($Arguments -join ' ')" }
}

function Invoke-Compose {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Invoke-Docker (@('compose', '-f', $compose) + $Arguments)
}

function Set-ComposeEnvironment {
    param([Parameter(Mandatory)][pscustomobject]$Artifact)
    $env:OPTICON_E2E_ADMIN_HMAC_KEY = $adminHmac
    $env:OPTICON_E2E_HEADSCALE_API_KEY = $apiKey
    $env:OPTICON_E2E_SOURCE_KEY_ID = [string]$Artifact.sourceManifestKeyId
    $env:OPTICON_E2E_PRODUCT_SIGNER = [string]$Artifact.productSignerThumbprint
    $env:OPTICON_E2E_SIGNING_PROFILE = [string]$Artifact.signingProfile
}

function Get-CurrentArtifact {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $items = @($manifest.artifacts | Where-Object product -CEQ 'OpticonSource')
    if ([int]$manifest.schemaVersion -ne 2 -or $items.Count -ne 1) {
        throw 'The real source builder did not produce one schema-2 Opticon source artifact.'
    }
    return $items[0]
}

function Wait-File {
    param([Parameter(Mandatory)][string]$Path, [int]$Seconds = 120)
    for ($attempt = 0; $attempt -lt $Seconds * 2; $attempt++) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return }
        $state = & docker inspect --format '{{.State.Status}}' $deviceContainer 2>$null
        if ($LASTEXITCODE -eq 0 -and $state -eq 'exited') {
            $detail = (& docker logs --tail 80 $deviceContainer 2>&1) -join [Environment]::NewLine
            throw "The Docker device exited before producing its result:`n$detail"
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $Path"
}

try {
    New-Item -ItemType Directory -Path $inputDirectory, $outputDirectory -Force | Out-Null
    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        docker desktop start
        if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop could not be started.' }
        $startedDocker = $true
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            docker info *> $null
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 1
        }
        if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop did not become ready.' }
    }

    $props = [xml](Get-Content -Raw -LiteralPath (Join-Path $repo 'Directory.Build.props'))
    $version = [string]$props.Project.PropertyGroup.Version
    $existing = Get-CurrentArtifact
    $sourceThumbprint = [string]$existing.sourceManifestKeyId
    $productThumbprint = [string]$existing.productSignerThumbprint
    $signingProfile = [string]$existing.signingProfile

    if (-not $SkipReleaseBuild) {
        $signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse |
            Where-Object FullName -Match '\\x64\\signtool\.exe$' |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        if ([string]::IsNullOrWhiteSpace($signTool)) { throw 'The x64 Windows SignTool was not found.' }
        Write-Host "Building and signing the real Opticon $version source release..."
        & (Join-Path $repo 'fly-headscale\scripts\Build-OpticonSourceRelease.ps1') `
            -Version $version -SigningProfile $signingProfile `
            -SourceReleaseCertificateThumbprint $sourceThumbprint `
            -ProductCertificateThumbprint $productThumbprint `
            -Rfc3161TimestampUrl 'http://timestamp.digicert.com' `
            -SignToolPath $signTool
        if ($LASTEXITCODE -ne 0) { throw 'The real Opticon source-release build failed.' }
    }

    $artifact = Get-CurrentArtifact
    if ([string]$artifact.version -ne $version) {
        throw "The local artifact is $($artifact.version), but this checkout is $version. Run without -SkipReleaseBuild."
    }
    $artifact | Add-Member -MemberType NoteProperty -Name downloadUrl `
        -Value "https://local-e2e.cloudfront.net/opticon/releases/$version/$($artifact.file)" -Force
    $manifest = [ordered]@{ schemaVersion = 2; artifacts = @($artifact) }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

    $sourcePath = Join-Path $artifactDirectory ([string]$artifact.file)
    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Item -LiteralPath $sourcePath).Length -ne [long]$artifact.size -or
        $sourceHash -ne ([string]$artifact.sha256).ToLowerInvariant()) {
        throw 'The locally served source archive does not match the real builder manifest.'
    }

    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($sourcePath)
    try {
        $entry = $archive.GetEntry('source-manifest.json')
        if ($null -eq $entry) { throw 'The source archive has no signed inner manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $inner = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }

    Set-ComposeEnvironment $artifact
    Invoke-Compose @('down', '--volumes', '--remove-orphans')
    Write-Host 'Building and starting the real gateway image with local infrastructure edges...'
    Invoke-Compose @('build', 'gateway')
    Invoke-Compose @('up', '-d')

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker run --rm --network host curlimages/curl:8.16.0 -ksSf https://localhost:18443/health *> $null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 1
    }
    if ($LASTEXITCODE -ne 0) { throw 'The local Opticon gateway did not become healthy.' }

    Invoke-Compose @('exec', '-T', 'gateway', '/ko-app/headscale', 'users', 'create', 'admin@taildesk.local')
    $apiKey = (& docker compose -f $compose exec -T gateway /ko-app/headscale apikeys create --expiration 2h |
        Where-Object { $_ -match '^hskey-api-' } | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $apiKey -notmatch '^hskey-api-') { throw 'Headscale did not create the disposable API key.' }
    $hubKey = (& docker compose -f $compose exec -T gateway /ko-app/headscale preauthkeys create -u 1 --tags tag:taildesk-hub --expiration 2h |
        Where-Object { $_ -match '^hskey-auth-' } | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $hubKey -notmatch '^hskey-auth-') { throw 'Headscale did not create the disposable hub key.' }
    Set-ComposeEnvironment $artifact
    Invoke-Compose @('up', '-d', '--force-recreate', 'gateway')

    Invoke-Compose @('cp', 'caddy:/data/caddy/pki/authorities/local/root.crt', $rootCertificatePath)
    $root = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
    $importedRootThumbprint = $root.Thumbprint
    $rootWasAlreadyTrusted = $null -ne (Get-ChildItem Cert:\CurrentUser\Root |
        Where-Object Thumbprint -EQ $importedRootThumbprint | Select-Object -First 1)
    & certutil.exe -user -addstore Root $rootCertificatePath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The disposable local TLS root could not be trusted.' }

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { $null = Invoke-RestMethod https://localhost:18443/health -TimeoutSec 3; break }
        catch { if ($attempt -eq 29) { throw }; Start-Sleep -Seconds 1 }
    }

    & docker rm -f $hubContainer *> $null
    $hubId = & docker run -d --name $hubContainer --network host --read-only --cap-drop=ALL `
        --security-opt no-new-privileges --tmpfs /tmp:rw,noexec,nosuid,size=32m `
        --mount "type=bind,source=$rootCertificatePath,target=/run/opticon-root.crt,readonly" `
        -e SSL_CERT_FILE=/run/opticon-root.crt --entrypoint tailscaled $tailscaleImage `
        --tun=userspace-networking --socket=/tmp/tailscaled.sock --state=/tmp/tailscaled.state
    $hubId = ([string]$hubId).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($hubId)) { throw 'The disposable hub container did not start.' }
    Start-Sleep -Seconds 1
    Invoke-Docker @('exec', $hubContainer, 'tailscale', '--socket=/tmp/tailscaled.sock', 'up',
        '--login-server=https://localhost:18443', "--auth-key=$hubKey", '--hostname=opticon-e2e-hub', '--accept-dns=false')
    $hubStatus = (& docker exec $hubContainer tailscale --socket=/tmp/tailscaled.sock status --json | ConvertFrom-Json)
    $hubIp = @($hubStatus.Self.TailscaleIPs | Where-Object { $_ -match '^100\.' })[0]
    if ([string]::IsNullOrWhiteSpace($hubIp)) { throw 'The disposable hub has no Tailscale IPv4 identity.' }

    Write-Host 'Building the Docker device adapter and publishable Command Center integration driver...'
    Invoke-Docker @('build', '--pull', '--tag', 'opticon-invite-acceptance:local', $acceptanceContext)
    $buildProperties = @(
        "-p:OpticonSigningProfile=$signingProfile",
        "-p:OpticonSourceReleaseKeyId=$sourceThumbprint",
        "-p:OpticonSourceReleaseCertificateBase64=$($inner.sourceReleaseCertificateBase64)",
        "-p:OpticonProductSignerThumbprint=$productThumbprint",
        "-p:OpticonProductSigningCertificateBase64=$($inner.productSigningCertificateBase64)"
    )
    & dotnet build $integrationProject -c Release @buildProperties
    if ($LASTEXITCODE -ne 0) { throw 'The real Command Center integration driver did not build.' }
    $driver = Join-Path $PSScriptRoot 'Taildesk.HostedInviteIntegration\bin\Release\net10.0-windows10.0.19041.0\Taildesk.HostedInviteIntegration.dll'

    Write-Host 'Running the real release preflight and creating a real signed one-use invitation...'
    & dotnet $driver --local-e2e-create $configPath https://localhost:18443 $adminHmac 1 $inputPath 'Opticon Docker E2E device'
    if ($LASTEXITCODE -ne 0) { throw 'The real Command Center invitation flow failed.' }

    & docker rm -f $deviceContainer *> $null
    $deviceId = & docker run -d --name $deviceContainer --network host --read-only --cap-drop=ALL `
        --security-opt no-new-privileges --pids-limit=96 --memory=512m --cpus=2 `
        --tmpfs /tmp:rw,noexec,nosuid,size=64m `
        --mount "type=bind,source=$inputDirectory,target=/run/opticon-input,readonly" `
        --mount "type=bind,source=$outputDirectory,target=/run/opticon-output" `
        --mount "type=bind,source=$rootCertificatePath,target=/run/opticon-root.crt,readonly" `
        -e SSL_CERT_FILE=/run/opticon-root.crt opticon-invite-acceptance:local
    $deviceId = ([string]$deviceId).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($deviceId)) { throw 'The Docker device did not start.' }
    Wait-File -Path $resultPath -Seconds 180
    $acceptance = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($acceptance.status -ne 'passed' -or [string]::IsNullOrWhiteSpace([string]$acceptance.tailnetDeviceId)) {
        throw 'The Docker device did not complete real invitation acceptance and Tailscale enrollment.'
    }

    Write-Host 'Running the production enrollment transaction and real Command Center refresh...'
    & dotnet $driver --local-e2e-enroll $configPath $resultPath $hubIp
    if ($LASTEXITCODE -ne 0) { throw 'Command Center did not display the enrolled Docker device.' }

    Write-Host ''
    Write-Host 'PASS Opticon local Docker E2E: built/deployed source, accepted invite, connected device, and Command Center visibility verified.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $cleanupManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
            foreach ($cleanupArtifact in @($cleanupManifest.artifacts)) {
                if ([string]$cleanupArtifact.downloadUrl -like 'https://local-e2e.cloudfront.net/*') {
                    $cleanupArtifact.PSObject.Properties.Remove('downloadUrl')
                }
            }
            [IO.File]::WriteAllText($manifestPath, ($cleanupManifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        } catch { Write-Warning "Could not remove the local E2E manifest URL: $($_.Exception.Message)" }
    }
    if (-not $KeepEnvironment) {
        & docker kill $deviceContainer $hubContainer *> $null
        & docker rm -f $deviceContainer $hubContainer *> $null
        if (Test-Path -LiteralPath $compose) {
            & docker compose -f $compose kill *> $null
            & docker compose -f $compose down --volumes --remove-orphans *> $null
        }
        if (-not $rootWasAlreadyTrusted -and -not [string]::IsNullOrWhiteSpace($importedRootThumbprint)) {
            & certutil.exe -user -delstore Root $importedRootThumbprint *> $null
        }
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
        if ($startedDocker) { docker desktop stop *> $null }
    } else {
        Write-Host "E2E environment retained. Temporary state: $tempRoot" -ForegroundColor Yellow
    }
    foreach ($name in @('OPTICON_E2E_ADMIN_HMAC_KEY','OPTICON_E2E_HEADSCALE_API_KEY','OPTICON_E2E_SOURCE_KEY_ID','OPTICON_E2E_PRODUCT_SIGNER','OPTICON_E2E_SIGNING_PROFILE')) {
        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    }
}
