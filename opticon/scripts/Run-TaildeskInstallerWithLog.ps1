[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
try {
    & $InstallerPath *>&1 | Out-File -LiteralPath $LogPath -Encoding UTF8
    exit 0
}
catch {
    $_ | Format-List * -Force | Out-File -LiteralPath $LogPath -Encoding UTF8
    exit 1
}

