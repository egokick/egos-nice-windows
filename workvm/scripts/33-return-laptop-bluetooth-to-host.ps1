#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [switch]$NoElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$CachePath = Join-Path $Root ".cache"
$LogPath = Join-Path $CachePath "bluetooth-return-to-host.log"
$BluetoothMutexName = "Global\StayActiveWorkVmBluetoothHandoff"
$BluetoothFilterName = "StayActive MediaTek Bluetooth 13d3:3602"
$GlobalHoldFilterName = "StayActive temporary Bluetooth hold 13d3:3602"
$BluetoothVendorId = "13d3"
$BluetoothProductId = "3602"
$script:BluetoothMutex = $null
$script:VBoxManage = $null

New-Item -ItemType Directory -Force -Path $CachePath | Out-Null

function Write-Log {
    param([string]$Message)

    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    $written = $false
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Add-Content -LiteralPath $LogPath -Value $line -ErrorAction Stop
            $written = $true
            break
        }
        catch {
            if ($attempt -lt 5) {
                Start-Sleep -Milliseconds 100
            }
        }
    }
    if (-not $written) {
        Write-Warning "Could not append to '$LogPath'; continuing the Bluetooth return."
    }
    Write-Host $line
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Enter-BluetoothHandoffLock {
    $createdNew = $false
    $script:BluetoothMutex = [System.Threading.Mutex]::new(
        $false,
        $BluetoothMutexName,
        [ref]$createdNew
    )
    try {
        if (-not $script:BluetoothMutex.WaitOne(0)) {
            throw "Another WorkVM Bluetooth handoff is already running."
        }
    }
    catch [System.Threading.AbandonedMutexException] {
        Write-Log "Recovered the Bluetooth handoff lock from a terminated process."
    }
}

function Exit-BluetoothHandoffLock {
    if ($null -eq $script:BluetoothMutex) {
        return
    }
    try {
        $script:BluetoothMutex.ReleaseMutex()
    }
    catch {
    }
    finally {
        $script:BluetoothMutex.Dispose()
        $script:BluetoothMutex = $null
    }
}

function Get-VBoxManagePath {
    $command = Get-Command VBoxManage -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }
    foreach ($path in @(
        "$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe",
        "${env:ProgramFiles(x86)}\Oracle\VirtualBox\VBoxManage.exe"
    )) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }
    throw "VirtualBox was not found."
}

function ConvertTo-ProcessArgument {
    param([AllowEmptyString()][string]$Argument)

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
        [string]$DisplayCommand
    )

    if (-not $DisplayCommand) {
        $DisplayCommand = "$FileName $($Arguments -join ' ')"
    }
    Write-Log $DisplayCommand

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-ProcessArgument -Argument $_
    }) -join " ")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        Write-Log "  timed out after ${TimeoutSeconds}s; terminating PID $($process.Id)"
        try {
            $process.Kill()
        }
        catch {
        }
        if (-not $process.WaitForExit(5000)) {
            Write-Log "  PID $($process.Id) did not terminate within the 5s kill grace period."
            $process.Dispose()
            return [pscustomobject]@{
                Success = $false
                TimedOut = $true
                ExitCode = -1
                Output = ""
            }
        }
    }

    $streamsCompleted = $false
    try {
        $streamsCompleted = [System.Threading.Tasks.Task]::WaitAll(
            [System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask),
            5000
        )
    }
    catch {
        Write-Log "  redirected process output failed: $($_.Exception.Message)"
    }
    if (-not $streamsCompleted) {
        Write-Log "  redirected process output did not close within 5s."
        $exitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }
        $process.Dispose()
        return [pscustomobject]@{
            Success = $false
            TimedOut = $true
            ExitCode = $exitCode
            Output = ""
        }
    }

    $output = $stdoutTask.Result + $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()

    if ($output) {
        $output.TrimEnd() -split "\r?\n" | ForEach-Object { Write-Log "  $_" }
    }

    return [pscustomobject]@{
        Success = (-not $timedOut -and $exitCode -eq 0)
        TimedOut = $timedOut
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-VBox {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 20,
        [switch]$AllowFail
    )

    $result = Invoke-ProcessTimed `
        -FileName $script:VBoxManage `
        -Arguments $Arguments `
        -TimeoutSeconds $TimeoutSeconds `
        -DisplayCommand "VBoxManage $($Arguments -join ' ')"
    $failed = -not $result.Success -or
        $result.Output -match "VBoxManage(?:\.exe)?: error:"
    if ($failed -and -not $AllowFail) {
        if ($result.TimedOut) {
            throw "VBoxManage timed out after ${TimeoutSeconds}s: $($Arguments -join ' ')"
        }
        throw "VBoxManage failed with exit code $($result.ExitCode): $($Arguments -join ' ')"
    }
    return $result
}

function Get-VmInfo {
    param([switch]$MachineReadable)

    $arguments = @("showvminfo", $VMName)
    if ($MachineReadable) {
        $arguments += "--machinereadable"
    }
    return (Invoke-VBox -Arguments $arguments -TimeoutSeconds 20).Output
}

function Get-BluetoothFilterRecords {
    $info = Get-VmInfo -MachineReadable
    $records = @()
    foreach ($match in [regex]::Matches(
        $info,
        '(?m)^USBFilterName(?<ordinal>\d+)="(?<name>[^"]*)"\r?$'
    )) {
        $ordinal = [int]$match.Groups["ordinal"].Value
        $vendorMatch = [regex]::Match(
            $info,
            ('(?m)^USBFilterVendorId{0}="(?<value>[^"]*)"\r?$' -f $ordinal)
        )
        $productMatch = [regex]::Match(
            $info,
            ('(?m)^USBFilterProductId{0}="(?<value>[^"]*)"\r?$' -f $ordinal)
        )
        $records += [pscustomobject]@{
            Index = $ordinal - 1
            Name = $match.Groups["name"].Value
            VendorId = $(if ($vendorMatch.Success) {
                ($vendorMatch.Groups["value"].Value -replace '^0x', '').ToLowerInvariant()
            } else { "" })
            ProductId = $(if ($productMatch.Success) {
                ($productMatch.Groups["value"].Value -replace '^0x', '').ToLowerInvariant()
            } else { "" })
        }
    }
    return @($records)
}

function Test-IsBluetoothFilter {
    param($Filter)

    return $Filter.Name -in @(
        $BluetoothFilterName,
        "Laptop MediaTek Bluetooth Adapter",
        "Laptop MediaTek Bluetooth Adapter VIDPID"
    ) -or (
        $Filter.VendorId -eq $BluetoothVendorId -and
        $Filter.ProductId -eq $BluetoothProductId
    )
}

function Disable-BluetoothUsbFilters {
    $filters = @(Get-BluetoothFilterRecords | Where-Object {
        Test-IsBluetoothFilter -Filter $_
    })
    foreach ($filter in $filters) {
        Write-Log "Disabling only Bluetooth filter index $($filter.Index); unrelated USB filters are unchanged."
        [void](Invoke-VBox -Arguments @(
            "usbfilter", "modify", "$($filter.Index)",
            "--target", $VMName,
            "--active", "no"
        ))
    }
    if ($filters.Count -eq 0) {
        Write-Log "No dedicated MediaTek Bluetooth filter exists; no USB filter was changed."
    }
}

function Get-GlobalBluetoothHoldFilters {
    $output = (Invoke-VBox -Arguments @("list", "usbfilters") -TimeoutSeconds 20).Output
    $records = @()
    foreach ($block in ($output -split "(?:\r?\n){2,}")) {
        $indexMatch = [regex]::Match($block, '(?m)^Index:\s*(\d+)\s*$')
        $nameMatch = [regex]::Match($block, '(?m)^Name:\s*(.+?)\s*$')
        if (-not $indexMatch.Success -or -not $nameMatch.Success) {
            continue
        }
        $records += [pscustomobject]@{
            Index = [int]$indexMatch.Groups[1].Value
            Name = $nameMatch.Groups[1].Value.Trim()
        }
    }
    return @($records | Where-Object { $_.Name -eq $GlobalHoldFilterName })
}

function Remove-GlobalBluetoothHoldFilter {
    $filters = @(Get-GlobalBluetoothHoldFilters | Sort-Object Index -Descending)
    foreach ($filter in $filters) {
        Write-Log "Removing temporary global Bluetooth hold filter index $($filter.Index)."
        [void](Invoke-VBox -Arguments @(
            "usbfilter", "remove", "$($filter.Index)",
            "--target", "global"
        ))
    }
    if ($filters.Count -eq 0) {
        Write-Log "No temporary global Bluetooth hold filter exists."
    }
}

function ConvertFrom-VBoxUsbBlocks {
    param([string]$Text)

    $devices = @()
    foreach ($block in ($Text -split "(?:\r?\n){2,}")) {
        $uuidMatch = [regex]::Match($block, '(?m)^\s*UUID:\s*([0-9a-fA-F-]+)\s*$')
        $vendorMatch = [regex]::Match($block, '(?m)^\s*VendorId:\s*0x([0-9a-fA-F]+)')
        $productMatch = [regex]::Match($block, '(?m)^\s*ProductId:\s*0x([0-9a-fA-F]+)')
        if (-not $uuidMatch.Success -or
            -not $vendorMatch.Success -or
            -not $productMatch.Success) {
            continue
        }
        $stateMatch = [regex]::Match($block, '(?m)^\s*Current State:\s*(.+?)\s*$')
        $addressMatch = [regex]::Match($block, '(?m)^\s*Address:\s*(.+?)\s*$')
        $portMatch = [regex]::Match($block, '(?m)^\s*Port:\s*(.+?)\s*$')
        $devices += [pscustomobject]@{
            Uuid = $uuidMatch.Groups[1].Value
            VendorId = $vendorMatch.Groups[1].Value.ToLowerInvariant()
            ProductId = $productMatch.Groups[1].Value.ToLowerInvariant()
            State = $(if ($stateMatch.Success) {
                $stateMatch.Groups[1].Value.Trim()
            } else { "" })
            Address = $(if ($addressMatch.Success) {
                $addressMatch.Groups[1].Value.Trim()
            } else { "" })
            Port = $(if ($portMatch.Success) {
                $portMatch.Groups[1].Value.Trim()
            } else { "" })
        }
    }
    return @($devices)
}

function Get-AttachedBluetoothDevices {
    $info = Get-VmInfo
    $section = [regex]::Match(
        $info,
        'Currently attached USB devices:\s*(?<devices>[\s\S]*?)(?:\r?\nBandwidth groups:|\r?\nShared folders:|\r?\nVRDE:|\z)'
    )
    if (-not $section.Success -or $section.Groups["devices"].Value -match "<none>") {
        return @()
    }
    return @(ConvertFrom-VBoxUsbBlocks -Text $section.Groups["devices"].Value |
        Where-Object {
            $_.VendorId -eq $BluetoothVendorId -and
            $_.ProductId -eq $BluetoothProductId
        })
}

function Get-HostBluetoothUsbDevices {
    $output = (Invoke-VBox -Arguments @("list", "usbhost") -TimeoutSeconds 20).Output
    return @(ConvertFrom-VBoxUsbBlocks -Text $output | Where-Object {
        $_.VendorId -eq $BluetoothVendorId -and
        $_.ProductId -eq $BluetoothProductId
    })
}

function Wait-BluetoothDetached {
    param(
        [string]$Uuid,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $attached = @(Get-AttachedBluetoothDevices | Where-Object {
            $_.Uuid -eq $Uuid
        })
        if ($attached.Count -eq 0) {
            return
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw "Exact Bluetooth UUID $Uuid remained attached after ${TimeoutSeconds}s."
}

function Test-PnpDisabled {
    param($Device)

    return [string]$Device.Status -eq "Disabled" -or
        [string]$Device.Problem -match "DISABLED|^22$"
}

function Test-PnpHealthy {
    param($Device)

    if ($null -eq $Device -or [string]$Device.Status -ne "OK") {
        return $false
    }

    $problem = [string]$Device.Problem
    return [string]::IsNullOrWhiteSpace($problem) -or
        $problem -eq "0" -or
        $problem -eq "CM_PROB_NONE"
}

function Start-HostBluetoothServices {
    Start-Service -Name "bthserv" -ErrorAction SilentlyContinue
    $userServices = @(Get-Service `
        -Name "BluetoothUserService_*" `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^BluetoothUserService_.+' })
    foreach ($service in $userServices) {
        Start-Service -Name $service.Name -ErrorAction SilentlyContinue
    }
    return @($userServices |
        ForEach-Object {
            Get-Service -Name $_.Name -ErrorAction SilentlyContinue
        })
}

function Get-HostBluetoothPhysicalDevice {
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602\*"
        })
    if ($devices.Count -gt 1) {
        throw "More than one present physical USB 13d3:3602 parent matches the Bluetooth adapter."
    }
    if ($devices.Count -eq 0) {
        return $null
    }
    return $devices[0]
}

function Get-HostBluetoothInterfaces {
    return @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*"
        })
}

function Scan-HostBluetoothHardware {
    $result = Invoke-ProcessTimed `
        -FileName "pnputil.exe" `
        -Arguments @("/scan-devices") `
        -TimeoutSeconds 45 `
        -DisplayCommand "pnputil.exe /scan-devices"
    if (-not $result.Success -and $result.ExitCode -ne 3010) {
        # A system-wide scan can block behind an unrelated PnP operation even
        # after this exact adapter has already returned. In that state the exact
        # parent and MI_00 health are authoritative; the scan is only advisory.
        $parent = Get-HostBluetoothPhysicalDevice
        $interfaces = @(Get-HostBluetoothInterfaces)
        if ((Test-PnpHealthy -Device $parent) -and
            $interfaces.Count -eq 1 -and
            (Test-PnpHealthy -Device $interfaces[0])) {
            Write-Log "The system-wide hardware scan did not complete, but the exact MediaTek parent and MI_00 interface are already healthy; treating the scan as advisory."
            return
        }
        throw "Windows hardware scan failed (pnputil exit $($result.ExitCode))."
    }
}

function Release-OrphanedBluetoothProxyOwnership {
    # `VBoxManage list usbhost` can retain a phantom Held/Captured row after the
    # VM has detached the device. That list is diagnostic only: showvminfo is the
    # authoritative guest-attachment check and Windows PnP is authoritative for
    # host ownership. Never issue another detach based on a usbhost row.
    $usbHostRows = @(Get-HostBluetoothUsbDevices)
    $advisoryRows = @($usbHostRows | Where-Object {
        $_.State -in @("Captured", "Held")
    })
    if ($advisoryRows.Count -gt 0) {
        $summary = ($advisoryRows | ForEach-Object {
            "$($_.Uuid)=$($_.State)"
        }) -join ", "
        Write-Log "VirtualBox reports advisory stale-looking row(s): $summary."
    }

    # The native parent can coexist with a stale VBox row. In that normal case,
    # proceed directly to exact Windows health recovery and leave both the VM and
    # every PnP node untouched.
    if ($null -ne (Get-HostBluetoothPhysicalDevice)) {
        Write-Log "The native MediaTek parent is present; Windows health verification is authoritative."
        return
    }

    # Give the supported detach/filter-release path time to re-enumerate the
    # native device before considering any proxy cleanup.
    Write-Log "The native MediaTek parent is not present yet; waiting for VirtualBox to release its exact proxy."
    $releaseDeadline = (Get-Date).AddSeconds(20)
    $proxyDevice = $null
    do {
        Scan-HostBluetoothHardware
        if ($null -ne (Get-HostBluetoothPhysicalDevice)) {
            Write-Log "The native MediaTek parent returned without proxy removal."
            return
        }

        $proxyDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
            Where-Object {
                $_.InstanceId -eq "USB\VID_80EE&PID_CAFE\000000000"
            })
        if ($proxyDevices.Count -gt 1) {
            throw "More than one exact VirtualBox Bluetooth proxy devnode is present."
        }
        $proxyDevice = if ($proxyDevices.Count -eq 1) {
            $proxyDevices[0]
        } else {
            $null
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $releaseDeadline)

    if ($null -eq $proxyDevice) {
        Write-Log "No present VirtualBox Bluetooth proxy exists. Preserving all device nodes; the bounded host health check will continue."
        return
    }

    # Last-resort cleanup is permitted only for the exact disposable VBox proxy,
    # never the physical 13d3:3602 parent or its subtree. Reconfirm that WorkRDP
    # has no attachment immediately before changing the proxy devnode.
    $stillAttached = @(Get-AttachedBluetoothDevices)
    if ($stillAttached.Count -ne 0) {
        throw "WorkRDP regained the Bluetooth attachment while host recovery was waiting; no proxy device was changed."
    }

    Write-Log "Removing the exact disposable VirtualBox Bluetooth proxy $($proxyDevice.InstanceId) as a last-resort release."
    $proxyRemove = Invoke-ProcessTimed `
        -FileName "pnputil.exe" `
        -Arguments @("/remove-device", $proxyDevice.InstanceId) `
        -TimeoutSeconds 45 `
        -DisplayCommand "pnputil.exe /remove-device <exact VirtualBox Bluetooth proxy>"
    $rebootRequired = $proxyRemove.ExitCode -eq 3010 -or
        $proxyRemove.Output -match '(?i)(?:reboot|restart).*(?:required|needed)|(?:required|needed).*(?:reboot|restart)'
    if ($rebootRequired) {
        throw "Windows requested a reboot while releasing the VirtualBox proxy. The physical MediaTek parent was never removed."
    }
    if (-not $proxyRemove.Success) {
        throw "Windows could not remove the exact VirtualBox Bluetooth proxy (pnputil exit $($proxyRemove.ExitCode))."
    }

    $proxyDeadline = (Get-Date).AddSeconds(20)
    do {
        $proxyStillPresent = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
            Where-Object {
                $_.InstanceId -eq $proxyDevice.InstanceId
            } |
            Select-Object -First 1
        if ($null -eq $proxyStillPresent) {
            Write-Log "The exact VirtualBox Bluetooth proxy devnode is absent."
            break
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $proxyDeadline)
    if ($null -ne $proxyStillPresent) {
        throw "The exact VirtualBox Bluetooth proxy remained present after the bounded release."
    }

    Scan-HostBluetoothHardware
}

function Restore-HostBluetooth {
    Scan-HostBluetoothHardware

    $deadline = (Get-Date).AddSeconds(90)
    $parentEnableAttempted = $false
    $parentRestartAttempted = $false
    $childEnableAttempted = $false
    $childRestartAttempted = $false
    $additionalScanCount = 0
    $lastState = "not present"
    do {
        $parent = Get-HostBluetoothPhysicalDevice
        if ($null -eq $parent -and $additionalScanCount -lt 2) {
            Write-Log "The exact physical parent is not present yet; requesting another bounded hardware scan."
            Scan-HostBluetoothHardware
            $additionalScanCount++
            Start-Sleep -Seconds 3
            continue
        }

        if ($null -ne $parent -and
            (Test-PnpDisabled -Device $parent) -and
            -not $parentEnableAttempted) {
            Write-Log "Re-enabling exact physical USB parent $($parent.InstanceId)."
            $result = Invoke-ProcessTimed `
                -FileName "pnputil.exe" `
                -Arguments @("/enable-device", $parent.InstanceId) `
                -TimeoutSeconds 30 `
                -DisplayCommand "pnputil.exe /enable-device <MediaTek USB parent>"
            if (-not $result.Success -and $result.ExitCode -ne 3010) {
                throw "Windows could not enable the MediaTek USB parent (pnputil exit $($result.ExitCode))."
            }
            $parentEnableAttempted = $true
            Scan-HostBluetoothHardware
            Start-Sleep -Seconds 3
            continue
        }

        if ($null -ne $parent -and
            -not (Test-PnpDisabled -Device $parent) -and
            [string]$parent.Status -ne "OK" -and
            -not $parentRestartAttempted) {
            Write-Log "Restarting the exact physical USB parent once because it is unhealthy."
            $result = Invoke-ProcessTimed `
                -FileName "pnputil.exe" `
                -Arguments @("/restart-device", $parent.InstanceId) `
                -TimeoutSeconds 30 `
                -DisplayCommand "pnputil.exe /restart-device <MediaTek USB parent>"
            if (-not $result.Success -and $result.ExitCode -ne 3010) {
                Write-Log "The physical-parent restart returned exit $($result.ExitCode); continuing the health wait."
            }
            $parentRestartAttempted = $true
            Scan-HostBluetoothHardware
            Start-Sleep -Seconds 3
            continue
        }

        $devices = @(Get-HostBluetoothInterfaces)
        if ($devices.Count -gt 1) {
            throw "More than one present host Bluetooth MI_00 interface matches 13d3:3602."
        }
        if ($devices.Count -eq 1) {
            $device = $devices[0]
            $parentState = if ($null -eq $parent) {
                "missing"
            } else {
                "$($parent.Status)/$($parent.Problem)"
            }
            $lastState = "parent=$parentState, child=$($device.Status)/$($device.Problem), id=$($device.InstanceId)"

            if ((Test-PnpDisabled -Device $device) -and -not $childEnableAttempted) {
                Write-Log "Re-enabling exact host interface $($device.InstanceId)."
                $result = Invoke-ProcessTimed `
                    -FileName "pnputil.exe" `
                    -Arguments @("/enable-device", $device.InstanceId) `
                    -TimeoutSeconds 30 `
                    -DisplayCommand "pnputil.exe /enable-device <MediaTek Bluetooth MI_00>"
                if (-not $result.Success -and $result.ExitCode -ne 3010) {
                    throw "Windows could not enable the MediaTek interface (pnputil exit $($result.ExitCode))."
                }
                if ($result.ExitCode -eq 3010) {
                    Write-Log "Windows accepted the device restore with reboot-required code 3010; verifying live health."
                }
                $childEnableAttempted = $true
                Start-Sleep -Seconds 3
                continue
            }

            if (-not (Test-PnpDisabled -Device $device) -and
                [string]$device.Status -ne "OK" -and
                -not $childRestartAttempted) {
                Write-Log "Restarting the exact enabled interface once because it is unhealthy."
                $result = Invoke-ProcessTimed `
                    -FileName "pnputil.exe" `
                    -Arguments @("/restart-device", $device.InstanceId) `
                    -TimeoutSeconds 30 `
                    -DisplayCommand "pnputil.exe /restart-device <MediaTek Bluetooth MI_00>"
                if (-not $result.Success) {
                    Write-Log "The bounded restart returned exit $($result.ExitCode); continuing the health wait."
                }
                $childRestartAttempted = $true
                Start-Sleep -Seconds 3
                continue
            }

            $userServices = @(Start-HostBluetoothServices)
            $service = Get-Service -Name "bthserv" -ErrorAction SilentlyContinue
            $userServicesHealthy = @($userServices | Where-Object {
                $null -eq $_ -or [string]$_.Status -ne "Running"
            }).Count -eq 0
            if ($null -ne $parent -and
                (Test-PnpHealthy -Device $parent) -and
                (Test-PnpHealthy -Device $device) -and
                $null -ne $service -and
                [string]$service.Status -eq "Running" -and
                $userServicesHealthy) {
                $presentVBoxProxies = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.InstanceId -eq "USB\VID_80EE&PID_CAFE\000000000"
                    })
                if ($presentVBoxProxies.Count -gt 0) {
                    Write-Log "The native Bluetooth adapter is healthy; an inert exact VirtualBox proxy devnode is still visible and is being treated as advisory."
                }

                $attachedNow = @(Get-AttachedBluetoothDevices)
                if ($attachedNow.Count -ne 0) {
                    throw "The native Bluetooth adapter is healthy, but WorkRDP still reports it attached. No physical device was removed."
                }

                $staleProxyRecords = @(Get-HostBluetoothUsbDevices | Where-Object {
                    $_.State -in @("Captured", "Held")
                })
                if ($staleProxyRecords.Count -gt 0) {
                    $proxySummary = ($staleProxyRecords | ForEach-Object {
                        "$($_.Uuid)=$($_.State)"
                    }) -join ", "
                    Write-Log "Windows owns a healthy exact adapter despite stale VirtualBox proxy record(s): $proxySummary."
                }
                $userServiceSummary = if ($userServices.Count -eq 0) {
                    "no per-user Bluetooth service instance"
                } else {
                    ($userServices | ForEach-Object {
                        "$($_.Name)=$($_.Status)"
                    }) -join ", "
                }
                Write-Log "Host Bluetooth is healthy: $lastState, bthserv=Running, $userServiceSummary."
                return
            }
        }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    throw "Host MediaTek Bluetooth did not become healthy within 90 seconds ($lastState)."
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-NoElevate"
    )
    Write-Host "Requesting administrator elevation to return Bluetooth to the laptop..."
    $process = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList $arguments `
        -Verb RunAs `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

if (-not (Test-IsAdmin)) {
    Write-Error "This script must run as administrator."
    exit 1
}

$failed = $false
$failureMessage = ""
try {
    Enter-BluetoothHandoffLock
    # Do not truncate the shared log until this process owns the cross-process
    # handoff mutex. A concurrent click must never disrupt the active operation.
    Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
    Write-Log "Starting reliable Bluetooth return from VM '$VMName' to the laptop."
    $script:VBoxManage = Get-VBoxManagePath

    # Disable the VM filters while the bounded hold still owns any in-flight
    # re-enumeration, then remove that hold before detach. This closes the race
    # where releasing the global hold could feed an active per-VM filter.
    # Every unrelated global and per-VM USB filter is preserved.
    Disable-BluetoothUsbFilters
    Remove-GlobalBluetoothHoldFilter
    $attached = @(Get-AttachedBluetoothDevices)
    if ($attached.Count -gt 1) {
        throw "VM '$VMName' unexpectedly has more than one 13d3:3602 device attached."
    }
    foreach ($device in $attached) {
        Write-Log "Detaching exact MediaTek UUID $($device.Uuid) once."
        $result = Invoke-VBox `
            -Arguments @("controlvm", $VMName, "usbdetach", $device.Uuid) `
            -TimeoutSeconds 30 `
            -AllowFail
        Wait-BluetoothDetached -Uuid $device.Uuid -TimeoutSeconds 30
        if (-not $result.Success) {
            Write-Log "VBoxManage returned exit $($result.ExitCode), but the exact UUID is confirmed detached."
        }
    }
    if ($attached.Count -eq 0) {
        Write-Log "No exact 13d3:3602 device is attached; no detach was issued."
    }

    Release-OrphanedBluetoothProxyOwnership
    Restore-HostBluetooth
    Write-Log "Bluetooth return complete."
}
catch {
    $failed = $true
    $failureMessage = $_.Exception.Message
    Write-Log "ERROR: $failureMessage"
}
finally {
    Exit-BluetoothHandoffLock
}

if ($failed) {
    Write-Error $failureMessage
    exit 1
}
exit 0
