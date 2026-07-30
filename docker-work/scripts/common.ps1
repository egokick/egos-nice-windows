#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:DockerWorkRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$script:RepoRoot = (Resolve-Path -LiteralPath (Join-Path $script:DockerWorkRoot "..")).Path
$script:DockerWorkCache = Join-Path $script:DockerWorkRoot ".cache"
$script:DockerWorkState = Join-Path $script:DockerWorkRoot ".state"
$script:DockerWorkLog = Join-Path $script:DockerWorkCache "docker-work.log"
$script:DockerWorkDistro = "StayActiveDocker"
$script:DockerWorkContainer = "stayactive-work-browser"
$script:BluetoothHardwareId = "13d3:3602"
$script:BluetoothWindowsParentPattern = '^USB\\VID_13D3&PID_3602\\'
$script:BluetoothWindowsInterfacePattern = '^USB\\VID_13D3&PID_3602&MI_00\\'
$script:BluetoothMutexName = "Global\StayActiveWorkVmBluetoothHandoff"
$script:BluetoothMutex = $null
$script:BluetoothMutexOwned = $false

New-Item -ItemType Directory -Force -Path $script:DockerWorkCache | Out-Null
New-Item -ItemType Directory -Force -Path $script:DockerWorkState | Out-Null

function Write-DockerWorkLog {
    param([Parameter(Mandatory)][string]$Message)

    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $script:DockerWorkLog -Value $line -ErrorAction SilentlyContinue
    Write-Host $line
}

function Test-DockerWorkAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-DockerWorkElevatedScript {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [string[]]$Arguments = @()
    )

    $quotedScript = "'" + $ScriptPath.Replace("'", "''") + "'"
    $command = "& $quotedScript"
    foreach ($argument in $Arguments) {
        if ($argument -match '^-[A-Za-z][A-Za-z0-9]*$') {
            $command += " $argument"
        }
        else {
            $command += " '" + $argument.Replace("'", "''") + "'"
        }
    }

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Write-Host "Requesting administrator permission..."
    $process = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", $encoded
        ) `
        -Verb RunAs `
        -Wait `
        -PassThru
    return $process.ExitCode
}

function ConvertTo-DockerWorkProcessArgument {
    param([AllowEmptyString()][string]$Argument)

    if ($Argument -eq "") {
        return '""'
    }
    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [Text.StringBuilder]::new()
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

function Invoke-DockerWorkProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [string[]]$Arguments = @(),
        [int]$TimeoutSeconds = 60,
        [string]$DisplayCommand,
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    if (-not $DisplayCommand) {
        $DisplayCommand = "$FileName $($Arguments -join ' ')"
    }
    if (-not $Quiet) {
        Write-DockerWorkLog $DisplayCommand
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-DockerWorkProcessArgument -Argument $_
    }) -join " ")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        try {
            $process.Kill()
        }
        catch {
        }
        [void]$process.WaitForExit(5000)
    }

    try {
        [void][Threading.Tasks.Task]::WaitAll(
            [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask),
            5000
        )
    }
    catch {
    }

    $stdout = if ($stdoutTask.IsCompleted) { $stdoutTask.Result } else { "" }
    $stderr = if ($stderrTask.IsCompleted) { $stderrTask.Result } else { "" }
    $exitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }
    $process.Dispose()
    $output = ($stdout + $stderr).Trim()

    if (-not $Quiet -and $output) {
        $output -split "\r?\n" | ForEach-Object {
            Write-DockerWorkLog "  $_"
        }
    }

    $result = [pscustomobject]@{
        Success = (-not $timedOut -and $exitCode -eq 0)
        TimedOut = $timedOut
        ExitCode = $exitCode
        Output = $output
        StdOut = $stdout.Trim()
        StdErr = $stderr.Trim()
    }

    if (-not $result.Success -and -not $AllowFailure) {
        $reason = if ($timedOut) {
            "timed out after $TimeoutSeconds seconds"
        }
        elseif ($output) {
            "failed with exit code ${exitCode}: $output"
        }
        else {
            "failed with exit code $exitCode"
        }
        throw "$DisplayCommand $reason."
    }

    return $result
}

function Enter-DockerWorkBluetoothLock {
    if ($script:BluetoothMutexOwned -or $null -ne $script:BluetoothMutex) {
        throw "This process already has a Bluetooth handoff lock object."
    }

    $createdNew = $false
    $mutex = [Threading.Mutex]::new(
        $false,
        $script:BluetoothMutexName,
        [ref]$createdNew
    )
    $acquired = $false
    try {
        $acquired = $mutex.WaitOne(0)
        if (-not $acquired) {
            throw "Another StayActive Bluetooth handoff is already running."
        }
    }
    catch [Threading.AbandonedMutexException] {
        $acquired = $true
        Write-DockerWorkLog "Recovered the Bluetooth handoff lock from a terminated process."
    }
    catch {
        $mutex.Dispose()
        throw
    }

    $script:BluetoothMutex = $mutex
    $script:BluetoothMutexOwned = $true
}

function Test-DockerWorkBluetoothLockHeld {
    return $script:BluetoothMutexOwned -and $null -ne $script:BluetoothMutex
}

function Exit-DockerWorkBluetoothLock {
    if ($null -eq $script:BluetoothMutex) {
        $script:BluetoothMutexOwned = $false
        return
    }
    try {
        if ($script:BluetoothMutexOwned) {
            $script:BluetoothMutex.ReleaseMutex()
        }
    }
    catch {
    }
    finally {
        $script:BluetoothMutex.Dispose()
        $script:BluetoothMutex = $null
        $script:BluetoothMutexOwned = $false
    }
}

function ConvertTo-BashSingleQuoted {
    param([Parameter(Mandatory)][string]$Value)

    return "'" + $Value.Replace("'", "'""'""'") + "'"
}

function Test-DockerWorkDistroInstalled {
    $result = Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--list", "--quiet") `
        -TimeoutSeconds 15 `
        -AllowFailure `
        -Quiet
    if (-not $result.Success) {
        return $false
    }

    $names = ($result.Output -replace [char]0, "") -split "\r?\n"
    return @($names | Where-Object {
        $_.Trim() -eq $script:DockerWorkDistro
    }).Count -eq 1
}

function Test-DockerWorkDistroRunning {
    $result = Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @("--list", "--running", "--quiet") `
        -TimeoutSeconds 15 `
        -AllowFailure `
        -Quiet
    if (-not $result.Success) {
        return $false
    }

    $names = ($result.Output -replace [char]0, "") -split "\r?\n"
    return @($names | Where-Object {
        $_.Trim() -eq $script:DockerWorkDistro
    }).Count -eq 1
}

function Invoke-DockerWorkWsl {
    param(
        [Parameter(Mandatory)][string]$Command,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    # wsl.exe applies one shell-like expansion layer to command arguments on
    # this host even after `--`. Sending the script as Base64 prevents `$name`
    # and command substitutions from being expanded before the intended Bash
    # process receives them.
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($Command)
    )
    # End the Linux process group before the Windows-side process timeout can
    # release the handoff mutex and begin rollback.
    $linuxTimeoutSeconds = [Math]::Max(1, $TimeoutSeconds - 2)
    $bootstrap = "printf '%s' '$encodedCommand' | base64 --decode | timeout --signal=TERM --kill-after=5s ${linuxTimeoutSeconds}s /bin/bash"

    return Invoke-DockerWorkProcess `
        -FileName "wsl.exe" `
        -Arguments @(
            "--distribution", $script:DockerWorkDistro,
            "--user", "root",
            "--",
            "bash", "-lc", $bootstrap
        ) `
        -TimeoutSeconds $TimeoutSeconds `
        -DisplayCommand "wsl -d $script:DockerWorkDistro -- <command>" `
        -AllowFailure:$AllowFailure `
        -Quiet:$Quiet
}

function Get-DockerWorkWslRoot {
    $quoted = ConvertTo-BashSingleQuoted -Value $script:DockerWorkRoot
    $result = Invoke-DockerWorkWsl `
        -Command "wslpath -a $quoted" `
        -TimeoutSeconds 15 `
        -Quiet
    return $result.StdOut.Trim()
}

function Invoke-DockerWorkCompose {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 120,
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    $root = ConvertTo-BashSingleQuoted -Value (Get-DockerWorkWslRoot)
    $composeArguments = ($Arguments | ForEach-Object {
        ConvertTo-BashSingleQuoted -Value $_
    }) -join " "
    return Invoke-DockerWorkWsl `
        -Command "cd $root && docker compose $composeArguments" `
        -TimeoutSeconds $TimeoutSeconds `
        -AllowFailure:$AllowFailure `
        -Quiet:$Quiet
}

function Get-UsbipdPath {
    $command = Get-Command "usbipd.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles "usbipd-win\usbipd.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "usbipd-win\usbipd.exe")
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }
    throw "usbipd-win is not installed."
}

function Test-DockerWorkUsbipdInstalled {
    try {
        [void](Get-UsbipdPath)
        return $true
    }
    catch {
        return $false
    }
}

function Invoke-DockerWorkUsbipd {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    return Invoke-DockerWorkProcess `
        -FileName (Get-UsbipdPath) `
        -Arguments $Arguments `
        -TimeoutSeconds $TimeoutSeconds `
        -DisplayCommand "usbipd $($Arguments -join ' ')" `
        -AllowFailure:$AllowFailure `
        -Quiet:$Quiet
}

function Get-DockerWorkUsbipdBluetoothState {
    if (-not (Test-DockerWorkUsbipdInstalled)) {
        return "Unavailable"
    }

    $result = Invoke-DockerWorkUsbipd `
        -Arguments @("list") `
        -TimeoutSeconds 30 `
        -AllowFailure `
        -Quiet
    if (-not $result.Success) {
        return "Unknown"
    }

    $matchingLines = @($result.Output -split "\r?\n" | Where-Object {
        $_ -match '(?i)(?:^|\s)13d3:3602(?:\s|$)'
    })
    if ($matchingLines.Count -eq 0) {
        return "Missing"
    }
    if ($matchingLines.Count -ne 1) {
        throw "usbipd listed more than one 13d3:3602 adapter."
    }

    $line = $matchingLines[0]
    foreach ($state in @("Not shared", "Shared", "Attached")) {
        if ($line -match "(?i)\s+$([regex]::Escape($state))\s*$") {
            return $state
        }
    }
    return "Unknown"
}

function Test-VirtualBoxOwnsBluetooth {
    $vboxManage = Join-Path $env:ProgramFiles "Oracle\VirtualBox\VBoxManage.exe"
    if (-not (Test-Path -LiteralPath $vboxManage)) {
        return $false
    }

    $result = Invoke-DockerWorkProcess `
        -FileName $vboxManage `
        -Arguments @("showvminfo", "WorkRDP") `
        -TimeoutSeconds 10 `
        -AllowFailure `
        -Quiet
    if (-not $result.Success) {
        return $false
    }

    $section = [regex]::Match(
        $result.Output,
        'Currently attached USB devices:\s*(?<devices>[\s\S]*?)(?:\r?\nBandwidth groups:|\r?\nShared folders:|\r?\nVRDE:|\z)',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    if (-not $section.Success -or $section.Groups["devices"].Value -match '<none>') {
        return $false
    }

    return $section.Groups["devices"].Value -match '(?im)^\s*VendorId\s*:\s*(?:0x)?13d3\b' -and
        $section.Groups["devices"].Value -match '(?im)^\s*ProductId\s*:\s*(?:0x)?3602\b'
}

function Assert-VirtualBoxDoesNotOwnBluetooth {
    if (Test-VirtualBoxOwnsBluetooth) {
        throw "WorkRDP still owns the exact 13d3:3602 Bluetooth adapter."
    }
}

function Release-VirtualBoxBluetooth {
    if (-not (Test-VirtualBoxOwnsBluetooth)) {
        return
    }

    $returnScript = Join-Path $script:RepoRoot "workvm\scripts\33-return-laptop-bluetooth-to-host.ps1"
    if (-not (Test-Path -LiteralPath $returnScript)) {
        throw "The audited VirtualBox Bluetooth return script was not found."
    }

    Write-DockerWorkLog "Releasing the exact Bluetooth adapter from WorkRDP first."
    $result = Invoke-DockerWorkProcess `
        -FileName "powershell.exe" `
        -Arguments @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $returnScript,
            "-VMName", "WorkRDP",
            "-NoElevate"
        ) `
        -TimeoutSeconds 1200 `
        -DisplayCommand "workvm script 33 (return exact Bluetooth adapter)" `
        -AllowFailure
    if (-not $result.Success) {
        throw "VirtualBox could not return Bluetooth safely: $($result.Output)"
    }
}

function Test-HealthyPnpDevice {
    param($Device)

    if ($null -eq $Device -or [string]$Device.Status -ne "OK") {
        return $false
    }
    $problem = [string]$Device.Problem
    return [string]::IsNullOrWhiteSpace($problem) -or
        $problem -eq "0" -or
        $problem -eq "CM_PROB_NONE"
}

function Get-DockerWorkHostBluetoothState {
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)
    $parents = @($devices | Where-Object {
        $_.InstanceId -match $script:BluetoothWindowsParentPattern -and
        $_.InstanceId -notmatch '&MI_[0-9A-F]{2}\\'
    })
    $interfaces = @($devices | Where-Object {
        $_.InstanceId -match $script:BluetoothWindowsInterfacePattern
    })
    $service = Get-Service -Name "bthserv" -ErrorAction SilentlyContinue

    return [pscustomobject]@{
        Parents = $parents
        Interfaces = $interfaces
        Service = $service
        Healthy = (
            $parents.Count -eq 1 -and
            $interfaces.Count -eq 1 -and
            (Test-HealthyPnpDevice -Device $parents[0]) -and
            (Test-HealthyPnpDevice -Device $interfaces[0]) -and
            $null -ne $service -and
            [string]$service.Status -eq "Running"
        )
    }
}

function Restart-DockerWorkFailedBluetoothUsbPlaceholder {
    # A failed USB/IP return can leave the built-in port present as the
    # standard VID_0000/PID_0002 "Device Descriptor Request Failed" node while
    # the real MediaTek parent is a phantom. Correlate both devnodes by their
    # exact hub parent and physical port, then restart only that failed
    # placeholder. Never restart the root hub and never remove a device.
    $phantomParents = @(Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InstanceId -match $script:BluetoothWindowsParentPattern -and
            $_.InstanceId -notmatch '&MI_[0-9A-F]{2}\\'
        })
    if ($phantomParents.Count -ne 1) {
        return $false
    }

    $phantomId = [string]$phantomParents[0].InstanceId
    $phantomHub = [string](Get-PnpDeviceProperty `
        -InstanceId $phantomId `
        -KeyName "DEVPKEY_Device_Parent" `
        -ErrorAction SilentlyContinue).Data
    $phantomLocation = [string](Get-PnpDeviceProperty `
        -InstanceId $phantomId `
        -KeyName "DEVPKEY_Device_LocationInfo" `
        -ErrorAction SilentlyContinue).Data
    if ([string]::IsNullOrWhiteSpace($phantomHub) -or
        [string]::IsNullOrWhiteSpace($phantomLocation)) {
        return $false
    }

    $matchingPlaceholders = @()
    $failedUsbDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InstanceId -match '^USB\\VID_0000&PID_0002\\' -and
            -not (Test-HealthyPnpDevice -Device $_)
        })
    foreach ($failedDevice in $failedUsbDevices) {
        $failedId = [string]$failedDevice.InstanceId
        $failedHub = [string](Get-PnpDeviceProperty `
            -InstanceId $failedId `
            -KeyName "DEVPKEY_Device_Parent" `
            -ErrorAction SilentlyContinue).Data
        $failedLocation = [string](Get-PnpDeviceProperty `
            -InstanceId $failedId `
            -KeyName "DEVPKEY_Device_LocationInfo" `
            -ErrorAction SilentlyContinue).Data
        if ($failedHub -eq $phantomHub -and
            $failedLocation -eq $phantomLocation) {
            $matchingPlaceholders += $failedDevice
        }
    }

    if ($matchingPlaceholders.Count -gt 1) {
        throw "More than one failed USB placeholder matched the exact Bluetooth port."
    }
    if ($matchingPlaceholders.Count -eq 0) {
        return $false
    }

    Write-DockerWorkLog "Restarting the exact failed USB placeholder on the MediaTek Bluetooth port."
    $restart = Invoke-DockerWorkProcess `
        -FileName "pnputil.exe" `
        -Arguments @("/restart-device", [string]$matchingPlaceholders[0].InstanceId) `
        -TimeoutSeconds 45 `
        -AllowFailure
    if (-not $restart.Success -and $restart.ExitCode -ne 3010) {
        throw "Windows could not restart the failed Bluetooth-port placeholder (pnputil exit $($restart.ExitCode))."
    }
    [void](Invoke-DockerWorkProcess `
        -FileName "pnputil.exe" `
        -Arguments @("/scan-devices") `
        -TimeoutSeconds 45 `
        -AllowFailure)
    return $true
}

function Restore-DockerWorkHostBluetooth {
    Write-DockerWorkLog "Restoring and verifying the exact Windows Bluetooth adapter."
    [void](Invoke-DockerWorkProcess `
        -FileName "pnputil.exe" `
        -Arguments @("/scan-devices") `
        -TimeoutSeconds 45 `
        -AllowFailure)

    $deadline = (Get-Date).AddSeconds(90)
    $nextRescan = (Get-Date).AddSeconds(8)
    $failedPlaceholderRestartAttempted = $false
    $enabled = @{}
    $restarted = @{}
    do {
        Get-Service -Name "bthserv", "BluetoothUserService_*" -ErrorAction SilentlyContinue |
            Where-Object { $_.Status -ne "Running" } |
            ForEach-Object {
                Start-Service -Name $_.Name -ErrorAction SilentlyContinue
            }

        $state = Get-DockerWorkHostBluetoothState
        if ($state.Healthy) {
            Write-DockerWorkLog "STAYACTIVE_BLUETOOTH_HOST_READY"
            return
        }

        if (($state.Parents.Count -ne 1 -or $state.Interfaces.Count -ne 1) -and
            -not $failedPlaceholderRestartAttempted) {
            $failedPlaceholderRestartAttempted = $true
            if (Restart-DockerWorkFailedBluetoothUsbPlaceholder) {
                Start-Sleep -Seconds 3
                continue
            }
        }

        if (($state.Parents.Count -ne 1 -or $state.Interfaces.Count -ne 1) -and
            (Get-Date) -ge $nextRescan) {
            [void](Invoke-DockerWorkProcess `
                -FileName "pnputil.exe" `
                -Arguments @("/scan-devices") `
                -TimeoutSeconds 45 `
                -AllowFailure)
            $nextRescan = (Get-Date).AddSeconds(8)
        }

        $candidates = @($state.Parents) + @($state.Interfaces)
        foreach ($device in $candidates) {
            $id = [string]$device.InstanceId
            $disabled = [string]$device.Status -eq "Disabled" -or
                [string]$device.Problem -match 'DISABLED|^22$'
            if ($disabled -and -not $enabled.ContainsKey($id)) {
                [void](Invoke-DockerWorkProcess `
                    -FileName "pnputil.exe" `
                    -Arguments @("/enable-device", $id) `
                    -TimeoutSeconds 30 `
                    -AllowFailure)
                $enabled[$id] = $true
            }
            elseif (-not (Test-HealthyPnpDevice -Device $device) -and
                -not $restarted.ContainsKey($id)) {
                [void](Invoke-DockerWorkProcess `
                    -FileName "pnputil.exe" `
                    -Arguments @("/restart-device", $id) `
                    -TimeoutSeconds 30 `
                    -AllowFailure)
                $restarted[$id] = $true
            }
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    $final = Get-DockerWorkHostBluetoothState
    $parentSummary = @($final.Parents | ForEach-Object {
        "$($_.Status)/$($_.Problem):$($_.InstanceId)"
    }) -join ", "
    $interfaceSummary = @($final.Interfaces | ForEach-Object {
        "$($_.Status)/$($_.Problem):$($_.InstanceId)"
    }) -join ", "
    throw "Windows Bluetooth did not become healthy (parents=[$parentSummary], interfaces=[$interfaceSummary])."
}

function Test-DockerWorkWslUsbPresent {
    if (-not (Test-DockerWorkDistroRunning)) {
        return $false
    }

    $result = Invoke-DockerWorkWsl `
        -Command "lsusb -d $script:BluetoothHardwareId >/dev/null 2>&1" `
        -TimeoutSeconds 15 `
        -AllowFailure `
        -Quiet
    return $result.Success
}

function Wait-DockerWorkWslUsb {
    param(
        [bool]$Present,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if ((Test-DockerWorkWslUsbPresent) -eq $Present) {
            return
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    $description = if ($Present) { "appear in WSL" } else { "leave WSL" }
    throw "The exact 13d3:3602 adapter did not $description within $TimeoutSeconds seconds."
}

function Test-DockerWorkContainerRunning {
    if (-not (Test-DockerWorkDistroRunning)) {
        return $false
    }
    $result = Invoke-DockerWorkWsl `
        -Command "docker inspect -f '{{.State.Running}}' $script:DockerWorkContainer 2>/dev/null" `
        -TimeoutSeconds 15 `
        -AllowFailure `
        -Quiet
    return $result.Success -and $result.StdOut.Trim() -eq "true"
}

function Wait-DockerWorkContainerBaseReady {
    param([int]$TimeoutSeconds = 120)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-DockerWorkContainerRunning) {
            $health = Invoke-DockerWorkContainer `
                -Arguments @("/opt/stayactive/healthcheck.sh", "--base") `
                -TimeoutSeconds 15 `
                -AllowFailure `
                -Quiet
            if ($health.Success -and
                $health.Output -match "STAYACTIVE_DOCKER_WORK_HEALTHY") {
                return
            }
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw "The work-browser container did not reach its base GUI/Chrome health contract within $TimeoutSeconds seconds."
}

function Assert-DockerWorkWindowsViewerReachable {
    try {
        $response = Invoke-WebRequest `
            -UseBasicParsing `
            -Uri "http://127.0.0.1:6080/vnc.html" `
            -TimeoutSec 10
    }
    catch {
        throw "Windows could not reach the container noVNC viewer on loopback: $($_.Exception.Message)"
    }

    if ([int]$response.StatusCode -ne 200 -or
        [string]$response.Content -notmatch '(?i)\bnoVNC\b') {
        throw "Windows loopback port 6080 did not return the expected noVNC page."
    }
}

function Invoke-DockerWorkContainer {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    $containerArguments = ($Arguments | ForEach-Object {
        ConvertTo-BashSingleQuoted -Value $_
    }) -join " "
    return Invoke-DockerWorkWsl `
        -Command "docker exec $script:DockerWorkContainer $containerArguments" `
        -TimeoutSeconds $TimeoutSeconds `
        -AllowFailure:$AllowFailure `
        -Quiet:$Quiet
}

function Attach-DockerWorkBluetooth {
    Assert-VirtualBoxDoesNotOwnBluetooth

    $wslUsbPresent = Test-DockerWorkWslUsbPresent
    $usbipdState = Get-DockerWorkUsbipdBluetoothState
    if (-not $wslUsbPresent -and $usbipdState -eq "Attached") {
        Write-DockerWorkLog "Recovering a stale usbipd Attached state before the new handoff."
        [void](Invoke-DockerWorkUsbipd `
            -Arguments @(
                "detach",
                "--hardware-id", $script:BluetoothHardwareId
            ) `
            -TimeoutSeconds 45 `
            -AllowFailure)
        Wait-DockerWorkWslUsb -Present $false -TimeoutSeconds 30
        Restore-DockerWorkHostBluetooth
        $usbipdState = Get-DockerWorkUsbipdBluetoothState
    }

    $hostState = Get-DockerWorkHostBluetoothState
    if (-not $hostState.Healthy -and -not $wslUsbPresent) {
        Restore-DockerWorkHostBluetooth
        $hostState = Get-DockerWorkHostBluetoothState
    }
    if (-not $hostState.Healthy -and -not $wslUsbPresent) {
        throw "Bluetooth is owned by neither a healthy Windows host nor the Docker WSL distribution."
    }
    if (-not $wslUsbPresent -and $usbipdState -eq "Not shared") {
        Write-DockerWorkLog "Restoring normal usbipd sharing for the exact adapter."
        $bind = Invoke-DockerWorkUsbipd `
            -Arguments @(
                "bind",
                "--hardware-id", $script:BluetoothHardwareId
            ) `
            -TimeoutSeconds 90 `
            -AllowFailure
        if (-not $bind.Success -and
            $bind.Output -notmatch '(?i)already (?:shared|bound)') {
            throw "usbipd could not normally share 13d3:3602: $($bind.Output)"
        }
        $usbipdState = Get-DockerWorkUsbipdBluetoothState
    }
    if (-not $wslUsbPresent -and $usbipdState -ne "Shared") {
        throw "usbipd must list 13d3:3602 as Shared before attach; current state is '$usbipdState'. Run setup.ps1."
    }

    if (-not $wslUsbPresent) {
        # WSL services hot-plugged USB from its hidden system namespace. Put
        # firmware in the modules VHD (visible there as /modules) and select
        # that stable path before btusb handles the new controller.
        [void](Invoke-DockerWorkWsl `
            -Command @'
set -Eeuo pipefail
release="$(uname -r)"
firmware='mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin'
test -r "/usr/lib/modules/$release/firmware/$firmware"
printf '%s' '/modules/firmware' \
    > /sys/module/firmware_class/parameters/path
test "$(cat /sys/module/firmware_class/parameters/path)" = '/modules/firmware'
'@ `
            -TimeoutSeconds 30)
        [void](Invoke-DockerWorkUsbipd `
            -Arguments @(
                "attach",
                "--wsl", $script:DockerWorkDistro,
                "--hardware-id", $script:BluetoothHardwareId
            ) `
            -TimeoutSeconds 60)
        Wait-DockerWorkWslUsb -Present $true -TimeoutSeconds 30
    }
    if ((Get-DockerWorkUsbipdBluetoothState) -ne "Attached") {
        throw "usbipd did not report 13d3:3602 as Attached after the WSL handoff."
    }

    [void](Invoke-DockerWorkWsl `
        -Command "modprobe btusb >/dev/null 2>&1 || true; udevadm settle --timeout=20 || true" `
        -TimeoutSeconds 30)

    if (-not (Test-DockerWorkContainerRunning)) {
        throw "The work-browser container is not running."
    }

    [void](Invoke-DockerWorkContainer `
        -Arguments @("/opt/stayactive/resume-bluetooth.sh") `
        -TimeoutSeconds 60)
    Write-DockerWorkLog "STAYACTIVE_BLUETOOTH_CONTAINER_READY"
}

function Detach-DockerWorkBluetooth {
    if (Test-DockerWorkContainerRunning) {
        [void](Invoke-DockerWorkContainer `
            -Arguments @("/opt/stayactive/prepare-detach.sh") `
            -TimeoutSeconds 30 `
            -AllowFailure)
    }

    if (Test-DockerWorkUsbipdInstalled) {
        [void](Invoke-DockerWorkUsbipd `
            -Arguments @(
                "detach",
                "--hardware-id", $script:BluetoothHardwareId
            ) `
            -TimeoutSeconds 45 `
            -AllowFailure)
    }

    $usbStillInWsl = Test-DockerWorkWslUsbPresent
    $usbipdState = Get-DockerWorkUsbipdBluetoothState
    if ($usbStillInWsl -or $usbipdState -eq "Attached") {
        Write-DockerWorkLog "Normal USB/IP detach did not release the adapter; terminating only the dedicated WSL distribution and retrying exact detach."
        [void](Invoke-DockerWorkProcess `
            -FileName "wsl.exe" `
            -Arguments @("--terminate", $script:DockerWorkDistro) `
            -TimeoutSeconds 45 `
            -AllowFailure)
        if (Test-DockerWorkUsbipdInstalled) {
            [void](Invoke-DockerWorkUsbipd `
                -Arguments @(
                    "detach",
                    "--hardware-id", $script:BluetoothHardwareId
                ) `
                -TimeoutSeconds 45 `
                -AllowFailure)
        }
    }

    Wait-DockerWorkWslUsb -Present $false -TimeoutSeconds 30
    try {
        $finalUsbipdState = Get-DockerWorkUsbipdBluetoothState
    }
    catch {
        $finalUsbipdState = "Unknown"
        Write-DockerWorkLog "usbipd final-state corroboration was unavailable: $($_.Exception.Message)"
    }
    Restore-DockerWorkHostBluetooth
    if ($finalUsbipdState -eq "Attached") {
        throw "usbipd still reported 13d3:3602 as Attached after Windows PnP recovery."
    }
    if ($finalUsbipdState -in @("Unknown", "Unavailable")) {
        Write-DockerWorkLog "usbipd state was '$finalUsbipdState'; exact healthy Windows PnP ownership is authoritative."
    }
}

function Assert-DockerWorkInstalled {
    if (-not (Test-DockerWorkDistroInstalled)) {
        throw "StayActiveDocker is not installed. Run docker-work\scripts\setup.ps1 first."
    }
    [void](Get-UsbipdPath)
    if (-not (Test-Path -LiteralPath (Join-Path $script:DockerWorkState "setup-complete.json"))) {
        throw "Docker work-browser setup is incomplete. Run setup.ps1."
    }

    $kernelReady = Invoke-DockerWorkWsl `
        -Command @'
set -Eeuo pipefail
zgrep -qx 'CONFIG_BT_HCIBTUSB_MTK=y' /proc/config.gz
modinfo btusb >/dev/null
modinfo btmtk >/dev/null
test -r /lib/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin
test -r "/usr/lib/modules/$(uname -r)/firmware/mediatek/mt7925/BT_RAM_CODE_MT7925_1_1_hdr.bin"
'@ `
        -TimeoutSeconds 30 `
        -AllowFailure `
        -Quiet
    if (-not $kernelReady.Success) {
        throw "The active WSL kernel is not ready for MediaTek Bluetooth. Run setup.ps1."
    }
}
