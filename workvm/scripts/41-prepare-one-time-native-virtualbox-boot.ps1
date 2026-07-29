#requires -Version 5.1

param(
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$StateRoot = Join-Path $env:ProgramData "StayActive\BootBackups"
$StatePath = Join-Path $StateRoot "VirtualBoxNativeTest.json"
$Description = "Windows 11 - one-time VirtualBox native test"
$script:TestEntry = $null
$script:BitLockerSuspended = $false

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Invoke-BcdEdit {
    param(
        [string[]]$Arguments,
        [switch]$AllowFail
    )

    $output = @(& bcdedit.exe @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) {
        throw "bcdedit failed with exit code ${LASTEXITCODE}: $($output -join ' ')"
    }
    return $output
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

New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
$backupPath = Join-Path $StateRoot (
    "BCD-{0}.bak" -f (Get-Date -Format "yyyyMMdd-HHmmss")
)

try {
    [void](Invoke-BcdEdit -Arguments @("/export", $backupPath))
    if (-not (Test-Path -LiteralPath $backupPath)) {
        throw "BCD export reported success but did not create '$backupPath'."
    }

    if (Test-Path -LiteralPath $StatePath) {
        try {
            $oldState = Get-Content -LiteralPath $StatePath -Raw |
                ConvertFrom-Json
            $oldEntry = [string]$oldState.TestEntry
            if ($oldEntry -match '^\{[0-9a-fA-F-]{36}\}$') {
                [void](Invoke-BcdEdit `
                    -Arguments @("/delete", $oldEntry, "/cleanup") `
                    -AllowFail)
            }
        }
        finally {
            Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
        }
    }

    $systemDrive = $env:SystemDrive
    $bitLockerVolume = Get-BitLockerVolume `
        -MountPoint $systemDrive `
        -ErrorAction Stop
    $protectionOn = [string]$bitLockerVolume.ProtectionStatus -eq "On"
    if ($protectionOn) {
        $hasRecoveryPassword = @($bitLockerVolume.KeyProtector).Where({
            [string]$_.KeyProtectorType -eq "RecoveryPassword"
        }).Count -gt 0
        if (-not $hasRecoveryPassword) {
            throw (
                "BitLocker/device encryption is protected, but no recovery-password " +
                "protector is registered. Refusing to alter the boot sequence."
            )
        }

        Suspend-BitLocker `
            -MountPoint $systemDrive `
            -RebootCount 1 `
            -ErrorAction Stop |
            Out-Null
        $script:BitLockerSuspended = $true
    }

    $copyOutput = Invoke-BcdEdit -Arguments @(
        "/copy", "{current}", "/d", $Description
    )
    $entryMatch = [regex]::Match(
        ($copyOutput -join " "),
        '\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}'
    )
    if (-not $entryMatch.Success) {
        throw "Could not extract the copied boot-entry identifier."
    }
    $script:TestEntry = $entryMatch.Value

    [void](Invoke-BcdEdit -Arguments @(
        "/set", $script:TestEntry, "hypervisorlaunchtype", "Off"
    ))
    [void](Invoke-BcdEdit -Arguments @(
        "/set", $script:TestEntry, "vsmlaunchtype", "Off"
    ))
    [void](Invoke-BcdEdit -Arguments @(
        "/displayorder", $script:TestEntry, "/remove"
    ))
    [void](Invoke-BcdEdit -Arguments @(
        "/bootsequence", $script:TestEntry
    ))

    [ordered]@{
        SchemaVersion = 1
        TestEntry = $script:TestEntry
        Description = $Description
        BcdBackupPath = $backupPath
        BitLockerSuspendedForOneReboot = $script:BitLockerSuspended
        PreparedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $StatePath -Encoding UTF8

    Write-Host "STAYACTIVE_NATIVE_VBOX_TEST_READY"
    Write-Host "The next restart only will use Windows without Hyper-V/VBS."
    Write-Host "The normal secured Windows entry remains the default after that boot."
    Write-Host "State: $StatePath"
}
catch {
    if ($null -ne $script:TestEntry) {
        [void](Invoke-BcdEdit `
            -Arguments @("/delete", $script:TestEntry, "/cleanup") `
            -AllowFail)
    }
    if ($script:BitLockerSuspended) {
        Resume-BitLocker `
            -MountPoint $env:SystemDrive `
            -ErrorAction SilentlyContinue |
            Out-Null
    }
    throw
}

