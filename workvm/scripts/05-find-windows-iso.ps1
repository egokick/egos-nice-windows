#requires -Version 5.1

param(
    [switch]$OpenMicrosoftDownloadPage,
    [string[]]$SearchRoots = @(
        "$env:USERPROFILE\Downloads",
        "$env:USERPROFILE\Desktop",
        "C:\Users\Public\Downloads",
        "C:\source"
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$found = foreach ($root in $SearchRoots) {
    if (Test-Path -LiteralPath $root) {
        Get-ChildItem -LiteralPath $root -Recurse -Filter *.iso -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -match "Win|Windows|W11|Win11" -or
                $_.Length -gt 4GB
            } |
            Select-Object FullName, @{Name = "SizeGB"; Expression = { [math]::Round($_.Length / 1GB, 2) } }, LastWriteTime
    }
}

if ($found) {
    Write-Host "Candidate Windows ISO files:"
    $found | Sort-Object LastWriteTime -Descending | Format-Table -AutoSize
    Write-Host ""
    Write-Host "Use the FullName value with:"
    Write-Host '  .\scripts\20-create-vm.ps1 -IsoPath "FULL_ISO_PATH_HERE"'
    exit 0
}

Write-Warning "No Windows ISO was found in the common locations."
Write-Host ""
Write-Host "Download the official Windows 11 ISO from Microsoft:"
Write-Host "  https://www.microsoft.com/software-download/windows11"
Write-Host ""
Write-Host "On that page, use 'Download Windows 11 Disk Image (ISO) for x64 devices'."
Write-Host "After it finishes, run this script again or pass the downloaded ISO path to scripts\20-create-vm.ps1."

if ($OpenMicrosoftDownloadPage) {
    Start-Process "https://www.microsoft.com/software-download/windows11"
}

