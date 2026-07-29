#requires -Version 5.1

param(
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$StatePath = Join-Path $env:ProgramData (
    "StayActive\BootBackups\VirtualBoxNativeTest.json"
)

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-NoElevate"
    )
    $process = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList $arguments `
        -Verb RunAs `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

if (-not (Test-IsAdmin)) {
    throw "This script must run as administrator."
}

if (-not (Test-Path -LiteralPath $StatePath)) {
    Write-Host "No VirtualBox native-test boot entry is recorded."
    exit 0
}

$state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$testEntry = [string]$state.TestEntry
if ($testEntry -notmatch '^\{[0-9a-fA-F-]{36}\}$') {
    throw "The recorded boot-entry identifier is invalid: $testEntry"
}

& bcdedit.exe /delete $testEntry /cleanup
if ($LASTEXITCODE -ne 0) {
    throw "Could not remove test boot entry '$testEntry'."
}

Resume-BitLocker `
    -MountPoint $env:SystemDrive `
    -ErrorAction SilentlyContinue |
    Out-Null
Remove-Item -LiteralPath $StatePath -Force
Write-Host "STAYACTIVE_NATIVE_VBOX_TEST_ENTRY_REMOVED"
