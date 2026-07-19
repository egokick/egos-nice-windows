param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 99)]
    [int]$MajorVersion,

    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'

[System.IO.Directory]::CreateDirectory($InstallDirectory) | Out-Null
$installer = Join-Path ([System.IO.Path]::GetTempPath()) "dotnet-install-$PID.ps1"

try {
    $downloadParameters = @{
        UseBasicParsing = $true
        Uri = 'https://dot.net/v1/dotnet-install.ps1'
        OutFile = $installer
    }
    Invoke-WebRequest @downloadParameters
    & $installer -Channel "$MajorVersion.0" -InstallDir $InstallDirectory
    if (-not $?) {
        throw "dotnet-install.ps1 exited with code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
}
