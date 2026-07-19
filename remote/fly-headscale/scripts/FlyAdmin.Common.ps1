Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:FlyAppName = 'stayactive-headscale-egokick'
$script:FlyMachineId = '80e52ea6144018'

function Get-FlyAccessToken([string]$EnvironmentFile) {
    if (-not (Test-Path -LiteralPath $EnvironmentFile -PathType Leaf)) {
        throw "Fly credential file is missing: $EnvironmentFile"
    }
    $tokenLine = Get-Content -LiteralPath $EnvironmentFile |
        Where-Object { $_ -match '^\s*FLY_API_TOKEN\s*=' } |
        Select-Object -First 1
    if ($null -eq $tokenLine) {
        throw 'FLY_API_TOKEN is missing from the selected credential file.'
    }
    $token = (($tokenLine -split '=', 2)[1]).Trim().Trim('"').Trim("'")
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'FLY_API_TOKEN is empty.'
    }
    return $token
}

function Invoke-FlyCliCapture(
    [string]$Arguments,
    [string]$EnvironmentFile,
    [int]$TimeoutSeconds = 60) {
    $fly = Get-Command flyctl.exe -ErrorAction SilentlyContinue
    if ($null -eq $fly) {
        throw 'flyctl is not installed on this controller laptop.'
    }

    $token = Get-FlyAccessToken $EnvironmentFile
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $fly.Source
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['FLY_API_TOKEN'] = $token

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Unable to start flyctl.'
        }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw 'flyctl did not finish before the protected administration timeout.'
        }
        $output = $standardOutput.GetAwaiter().GetResult()
        $errorOutput = $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Fly administration failed: $errorOutput"
        }
        return $output.Trim()
    }
    finally {
        $token = $null
        $process.Dispose()
    }
}

function Invoke-HeadscaleFlyCommand(
    [string]$RemoteCommand,
    [string]$EnvironmentFile) {
    if ($RemoteCommand -notmatch '^[A-Za-z0-9 .,:/_-]+$') {
        throw 'The Headscale administration command contains an unexpected character.'
    }
    # `fly ssh console` attempts to touch the inherited Windows console handle
    # even with PTY allocation disabled. That breaks when this function is run
    # by a hidden service or with redirected output. Machine exec is the
    # non-interactive API designed for this exact use case.
    $arguments = "machine exec $script:FlyMachineId `"$RemoteCommand`" --app $script:FlyAppName --timeout 30 --json"
    $resultJson = Invoke-FlyCliCapture $arguments $EnvironmentFile
    try {
        $result = $resultJson | ConvertFrom-Json
    }
    catch {
        throw 'Fly Machine Exec returned an invalid protected administration response.'
    }
    $hasExitCode = $null -ne $result -and $result.PSObject.Properties.Name -contains 'exit_code'
    if ($null -eq $result -or ($hasExitCode -and [int]$result.exit_code -ne 0)) {
        # Do not relay arbitrary remote stderr. Enrollment administration must
        # never become a channel that can reflect a key into StayActive logs.
        throw 'The protected Headscale administration command failed.'
    }
    if ($result.PSObject.Properties.Name -notcontains 'stdout') {
        throw 'Fly Machine Exec returned no protected administration output.'
    }
    return ([string]$result.stdout).Trim()
}

function Get-StayActiveHeadscaleUserId([string]$EnvironmentFile) {
    $json = Invoke-HeadscaleFlyCommand 'headscale users list --output json' $EnvironmentFile
    $parsed = $json | ConvertFrom-Json
    $users = @($parsed)
    $user = @($users | Where-Object { $_.name -eq 'stayactive-admin' })
    if ($user.Count -ne 1) {
        throw 'The stayactive-admin Headscale owner is missing or ambiguous.'
    }
    return [uint64]$user[0].id
}

function New-StayActiveEnrollmentKey(
    [bool]$ExitCapable,
    [string]$EnvironmentFile) {
    $userId = Get-StayActiveHeadscaleUserId $EnvironmentFile
    $tags = if ($ExitCapable) { 'tag:stayactive,tag:stayactive-exit' } else { 'tag:stayactive' }
    $command = "headscale preauthkeys create --user $userId --expiration 15m --tags $tags --output json"
    $json = Invoke-HeadscaleFlyCommand $command $EnvironmentFile
    $record = $json | ConvertFrom-Json
    $key = [string]$record.key
    if ($key -notmatch '^[A-Za-z0-9_-]{16,4096}$') {
        throw 'Headscale did not return a valid one-use enrollment key.'
    }
    # Headscale's protobuf JSON omits false-valued boolean fields. Treat an
    # absent property as false, but still reject either capability if a future
    # server explicitly returns it as enabled.
    $isReusable = $record.PSObject.Properties.Name -contains 'reusable' -and [bool]$record.reusable
    $isEphemeral = $record.PSObject.Properties.Name -contains 'ephemeral' -and [bool]$record.ephemeral
    if ($isReusable -or $isEphemeral) {
        throw 'Headscale returned an enrollment key with unexpected reuse or ephemeral settings.'
    }
    if ([uint64]$record.id -lt 1) {
        throw 'Headscale returned an enrollment ticket without a valid identifier.'
    }
    $lifetimeSeconds = [int64]$record.expiration.seconds - [int64]$record.created_at.seconds
    if ($lifetimeSeconds -lt 840 -or $lifetimeSeconds -gt 960) {
        throw 'Headscale returned an enrollment ticket outside the required 15-minute lifetime.'
    }
    return [pscustomobject]@{
        Id = [uint64]$record.id
        Key = $key
        ExpiresAtUtc = [DateTimeOffset]::FromUnixTimeSeconds([int64]$record.expiration.seconds).UtcDateTime
    }
}

function Expire-StayActiveEnrollmentKey(
    [uint64]$EnrollmentId,
    [string]$EnvironmentFile) {
    if ($EnrollmentId -lt 1) {
        throw 'EnrollmentId must be a positive Headscale pre-auth key identifier.'
    }
    $null = Invoke-HeadscaleFlyCommand "headscale preauthkeys expire --id $EnrollmentId --force" $EnvironmentFile
}
