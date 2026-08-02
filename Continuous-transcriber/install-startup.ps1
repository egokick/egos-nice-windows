[CmdletBinding()]
param(
    [switch]$Remove,
    [ValidateSet("default", "keep-audio")]
    [string]$Mode = "keep-audio"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$appDirectory = $PSScriptRoot
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory "Continuous Transcriber.lnk"

if ($Remove) {
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        Remove-Item -LiteralPath $shortcutPath -Force
        Write-Host "Removed the Continuous Transcriber Startup shortcut."
    }
    else {
        Write-Host "The Continuous Transcriber Startup shortcut was not installed."
    }
    return
}

& (Join-Path $appDirectory "prepare-runtime.ps1")

$venvPython = Join-Path $appDirectory ".venv\Scripts\python.exe"
$python = if (Test-Path -LiteralPath $venvPython -PathType Leaf) {
    $venvPython
}
else {
    $null
}
if ([string]::IsNullOrWhiteSpace($python)) {
    try {
        $python = (& py.exe -3 -c "import sys; print(sys.executable)" 2>$null | Select-Object -First 1)
    }
    catch {
        $python = $null
    }
}
if ([string]::IsNullOrWhiteSpace($python)) {
    $pythonCommand = Get-Command "python.exe" -ErrorAction SilentlyContinue
    if ($pythonCommand) {
        $python = $pythonCommand.Source
    }
}
if ([string]::IsNullOrWhiteSpace($python)) {
    throw "Python 3 was not found. Install Python 3 and run this script again."
}

$pythonw = Join-Path (Split-Path -Parent $python.Trim()) "pythonw.exe"
if (-not (Test-Path -LiteralPath $pythonw -PathType Leaf)) {
    throw "pythonw.exe was not found next to $python"
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $pythonw
$shortcut.Arguments = (
    '"' + (Join-Path $appDirectory "monitor_transcriber.py") + '" --mode ' + $Mode
)
$shortcut.WorkingDirectory = $appDirectory
$shortcut.Description = "Continuous local microphone transcription watchdog"
$shortcut.Save()

Write-Host "Installed Startup shortcut: $shortcutPath"
Write-Host "The monitor will start in '$Mode' mode at the next sign-in."
