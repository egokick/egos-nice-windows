#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$BitsPerPixel = 32,
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$LogPath = Join-Path $Root ".cache\bluetooth-passthrough-repair.log"
$StartReadyScriptPath = Join-Path $PSScriptRoot "34-start-workvm-ready.ps1"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
$script:RepairMutex = $null

function Write-Log {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $LogPath -Value $line
    Write-Host $line
}

function Enter-RepairLock {
    $createdNew = $false
    $script:RepairMutex = [System.Threading.Mutex]::new($false, "Global\StayActiveWorkVmBluetoothRepair", [ref]$createdNew)
    if (-not $script:RepairMutex.WaitOne(0)) {
        throw "Another WorkVM Bluetooth repair is already running. Wait for it to finish before trying again."
    }
}

function Exit-RepairLock {
    if ($null -ne $script:RepairMutex) {
        try {
            $script:RepairMutex.ReleaseMutex()
        }
        catch {
        }
        $script:RepairMutex.Dispose()
        $script:RepairMutex = $null
    }
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

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

    throw "VirtualBox was not found."
}

function Invoke-VBox {
    param([string[]]$Arguments, [switch]$AllowFail)

    Write-Log "VBoxManage $($Arguments -join ' ')"
    & $script:VBoxManage @Arguments 2>&1 | ForEach-Object { Write-Log "  $_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFail) {
        throw "VBoxManage failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Invoke-VBoxTimed {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 20
    )

    Write-Log "VBoxManage $($Arguments -join ' ')"
    $outputPath = Join-Path $Root ".cache\vboxmanage-timed-output.txt"
    $errorPath = Join-Path $Root ".cache\vboxmanage-timed-error.txt"
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $errorPath -Force -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $script:VBoxManage -ArgumentList $Arguments -NoNewWindow -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Log "  timed out after ${TimeoutSeconds}s; killing VBoxManage PID $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        return $false
    }

    $output = ""
    if (Test-Path -LiteralPath $outputPath) {
        $output += (Get-Content -LiteralPath $outputPath -Raw -ErrorAction SilentlyContinue)
    }
    if (Test-Path -LiteralPath $errorPath) {
        $output += (Get-Content -LiteralPath $errorPath -Raw -ErrorAction SilentlyContinue)
    }
    if ($output) {
        $output.TrimEnd() -split "\r?\n" | ForEach-Object { Write-Log "  $_" }
    }

    $process.Refresh()
    if (($null -ne $process.ExitCode -and $process.ExitCode -ne 0) -or
        $output -match "VBoxManage(?:\.exe)?: error:") {
        Write-Log "  exited with code $($process.ExitCode)"
        return $false
    }

    return $true
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

function Invoke-ProcessTimed {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 20,
        [switch]$AllowFail
    )

    Write-Log "$FileName $($Arguments -join ' ')"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-ProcessArgument -Argument $_ }) -join " ")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Log "  timed out after ${TimeoutSeconds}s; killing PID $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        if ($AllowFail) { return $false }
        throw "$FileName timed out after ${TimeoutSeconds}s: $($Arguments -join ' ')"
    }

    $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    if ($output) {
        $output.TrimEnd() -split "\r?\n" | ForEach-Object { Write-Log "  $_" }
    }

    $process.Refresh()
    if (($null -ne $process.ExitCode -and $process.ExitCode -ne 0) -or
        $output -match "error:|Failed to|is not supported|pending system reboot") {
        Write-Log "  exited with code $($process.ExitCode)"
        if ($AllowFail) { return $false }
        throw "$FileName failed with exit code $($process.ExitCode): $($Arguments -join ' ')"
    }

    return $true
}

function Stop-VirtualBoxProcesses {
    Write-Log "Force-clearing VirtualBox helper processes."
    foreach ($name in @("VBoxManage", "VirtualBoxVM", "VBoxHeadless", "VBoxSVC")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Log "Stopping $name PID $($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Start-Sleep -Seconds 4
}

function Get-VmState {
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
    if ($info -match 'VMState="([^"]+)"') { return $matches[1] }
    throw "Could not read VM state."
}

function Get-WorkVmProcessIds {
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -match 'VirtualBoxVM|VBoxHeadless' -and (
                $_.CommandLine -match [regex]::Escape($VMName) -or
                -not $_.CommandLine
            )
        } |
        Select-Object -ExpandProperty ProcessId
}

function Stop-WorkVm {
    $state = Get-VmState
    Write-Log "VM state before stop: $state"
    if ($state -in @("running", "stuck", "stopping", "starting")) {
        [void](Invoke-VBoxTimed @("controlvm", $VMName, "poweroff") -TimeoutSeconds 60)
        Start-Sleep -Seconds 5
    }

    Stop-VirtualBoxProcesses

    $finalState = Get-VmState
    if ($finalState -eq "aborted-saved") {
        Write-Log "Discarding stale aborted saved state."
        Invoke-VBox @("discardstate", $VMName)
        $finalState = Get-VmState
    }

    Write-Log "VM state after stop/clear: $finalState"
    if ($finalState -notin @("poweroff", "aborted", "saved")) {
        throw "Could not clear WorkVM state. Current state: $finalState"
    }
}

function Wait-VmState {
    param(
        [string[]]$States,
        [int]$TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = Get-VmState
        if ($state -in $States) {
            return $state
        }

        Start-Sleep -Seconds 2
    }

    return Get-VmState
}

function Restart-VirtualBoxBackendPreservingVmState {
    Write-Log "Saving VM state to clear stale VirtualBox USB capture without discarding the guest state."
    $state = Get-VmState
    if ($state -eq "running") {
        if (-not (Invoke-VBoxTimed @("controlvm", $VMName, "savestate") -TimeoutSeconds 120)) {
            throw "Could not save VM state before repairing stale USB capture."
        }
    }

    $savedState = Wait-VmState -States @("saved", "poweroff", "aborted") -TimeoutSeconds 120
    Write-Log "VM state after save request: $savedState"
    if ($savedState -notin @("saved", "poweroff", "aborted")) {
        throw "VM did not reach a safe saved/powered-off state before VirtualBox backend restart. Current state: $savedState"
    }

    foreach ($name in @("VBoxSVC", "VBoxManage")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Log "Stopping $name PID $($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Start-Sleep -Seconds 5
    Start-WorkVmReady
    Ensure-WorkVmDisplayReady
}

function Get-BluetoothUsbBlocks {
    $usb = (& $script:VBoxManage list usbhost) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $usb
    $usb -split "(?:\r?\n){2,}" |
        Where-Object { $_ -match 'VendorId:\s+0x13d3' -and $_ -match 'ProductId:\s+0x3602' }
}

function Get-BluetoothUsbUuid {
    $blocks = @(Get-BluetoothUsbBlocks)
    foreach ($block in $blocks) {
        if ($block -match "UUID:\s+([0-9a-fA-F-]+)") {
            return $matches[1]
        }
    }

    throw "Could not find MediaTek Bluetooth USB device 13d3:3602 in VirtualBox USB host list."
}

function Test-HostBluetoothPendingReboot {
    $devices = @(Get-PnpDevice | Where-Object {
        $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or
        ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*")
    })

    foreach ($device in $devices) {
        Write-Log "Host Bluetooth PnP: $($device.Status) $($device.Class) $($device.FriendlyName) $($device.InstanceId) problem=$($device.Problem)"
    }

    $composite = $devices | Where-Object { $_.InstanceId -like "USB\VID_13D3&PID_3602\*" } | Select-Object -First 1
    if (-not $composite) {
        return $false
    }

    $outputPath = Join-Path $Root ".cache\pnputil-restart-check-output.txt"
    $errorPath = Join-Path $Root ".cache\pnputil-restart-check-error.txt"
    Remove-Item -LiteralPath $outputPath,$errorPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath "pnputil.exe" -ArgumentList @("/restart-device", $composite.InstanceId) -NoNewWindow -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    [void]$process.WaitForExit(30000)

    $output = ""
    foreach ($path in @($outputPath, $errorPath)) {
        if (Test-Path -LiteralPath $path) {
            $output += Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        }
    }

    if ($output) {
        $output.TrimEnd() -split "\r?\n" | ForEach-Object { Write-Log "  pnputil check: $_" }
    }

    return $output -match "pending system reboot"
}

function Test-BluetoothUsbAttached {
    $vmInfo = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $vmInfo
    $attachedMatch = [regex]::Match(
        $vmInfo,
        'Currently attached USB devices:\s*(?<devices>[\s\S]*?)(?:\r?\n\r?\n|\r?\nBandwidth groups:|\r?\nShared folders:|\r?\nVRDE:|\z)'
    )
    if (-not $attachedMatch.Success) {
        return $false
    }

    $attachedDevices = $attachedMatch.Groups["devices"].Value
    if ($attachedDevices -match '<none>') {
        return $false
    }

    return ($attachedDevices -match '13d3' -or
        $attachedDevices -match '3602' -or
        $attachedDevices -match 'Wireless_Device' -or
        $attachedDevices -match 'MediaTek' -or
        $attachedDevices -match 'IMC Networks')
}

function Get-GuestCredentials {
    $credentialPath = Join-Path $Root "vm-credentials.txt"
    if (-not (Test-Path -LiteralPath $credentialPath)) {
        Write-Log "Guest credentials file was not found: $credentialPath"
        return $null
    }

    $content = Get-Content -LiteralPath $credentialPath
    $username = ($content | Where-Object { $_ -match '^Guest username:\s*(.+)$' } | Select-Object -First 1) -replace '^Guest username:\s*', ''
    $password = ($content | Where-Object { $_ -match '^Guest password:\s*(.+)$' } | Select-Object -First 1) -replace '^Guest password:\s*', ''
    if (-not $username -or -not $password) {
        Write-Log "Guest credentials file does not contain a username and password."
        return $null
    }

    return [pscustomobject]@{
        Username = $username
        Password = $password
    }
}

function Test-GuestBluetoothVisible {
    $credentials = Get-GuestCredentials
    if (-not $credentials) {
        return $false
    }

    $guestCommand = @'
$devices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
    $_.Class -eq "Bluetooth" -or
    $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or
    $_.FriendlyName -match "Bluetooth|MediaTek|Wireless"
} | Select-Object -First 20 Status,Class,FriendlyName,InstanceId
if ($devices) {
    $devices | Format-List
    exit 0
}
exit 2
'@

    Write-Log "Checking whether Windows inside the VM can see Bluetooth."
    $outputPath = Join-Path $Root ".cache\guest-bluetooth-check-output.txt"
    $errorPath = Join-Path $Root ".cache\guest-bluetooth-check-error.txt"
    Remove-Item -LiteralPath $outputPath,$errorPath -Force -ErrorAction SilentlyContinue

    $arguments = @(
        "guestcontrol", $VMName, "run",
        "--username", $credentials.Username,
        "--password", $credentials.Password,
        "--exe", "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
        "--",
        "powershell.exe",
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-Command", $guestCommand
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:VBoxManage
    $startInfo.Arguments = (($arguments | ForEach-Object { ConvertTo-ProcessArgument -Argument $_ }) -join " ")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    if (-not $process.WaitForExit(30000)) {
        Write-Log "  guest Bluetooth check timed out; killing VBoxManage PID $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        return $false
    }

    $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    if ($output) {
        $output.TrimEnd() -split "\r?\n" | ForEach-Object { Write-Log "  $_" }
    }

    if ($process.ExitCode -eq 0) {
        Write-Log "Windows inside the VM can see a Bluetooth device."
        return $true
    }

    Write-Log "Guest Bluetooth check exited with code $($process.ExitCode)."
    return $false
}

function Restart-HostBluetoothUsb {
    $devices = Get-PnpDevice |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or
            ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*")
        }

    foreach ($device in $devices) {
        Write-Log "PnP before: $($device.Status) $($device.Class) $($device.FriendlyName) $($device.InstanceId)"
    }

    if (-not $devices) {
        Write-Log "No matching host PnP Bluetooth devices found to reset."
        return
    }

    $deviceIds = @($devices | Select-Object -ExpandProperty InstanceId -Unique)
    foreach ($deviceId in $deviceIds) {
        Write-Log "Disabling $deviceId"
        [void](Invoke-ProcessTimed -FileName "pnputil.exe" -Arguments @("/disable-device", $deviceId) -TimeoutSeconds 20 -AllowFail)
    }
    Start-Sleep -Seconds 4

    foreach ($deviceId in $deviceIds) {
        Write-Log "Enabling $deviceId"
        [void](Invoke-ProcessTimed -FileName "pnputil.exe" -Arguments @("/enable-device", $deviceId) -TimeoutSeconds 20 -AllowFail)
    }
    Start-Sleep -Seconds 8

    Get-PnpDevice |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602*" -or
            ($_.Class -eq "Bluetooth" -and $_.FriendlyName -like "*MediaTek*")
        } |
        ForEach-Object { Write-Log "PnP after: $($_.Status) $($_.Class) $($_.FriendlyName) $($_.InstanceId)" }
}

function Set-BroadBluetoothFilter {
    $info = (& $script:VBoxManage showvminfo $VMName --machinereadable) -join "`n"
    $indexes = [regex]::Matches($info, 'USBFilterName(\d+)=') |
        ForEach-Object { [int]$_.Groups[1].Value - 1 } |
        Sort-Object -Descending -Unique

    foreach ($idx in $indexes) {
        Invoke-VBox @("usbfilter", "remove", "$idx", "--target", $VMName) -AllowFail
    }

    Invoke-VBox @("usbfilter", "add", "0", "--target", $VMName, "--name", "Laptop MediaTek Bluetooth Adapter VIDPID", "--vendorid", "13d3", "--productid", "3602", "--active", "yes")

    $state = Get-VmState
    if ($state -eq "running") {
        Write-Log "VM is running. Leaving powered-off-only USB controller settings unchanged."
        return
    }

    Invoke-VBox @("modifyvm", $VMName, "--usb", "on", "--usbxhci", "on", "--boot1=disk", "--boot2=none", "--boot3=none", "--boot4=none")
}

function Start-WorkVmReady {
    if (-not (Test-Path -LiteralPath $StartReadyScriptPath)) {
        throw "Start-ready script was not found: $StartReadyScriptPath"
    }

    Write-Log "Starting WorkVM through start-ready script."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $StartReadyScriptPath -VMName $VMName -Width $Width -Height $Height -BitsPerPixel $BitsPerPixel 2>&1 |
        ForEach-Object { Write-Log "  $_" }

    if ($LASTEXITCODE -ne 0) {
        throw "Start-ready script failed with exit code $LASTEXITCODE."
    }

    Start-Sleep -Seconds 8
    [void](Invoke-VBoxTimed @("controlvm", $VMName, "setscreenlayout", "0", "primary", "0", "0", "$Width", "$Height", "$BitsPerPixel") -TimeoutSeconds 10)
    [void](Invoke-VBoxTimed @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel", "0", "yes", "0", "0") -TimeoutSeconds 10)
}

function Test-WorkVmDisplayReady {
    $info = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    return $info -match "State:\s+running" -and
        $info -match "Video mode:\s+\d+x\d+x32.*enabled"
}

function Reset-GuestGraphicsDriver {
    Write-Log "Resetting Windows guest graphics driver with Win+Ctrl+Shift+B."
    [void](Invoke-VBoxTimed @("controlvm", $VMName, "keyboardputscancode", "e0", "5b", "1d", "2a", "30", "b0", "aa", "9d", "e0", "db") -TimeoutSeconds 10)
    Start-Sleep -Seconds 5
}

function Wait-WorkVmDisplayReady {
    param([int]$TimeoutSeconds = 180)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $resetSent = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-WorkVmDisplayReady) {
            Write-Log "WorkVM display is active."
            return $true
        }

        $info = (& $script:VBoxManage showvminfo $VMName) -join "`n"
        if (-not $resetSent -and
            $info -match "State:\s+running" -and
            $info -match "Video mode:\s+\d+x\d+x0.*disabled") {
            Reset-GuestGraphicsDriver
            $resetSent = $true
            continue
        }

        Start-Sleep -Seconds 5
    }

    $info = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    ($info -split "`n" | Select-String -Pattern "State:|Video mode:|Graphics Mode|Additions run level" -Context 0,3) |
        ForEach-Object { Write-Log $_.ToString() }
    return $false
}

function Ensure-WorkVmDisplayReady {
    if (Wait-WorkVmDisplayReady -TimeoutSeconds 180) {
        return
    }

    Write-Log "WorkVM display did not become active. Sending one final guest graphics reset without restarting the VM."
    Reset-GuestGraphicsDriver
    if (-not (Wait-WorkVmDisplayReady -TimeoutSeconds 30)) {
        throw "WorkVM is running but the VirtualBox display never became active."
    }
}

function Start-And-AttachBluetooth {
    $state = Get-VmState
    if ($state -eq "aborted") {
        throw "WorkRDP is aborted after a failed VirtualBox USB capture. Reboot the Windows host to clear the stale Bluetooth/VirtualBox USB state before trying Bluetooth passthrough again."
    }

    if ($state -ne "running") {
        Start-WorkVmReady
    } else {
        Write-Log "VM is already running."
        [void](Invoke-VBoxTimed @("controlvm", $VMName, "setscreenlayout", "0", "primary", "0", "0", "$Width", "$Height", "$BitsPerPixel") -TimeoutSeconds 10)
        [void](Invoke-VBoxTimed @("controlvm", $VMName, "setvideomodehint", "$Width", "$Height", "$BitsPerPixel", "0", "yes", "0", "0") -TimeoutSeconds 10)
    }

    Ensure-WorkVmDisplayReady

    $uuid = Get-BluetoothUsbUuid
    $backendRestartAttempted = $false
    if (Test-BluetoothUsbAttached) {
        Write-Log "MediaTek Bluetooth USB device is already attached to VM."
        if (Test-GuestBluetoothVisible) {
            return
        }

        Write-Log "VirtualBox reports the USB device already attached, but the guest does not see Bluetooth yet."
    }

    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Write-Log "Attaching MediaTek Bluetooth USB device attempt ${attempt}: $uuid"
        if (-not (Invoke-VBoxTimed @("controlvm", $VMName, "usbdetach", $uuid) -TimeoutSeconds 10)) {
            Write-Log "  usbdetach did not complete; continuing with attach."
        }
        Start-Sleep -Seconds 2
        $attachedNow = Invoke-VBoxTimed @("controlvm", $VMName, "usbattach", $uuid) -TimeoutSeconds 15
        if (-not $attachedNow) {
            Write-Log "  usbattach did not complete on attempt $attempt."
        }
        Start-Sleep -Seconds 5

        if (Test-BluetoothUsbAttached) {
            Write-Log "MediaTek Bluetooth USB device is attached to VM."
            if (Test-GuestBluetoothVisible) {
                return
            }

            Write-Log "VirtualBox reports the USB device attached, but the guest does not see Bluetooth yet."
        }

        $usbBlocks = @(Get-BluetoothUsbBlocks) -join "`n"
        $state = Get-VmState
        if ($state -eq "aborted") {
            throw "WorkRDP aborted while VirtualBox was trying to attach Bluetooth. Reboot the Windows host to clear the stale Bluetooth/VirtualBox USB state."
        }

        $capturedButNotAttached = $usbBlocks -match "Current State:\s+Captured"
        if ($capturedButNotAttached) {
            Write-Log "MediaTek Bluetooth USB device is captured by VirtualBox but is not listed as attached to this VM yet."
        }

        if (-not $backendRestartAttempted -and (
            (-not $attachedNow -and $capturedButNotAttached) -or
            $usbBlocks -match "busy with a previous request" -or
            $usbBlocks -match "Current State:\s+Busy")) {
            Write-Log "Bluetooth USB is still busy on the host. Restarting the VirtualBox backend while preserving VM saved state."
            $backendRestartAttempted = $true
            Restart-VirtualBoxBackendPreservingVmState
            $uuid = Get-BluetoothUsbUuid
            Start-Sleep -Seconds 5
        }
    }

    $vmInfo = (& $script:VBoxManage showvminfo $VMName) -join "`n"
    Add-Content -LiteralPath $LogPath -Value $vmInfo
    Write-Log "Final attached USB excerpt:"
    ($vmInfo -split "`n" | Select-String -Pattern "Currently attached USB devices|13d3|3602|MediaTek|Wireless|IMC" -Context 0,6) |
        ForEach-Object { Write-Log $_.ToString() }

    Ensure-WorkVmDisplayReady

    $usbBlocks = @(Get-BluetoothUsbBlocks) -join "`n"
    if ($usbBlocks -match "Current State:\s+Captured") {
        throw "MediaTek Bluetooth is stuck in VirtualBox as captured but not attached to WorkRDP. Windows/VirtualBox did not release the USB capture cleanly; reboot the Windows host, then click Open VM again. See $LogPath."
    }

    throw "MediaTek Bluetooth USB device was not attached to VM. See $LogPath."
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-Width", "$Width",
        "-Height", "$Height",
        "-BitsPerPixel", "$BitsPerPixel",
        "-NoElevate"
    )
    Write-Host "Requesting administrator elevation for Bluetooth USB repair..."
    Start-Process -FilePath "powershell.exe" -ArgumentList $args -Verb RunAs
    exit 0
}

if (-not (Test-IsAdmin)) {
    throw "This script must run as administrator."
}

Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
try {
    Enter-RepairLock
    Write-Log "Starting Bluetooth passthrough repair."
    $script:VBoxManage = Get-VBoxManagePath
    if (Test-HostBluetoothPendingReboot) {
        throw "Windows reports the MediaTek Bluetooth USB device is pending a host reboot. Reboot Windows before trying WorkVM Bluetooth passthrough again."
    }

    $initialState = Get-VmState
    Write-Log "VM state before Bluetooth attach: $initialState"
    Set-BroadBluetoothFilter
    if ($initialState -ne "running") {
        Start-WorkVmReady
    }
    Start-And-AttachBluetooth
    Write-Log "Bluetooth passthrough repair complete."
}
finally {
    Exit-RepairLock
}
