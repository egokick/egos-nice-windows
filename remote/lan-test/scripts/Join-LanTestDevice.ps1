#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ServerIp,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCertificateSha256,

    [string]$CertificatePath = (Join-Path (Join-Path $PSScriptRoot '..') 'certs\caddy-root.crt'),

    [switch]$AdvertiseExitNode,

    [switch]$InstallTailscale,

    [switch]$PublicPinned,

    [switch]$ForceReenroll,

    [Security.SecureString]$EnrollmentKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $principal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an Administrator PowerShell window.'
    }
}

function Resolve-TailscaleExecutable {
    $command = Get-Command tailscale.exe -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    foreach ($path in @(
            (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'),
            (Join-Path $env:LOCALAPPDATA 'Tailscale\tailscale.exe'))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return $path
        }
    }

    return $null
}

function Install-TailscaleIfRequested {
    param([switch]$Requested)

    $existing = Resolve-TailscaleExecutable
    if ($null -ne $existing) {
        return $existing
    }

    if (-not $Requested) {
        throw 'The Tailscale client is not installed. Install it with winget (or re-run this script with -InstallTailscale), then retry.'
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        throw 'winget is unavailable. Install the supported Tailscale Windows client, then rerun this script.'
    }

    & $winget.Source install `
        --exact `
        --id Tailscale.Tailscale `
        --silent `
        --disable-interactivity `
        --custom 'TS_NOLAUNCH=1' `
        --accept-package-agreements `
        --accept-source-agreements 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to install the Tailscale client.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $installed = $null
    $installedService = $null
    do {
        $installed = Resolve-TailscaleExecutable
        $installedService = Get-Service -Name Tailscale -ErrorAction SilentlyContinue
        if ($null -ne $installed -and $null -ne $installedService) {
            break
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($null -eq $installed -or $null -eq $installedService) {
        throw 'Tailscale installation completed but its executable and service did not become available within 90 seconds.'
    }

    return $installed
}

function Ensure-TailscaleServiceRunning {
    [Environment]::SetEnvironmentVariable('TS_NO_LOGS_NO_SUPPORT', 'true', [EnvironmentVariableTarget]::Machine)
    $service = Get-Service -Name Tailscale -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw 'The Tailscale Windows service is not installed.'
    }
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Start-Service -Name Tailscale -ErrorAction Stop
    }
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
}

function Restart-TailscaleServiceAndDisableSupportLogs {
    [Environment]::SetEnvironmentVariable('TS_NO_LOGS_NO_SUPPORT', 'true', [EnvironmentVariableTarget]::Machine)
    Restart-Service -Name Tailscale -Force -ErrorAction Stop
    (Get-Service -Name Tailscale).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
}

function Set-TailscaleMachinePolicy([bool]$ShouldAdvertiseExitNode) {
    $policyPath = 'HKLM:\SOFTWARE\Policies\Tailscale'
    $null = New-Item -Path $policyPath -Force
    $values = @{
        LoginURL = 'https://headscale.stayactive.test'
        UseTailscaleDNSSettings = 'always'
        UnattendedMode = 'always'
        InstallUpdates = 'never'
        AdminConsole = 'hide'
        OnboardingFlow = 'hide'
        AdvertiseExitNode = if ($ShouldAdvertiseExitNode) { 'always' } else { 'never' }
    }
    foreach ($entry in $values.GetEnumerator()) {
        $null = New-ItemProperty -Path $policyPath -Name $entry.Key -Value $entry.Value -PropertyType String -Force
    }
}

function Get-TailscaleStatus([string]$Executable) {
    $statusJson = & $Executable status --json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($statusJson -join [Environment]::NewLine))) {
        return $null
    }
    try {
        return (($statusJson -join [Environment]::NewLine) | ConvertFrom-Json)
    }
    catch {
        throw 'Tailscale returned an invalid status response.'
    }
}

function Assert-ReenrollmentSafe([string]$Executable, [bool]$AllowReenroll) {
    $status = Get-TailscaleStatus $Executable
    if ($null -eq $status) {
        return
    }

    $hasIdentity = -not [string]::IsNullOrWhiteSpace([string]$status.Self.ID) -or
        (([string]$status.Self.PublicKey) -notmatch '^nodekey:0+$' -and -not [string]::IsNullOrWhiteSpace([string]$status.Self.PublicKey))
    if (($status.BackendState -eq 'Running' -or $hasIdentity) -and -not $AllowReenroll) {
        throw 'This laptop is already enrolled in a Tailscale or Headscale network. Rerun with -ForceReenroll only if replacing that configuration is intentional.'
    }

    if ($AllowReenroll -and ($status.BackendState -eq 'Running' -or $hasIdentity)) {
        & $Executable logout 1>$null 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to leave the existing Tailscale or Headscale network.'
        }
    }
}

function Read-OneTimeJoinCommand([Security.SecureString]$SuppliedEnrollmentKey) {
    $secureCommand = $SuppliedEnrollmentKey
    $pointer = [IntPtr]::Zero
    try {
        if ($null -eq $secureCommand) {
            Write-Host 'Paste the one-time command shown by StayActive Remotes > Add device. It will not be echoed or written to PowerShell history by this script.'
            $secureCommand = Read-Host -AsSecureString 'One-time join command'
        }
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureCommand)
        $plainValue = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ($null -ne $SuppliedEnrollmentKey) {
            if ($plainValue -notmatch '^[A-Za-z0-9_-]{16,4096}$') {
                throw 'The supplied one-time enrollment key is invalid.'
            }
            return "tailscale up --login-server https://headscale.stayactive.test --auth-key $plainValue"
        }
        return $plainValue
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        if ($null -ne $secureCommand -and $null -eq $SuppliedEnrollmentKey) {
            $secureCommand.Dispose()
        }
    }
}

function Parse-OneTimeJoinCommand([string]$JoinCommand) {
    # Accept only the exact command emitted by StayActive. Do not use
    # Invoke-Expression or run any pasted shell text.
    $pattern = '^\s*tailscale(?:\.exe)?\s+up\s+--login-server\s+(?<server>https://headscale\.stayactive\.test)\s+--auth-key\s+(?<key>[A-Za-z0-9_-]{16,4096})\s*$'
    $match = [regex]::Match($JoinCommand, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw 'The pasted command is not the exact one-time self-hosted StayActive enrollment command.'
    }

    $server = $match.Groups['server'].Value
    $key = $match.Groups['key'].Value
    if ($key.StartsWith('-', [StringComparison]::Ordinal)) {
        throw 'The one-time enrollment key is invalid.'
    }

    return [pscustomobject]@{
        Server = $server
        Key = $key
    }
}

function Invoke-TailscaleJoin(
    [string]$Executable,
    [string]$LoginServer,
    [string]$AuthKey,
    [bool]$ShouldAdvertiseExitNode) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # Windows PowerShell 5.1 does not expose ProcessStartInfo.ArgumentList.
    # Every dynamic value below has already been restricted to a fixed HTTPS
    # origin or an alphanumeric one-time key, so a direct argument string is
    # safe and does not invoke a command shell.
    $arguments = @(
        'up',
        '--reset',
        '--login-server', $LoginServer,
        '--auth-key', $AuthKey,
        '--unattended',
        '--accept-dns=true'
    )
    if ($ShouldAdvertiseExitNode) {
        $arguments += '--advertise-exit-node'
    }
    $startInfo.Arguments = $arguments -join ' '

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The Tailscale client could not be started.'
        }

        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) {
            try { $process.Kill() } catch { }
            throw 'Tailscale enrollment did not finish within two minutes.'
        }
        # Do not display process output: it is not needed for the happy path and
        # a future client must not be able to reflect a supplied one-time key.
        $null = $standardOutput.GetAwaiter().GetResult()
        $null = $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw 'Tailscale did not accept the one-time enrollment command. The key may have expired, been revoked, or already been used.'
        }
    }
    finally {
        $process.Dispose()
    }
}

function Set-TailscalePrivacyPreferences([string]$Executable) {
    & $Executable set `
        --accept-dns=true `
        --update-check=false `
        --auto-update=false `
        --report-posture=false `
        --ssh=false `
        --webclient=false 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to apply the self-hosted Tailscale client privacy settings.'
    }
}

function Clear-JoinSecretFromClipboard([string]$JoinCommand, [string]$AuthKey) {
    try {
        $clipboard = Get-Clipboard -Raw -ErrorAction Stop
        $containsCommand = -not [string]::IsNullOrEmpty($JoinCommand) -and $clipboard.Contains($JoinCommand)
        $containsKey = -not [string]::IsNullOrEmpty($AuthKey) -and $clipboard.Contains($AuthKey)
        if (-not [string]::IsNullOrEmpty($clipboard) -and ($containsCommand -or $containsKey)) {
            Set-Clipboard -Value ''
        }
    }
    catch {
        # Clipboard access is best effort. Enrollment never writes the secret
        # to a file, settings, output, or PowerShell history.
    }
}

function Assert-TailscaleJoin(
    [string]$Executable,
    [bool]$ShouldAdvertiseExitNode) {
    $status = Get-TailscaleStatus $Executable
    if ($null -eq $status -or $status.BackendState -ne 'Running') {
        throw 'Tailscale did not reach the Running state after enrollment.'
    }

    $prefsJson = & $Executable debug prefs 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to verify the enrolled Tailscale preferences.'
    }
    $prefs = (($prefsJson -join [Environment]::NewLine) | ConvertFrom-Json)
    if ([string]$prefs.ControlURL -ne 'https://headscale.stayactive.test') {
        throw 'Tailscale is not using the self-hosted StayActive Headscale control plane.'
    }
    if (-not [bool]$prefs.CorpDNS) {
        throw 'Tailscale DNS handling is disabled; exit-node DNS could leak to the local network.'
    }
    if ([bool]$prefs.AutoUpdate.Check -or [bool]$prefs.AutoUpdate.Apply -or [bool]$prefs.PostureChecking) {
        throw 'Tailscale client update preferences or posture reporting are unexpectedly enabled.'
    }
    if ([Environment]::GetEnvironmentVariable('TS_NO_LOGS_NO_SUPPORT', [EnvironmentVariableTarget]::Machine) -ne 'true') {
        throw 'Tailscale support-log transmission is not disabled at machine scope.'
    }
    $policy = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Tailscale' -ErrorAction Stop
    $expectedAdvertisePolicy = if ($ShouldAdvertiseExitNode) { 'always' } else { 'never' }
    if (
        [string]$policy.LoginURL -ne 'https://headscale.stayactive.test' -or
        [string]$policy.UseTailscaleDNSSettings -ne 'always' -or
        [string]$policy.AdvertiseExitNode -ne $expectedAdvertisePolicy) {
        throw 'The Windows machine policy does not pin this client to its assigned self-hosted role.'
    }
    if ($ShouldAdvertiseExitNode -and (
            @($prefs.AdvertiseRoutes) -notcontains '0.0.0.0/0' -or
            @($prefs.AdvertiseRoutes) -notcontains '::/0')) {
        throw 'Tailscale joined but is not advertising both IPv4 and IPv6 default routes.'
    }

    return $status
}

function Assert-HeadscalePublicBoundary([bool]$IsPublicPinned) {
    if (-not $IsPublicPinned) {
        return
    }

    $health = Invoke-WebRequest -Uri 'https://headscale.stayactive.test/health' -UseBasicParsing -TimeoutSec 15
    if ($health.StatusCode -ne 200) {
        throw 'The public self-hosted Headscale health endpoint is unavailable.'
    }

    $apiStatus = $null
    try {
        $response = Invoke-WebRequest -Uri 'https://headscale.stayactive.test/api/v1/users' -UseBasicParsing -TimeoutSec 15
        $apiStatus = [int]$response.StatusCode
    }
    catch [System.Net.WebException] {
        if ($null -ne $_.Exception.Response) {
            $apiStatus = [int]$_.Exception.Response.StatusCode
        }
    }
    if ($apiStatus -ne 404) {
        throw 'The public Headscale administrative API boundary is not closed.'
    }
}

Assert-Administrator
$existingTailscale = Resolve-TailscaleExecutable
if ($null -eq $existingTailscale) {
    # A fresh MSI can otherwise launch its default hosted onboarding UI before
    # enrollment. Pin the URL/role and support-log opt-out before installation;
    # existing clients are deliberately not changed until the safety check.
    Set-TailscaleMachinePolicy $AdvertiseExitNode
    [Environment]::SetEnvironmentVariable('TS_NO_LOGS_NO_SUPPORT', 'true', [EnvironmentVariableTarget]::Machine)
}
$tailscale = Install-TailscaleIfRequested -Requested:$InstallTailscale
Ensure-TailscaleServiceRunning
Assert-ReenrollmentSafe $tailscale $ForceReenroll

$hostsScript = Join-Path $PSScriptRoot 'Set-LanTestHosts.ps1'
$certificateScript = Join-Path $PSScriptRoot 'Install-CaddyRoot.ps1'
if (-not (Test-Path -LiteralPath $hostsScript -PathType Leaf) -or -not (Test-Path -LiteralPath $certificateScript -PathType Leaf)) {
    throw 'This checkout is missing the required StayActive LAN setup scripts.'
}

$hostsMode = if ($PublicPinned) { 'PublicPinned' } else { 'Lan' }
& $hostsScript -Mode $hostsMode -ServerIp $ServerIp -Confirm:$false
& $certificateScript -CertificatePath $CertificatePath -ExpectedCertificateSha256 $ExpectedCertificateSha256 -Confirm:$false
Assert-HeadscalePublicBoundary $PublicPinned

Set-TailscaleMachinePolicy $AdvertiseExitNode
Restart-TailscaleServiceAndDisableSupportLogs
$joinCommand = $null
$join = $null
$status = $null
try {
    $joinCommand = Read-OneTimeJoinCommand $EnrollmentKey
    $join = Parse-OneTimeJoinCommand $joinCommand
    Invoke-TailscaleJoin $tailscale $join.Server $join.Key $AdvertiseExitNode
    Set-TailscalePrivacyPreferences $tailscale
    Clear-JoinSecretFromClipboard $joinCommand $join.Key
    $status = Assert-TailscaleJoin $tailscale $AdvertiseExitNode
}
finally {
    if ($null -ne $join) {
        Clear-JoinSecretFromClipboard ([string]$joinCommand) ([string]$join.Key)
        $join.Key = $null
    }
    elseif (-not [string]::IsNullOrEmpty([string]$joinCommand)) {
        # A malformed paste can still contain a genuine enrollment key. Clear
        # the exact pasted value even when strict parsing rejected it.
        Clear-JoinSecretFromClipboard ([string]$joinCommand) ''
    }
    $joinCommand = $null
}

if ($AdvertiseExitNode) {
    & powercfg.exe /change standby-timeout-ac 0 1>$null 2>$null
    $standbyExitCode = $LASTEXITCODE
    & powercfg.exe /change hibernate-timeout-ac 0 1>$null 2>$null
    $hibernateExitCode = $LASTEXITCODE
    if ($standbyExitCode -ne 0 -or $hibernateExitCode -ne 0) {
        Write-Warning 'The VPN joined, but Windows power settings could not be updated. Keep this laptop awake manually.'
    }
    Write-Host 'This laptop joined the self-hosted Headscale network and is advertising its default route. On the controller laptop, approve that route before selecting it as an exit node.'
    Write-Host 'Leave this laptop plugged in, awake, connected to the internet, and with its lid open.'
}
else {
    Write-Host 'This laptop joined the self-hosted Headscale network. It is ready to select an approved exit node.'
}

if ($null -ne $status) {
    Write-Host "Self-hosted VPN address: $(@($status.Self.TailscaleIPs) -join ', ')"
}
