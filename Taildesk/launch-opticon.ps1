[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$RebuildScript,
    [Parameter(Mandatory)][string]$OpticonExecutable,
    [Parameter(Mandatory)][string]$ControlUrl
)

$ErrorActionPreference = 'Stop'

try {
    Write-Host 'Preparing Opticon...' -ForegroundColor Cyan
    if ((Test-Path -LiteralPath (Join-Path $SourceRoot 'Taildesk.sln')) -and
        (Test-Path -LiteralPath $RebuildScript -PathType Leaf)) {
        & $RebuildScript -SourceRoot $SourceRoot
        if ($LASTEXITCODE -ne 0) {
            throw "The Opticon rebuild returned exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $OpticonExecutable -PathType Leaf)) {
        throw 'Opticon is not installed. Build and run the signed OwnerManaged Install-Opticon.exe package first.'
    }

    try {
        $response = Invoke-WebRequest -UseBasicParsing "$ControlUrl/health" -TimeoutSec 8
        if ($response.StatusCode -ne 200) {
            Write-Warning 'Opticon''s Fly control server did not return a healthy status. The command center will still open.'
        }
    } catch {
        Write-Warning 'Opticon''s Fly control server is currently unreachable. The command center will still open.'
    }

    Write-Host 'Starting Opticon...' -ForegroundColor Green
    Start-Process -FilePath $OpticonExecutable -WorkingDirectory (Split-Path -Parent $OpticonExecutable)
} catch {
    $message = "Opticon could not start safely.`n`n$($_.Exception.Message)"
    Write-Host $message -ForegroundColor Red
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            $message,
            'Opticon startup failed',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    } catch { }
    exit 1
}
