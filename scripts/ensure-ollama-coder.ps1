$ErrorActionPreference = 'Stop'

function Test-OllamaModel {
    param(
        [string]$Executable,
        [string]$Model
    )

    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        & $Executable show $Model *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
}

$knownOllamaPaths = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
    (Join-Path $env:ProgramFiles 'Ollama\ollama.exe')
)

& (Join-Path $PSScriptRoot 'ensure-winget-package.ps1') `
    -PackageId 'Ollama.Ollama' `
    -DisplayName 'Ollama' `
    -CommandName 'ollama.exe' `
    -KnownPaths $knownOllamaPaths

$ollamaCommand = Get-Command ollama.exe -ErrorAction SilentlyContinue
$ollama = if ($ollamaCommand) {
    $ollamaCommand.Source
} else {
    $knownOllamaPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $ollama) {
    throw 'Ollama was installed, but ollama.exe could not be found.'
}

try {
    Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null
}
catch {
    Write-Host 'Starting the Ollama service...'
    Start-Process -FilePath $ollama -ArgumentList 'serve' -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(45)
    $ready = $false
    do {
        Start-Sleep -Seconds 2
        try {
            Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        }
        catch {
        }
    } while ((Get-Date) -lt $deadline)

    if (-not $ready) {
        throw 'The Ollama service did not become ready within 45 seconds.'
    }
}

if (-not (Test-OllamaModel -Executable $ollama -Model 'coder-files')) {
    Write-Host 'The coder-files model is missing. Downloading qwen3-coder:30b (approximately 19 GB)...'
    & $ollama pull 'qwen3-coder:30b'
    if ($LASTEXITCODE -ne 0) {
        throw 'Ollama could not download qwen3-coder:30b.'
    }

    & $ollama cp 'qwen3-coder:30b' 'coder-files'
    if ($LASTEXITCODE -ne 0) {
        throw 'Ollama could not create the coder-files model alias.'
    }
}

Write-Host 'Ollama and the coder-files model are ready.'
