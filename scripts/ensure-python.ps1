param(
    [string]$Version = '3.12',
    [string]$AppDirectory,
    [string]$RequirementsFile
)

$ErrorActionPreference = 'Stop'

function Test-PythonVersion {
    param(
        [string]$Executable,
        [string[]]$PrefixArguments = @()
    )

    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf) -and -not (Get-Command $Executable -ErrorAction SilentlyContinue)) {
        return $false
    }

    $arguments = @($PrefixArguments) + @('-c', "import sys; raise SystemExit(0 if sys.version_info[:2] == tuple(map(int, '$Version'.split('.'))) else 1)")
    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        & $Executable @arguments *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
}

function Find-Python {
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher -and (Test-PythonVersion -Executable $launcher.Source -PrefixArguments @("-$Version"))) {
        return [pscustomobject]@{ Executable = $launcher.Source; PrefixArguments = @("-$Version") }
    }

    $compactVersion = $Version.Replace('.', '')
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Python\Python$compactVersion\python.exe"),
        (Join-Path $env:ProgramFiles "Python$compactVersion\python.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-PythonVersion -Executable $candidate) {
            return [pscustomobject]@{ Executable = $candidate; PrefixArguments = @() }
        }
    }

    $python = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($python -and (Test-PythonVersion -Executable $python.Source)) {
        return [pscustomobject]@{ Executable = $python.Source; PrefixArguments = @() }
    }

    return $null
}

$python = Find-Python
if (-not $python) {
    if ($Version -ne '3.12') {
        throw "No unattended installer is configured for Python $Version."
    }

    $installerUri = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe'
    $expectedHash = '67b5635e80ea51072b87941312d00ec8927c4db9ba18938f7ad2d27b328b95fb'
    $installer = Join-Path ([System.IO.Path]::GetTempPath()) "python-3.12.10-$PID.exe"
    Write-Host 'Python 3.12 was not found. Installing Python 3.12.10 for the current user...'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $installerUri -OutFile $installer
        $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The Python installer checksum did not match the expected Python.org checksum.'
        }

        $installProcess = Start-Process -FilePath $installer -ArgumentList @(
            '/quiet',
            'InstallAllUsers=0',
            'Include_launcher=0',
            'Include_test=0',
            'PrependPath=0'
        ) -Wait -PassThru
        if ($installProcess.ExitCode -ne 0) {
            throw "The Python installer exited with code $($installProcess.ExitCode)."
        }
    }
    finally {
        Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
    }

    $python = Find-Python
    if (-not $python) {
        throw "Python $Version was installed, but its interpreter could not be found."
    }
}

$baseArguments = @($python.PrefixArguments)
& $python.Executable @baseArguments --version
if ($LASTEXITCODE -ne 0) {
    throw "Python $Version did not start successfully."
}

if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    exit 0
}

$resolvedAppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
$venvDirectory = Join-Path $resolvedAppDirectory '.venv'
$venvPython = Join-Path $venvDirectory 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    Write-Host "Creating Python $Version environment in $venvDirectory..."
    & $python.Executable @baseArguments -m venv $venvDirectory
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
        throw "Python could not create the virtual environment at $venvDirectory."
    }
}

if ([string]::IsNullOrWhiteSpace($RequirementsFile)) {
    exit 0
}

$resolvedRequirements = (Resolve-Path -LiteralPath $RequirementsFile).Path
$requirementsHash = (Get-FileHash -LiteralPath $resolvedRequirements -Algorithm SHA256).Hash
$hashMarker = Join-Path $venvDirectory '.requirements.sha256'
$installedHash = if (Test-Path -LiteralPath $hashMarker) {
    (Get-Content -LiteralPath $hashMarker -Raw).Trim()
} else {
    ''
}

$needsInstall = $installedHash -ne $requirementsHash
if (-not $needsInstall) {
    & $venvPython -m pip check | Out-Host
    $needsInstall = $LASTEXITCODE -ne 0
}

if ($needsInstall) {
    Write-Host "Installing Python dependencies from $resolvedRequirements..."
    & $venvPython -m pip install --upgrade pip
    if ($LASTEXITCODE -ne 0) {
        throw 'pip could not update itself.'
    }

    & $venvPython -m pip install --requirement $resolvedRequirements
    if ($LASTEXITCODE -ne 0) {
        throw "pip could not install $resolvedRequirements."
    }

    & $venvPython -m pip check
    if ($LASTEXITCODE -ne 0) {
        throw 'The Python environment contains incompatible packages.'
    }

    Set-Content -LiteralPath $hashMarker -Value $requirementsHash -Encoding ASCII
}
