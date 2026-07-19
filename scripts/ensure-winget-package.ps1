param(
    [Parameter(Mandatory = $true)]
    [string]$PackageId,

    [Parameter(Mandatory = $true)]
    [string]$DisplayName,

    [string]$CommandName,
    [string[]]$KnownPaths = @()
)

$ErrorActionPreference = 'Stop'

function Find-Dependency {
    if (-not [string]::IsNullOrWhiteSpace($CommandName)) {
        $command = Get-Command $CommandName -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    foreach ($path in $KnownPaths) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $path
        }
    }

    return $null
}

$dependency = Find-Dependency
if ($dependency) {
    Write-Host "$DisplayName is available: $dependency"
    exit 0
}

$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if (-not $winget) {
    throw "$DisplayName is required, and winget is unavailable to install it."
}

Write-Host "$DisplayName was not found. Installing $PackageId..."
& $winget.Source install --id $PackageId --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
if ($LASTEXITCODE -ne 0) {
    throw "winget could not install $PackageId (exit code $LASTEXITCODE)."
}

$dependency = Find-Dependency
if (-not $dependency) {
    throw "$DisplayName was installed, but its executable could not be found. A sign-out or reboot may be required."
}

Write-Host "$DisplayName installed: $dependency"
