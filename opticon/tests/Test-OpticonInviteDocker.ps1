[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "Taildesk.HostedInviteIntegration\Taildesk.HostedInviteIntegration.csproj"
$env:DOTNET_ROLL_FORWARD = "Major"
$startedDocker = $false
function Test-DockerReady {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    docker info *> $null
    $ready = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previousPreference
    return $ready
}

try {
    if (-not (Test-DockerReady)) {
        docker desktop start
        if ($LASTEXITCODE -ne 0) { throw "Docker Desktop could not be started." }
        $startedDocker = $true
        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if (Test-DockerReady) { $ready = $true; break }
            Start-Sleep -Seconds 2
        }
        if (-not $ready) { throw "Docker Desktop did not become ready within one minute." }
    }

    $null = Invoke-WebRequest -Uri "https://taildesk-egokick-control.fly.dev/health" -UseBasicParsing -TimeoutSec 20
    $null = Invoke-WebRequest -Uri "https://www.microsoft.com/favicon.ico" -Method Head -UseBasicParsing -TimeoutSec 20
    dotnet run --project $project -c Release -p:UseAppHost=false -- --docker-e2e
    if ($LASTEXITCODE -ne 0) { throw "The Opticon Docker invitation acceptance test failed." }
}
finally {
    if ($startedDocker) { docker desktop stop }
}
