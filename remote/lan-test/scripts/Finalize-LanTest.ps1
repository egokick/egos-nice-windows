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

function Enable-DockerDesktopCli {
    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    $dockerPath = if ($null -ne $command) {
        $command.Source
    }
    else {
        Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    }

    if (-not (Test-Path -LiteralPath $dockerPath -PathType Leaf)) {
        throw 'Docker Desktop is required. Start Docker Desktop and try again.'
    }

    $dockerDirectory = Split-Path -Parent $dockerPath
    if ($dockerDirectory -notin ($env:Path -split ';')) {
        $env:Path = $dockerDirectory + ';' + $env:Path
    }
}

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

function Assert-PrivateIpv4([string]$Value, [string]$ParameterName) {
    $address = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "$ParameterName must be an IPv4 address."
    }

    $bytes = $address.GetAddressBytes()
    $isPrivate = $bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $isPrivate) {
        throw "$ParameterName must be an RFC1918 private IPv4 address."
    }

    return $address.IPAddressToString
}

function Get-WslControllerEndpoint {
    $candidates = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
            $_.AddressState -eq 'Preferred' -and $_.InterfaceAlias -like 'vEthernet (WSL*' -and $_.IPAddress -notlike '169.254.*'
        })

    if ($candidates.Count -ne 1) {
        throw 'Expected exactly one Preferred IPv4 address on vEthernet (WSL*). Ensure Docker Desktop is using its WSL virtual network before finalizing.'
    }

    $candidate = $candidates[0]
    $address = Assert-PrivateIpv4 ([string]$candidate.IPAddress) 'WSL controller address'
    $adapter = Get-NetAdapter -InterfaceIndex $candidate.InterfaceIndex -ErrorAction Stop
    if ($adapter.Status -ne 'Up') {
        throw "The WSL controller interface is not up: $($candidate.InterfaceAlias)"
    }

    return [pscustomobject]@{
        Address = $address
        InterfaceAlias = [string]$candidate.InterfaceAlias
    }
}

function Protect-LocalSecret([string]$Path) {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $fileGrants = @(
        "$($identity):(F)",
        'SYSTEM:(F)',
        'BUILTIN\Administrators:(F)')
    $isDirectory = Test-Path -LiteralPath $Path -PathType Container

    if ($isDirectory) {
        & icacls $Path /inheritance:r /grant:r @fileGrants /T | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict existing local state below $Path."
        }

        $directoryGrants = @(
            "$($identity):(OI)(CI)F",
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

function Test-ReparsePoint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $item = Get-Item -LiteralPath $Path -Force
    return ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

function Protect-AdministratorOnlyPath(
    [string]$Path,
    [string]$ServiceIdentity = '',
    [switch]$Recursive) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Cannot protect a missing deployment path: $Path"
    }
    if (Test-ReparsePoint $Path) {
        throw "Refusing to trust a reparse-point deployment path: $Path"
    }

    $isDirectory = Test-Path -LiteralPath $Path -PathType Container
    $grants = if ($isDirectory) {
        @('SYSTEM:(OI)(CI)F', 'BUILTIN\Administrators:(OI)(CI)F')
    }
    else {
        @('SYSTEM:(F)', 'BUILTIN\Administrators:(F)')
    }
    if (-not [string]::IsNullOrWhiteSpace($ServiceIdentity)) {
        $grants += if ($isDirectory) {
            "${ServiceIdentity}:(OI)(CI)RX"
        }
        else {
            "${ServiceIdentity}:(RX)"
        }
    }

    $icaclsArguments = @($Path, '/inheritance:r', '/grant:r') + $grants
    if ($Recursive) {
        $icaclsArguments += '/T'
    }
    & icacls @icaclsArguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to restrict deployment path: $Path"
    }
}
function Write-AtomicUtf8File([string]$Path, [string]$Content) {
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $pendingPath = "$Path.pending"
    [System.IO.File]::WriteAllText($pendingPath, $Content, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $pendingPath -Destination $Path -Force
}

function Set-EnvironmentValue([string[]]$Lines, [string]$Name, [string]$Value) {
    $updatedLines = [System.Collections.Generic.List[string]]::new()
    $found = $false
    foreach ($line in $Lines) {
        if ($line -like "$Name=*") {
            if (-not $found) {
                $updatedLines.Add("$Name=$Value")
                $found = $true
            }

            continue
        }

        $updatedLines.Add($line)
    }

    if (-not $found) {
        $updatedLines.Add("$Name=$Value")
    }

    return $updatedLines.ToArray()
}

function Get-HeadscaleUsers([string[]]$ComposePrefix) {
    $userListOutput = & docker @($ComposePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'list', '--output', 'json'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list Headscale users. Verify the bootstrap stack is running.'
    }

    try {
        return @($userListOutput | Out-String | ConvertFrom-Json | Where-Object { $null -ne $_ })
    }
    catch {
        throw 'Headscale did not return a JSON user list; refusing to provision the Windows enrollment controller.'
    }
}

function Wait-For-RunningService([string[]]$ComposePrefix, [string]$ServiceName, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableChecks = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        $runningServices = & docker @($ComposePrefix + @('ps', '--status', 'running', '--services'))
        if ($LASTEXITCODE -eq 0 -and $ServiceName -in $runningServices) {
            $stableChecks++
            if ($stableChecks -ge 3) {
                return
            }
        }
        else {
            $stableChecks = 0
        }

        Start-Sleep -Seconds 2
    }

    throw "The $ServiceName service did not remain running. The LAN listener is still loopback-only."
}

function Assert-HeadscaleApiIsBlockedOnHost {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $response = $client.GetAsync('https://headscale.stayactive.test/api/v1/users').GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ne 404) {
                throw "Caddy returned HTTP $([int]$response.StatusCode) for a public Headscale API request."
            }
        }
        finally {
            $response.Dispose()
        }
    }
    catch {
        throw "Unable to verify that the public Caddy listener blocks Headscale API requests: $($_.Exception.Message)"
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Assert-EnrollmentControllerRoute {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $response = $client.GetAsync('https://remotehub.stayactive.test/api/v1/enrollment-tickets/00000000-0000-0000-0000-000000000000').GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ne 401) {
                throw "Caddy returned HTTP $([int]$response.StatusCode) instead of the controller authentication challenge."
            }
        }
        finally {
            $response.Dispose()
        }
    }
    catch {
        throw "Unable to verify Caddy-to-Windows enrollment-controller routing: $($_.Exception.Message)"
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Assert-ReviewedControllerInputs([string]$RepositoryRoot) {
    $keyAffectingPaths = @(
        'remote/EnrollmentBroker',
        'remote/lan-test/compose.yaml',
        'remote/lan-test/Caddyfile',
        'remote/lan-test/config/enrollmentbroker/appsettings.Production.json.template',
        'remote/lan-test/config/headscale/config.yaml.template',
        'remote/lan-test/config/headscale/policy.hujson',
        'remote/lan-test/config/image-pins.json',
        'remote/lan-test/config/keycloak/configure-scope-mappings.sh.template',
        'remote/lan-test/scripts/Initialize-LanTest.ps1',
        'remote/lan-test/scripts/Finalize-LanTest.ps1',
        'remote/lan-test/scripts/Start-LanTest.ps1',
        'remote/lan-test/scripts/Install-LanTestEnrollmentController.ps1',
        'remote/lan-test/scripts/Enable-LanTestEnrollmentControllerFirewall.ps1',
        'remote/lan-test/scripts/Enable-LanTestFirewall.ps1',
        'remote/lan-test/scripts/Set-LanTestHosts.ps1'
    )

    foreach ($path in $keyAffectingPaths) {
        $tracked = @(& git -C $RepositoryRoot ls-files -- $path)
        if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
            throw "The required controller provenance path is not tracked: $path"
        }
    }

    $changes = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all -- $keyAffectingPaths)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to verify the reviewed controller deployment inputs.'
    }
    if ($changes.Count -ne 0) {
        throw 'Commit every reviewed controller, Caddy, policy, and deployment-script input before finalizing the LAN test.'
    }

    $revisionOutput = @(& git -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}')
    if ($LASTEXITCODE -ne 0 -or $revisionOutput.Count -ne 1) {
        throw 'Unable to resolve the reviewed controller source revision.'
    }

    $revision = ([string]$revisionOutput[0]).Trim()
    if ($revision -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw 'Git returned an invalid reviewed controller source revision.'
    }

    return $revision.ToLowerInvariant()
}

function Get-ReviewedGitText(
    [string]$RepositoryRoot,
    [string]$Revision,
    [string]$RepositoryPath) {
    if ($RepositoryPath -notmatch '^remote/[A-Za-z0-9._/-]+$') {
        throw 'The requested reviewed deployment path is invalid.'
    }

    $content = @(& git -C $RepositoryRoot show "${Revision}:$RepositoryPath")
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to load the reviewed deployment artifact: $RepositoryPath"
    }

    return $content -join [Environment]::NewLine
}

function Get-ReviewedImagePins(
    [string]$RepositoryRoot,
    [string]$Revision) {
    try {
        $document = Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/config/image-pins.json' | ConvertFrom-Json
    }
    catch {
        throw 'The reviewed image-pin manifest is invalid.'
    }

    $requiredNames = @(
        'CADDY_IMAGE',
        'HEADSCALE_IMAGE',
        'MESHCENTRAL_IMAGE',
        'KEYCLOAK_IMAGE',
        'POSTGRES_IMAGE',
        'REMOTEHUB_SDK_IMAGE',
        'REMOTEHUB_RUNTIME_IMAGE')
    $unexpectedNames = @($document.PSObject.Properties.Name | Where-Object { $_ -notin $requiredNames })
    if ($unexpectedNames.Count -ne 0) {
        throw 'The reviewed image-pin manifest contains an unexpected image entry.'
    }

    $pins = @{}
    foreach ($name in $requiredNames) {
        $reference = [string]$document.$name
        if ($reference -notmatch '^[a-z0-9][a-z0-9./_-]*@sha256:[a-f0-9]{64}$') {
            throw "The reviewed image-pin manifest has an invalid $name reference."
        }
        $pins[$name] = $reference
    }

    return $pins
}

function ConvertTo-EnvironmentMap([string[]]$Lines) {
    $environment = @{}
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) {
            continue
        }
        if ($line -notmatch '^([A-Z_][A-Z0-9_]*)=(.*)$') {
            throw 'The LAN environment file contains an invalid line.'
        }

        $name = $matches[1]
        $value = $matches[2]
        if ($environment.ContainsKey($name)) {
            throw "The LAN environment file contains a duplicate $name entry."
        }
        if ($value.Contains("`r") -or $value.Contains("`n")) {
            throw "The LAN environment file contains an invalid $name value."
        }
        $environment[$name] = $value
    }

    return $environment
}

function New-ValidatedControllerEnvironment(
    [string[]]$EnvironmentLines,
    [hashtable]$ImagePins,
    [string]$RepositoryRoot) {
    $environment = ConvertTo-EnvironmentMap $EnvironmentLines
    # Older bootstrap runs predate the controller-address variable. Treat its
    # absence as the only safe compatibility case; finalization replaces this
    # loopback-only value before Caddy can route to the controller.
    if (-not $environment.ContainsKey('WINDOWS_ENROLLMENT_CONTROLLER_IP')) {
        $environment['WINDOWS_ENROLLMENT_CONTROLLER_IP'] = '127.0.0.1'
    }
    $orderedNames = @(
        'COMPOSE_PROJECT_NAME',
        'LAN_IP',
        'CADDY_BIND_ADDRESS',
        'STUN_BIND_ADDRESS',
        'CONTROL_NETWORK_CIDR',
        'CADDY_CONTROL_IP',
        'HEADSCALE_CONTROL_IP',
        'MESHCENTRAL_CONTROL_IP',
        'REMOTEHUB_CONTROL_IP',
        'KEYCLOAK_CONTROL_IP',
        'POSTGRES_CONTROL_IP',
        'WINDOWS_ENROLLMENT_CONTROLLER_IP',
        'CADDY_IMAGE',
        'HEADSCALE_IMAGE',
        'MESHCENTRAL_IMAGE',
        'KEYCLOAK_IMAGE',
        'POSTGRES_IMAGE',
        'REMOTEHUB_SDK_IMAGE',
        'REMOTEHUB_RUNTIME_IMAGE',
        'REMOTEHUB_SOURCE_REVISION',
        'POSTGRES_DB',
        'POSTGRES_USER',
        'POSTGRES_PASSWORD',
        'KC_BOOTSTRAP_ADMIN_USERNAME',
        'KC_BOOTSTRAP_ADMIN_PASSWORD')

    foreach ($name in $environment.Keys) {
        if ($name -notin $orderedNames) {
            throw "The LAN environment file contains an unapproved $name entry."
        }
    }
    foreach ($name in $orderedNames) {
        if (-not $environment.ContainsKey($name)) {
            throw "The LAN environment file is missing $name."
        }
    }

    $fixedValues = @{
        COMPOSE_PROJECT_NAME = 'stayactive-remotes-lan-test'
        CADDY_BIND_ADDRESS = '127.0.0.1'
        STUN_BIND_ADDRESS = '127.0.0.1'
        CONTROL_NETWORK_CIDR = '172.30.60.0/24'
        CADDY_CONTROL_IP = '172.30.60.10'
        HEADSCALE_CONTROL_IP = '172.30.60.11'
        MESHCENTRAL_CONTROL_IP = '172.30.60.12'
        REMOTEHUB_CONTROL_IP = '172.30.60.13'
        KEYCLOAK_CONTROL_IP = '172.30.60.14'
        POSTGRES_CONTROL_IP = '172.30.60.15'
        WINDOWS_ENROLLMENT_CONTROLLER_IP = '127.0.0.1'
        POSTGRES_DB = 'keycloak'
        POSTGRES_USER = 'keycloak'
        KC_BOOTSTRAP_ADMIN_USERNAME = 'stayactive-bootstrap'
    }
    foreach ($entry in $fixedValues.GetEnumerator()) {
        if ([string]$environment[$entry.Key] -ne [string]$entry.Value) {
            throw "The LAN environment file has an unexpected $($entry.Key) value."
        }
    }

    $environment.LAN_IP = Assert-PrivateIpv4 ([string]$environment.LAN_IP) 'LAN_IP'
    foreach ($imageName in $ImagePins.Keys) {
        if ([string]$environment[$imageName] -ne [string]$ImagePins[$imageName]) {
            throw "The LAN environment file does not use the reviewed immutable $imageName reference."
        }
    }

    foreach ($secretName in @('POSTGRES_PASSWORD', 'KC_BOOTSTRAP_ADMIN_PASSWORD')) {
        if ([string]$environment[$secretName] -notmatch '^[A-Za-z0-9_-]{43}$') {
            throw "The LAN environment file has an invalid $secretName format."
        }
    }

    $remoteHubRevision = [string]$environment.REMOTEHUB_SOURCE_REVISION
    if ($remoteHubRevision -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw 'The LAN environment file has an invalid RemoteHub source revision.'
    }
    $null = & git -C $RepositoryRoot rev-parse --verify "${remoteHubRevision}^{commit}"
    if ($LASTEXITCODE -ne 0) {
        throw 'The LAN environment file refers to an unavailable RemoteHub source revision.'
    }

    return @($orderedNames | ForEach-Object { "$_=$($environment[$_])" })
}
function New-ReviewedControllerDeployment(
    [string]$RepositoryRoot,
    [string]$LanRoot,
    [string[]]$EnvironmentLines,
    [string]$Revision) {
    $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($programData)) {
        throw 'Windows CommonApplicationData is unavailable for reviewed controller staging.'
    }

    $productRoot = Join-Path $programData 'StayActiveRemotes'
    $controllerRoot = Join-Path $productRoot 'EnrollmentController'
    $existingServiceIdentity = ''
    $existingControllerAccount = Get-LocalUser -Name 'StayActiveHeadscaleController' -ErrorAction SilentlyContinue
    if ($null -ne $existingControllerAccount) {
        $existingServiceIdentity = "$env:COMPUTERNAME\StayActiveHeadscaleController"
    }

    foreach ($path in @($productRoot, $controllerRoot)) {
        if (Test-ReparsePoint $path) {
            throw "Refusing to use a reparse-point controller root: $path"
        }
        [System.IO.Directory]::CreateDirectory($path) | Out-Null
        Protect-AdministratorOnlyPath $path $existingServiceIdentity
    }

    $stageParent = Join-Path $controllerRoot 'reviewed-artifacts'
    if (Test-ReparsePoint $stageParent) {
        throw "Refusing to use a reparse-point reviewed-artifact root: $stageParent"
    }
    [System.IO.Directory]::CreateDirectory($stageParent) | Out-Null
    Protect-AdministratorOnlyPath $stageParent

    $stageRoot = Join-Path $stageParent ("{0}-{1}" -f $Revision, [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    Protect-AdministratorOnlyPath $stageRoot

    $sourceArchivePath = Join-Path $stageRoot 'enrollmentbroker-source.zip'
    $sourceDirectory = Join-Path $stageRoot 'source'
    $artifactDirectory = Join-Path $stageRoot 'artifact'
    $templatePath = Join-Path $stageRoot 'appsettings.Production.json.template'
    $caddyfilePath = Join-Path $stageRoot 'Caddyfile'
    $composeFilePath = Join-Path $stageRoot 'compose.yaml'
    $headscaleConfigPath = Join-Path $stageRoot 'headscale-config.yaml'
    $bootstrapPolicyPath = Join-Path $stageRoot 'headscale-bootstrap-policy.hujson'
    $policyPath = Join-Path $stageRoot 'policy.hujson'
    $imagePinsPath = Join-Path $stageRoot 'image-pins.json'
    $keycloakMapperTemplatePath = Join-Path $stageRoot 'configure-scope-mappings.sh'
    $environmentPath = Join-Path $stageRoot 'lan-test.env'
    $installerPath = Join-Path $stageRoot 'Install-LanTestEnrollmentController.ps1'
    $controllerFirewallPath = Join-Path $stageRoot 'Enable-LanTestEnrollmentControllerFirewall.ps1'
    $lanFirewallPath = Join-Path $stageRoot 'Enable-LanTestFirewall.ps1'
    $hostsPath = Join-Path $stageRoot 'Set-LanTestHosts.ps1'
    $manifestPath = Join-Path $stageRoot 'deployment.manifest.json'

    # Git archive/show read the exact committed tree object by hash. A tray
    # process can no longer race-replace working-tree code after this point.
    & git -C $RepositoryRoot archive --format=zip "--output=$sourceArchivePath" $Revision -- remote/EnrollmentBroker
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to stage the reviewed Windows enrollment-controller source.'
    }
    Expand-Archive -LiteralPath $sourceArchivePath -DestinationPath $sourceDirectory -Force

    $sourceProjectPath = Join-Path $sourceDirectory 'remote\EnrollmentBroker\EnrollmentBroker.csproj'
    if (-not (Test-Path -LiteralPath $sourceProjectPath -PathType Leaf)) {
        throw 'The reviewed controller archive did not contain EnrollmentBroker.csproj.'
    }

    & dotnet publish $sourceProjectPath -c Release -r win-x64 --self-contained false --locked-mode --nologo --output $artifactDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to publish the reviewed Windows enrollment controller into protected staging.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $artifactDirectory 'EnrollmentBroker.exe') -PathType Leaf)) {
        throw 'The reviewed Windows enrollment-controller publish did not produce EnrollmentBroker.exe.'
    }

    Write-AtomicUtf8File $templatePath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/config/enrollmentbroker/appsettings.Production.json.template')
    Write-AtomicUtf8File $caddyfilePath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/Caddyfile')
    Write-AtomicUtf8File $composeFilePath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/compose.yaml')
    $headscaleConfiguration = (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/config/headscale/config.yaml.template').Replace('__CADDY_CONTROL_IP__', '172.30.60.10')
    if ($headscaleConfiguration -match '__[A-Z0-9_]+__') {
        throw 'The reviewed Headscale configuration contains an unrendered placeholder.'
    }
    Write-AtomicUtf8File $headscaleConfigPath $headscaleConfiguration
    # The final policy names stayactive-admin as the tag owner, so Headscale
    # must first boot using this protected deny-all policy while that owner is
    # created. Neither bootstrap phase ever mounts generated working-tree policy.
    Write-AtomicUtf8File $bootstrapPolicyPath "{`n  `"grants`": []`n}`n"
    Write-AtomicUtf8File $policyPath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/config/headscale/policy.hujson')
    Write-AtomicUtf8File $imagePinsPath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/config/image-pins.json')
    Write-AtomicUtf8File $keycloakMapperTemplatePath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/config/keycloak/configure-scope-mappings.sh.template')
    Write-AtomicUtf8File $installerPath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/scripts/Install-LanTestEnrollmentController.ps1')
    Write-AtomicUtf8File $controllerFirewallPath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/scripts/Enable-LanTestEnrollmentControllerFirewall.ps1')
    Write-AtomicUtf8File $lanFirewallPath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/scripts/Enable-LanTestFirewall.ps1')
    Write-AtomicUtf8File $hostsPath (Get-ReviewedGitText $RepositoryRoot $Revision 'remote/lan-test/scripts/Set-LanTestHosts.ps1')

    $stagedEnvironmentLines = Set-EnvironmentValue $EnvironmentLines 'STAYACTIVE_CADDYFILE' ($caddyfilePath -replace '\\', '/')
    $stagedEnvironmentLines = Set-EnvironmentValue $stagedEnvironmentLines 'STAYACTIVE_HEADSCALE_CONFIG' ($headscaleConfigPath -replace '\\', '/')
    $stagedEnvironmentLines = Set-EnvironmentValue $stagedEnvironmentLines 'STAYACTIVE_HEADSCALE_POLICY' ($bootstrapPolicyPath -replace '\\', '/')
    Write-AtomicUtf8File $environmentPath (($stagedEnvironmentLines -join [Environment]::NewLine) + [Environment]::NewLine)

    $manifest = [ordered]@{
        SchemaVersion = 1
        SourceRevision = $Revision
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ComposeFile = $composeFilePath
        EnvironmentFile = $environmentPath
        Caddyfile = $caddyfilePath
        HeadscaleConfiguration = $headscaleConfigPath
        BootstrapPolicyFile = $bootstrapPolicyPath
        HeadscalePolicy = $policyPath
        ImagePins = $imagePinsPath
        ControllerArtifactDirectory = $artifactDirectory
        ControllerConfigurationTemplate = $templatePath
        InstallerScript = $installerPath
        ControllerFirewallScript = $controllerFirewallPath
        LanFirewallScript = $lanFirewallPath
        HostsScript = $hostsPath
        PolicyFile = $policyPath
        KeycloakMapperTemplate = $keycloakMapperTemplatePath
    } | ConvertTo-Json -Compress
    Write-AtomicUtf8File $manifestPath $manifest

    # Every staged file is administrator/SYSTEM-only. The service gets only a
    # separately ACL'd copy of the published app and rendered configuration.
    Protect-AdministratorOnlyPath $stageRoot -Recursive

    return [pscustomobject]@{
        StageRoot = $stageRoot
        ManifestPath = $manifestPath
        SourceRevision = $Revision
        ComposeFile = $composeFilePath
        EnvironmentFile = $environmentPath
        Caddyfile = $caddyfilePath
        HeadscaleConfiguration = $headscaleConfigPath
        BootstrapPolicyFile = $bootstrapPolicyPath
        HeadscalePolicy = $policyPath
        ImagePins = $imagePinsPath
        ControllerArtifactDirectory = $artifactDirectory
        ControllerConfigurationTemplate = $templatePath
        InstallerScript = $installerPath
        ControllerFirewallScript = $controllerFirewallPath
        LanFirewallScript = $lanFirewallPath
        HostsScript = $hostsPath
        PolicyFile = $policyPath
        KeycloakMapperTemplate = $keycloakMapperTemplatePath
    }
}
Enable-DockerDesktopCli

if (-not $MeshCentralAdministratorCreated -or -not $EnableLan) {
    throw 'Refusing to publish the LAN endpoint. Re-run only after creating the single MeshCentral administrator, with both confirmation switches.'
}

$lanRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $lanRoot '..\..')).Path
$environmentFile = Join-Path $lanRoot '.env'
$policyDestination = Join-Path $lanRoot 'generated\headscale\policy.hujson'
$meshCentralConfigPath = Join-Path $lanRoot 'generated\meshcentral\config.json'
$rootCertificatePath = Join-Path $lanRoot 'certs\caddy-root.crt'

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw 'LAN test is not initialized. Run Initialize-LanTest.ps1 first.'
}

foreach ($path in @($policyDestination, $meshCentralConfigPath, $rootCertificatePath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required LAN test file is missing: $path"
    }
}

$environmentLines = [System.IO.File]::ReadAllLines($environmentFile)
$lanIp = Assert-PrivateIpv4 (Get-EnvironmentValue $environmentLines 'LAN_IP') 'LAN_IP'
$caddyBindAddress = Get-EnvironmentValue $environmentLines 'CADDY_BIND_ADDRESS'
$stunBindAddress = Get-EnvironmentValue $environmentLines 'STUN_BIND_ADDRESS'
$caddyControlIp = Assert-PrivateIpv4 (Get-EnvironmentValue $environmentLines 'CADDY_CONTROL_IP') 'CADDY_CONTROL_IP'
if ($caddyBindAddress -ne '127.0.0.1' -or $stunBindAddress -ne '127.0.0.1') {
    throw 'Finalization requires both HTTPS and STUN to still be loopback-only. Refusing to proceed from a previously LAN-bound bootstrap state.'
}

$controllerEndpoint = Get-WslControllerEndpoint
if ($controllerEndpoint.Address -eq $lanIp) {
    throw 'The controller must bind to the WSL virtual-interface address, not the LAN-published Caddy address.'
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

# Freeze all credential-bearing deployment inputs from the exact reviewed Git
# commit before creating the Headscale policy owner or controller API key.
$reviewedRevision = Assert-ReviewedControllerInputs $repoRoot
$reviewedImagePins = Get-ReviewedImagePins $repoRoot $reviewedRevision
$validatedEnvironmentLines = New-ValidatedControllerEnvironment $environmentLines $reviewedImagePins $repoRoot
$reviewedDeployment = New-ReviewedControllerDeployment $repoRoot $lanRoot $validatedEnvironmentLines $reviewedRevision
$composePrefix = @(
    'compose',
    '--project-directory', $lanRoot,
    '--env-file', $reviewedDeployment.EnvironmentFile,
    '-f', $reviewedDeployment.ComposeFile)

# Recreate both key-adjacent containers before any Headscale administrative
# command. The running bootstrap containers must not retain a mutable Caddyfile,
# Headscale config, policy, Compose definition, or .env file at key-mint time.
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'caddy', 'headscale')) 'Unable to start Caddy and Headscale from the reviewed protected deployment.'
Wait-For-RunningService $composePrefix 'caddy' 30
Wait-For-RunningService $composePrefix 'headscale' 45

$headscaleUsers = Get-HeadscaleUsers $composePrefix
$policyOwner = $headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' } | Select-Object -First 1
if ($null -eq $policyOwner) {
    Invoke-Docker ($composePrefix + @('exec', '-T', 'headscale', 'headscale', 'users', 'create', 'stayactive-admin')) 'Unable to create the Headscale policy owner.'
    $headscaleUsers = Get-HeadscaleUsers $composePrefix
    $policyOwner = $headscaleUsers | Where-Object { $_.name -eq 'stayactive-admin' } | Select-Object -First 1
}

if ($null -eq $policyOwner -or [string]$policyOwner.id -notmatch '^[1-9][0-9]*$') {
    throw 'The stayactive-admin Headscale policy owner was not created with a valid numeric id.'
}

# Switch only the protected environment file from the reviewed deny-all policy
# to the reviewed production policy, then recreate Headscale so there is no
# mutable generated-policy mount during controller-key minting.
$stagedEnvironmentLines = [System.IO.File]::ReadAllLines($reviewedDeployment.EnvironmentFile)
$stagedEnvironmentLines = Set-EnvironmentValue $stagedEnvironmentLines 'STAYACTIVE_HEADSCALE_POLICY' ($reviewedDeployment.PolicyFile -replace '\\', '/')
Write-AtomicUtf8File $reviewedDeployment.EnvironmentFile (($stagedEnvironmentLines -join [Environment]::NewLine) + [Environment]::NewLine)
Protect-AdministratorOnlyPath $reviewedDeployment.EnvironmentFile
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'headscale')) 'Unable to apply the reviewed protected Headscale policy.'
Wait-For-RunningService $composePrefix 'headscale' 45

$stagedEnvironmentLines = Set-EnvironmentValue $stagedEnvironmentLines 'WINDOWS_ENROLLMENT_CONTROLLER_IP' $controllerEndpoint.Address
Write-AtomicUtf8File $reviewedDeployment.EnvironmentFile (($stagedEnvironmentLines -join [Environment]::NewLine) + [Environment]::NewLine)
Protect-AdministratorOnlyPath $reviewedDeployment.EnvironmentFile

Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'caddy')) 'Unable to reload Caddy with the Windows enrollment-controller route.'
Wait-For-RunningService $composePrefix 'caddy' 30
& $reviewedDeployment.HostsScript -Mode Bootstrap -Confirm:$false
Assert-HeadscaleApiIsBlockedOnHost

& $reviewedDeployment.InstallerScript `
    -ControllerArtifactDirectory $reviewedDeployment.ControllerArtifactDirectory `
    -ConfigurationTemplatePath $reviewedDeployment.ControllerConfigurationTemplate `
    -HeadscaleUserId ([string]$policyOwner.id) `
    -ControllerListenAddress $controllerEndpoint.Address `
    -EnvironmentFile $reviewedDeployment.EnvironmentFile `
    -ComposeFile $reviewedDeployment.ComposeFile `
    -ComposeProjectDirectory $lanRoot
& $reviewedDeployment.ControllerFirewallScript -ControllerAddress $controllerEndpoint.Address -CaddyAddress $caddyControlIp -InterfaceAlias $controllerEndpoint.InterfaceAlias -Confirm:$false
Assert-EnrollmentControllerRoute

$meshCentralConfig = [System.IO.File]::ReadAllText($meshCentralConfigPath)
if ($meshCentralConfig -match '"newAccounts"\s*:\s*true\b') {
    $meshCentralConfig = [regex]::Replace($meshCentralConfig, '("newAccounts"\s*:\s*)true\b', '$1false', 1)
}
elseif ($meshCentralConfig -notmatch '"newAccounts"\s*:\s*false\b') {
    throw 'MeshCentral configuration does not contain a boolean newAccounts setting.'
}

if ($meshCentralConfig -notmatch '"newAccounts"\s*:\s*false\b') {
    throw 'Refusing to expose the LAN endpoint while MeshCentral account registration is enabled.'
}

$tempMeshCentralConfigPath = "$meshCentralConfigPath.pending"
[System.IO.File]::WriteAllText($tempMeshCentralConfigPath, $meshCentralConfig, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempMeshCentralConfigPath -Destination $meshCentralConfigPath -Force
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'meshcentral')) 'Unable to restart MeshCentral with account registration disabled.'

$updatedEnvironmentLines = $stagedEnvironmentLines | ForEach-Object {
    if ($_ -like 'CADDY_BIND_ADDRESS=*') {
        "CADDY_BIND_ADDRESS=$lanIp"
    }
    elseif ($_ -like 'STUN_BIND_ADDRESS=*') {
        "STUN_BIND_ADDRESS=$lanIp"
    }
    else {
        $_
    }
}
Write-AtomicUtf8File $reviewedDeployment.EnvironmentFile (($updatedEnvironmentLines -join [Environment]::NewLine) + [Environment]::NewLine)
Protect-AdministratorOnlyPath $reviewedDeployment.EnvironmentFile

& $reviewedDeployment.LanFirewallScript -Confirm:$false
& $reviewedDeployment.HostsScript -Mode Lan -ServerIp $lanIp -Confirm:$false
Invoke-Docker ($composePrefix + @('up', '-d', '--force-recreate', 'caddy', 'headscale')) 'Unable to rebind Caddy and Headscale STUN to the configured LAN address.'
Assert-HeadscaleApiIsBlockedOnHost

$activeDeploymentPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'StayActiveRemotes\EnrollmentController\active-deployment.json'
$activeDeployment = [ordered]@{
    SchemaVersion = 1
    SourceRevision = $reviewedDeployment.SourceRevision
    StageManifest = $reviewedDeployment.ManifestPath
    ComposeFile = $reviewedDeployment.ComposeFile
    EnvironmentFile = $reviewedDeployment.EnvironmentFile
    KeycloakMapperTemplate = $reviewedDeployment.KeycloakMapperTemplate
} | ConvertTo-Json -Compress
Write-AtomicUtf8File $activeDeploymentPath $activeDeployment
Protect-AdministratorOnlyPath $activeDeploymentPath

Write-Host "The self-hosted control plane is now available to the local subnet at $lanIp on HTTPS and STUN only."
Write-Host 'The Windows enrollment controller is reachable only from Caddy over the WSL virtual interface; its Headscale API key remains in the dedicated service account Windows Credential Manager.'
Write-Host 'Do not distribute enrollment keys or the operator password through chat or email.'