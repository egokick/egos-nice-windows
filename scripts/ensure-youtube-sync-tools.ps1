param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'

function ConvertTo-ResponseText {
    param($Content)

    if ($Content -is [byte[]]) {
        return [Text.Encoding]::UTF8.GetString($Content)
    }

    return [string]$Content
}

$resolvedAppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
$assetRoot = Join-Path $resolvedAppDirectory 'youtube-sync'
$toolsRoot = Join-Path $assetRoot 'tools'
[System.IO.Directory]::CreateDirectory($assetRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($toolsRoot) | Out-Null

$ytDlp = Join-Path $assetRoot 'yt-dlp.exe'
if (-not (Test-Path -LiteralPath $ytDlp -PathType Leaf)) {
    Write-Host 'Downloading the official yt-dlp Windows executable...'
    $temporaryYtDlp = Join-Path ([System.IO.Path]::GetTempPath()) "yt-dlp-$PID.exe"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $temporaryYtDlp
        $sumsResponse = Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS'
        $sums = ConvertTo-ResponseText $sumsResponse.Content
        $match = [regex]::Match($sums, '(?im)^(?<hash>[a-f0-9]{64})\s+\*?yt-dlp\.exe\s*$')
        if (-not $match.Success) {
            throw 'The yt-dlp release checksum list did not contain yt-dlp.exe.'
        }

        $actualHash = (Get-FileHash -LiteralPath $temporaryYtDlp -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $match.Groups['hash'].Value, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The downloaded yt-dlp.exe checksum did not match the official release checksum.'
        }

        Move-Item -LiteralPath $temporaryYtDlp -Destination $ytDlp -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryYtDlp -Force -ErrorAction SilentlyContinue
    }
}

$ffmpeg = Get-ChildItem -LiteralPath $toolsRoot -Filter ffmpeg.exe -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$ffprobe = Get-ChildItem -LiteralPath $toolsRoot -Filter ffprobe.exe -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ffmpeg -or -not $ffprobe) {
    Write-Host 'Downloading the FFmpeg release essentials build linked by ffmpeg.org...'
    $archive = Join-Path ([System.IO.Path]::GetTempPath()) "ffmpeg-release-essentials-$PID.zip"
    try {
        $ffmpegUri = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $ffmpegUri -OutFile $archive
        $checksumResponse = Invoke-WebRequest -UseBasicParsing -Uri "$ffmpegUri.sha256"
        $checksumText = ConvertTo-ResponseText $checksumResponse.Content
        $checksumMatch = [regex]::Match($checksumText, '(?i)[a-f0-9]{64}')
        if (-not $checksumMatch.Success) {
            throw 'The FFmpeg publisher checksum response was invalid.'
        }

        $expectedHash = $checksumMatch.Value
        $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The downloaded FFmpeg archive checksum did not match the publisher checksum.'
        }

        Expand-Archive -LiteralPath $archive -DestinationPath $toolsRoot -Force
    }
    finally {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    }

    $ffmpeg = Get-ChildItem -LiteralPath $toolsRoot -Filter ffmpeg.exe -File -Recurse | Select-Object -First 1
    $ffprobe = Get-ChildItem -LiteralPath $toolsRoot -Filter ffprobe.exe -File -Recurse | Select-Object -First 1
}

if (-not (Test-Path -LiteralPath $ytDlp -PathType Leaf) -or -not $ffmpeg -or -not $ffprobe) {
    throw 'YouTubeSyncTray dependencies are incomplete after installation.'
}

& $ytDlp --version
if ($LASTEXITCODE -ne 0) {
    throw 'yt-dlp.exe did not start successfully.'
}

$ffmpegVersion = & $ffmpeg.FullName -version
$ffmpegExitCode = $LASTEXITCODE
if ($ffmpegExitCode -ne 0) {
    throw 'ffmpeg.exe did not start successfully.'
}

$ffmpegVersion | Select-Object -First 1
