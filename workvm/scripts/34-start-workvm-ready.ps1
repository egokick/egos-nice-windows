#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$BitsPerPixel = 32
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$DiskPath = Join-Path $Root "$VMName\$VMName.vdi"

function Get-VBoxManagePath {
    $cmd = Get-Command VBoxManage -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe",
        "${env:ProgramFiles(x86)}\Oracle\VirtualBox\VBoxManage.exe"
    )

    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) { return $path }
    }

    throw "VirtualBox was not found. Run workvm\setup-workvm.ps1 first."
}

function Invoke-VBox {
    param([string[]]$Arguments)

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $script:VBoxManage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function ConvertTo-ProcessArgument {
    param([string]$Argument)

    if ($Argument -eq "") {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0

    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append('\' * (($backslashes * 2) + 1))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append('\' * $backslashes)
            $backslashes = 0
        }

        [void]$builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append('\' * ($backslashes * 2))
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-VBoxCaptured {
    param([string[]]$Arguments)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:VBoxManage
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-ProcessArgument -Argument $_ }) -join " ")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    [void]$process.Start()
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = ($standardOutput + $standardError)
    }
}

function Test-VBoxLockError {
    param([string]$Output)

    return $Output -match "already locked for a session|already locked by a session|being locked or unlocked|being unlocked|VBOX_E_INVALID_OBJECT_STATE"
}

function Invoke-VBoxIfUnlocked {
    param([string[]]$Arguments)

    Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
    $result = Invoke-VBoxCaptured -Arguments $Arguments

    if ($result.Output) {
        Write-Host $result.Output.TrimEnd()
    }

    if ($result.ExitCode -eq 0) {
        return $true
    }

    if (Test-VBoxLockError -Output $result.Output) {
        Write-Host "VM '$VMName' is locked by VirtualBox right now. Skipping this pre-start setting and continuing." -ForegroundColor Yellow
        return $false
    }

    throw "VBoxManage failed with exit code $($result.ExitCode): $($Arguments -join ' ')"
}

function Invoke-VBoxWithLockRetry {
    param(
        [string[]]$Arguments,
        [int]$Attempts = 20,
        [int]$DelaySeconds = 1
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        Write-Host "VBoxManage $($Arguments -join ' ')" -ForegroundColor DarkGray
        $result = Invoke-VBoxCaptured -Arguments $Arguments

        if ($result.Output) {
            Write-Host $result.Output.TrimEnd()
        }

        if ($result.ExitCode -eq 0) {
            return
        }

        $locked = Test-VBoxLockError -Output $result.Output
        if (-not $locked -or $attempt -eq $Attempts) {
            throw "VBoxManage failed with exit code $($result.ExitCode): $($Arguments -join ' ')"
        }

        Write-Host "VM '$VMName' is locked by VirtualBox. Waiting ${DelaySeconds}s before retry $($attempt + 1)/$Attempts..." -ForegroundColor Yellow
        Start-Sleep -Seconds $DelaySeconds
    }
}

function Get-VmState {
    $line = (& $script:VBoxManage showvminfo $VMName --machinereadable | Select-String '^VMState=').ToString()
    if ($line -match 'VMState="([^"]+)"') {
        return $matches[1]
    }

    throw "Could not read VM state for '$VMName'."
}

function Enable-CopyPaste {
    param([string]$State)

    if ($State -eq "running") {
        Invoke-VBox @("controlvm", $VMName, "clipboard", "mode", "bidirectional")
        Invoke-VBox @("controlvm", $VMName, "clipboard", "filetransfers", "on")
        Invoke-VBox @("controlvm", $VMName, "draganddrop", "bidirectional")
        return
    }

    if (-not (Invoke-VBoxIfUnlocked -Arguments @(
        "modifyvm", $VMName,
        "--clipboard-mode=bidirectional",
        "--clipboard-file-transfers=enabled",
        "--drag-and-drop=bidirectional"
    ))) {
        return $false
    }

    return $true
}

function Set-DisplayMode {
    param([string]$State)

    if ($State -eq "running") {
        Invoke-VBox @("setextradata", $VMName, "CustomVideoMode1", "${Width}x${Height}x${BitsPerPixel}")
        Invoke-VBox @("setextradata", $VMName, "GUI/LastGuestSizeHint", "${Width},${Height}")
        Invoke-VBox @("setextradata", $VMName, "VBoxInternal2/EfiGraphicsResolution", "${Width}x${Height}")
        Invoke-VBox @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel")
        return $true
    }

    if (-not (Invoke-VBoxIfUnlocked -Arguments @("setextradata", $VMName, "CustomVideoMode1", "${Width}x${Height}x${BitsPerPixel}"))) {
        return $false
    }

    if (-not (Invoke-VBoxIfUnlocked -Arguments @("setextradata", $VMName, "GUI/LastGuestSizeHint", "${Width},${Height}"))) {
        return $false
    }

    if (-not (Invoke-VBoxIfUnlocked -Arguments @("setextradata", $VMName, "VBoxInternal2/EfiGraphicsResolution", "${Width}x${Height}"))) {
        return $false
    }

    if (-not (Invoke-VBoxIfUnlocked -Arguments @("modifyvm", $VMName, "--vram=128"))) {
        return $false
    }

    return $true
}

$script:VBoxManage = Get-VBoxManagePath

$vmList = & $script:VBoxManage list vms
$exists = [bool]($vmList | Where-Object { $_ -match ('^"' + [regex]::Escape($VMName) + '"') })
if (-not $exists) {
    throw "VM '$VMName' does not exist. Run workvm\setup-workvm.ps1 first."
}

$state = Get-VmState

if ($state -eq "saved") {
    Write-Host "VM '$VMName' is saved. Resuming it without changing powered-off-only settings."
    Invoke-VBoxWithLockRetry -Arguments @("startvm", $VMName, "--type", "gui")
    return
}

if ($state -eq "aborted-saved") {
    Write-Host "VM '$VMName' has a stale aborted saved state. Discarding the saved state before booting from disk."
    Invoke-VBoxWithLockRetry -Arguments @("discardstate", $VMName)
    $state = Get-VmState
}

if ($state -eq "running") {
    Enable-CopyPaste -State $state
    Set-DisplayMode -State $state
    Write-Host "VM '$VMName' is already running. Copy/paste settings are bidirectional and display hint is ${Width}x${Height}."
    return
}

if ($state -ne "poweroff" -and $state -ne "aborted") {
    Write-Host "VM '$VMName' is currently '$state'. Starting it without changing powered-off-only settings."
    Invoke-VBoxWithLockRetry -Arguments @("startvm", $VMName, "--type", "gui")
    return
}

$preStartSettingsApplied = $true
if (-not (Enable-CopyPaste -State $state)) {
    $preStartSettingsApplied = $false
}

if (-not (Set-DisplayMode -State $state)) {
    $preStartSettingsApplied = $false
}

if (-not $preStartSettingsApplied) {
    Write-Host "VM '$VMName' is locked, so Open VM will start it without changing boot/storage settings this time." -ForegroundColor Yellow
    Invoke-VBoxWithLockRetry -Arguments @("startvm", $VMName, "--type", "gui")
    return
}

if (-not (Test-Path -LiteralPath $DiskPath)) {
    throw "Expected VM disk was not found: $DiskPath"
}

Invoke-VBoxWithLockRetry -Arguments @(
    "modifyvm", $VMName,
    "--boot1=disk",
    "--boot2=none",
    "--boot3=none",
    "--boot4=none",
    "--firmware-boot-menu=disabled"
)

$info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
if ($info -notmatch [regex]::Escape($DiskPath)) {
    Invoke-VBoxWithLockRetry -Arguments @("storageattach", $VMName, "--storagectl", "SATA", "--port", "0", "--device", "0", "--type", "hdd", "--medium", $DiskPath)
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
}

$dvdMatch = [regex]::Match($info, '"SATA-1-0"="([^"]+)"')
if ($dvdMatch.Success -and $dvdMatch.Groups[1].Value -ne "none") {
    Invoke-VBoxWithLockRetry -Arguments @("storageattach", $VMName, "--storagectl", "SATA", "--port", "1", "--device", "0", "--type", "dvddrive", "--medium", "none")
}

Invoke-VBoxWithLockRetry -Arguments @("startvm", $VMName, "--type", "gui")
Start-Sleep -Seconds 5
Invoke-VBoxWithLockRetry -Arguments @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel")

Write-Host ""
Write-Host "VM '$VMName' is starting with the OS disk first in boot order and display hint ${Width}x${Height}."
