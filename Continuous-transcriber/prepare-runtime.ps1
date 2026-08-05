[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$appDirectory = $PSScriptRoot
$runtimeDirectory = Join-Path $appDirectory "runtime"
$binDirectory = Join-Path $runtimeDirectory "bin"
$modelDirectory = Join-Path $runtimeDirectory "models"
$downloadDirectory = Join-Path $runtimeDirectory "downloads"
$runtimeMarker = Join-Path $binDirectory ".whisper-runtime.sha256"
$ffmpegDirectory = Join-Path $runtimeDirectory "ffmpeg"
$ffmpegPathMarker = Join-Path $binDirectory "ffmpeg.path"

$whisperArchive = [pscustomobject]@{
    Name = "whisper.cpp v1.9.1 Windows x64 runtime"
    Url = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip"
    FileName = "whisper-bin-x64.zip"
    Size = [int64]7982101
    Sha256 = "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539"
}

$models = @(
    [pscustomobject]@{
        Name = "Parakeet TDT 0.6B v3 Q4_K model"
        Url = "https://huggingface.co/ggml-org/parakeet-GGUF/resolve/35156454d1a39de06863303dd209fd2bed6ee079/ggml-parakeet-tdt-0.6b-v3-q4_k.bin?download=true"
        FileName = "ggml-parakeet-tdt-0.6b-v3-q4_k.bin"
        Size = [int64]415611879
        Sha256 = "8b205b8b39c6535e153de6fb11c51db46125d45c4f16ba496fe41a0fe71b885e"
    },
    [pscustomobject]@{
        Name = "Silero VAD v6.2 model"
        Url = "https://huggingface.co/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/ggml-silero-v6.2.0.bin?download=true"
        FileName = "ggml-silero-v6.2.0.bin"
        Size = [int64]885098
        Sha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"
    }
)

function Test-VerifiedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int64]$ExpectedSize,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    if ((Get-Item -LiteralPath $Path).Length -ne $ExpectedSize) {
        return $false
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actualHash -eq $ExpectedSha256
}

function Get-VerifiedArtifact {
    param(
        [Parameter(Mandatory = $true)]$Artifact,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    $destination = Join-Path $DestinationDirectory $Artifact.FileName
    if (-not $Force -and (Test-VerifiedFile -Path $destination -ExpectedSize $Artifact.Size -ExpectedSha256 $Artifact.Sha256)) {
        Write-Host "$($Artifact.Name) is already present and verified."
        return $destination
    }
    if ($VerifyOnly) {
        throw "$($Artifact.Name) is missing or failed verification: $destination"
    }

    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    $temporary = "$destination.download"
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }

    try {
        Write-Host "Downloading $($Artifact.Name)..."
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -UseBasicParsing -Uri $Artifact.Url -OutFile $temporary
        if (-not (Test-VerifiedFile -Path $temporary -ExpectedSize $Artifact.Size -ExpectedSha256 $Artifact.Sha256)) {
            $actualSize = (Get-Item -LiteralPath $temporary).Length
            $actualHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
            throw (
                "$($Artifact.Name) failed verification. " +
                "Expected $($Artifact.Size) bytes / $($Artifact.Sha256); " +
                "received $actualSize bytes / $actualHash."
            )
        }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
    Write-Host "Verified $($Artifact.Name)."
    return $destination
}

function Test-DirectShowFfmpeg {
    param(
        [Parameter(Mandatory = $true)][string]$Executable
    )

    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        return $false
    }

    try {
        $devices = & $Executable -hide_banner -devices 2>&1
        return $LASTEXITCODE -eq 0 -and ($devices -match '(?im)^\s*D[\. ]\s+dshow\b')
    }
    catch {
        return $false
    }
}

function Find-DirectShowFfmpeg {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $ffmpegPathMarker -PathType Leaf) {
        $configuredPath = (Get-Content -LiteralPath $ffmpegPathMarker -Raw).Trim()
        if (-not [string]::IsNullOrWhiteSpace($configuredPath)) {
            $candidates.Add($configuredPath)
        }
    }

    $pathFfmpeg = Get-Command "ffmpeg.exe" -ErrorAction SilentlyContinue
    if ($pathFfmpeg) {
        $candidates.Add($pathFfmpeg.Source)
    }

    if (Test-Path -LiteralPath $ffmpegDirectory -PathType Container) {
        foreach ($candidate in Get-ChildItem -LiteralPath $ffmpegDirectory -Filter "ffmpeg.exe" -File -Recurse -ErrorAction SilentlyContinue) {
            $candidates.Add($candidate.FullName)
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-DirectShowFfmpeg -Executable $candidate) {
            return $candidate
        }
    }

    return $null
}

function Install-DirectShowFfmpeg {
    $archiveUri = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
    $archive = Join-Path $downloadDirectory 'ffmpeg-release-essentials.zip'
    $temporary = "$archive.download"

    New-Item -ItemType Directory -Force -Path $ffmpegDirectory, $downloadDirectory | Out-Null
    try {
        Write-Host 'Downloading the FFmpeg Windows essentials build with DirectShow capture support...'
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing -Uri $archiveUri -OutFile $temporary
        $checksumResponse = Invoke-WebRequest -UseBasicParsing -Uri "$archiveUri.sha256"
        $checksumMatch = [regex]::Match([string]$checksumResponse.Content, '(?i)[a-f0-9]{64}')
        if (-not $checksumMatch.Success) {
            throw 'The FFmpeg publisher checksum response was invalid.'
        }

        $actualHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $checksumMatch.Value, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The downloaded FFmpeg archive checksum did not match the publisher checksum.'
        }

        Move-Item -LiteralPath $temporary -Destination $archive -Force
        Expand-Archive -LiteralPath $archive -DestinationPath $ffmpegDirectory -Force
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }

    $ffmpeg = Find-DirectShowFfmpeg
    if (-not $ffmpeg) {
        throw 'The installed FFmpeg build does not provide DirectShow capture support.'
    }

    return $ffmpeg
}

$ffmpeg = Find-DirectShowFfmpeg
if (-not $ffmpeg) {
    if ($VerifyOnly) {
        throw 'FFmpeg with DirectShow capture support is not installed.'
    }

    $ffmpeg = Install-DirectShowFfmpeg
}

if (-not (Test-DirectShowFfmpeg -Executable $ffmpeg)) {
    throw 'FFmpeg could not be verified with DirectShow capture support.'
}

if (-not $VerifyOnly) {
    New-Item -ItemType Directory -Force -Path $binDirectory | Out-Null
    Set-Content -LiteralPath $ffmpegPathMarker -Value $ffmpeg -Encoding ASCII
}

if (-not $VerifyOnly) {
    New-Item -ItemType Directory -Force -Path $binDirectory, $modelDirectory, $downloadDirectory | Out-Null
}
$archivePath = Get-VerifiedArtifact -Artifact $whisperArchive -DestinationDirectory $downloadDirectory

$requiredRuntimeFiles = @(
    "parakeet-cli.exe",
    "whisper-vad-speech-segments.exe",
    "parakeet.dll",
    "whisper.dll",
    "ggml.dll",
    "ggml-base.dll"
)
$runtimeReady = $true
foreach ($fileName in $requiredRuntimeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $binDirectory $fileName) -PathType Leaf)) {
        $runtimeReady = $false
        break
    }
}
if (-not (Test-Path -LiteralPath $runtimeMarker -PathType Leaf) -or
    (Get-Content -LiteralPath $runtimeMarker -Raw).Trim() -ne $whisperArchive.Sha256) {
    $runtimeReady = $false
}

if ($VerifyOnly -and -not $runtimeReady) {
    throw "The whisper.cpp runtime is not installed or does not match v1.9.1."
}

if ($Force -or -not $runtimeReady) {
    Write-Host "Installing the verified whisper.cpp Release files..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.StartsWith("Release/", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $fileName = [System.IO.Path]::GetFileName($entry.FullName)
            if ([string]::IsNullOrWhiteSpace($fileName)) {
                continue
            }
            $destination = Join-Path $binDirectory $fileName
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
    Set-Content -LiteralPath $runtimeMarker -Value $whisperArchive.Sha256 -Encoding ASCII
}

foreach ($fileName in $requiredRuntimeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $binDirectory $fileName) -PathType Leaf)) {
        throw "The verified archive did not contain required runtime file: $fileName"
    }
}

foreach ($model in $models) {
    Get-VerifiedArtifact -Artifact $model -DestinationDirectory $modelDirectory | Out-Null
}

Write-Host "Continuous Transcriber runtime is ready."
