#requires -Version 5.1

param(
    [string]$VMName = "WorkRDP",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [int]$BitsPerPixel = 32,
    [switch]$NoElevate,
    [switch]$LibraryMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$CachePath = Join-Path $Root ".cache"
$LogPath = Join-Path $CachePath "bluetooth-passthrough-repair.log"
$GuestBluetoothProofPath = Join-Path $CachePath "guest-bluetooth-proof.json"
$StartReadyScriptPath = Join-Path $PSScriptRoot "34-start-workvm-ready.ps1"
$ConfigManagerWorkerPath = Join-Path $PSScriptRoot "38-config-manager-bluetooth-worker.ps1"
$CredentialPath = Join-Path $Root "vm-credentials.txt"
$BluetoothMutexName = "Global\StayActiveWorkVmBluetoothHandoff"
$BluetoothFilterName = "StayActive MediaTek Bluetooth 13d3:3602"
$GlobalHoldFilterName = "StayActive temporary Bluetooth hold 13d3:3602"
$BluetoothVendorId = "13d3"
$BluetoothProductId = "3602"
$BluetoothSerialNumber = "000000000"
$VirtualBoxProxyInstanceId = "USB\VID_80EE&PID_CAFE\000000000"
$VirtualBoxProxyAddressPrefix = "\\?\usb#vid_80ee&pid_cafe#"
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
        Write-Warning "Could not append to '$LogPath'; continuing the Bluetooth handoff."
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
        [string]$DisplayCommand,
        [switch]$SuppressOutputLog
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

    if (-not $SuppressOutputLog -and $output) {
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

function Get-VmState {
    $info = Get-VmInfo -MachineReadable
    if ($info -match '(?m)^VMState="([^"]+)"') {
        return $matches[1]
    }
    throw "Could not read VM state for '$VMName'."
}

function Get-VmUuid {
    $info = Get-VmInfo -MachineReadable
    if ($info -match '(?m)^UUID="([0-9a-fA-F-]+)"') {
        return $matches[1].ToLowerInvariant()
    }
    throw "Could not read VM UUID for '$VMName'."
}

function Get-VmSessionStartUtc {
    $info = Get-VmInfo -MachineReadable
    $match = [regex]::Match(
        $info,
        '(?m)^VMStateChangeTime="(?<value>[^"]+)"\r?$'
    )
    if (-not $match.Success) {
        return $null
    }

    # VBox emits UTC with up to nanosecond precision but without a trailing Z;
    # DateTimeOffset supports seven fractional digits, so trim only the excess.
    $value = [regex]::Replace(
        $match.Groups["value"].Value,
        '(\.\d{7})\d+',
        '$1'
    )
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        "$value`Z",
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$parsed
    )) {
        return $null
    }
    return $parsed.ToUniversalTime()
}

function Write-GuestBluetoothProof {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProofKind
    )

    [ordered]@{
        SchemaVersion = 1
        VmUuid = Get-VmUuid
        VendorId = $BluetoothVendorId
        ProductId = $BluetoothProductId
        ProofKind = $ProofKind
        VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $GuestBluetoothProofPath -Encoding UTF8
}

function Test-GuestBluetoothProof {
    if (-not (Test-Path -LiteralPath $GuestBluetoothProofPath)) {
        return $false
    }

    try {
        $proof = Get-Content -LiteralPath $GuestBluetoothProofPath -Raw |
            ConvertFrom-Json
        $verifiedAt = [DateTimeOffset]::MinValue
        $validTimestamp = [DateTimeOffset]::TryParse(
            [string]$proof.VerifiedAtUtc,
            [ref]$verifiedAt
        )
        $sessionStart = Get-VmSessionStartUtc
        return (
            [int]$proof.SchemaVersion -eq 1 -and
            [string]$proof.VmUuid -eq (Get-VmUuid) -and
            [string]$proof.VendorId -eq $BluetoothVendorId -and
            [string]$proof.ProductId -eq $BluetoothProductId -and
            [string]$proof.ProofKind -in @(
                "ExactGuestHealth",
                "PasskeyQrPrompt"
            ) -and
            $validTimestamp -and
            $null -ne $sessionStart -and
            $verifiedAt -ge $sessionStart.AddMinutes(-1) -and
            $verifiedAt -le [DateTimeOffset]::UtcNow.AddMinutes(5) -and
            ([DateTimeOffset]::UtcNow - $verifiedAt).TotalHours -le 12
        )
    }
    catch {
        Write-Log "Ignoring invalid guest Bluetooth proof: $($_.Exception.Message)"
        return $false
    }
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

function Prepare-BluetoothUsbFilter {
    $filters = @(Get-BluetoothFilterRecords | Where-Object {
        Test-IsBluetoothFilter -Filter $_
    })
    if ($filters.Count -eq 0) {
        Write-Log "Adding an inactive dedicated Bluetooth filter without changing other VM USB filters."
        [void](Invoke-VBox -Arguments @(
            "usbfilter", "add", "0",
            "--target", $VMName,
            "--name", $BluetoothFilterName,
            "--vendorid", $BluetoothVendorId,
            "--productid", $BluetoothProductId,
            "--serialnumber", "",
            "--active", "no"
        ))
    }
    else {
        $primaryFilter = @(
            $filters |
                Sort-Object @{
                    Expression = {
                        if ($_.Name -eq $BluetoothFilterName) { 0 } else { 1 }
                    }
                }, Index
        )[0]
        foreach ($filter in $filters) {
            if ($filter.Index -ne $primaryFilter.Index) {
                Write-Log "Disabling duplicate Bluetooth filter index $($filter.Index); unrelated USB filters are unchanged."
                [void](Invoke-VBox -Arguments @(
                    "usbfilter", "modify", "$($filter.Index)",
                    "--target", $VMName,
                    "--active", "no"
                ))
                continue
            }

            Write-Log "Preparing inactive Bluetooth filter index $($filter.Index); unrelated USB filters are unchanged."
            [void](Invoke-VBox -Arguments @(
                "usbfilter", "modify", "$($filter.Index)",
                "--target", $VMName,
                "--name", $BluetoothFilterName,
                "--vendorid", $BluetoothVendorId,
                "--productid", $BluetoothProductId,
                "--serialnumber", "",
                "--active", "no"
            ))
        }
    }

    if ((Get-VmState) -in @("poweroff", "aborted")) {
        [void](Invoke-VBox -Arguments @(
            "modifyvm", $VMName,
            "--mouse", "ps2",
            "--keyboard", "ps2",
            "--usb-ohci", "off",
            "--usb-xhci", "on"
        ))
    }
}

function Set-BluetoothVmFilterActive {
    param([bool]$Active)

    $filters = @(Get-BluetoothFilterRecords | Where-Object {
        Test-IsBluetoothFilter -Filter $_
    })
    if ($filters.Count -eq 0) {
        throw "The dedicated WorkVM Bluetooth filter is missing."
    }

    $primaryFilter = @(
        $filters |
            Sort-Object @{
                Expression = {
                    if ($_.Name -eq $BluetoothFilterName) { 0 } else { 1 }
                }
            }, Index
    )[0]
    foreach ($filter in $filters) {
        $isPrimary = $filter.Index -eq $primaryFilter.Index
        $activeValue = if ($isPrimary -and $Active) { "yes" } else { "no" }
        Write-Log "Setting Bluetooth filter index $($filter.Index) active=$activeValue."
        [void](Invoke-VBox -Arguments @(
            "usbfilter", "modify", "$($filter.Index)",
            "--target", $VMName,
            "--name", $(if ($isPrimary) {
                $BluetoothFilterName
            } else {
                $filter.Name
            }),
            "--vendorid", $BluetoothVendorId,
            "--productid", $BluetoothProductId,
            "--serialnumber", "",
            "--active", $activeValue
        ))
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
}

function Add-GlobalBluetoothHoldFilter {
    Remove-GlobalBluetoothHoldFilter
    Write-Log "Adding a bounded exact global hold for Bluetooth re-enumeration."
    [void](Invoke-VBox -Arguments @(
        "usbfilter", "add", "0",
        "--target", "global",
        "--name", $GlobalHoldFilterName,
        "--action", "hold",
        "--vendorid", $BluetoothVendorId,
        "--productid", $BluetoothProductId,
        "--active", "yes"
    ))
}

function Start-WorkVm {
    $state = Get-VmState
    Write-Log "VM state before Bluetooth handoff: $state"
    if ($state -ne "running") {
        if (-not (Test-Path -LiteralPath $StartReadyScriptPath)) {
            throw "Start-ready script was not found: $StartReadyScriptPath"
        }
        $result = Invoke-ProcessTimed `
            -FileName "powershell.exe" `
            -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", $StartReadyScriptPath,
                "-VMName", $VMName,
                "-Width", "$Width",
                "-Height", "$Height",
                "-BitsPerPixel", "$BitsPerPixel"
            ) `
            -TimeoutSeconds 300 `
            -DisplayCommand "powershell.exe 34-start-workvm-ready.ps1 -VMName $VMName"
        if (-not $result.Success) {
            throw "The WorkVM start-ready script failed with exit code $($result.ExitCode)."
        }
    }

    $deadline = (Get-Date).AddSeconds(120)
    do {
        $state = Get-VmState
        if ($state -eq "running") {
            [void](Invoke-VBox -Arguments @(
                "controlvm", $VMName, "setscreenlayout", "0", "primary",
                "0", "0", "$Width", "$Height", "$BitsPerPixel"
            ) -TimeoutSeconds 10 -AllowFail)
            [void](Invoke-VBox -Arguments @(
                "controlvm", $VMName, "setvideomodehint",
                "$Width", "$Height", "$BitsPerPixel", "0", "yes", "0", "0"
            ) -TimeoutSeconds 10 -AllowFail)
            return
        }
        if ($state -eq "aborted") {
            throw "VM '$VMName' entered the aborted state while starting."
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw "VM '$VMName' did not reach the running state within 120 seconds."
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
        $productNameMatch = [regex]::Match($block, '(?m)^\s*Product:\s*(.+?)\s*$')
        $serialNumberMatch = [regex]::Match($block, '(?m)^\s*SerialNumber:\s*(.+?)\s*$')
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
            ProductName = $(if ($productNameMatch.Success) {
                $productNameMatch.Groups[1].Value.Trim()
            } else { "" })
            SerialNumber = $(if ($serialNumberMatch.Success) {
                $serialNumberMatch.Groups[1].Value.Trim()
            } else { "" })
        }
    }
    return @($devices)
}

function Get-AttachedBluetoothDevice {
    $info = Get-VmInfo
    $section = [regex]::Match(
        $info,
        'Currently attached USB devices:\s*(?<devices>[\s\S]*?)(?:\r?\nBandwidth groups:|\r?\nShared folders:|\r?\nVRDE:|\z)'
    )
    if (-not $section.Success -or $section.Groups["devices"].Value -match "<none>") {
        return $null
    }
    $devices = @(ConvertFrom-VBoxUsbBlocks -Text $section.Groups["devices"].Value |
        Where-Object {
            $_.VendorId -eq $BluetoothVendorId -and
            $_.ProductId -eq $BluetoothProductId -and
            (
                [string]::IsNullOrWhiteSpace($_.SerialNumber) -or
                $_.SerialNumber -eq $BluetoothSerialNumber
            )
        })
    if ($devices.Count -gt 1) {
        throw "VM '$VMName' unexpectedly has more than one 13d3:3602 device attached."
    }
    if ($devices.Count -eq 1) {
        return $devices[0]
    }
    return $null
}

function Get-DirectAttachBluetoothDevice {
    # The same physical adapter has two legitimate VBox representations:
    # MediaTek/Available with full descriptors, or IMC Networks/Busy with
    # Product and SerialNumber omitted. Direct attach is reliable only for
    # Available. A Busy adapter is handed off by activating the exact VM filter
    # and re-enumerating it; asking usbattach to capture Busy can leave an
    # asynchronous "previous request" behind. Held/Captured aliases are never
    # selected here.
    $output = (Invoke-VBox -Arguments @("list", "usbhost") -TimeoutSeconds 20).Output
    $allBluetoothRows = @(ConvertFrom-VBoxUsbBlocks -Text $output | Where-Object {
        $_.VendorId -eq $BluetoothVendorId -and
        $_.ProductId -eq $BluetoothProductId -and
        -not ([string]$_.Address).StartsWith(
            $VirtualBoxProxyAddressPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )
    })

    # A native-address Captured row means an earlier live-attach request is
    # already waiting for Windows to release the parent. Do not stack another
    # asynchronous request; let the reversible rebind path complete it.
    $captureInProgress = @($allBluetoothRows | Where-Object {
        $_.State -eq "Captured" -and
        -not [string]::IsNullOrWhiteSpace($_.Address)
    })
    if ($captureInProgress.Count -gt 0) {
        Write-Log "VirtualBox already has a native Bluetooth capture request in progress; completing it with the reversible rebind path."
        return $null
    }

    $devices = @($allBluetoothRows | Where-Object {
        $_.State -eq "Available" -and
        -not [string]::IsNullOrWhiteSpace($_.Address) -and
        (
            [string]::IsNullOrWhiteSpace($_.SerialNumber) -or
            $_.SerialNumber -eq $BluetoothSerialNumber
        ) -and
        (
            [string]::IsNullOrWhiteSpace($_.ProductName) -or
            $_.ProductName -eq "Wireless_Device"
        )
    })
    if ($devices.Count -gt 1) {
        throw "VirtualBox found more than one eligible physical USB 13d3:3602 record; refusing an ambiguous handoff."
    }
    if ($devices.Count -eq 0) {
        $busyRows = @($allBluetoothRows | Where-Object {
            $_.State -eq "Busy" -and
            -not [string]::IsNullOrWhiteSpace($_.Address)
        })
        if ($busyRows.Count -gt 0) {
            Write-Log "The exact native Bluetooth row is Busy; using the active VM filter and reversible re-enumeration instead of queuing usbattach."
        }
        return $null
    }

    $parent = Get-HostBluetoothPhysicalDevice
    if (-not (Test-PnpHealthy -Device $parent)) {
        Write-Log "Ignoring the native Bluetooth row because its one exact physical PnP parent is not present and healthy."
        return $null
    }
    return $devices[0]
}

function Wait-BluetoothAttachment {
    param([int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $attached = Get-AttachedBluetoothDevice
        if ($null -ne $attached) {
            return $attached
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return Get-AttachedBluetoothDevice
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

function Test-PnpRebootRequired {
    param($Device)

    if ($null -eq $Device) {
        return $false
    }
    try {
        $property = Get-PnpDeviceProperty `
            -InstanceId ([string]$Device.InstanceId) `
            -KeyName "DEVPKEY_Device_IsRebootRequired" `
            -ErrorAction Stop
        return [bool]$property.Data
    }
    catch {
        Write-Log "Could not read the physical parent's reboot-required property; using the normal reversible restart path."
        return $false
    }
}

function Invoke-BoundedConfigManagerAction {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Disable", "Enable")]
        [string]$Action,

        [Parameter(Mandatory = $true)]
        [string]$InstanceId,

        [uint32]$Flags = 0,
        [int]$TimeoutSeconds = 15
    )

    if (-not (Test-Path -LiteralPath $ConfigManagerWorkerPath)) {
        throw "The bounded Config Manager worker is missing: $ConfigManagerWorkerPath"
    }

    $result = Invoke-ProcessTimed `
        -FileName "powershell.exe" `
        -Arguments @(
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-File", $ConfigManagerWorkerPath,
            "-Action", $Action,
            "-InstanceId", $InstanceId,
            "-Flags", "$Flags"
        ) `
        -TimeoutSeconds $TimeoutSeconds `
        -DisplayCommand "powershell.exe 38-config-manager-bluetooth-worker.ps1 -Action $Action -InstanceId <exact MediaTek USB parent> -Flags $Flags" `
        -SuppressOutputLog
    if ($result.TimedOut) {
        throw "The bounded Config Manager $Action call timed out after ${TimeoutSeconds}s; its worker was terminated so the parent handoff can run rollback."
    }
    if (-not $result.Success) {
        throw "The bounded Config Manager $Action worker failed with exit code $($result.ExitCode)."
    }

    try {
        $response = $result.Output.Trim() | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The bounded Config Manager $Action worker returned an invalid response."
    }
    if ([int]$response.SchemaVersion -ne 1 -or
        [string]$response.Action -ne $Action) {
        throw "The bounded Config Manager $Action worker returned an unexpected response."
    }
    if ([uint32]$response.LocateConfigRet -ne 0) {
        throw "Config Manager could not locate the exact Bluetooth parent (CONFIGRET $($response.LocateConfigRet))."
    }
    if ($null -eq $response.ActionConfigRet) {
        throw "The bounded Config Manager $Action worker returned no action result."
    }
    return [uint32]$response.ActionConfigRet
}

function Invoke-ConfigManagerBluetoothParentCycle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstanceId
    )

    # pnputil refuses every state change while this healthy composite parent
    # carries a stale DEVPKEY_Device_IsRebootRequired flag. Config Manager can
    # perform the same non-persistent live disable/enable without uninstalling
    # the device. Each potentially blocking CM_Disable_DevNode or
    # CM_Enable_DevNode call is isolated in a killable child process.
    $recoveryEnableRequired = $false
    try {
        # CM_DISABLE_UI_NOT_OK (0x4) performs the normal polite live disable
        # without allowing an unexpected driver dialog.
        # Set this before crossing the native boundary because a timed-out call
        # may have changed state even though it never returned a CONFIGRET.
        $recoveryEnableRequired = $true
        $disableResult = Invoke-BoundedConfigManagerAction `
            -Action "Disable" `
            -InstanceId $InstanceId `
            -Flags 0x4
        if ($disableResult -ne 0) {
            Write-Log "The polite Config Manager disable returned CONFIGRET $disableResult; retrying once for this exact stateless Bluetooth parent without a driver veto."
            # CM_DISABLE_ABSOLUTE | CM_DISABLE_UI_NOT_OK (0x1 | 0x4). This is
            # still temporary and non-persistent, and the finally block always
            # attempts a separately bounded recovery enable.
            $disableResult = Invoke-BoundedConfigManagerAction `
                -Action "Disable" `
                -InstanceId $InstanceId `
                -Flags 0x5
        }
        if ($disableResult -ne 0) {
            throw "Config Manager could not temporarily disable the exact Bluetooth parent (CONFIGRET $disableResult)."
        }

        Start-Sleep -Milliseconds 750
        $enableResult = Invoke-BoundedConfigManagerAction `
            -Action "Enable" `
            -InstanceId $InstanceId
        if ($enableResult -eq 0) {
            $recoveryEnableRequired = $false
            return
        }

        # A successful VBoxUSBMon claim can retire the native devnode before
        # CM_Enable_DevNode returns. That is success only when ownership is
        # independently visible as an attachment or exact present proxy.
        $parent = Get-HostBluetoothPhysicalDevice
        $attached = Get-AttachedBluetoothDevice
        $proxy = Wait-VirtualBoxBluetoothProxyReady -TimeoutSeconds 2
        if ($null -eq $parent -and
            ($null -ne $attached -or $null -ne $proxy)) {
            $recoveryEnableRequired = $false
            Write-Log "VirtualBox claimed the parent during the Config Manager cycle (enable returned CONFIGRET $enableResult)."
            return
        }
        throw "Config Manager could not re-enable the exact Bluetooth parent (CONFIGRET $enableResult)."
    }
    finally {
        if ($recoveryEnableRequired) {
            try {
                $recoveryEnable = Invoke-BoundedConfigManagerAction `
                    -Action "Enable" `
                    -InstanceId $InstanceId `
                    -TimeoutSeconds 15
                Write-Log "Config Manager recovery enable returned CONFIGRET $recoveryEnable."
            }
            catch {
                # Preserve the original failure so it reaches the top-level
                # handler, which disables the VM filter and runs host rollback.
                Write-Log "The bounded Config Manager recovery enable did not complete: $($_.Exception.Message)"
            }
        }
    }
}

function Enable-ConfigManagerBluetoothParent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstanceId
    )

    $enableResult = Invoke-BoundedConfigManagerAction `
        -Action "Enable" `
        -InstanceId $InstanceId
    if ($enableResult -ne 0) {
        throw "Config Manager could not enable the exact Bluetooth parent (CONFIGRET $enableResult)."
    }
}

function Get-RunningBluetoothServiceNames {
    $services = @()
    $baseService = Get-Service -Name "bthserv" -ErrorAction SilentlyContinue
    if ($null -ne $baseService -and [string]$baseService.Status -eq "Running") {
        $services += $baseService
    }
    $services += @(Get-Service `
        -Name "BluetoothUserService_*" `
        -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.Status -eq "Running" })
    return @($services |
        Select-Object -ExpandProperty Name -Unique)
}

function Restore-BluetoothServiceNames {
    param([string[]]$ServiceNames)

    $orderedNames = @($ServiceNames | Sort-Object @{
        Expression = { if ($_ -eq "bthserv") { 0 } else { 1 } }
    }, @{ Expression = { $_ } })
    foreach ($serviceName in $orderedNames) {
        Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -eq $service -or [string]$service.Status -ne "Running") {
            Write-Log "Bluetooth service '$serviceName' did not return to its prior Running state."
        }
    }
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

function Get-PresentVirtualBoxBluetoothProxyDevice {
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $_.InstanceId -eq $VirtualBoxProxyInstanceId
        })
    if ($devices.Count -gt 1) {
        throw "More than one exact VirtualBox Bluetooth proxy devnode is present."
    }
    if ($devices.Count -eq 0) {
        return $null
    }
    return $devices[0]
}

function Get-VirtualBoxBluetoothProxyRows {
    # A generic Held row is not sufficient. VBoxManage can retain a stale
    # native-looking Held record after a prior detach even when no proxy devnode
    # exists. Only the VID_80EE/PID_CAFE proxy address is attachable.
    $output = (Invoke-VBox -Arguments @("list", "usbhost") -TimeoutSeconds 20).Output
    $rows = @(ConvertFrom-VBoxUsbBlocks -Text $output | Where-Object {
        $_.VendorId -eq $BluetoothVendorId -and
        $_.ProductId -eq $BluetoothProductId -and
        (
            [string]::IsNullOrWhiteSpace($_.SerialNumber) -or
            $_.SerialNumber -eq $BluetoothSerialNumber
        ) -and
        $_.State -eq "Held" -and
        ([string]$_.Address).StartsWith(
            $VirtualBoxProxyAddressPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )
    })
    if ($rows.Count -gt 1) {
        throw "VirtualBox exposed more than one attachable proxy row for Bluetooth."
    }
    return $rows
}

function Wait-VirtualBoxBluetoothProxyReady {
    param([int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $proxyDevice = Get-PresentVirtualBoxBluetoothProxyDevice
        $proxyRows = @(Get-VirtualBoxBluetoothProxyRows)
        if ($null -ne $proxyDevice -and $proxyRows.Count -eq 1) {
            return $proxyRows[0]
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Invoke-ReversibleBluetoothPnpAction {
    param(
        [ValidateSet("restart-device", "disable-device", "enable-device")]
        [string]$Action,
        [string]$InstanceId,
        [string]$DisplayTarget,
        [switch]$AllowFailure
    )

    $result = Invoke-ProcessTimed `
        -FileName "pnputil.exe" `
        -Arguments @("/$Action", $InstanceId) `
        -TimeoutSeconds 30 `
        -DisplayCommand "pnputil.exe /$Action <$DisplayTarget>"
    $rebootRequired = $result.ExitCode -eq 3010 -or
        $result.Output -match '(?i)(?:reboot|restart).*(?:required|needed)|(?:required|needed).*(?:reboot|restart)'
    if ($rebootRequired) {
        throw "Windows requested a reboot while running pnputil /$Action for $DisplayTarget. No device node was deleted."
    }
    if (-not $result.Success) {
        if ($AllowFailure) {
            Write-Log "The reversible pnputil /$Action attempt returned exit $($result.ExitCode); rechecking the capture goal before using the next reversible fallback."
            return
        }
        throw "Windows could not run pnputil /$Action for $DisplayTarget (exit $($result.ExitCode))."
    }
}

function Rebind-HostBluetoothForVirtualBoxCapture {
    # The exact per-VM filter is active before this function is called. Pulse
    # only the existing parent devnode so the running VM claims its next start.
    # Never uninstall the physical parent or its MI_00 child.
    $attached = Wait-BluetoothAttachment -TimeoutSeconds 1
    if ($null -ne $attached) {
        return $null
    }

    $readyProxy = Wait-VirtualBoxBluetoothProxyReady -TimeoutSeconds 1
    if ($null -ne $readyProxy) {
        Write-Log "VirtualBox already has a present attachable Bluetooth proxy $($readyProxy.Uuid); no host PnP action is needed."
        return $readyProxy
    }

    $parent = Get-HostBluetoothPhysicalDevice
    if ($null -eq $parent) {
        throw "The exact physical 13d3:3602 parent is absent and no present VirtualBox proxy is ready. Windows is already missing the adapter devnode; no device was changed."
    }

    $parentInstanceId = [string]$parent.InstanceId
    $runningBluetoothServices = @(Get-RunningBluetoothServiceNames)

    try {
        foreach ($serviceName in @($runningBluetoothServices | Sort-Object @{
            Expression = { if ($_ -eq "bthserv") { 1 } else { 0 } }
        }, @{ Expression = { $_ } })) {
            Write-Log "Temporarily stopping Bluetooth service '$serviceName' before the exact USB parent is rebound."
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        }

        if (Test-PnpDisabled -Device $parent) {
            Write-Log "The exact physical parent is disabled; enabling it under the active WorkVM USB filter."
            Enable-ConfigManagerBluetoothParent -InstanceId $parentInstanceId
        }
        else {
            if (Test-PnpRebootRequired -Device $parent) {
                Write-Log "The healthy physical parent carries a stale reboot-required flag; Config Manager can safely re-enumerate it without honoring that stale installer flag."
            }
            Write-Log "Cycling the exact parent under the active WorkVM USB filter with a reversible, non-persistent Config Manager disable/enable."
            Invoke-ConfigManagerBluetoothParentCycle -InstanceId $parentInstanceId
        }

        Scan-HostBluetoothHardware
        $attached = Wait-BluetoothAttachment -TimeoutSeconds 20
        if ($null -ne $attached) {
            Write-Log "The running VM automatically claimed the exact adapter during the reversible parent re-enumeration."
            return $null
        }

        $readyProxy = Wait-VirtualBoxBluetoothProxyReady -TimeoutSeconds 5
        if ($null -ne $readyProxy) {
            Write-Log "VirtualBox held the exact adapter as a present attachable proxy during re-enumeration."
            return $readyProxy
        }
        throw "The exact VM filter and reversible parent re-enumeration completed, but WorkRDP did not attach Bluetooth."
    }
    finally {
        Restore-BluetoothServiceNames -ServiceNames $runningBluetoothServices
    }
}

function Scan-HostBluetoothHardware {
    $result = Invoke-ProcessTimed `
        -FileName "pnputil.exe" `
        -Arguments @("/scan-devices") `
        -TimeoutSeconds 45 `
        -DisplayCommand "pnputil.exe /scan-devices"
    if (-not $result.Success -and $result.ExitCode -ne 3010) {
        throw "Windows hardware scan failed (pnputil exit $($result.ExitCode))."
    }
}

function Release-VirtualBoxBluetoothProxyDevice {
    # Removing the global hold normally lets VBoxUSBMon return the native parent
    # without any PnP deletion. Prefer that supported path and treat usbhost rows
    # as advisory; the physical 13d3:3602 parent is authoritative for the host.
    if ($null -ne (Get-HostBluetoothPhysicalDevice)) {
        return
    }

    $deadline = (Get-Date).AddSeconds(20)
    $proxyDevice = $null
    do {
        Scan-HostBluetoothHardware
        if ($null -ne (Get-HostBluetoothPhysicalDevice)) {
            Write-Log "The native MediaTek parent returned without proxy cleanup."
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
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $proxyDevice) {
        Write-Log "No exact VirtualBox proxy is present; preserving all PnP nodes while rollback continues."
        return
    }

    # Reconfirm that the VM did not attach during the wait. The only removable
    # node is the exact disposable VID_80EE/PID_CAFE proxy, never the MediaTek
    # parent or MI_00 child, and never its subtree.
    $attached = Get-AttachedBluetoothDevice
    if ($null -ne $attached) {
        throw "WorkRDP attached exact Bluetooth UUID $($attached.Uuid) during rollback; no proxy device was changed."
    }

    Write-Log "Removing exact disposable VirtualBox proxy $($proxyDevice.InstanceId) during rollback."
    $proxyRemove = Invoke-ProcessTimed `
        -FileName "pnputil.exe" `
        -Arguments @("/remove-device", $proxyDevice.InstanceId) `
        -TimeoutSeconds 45 `
        -DisplayCommand "pnputil.exe /remove-device <exact VirtualBox Bluetooth proxy>"
    $rebootRequired = $proxyRemove.ExitCode -eq 3010 -or
        $proxyRemove.Output -match '(?i)(?:reboot|restart).*(?:required|needed)|(?:required|needed).*(?:reboot|restart)'
    if ($rebootRequired) {
        throw "Windows requested a reboot while releasing the disposable VirtualBox proxy. The physical MediaTek nodes were never deleted."
    }
    if (-not $proxyRemove.Success) {
        throw "Windows could not remove the exact VirtualBox Bluetooth proxy during rollback (pnputil exit $($proxyRemove.ExitCode))."
    }

    $deadline = (Get-Date).AddSeconds(20)
    do {
        $current = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
            Where-Object { $_.InstanceId -eq $proxyDevice.InstanceId } |
            Select-Object -First 1
        if ($null -eq $current) {
            Write-Log "The exact VirtualBox Bluetooth proxy devnode is absent."
            return
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw "The exact VirtualBox Bluetooth proxy remained present after bounded rollback cleanup."
}

function Restore-HostBluetoothAfterFailedHandoff {
    Write-Log "The handoff failed before attachment; restoring the exact adapter to the laptop."

    Remove-GlobalBluetoothHoldFilter
    $filters = @(Get-BluetoothFilterRecords | Where-Object {
        Test-IsBluetoothFilter -Filter $_
    })
    foreach ($filter in $filters) {
        Write-Log "Disabling Bluetooth filter index $($filter.Index) for host rollback."
        [void](Invoke-VBox -Arguments @(
            "usbfilter", "modify", "$($filter.Index)",
            "--target", $VMName,
            "--active", "no"
        ))
    }

    Release-VirtualBoxBluetoothProxyDevice
    Scan-HostBluetoothHardware

    $deadline = (Get-Date).AddSeconds(90)
    $parentEnableAttempted = $false
    $parentRestartAttempted = $false
    $childEnableAttempted = $false
    $restartAttempted = $false
    $lastState = "not present"
    do {
        $parent = Get-HostBluetoothPhysicalDevice
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
                throw "Windows could not re-enable the MediaTek USB parent during rollback (pnputil exit $($result.ExitCode))."
            }
            $parentEnableAttempted = $true
            Scan-HostBluetoothHardware
            Start-Sleep -Seconds 3
            continue
        }

        if ($null -ne $parent -and
            -not (Test-PnpDisabled -Device $parent) -and
            -not (Test-PnpHealthy -Device $parent) -and
            -not $parentRestartAttempted) {
            Write-Log "Restarting the exact physical USB parent once during rollback."
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

        $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
            Where-Object {
                $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*"
            })
        if ($devices.Count -gt 1) {
            throw "More than one host Bluetooth MI_00 interface matches 13d3:3602 during rollback."
        }
        if ($devices.Count -eq 1) {
            $device = $devices[0]
            $parentState = if ($null -eq $parent) {
                "missing"
            } else {
                "$($parent.Status)/$($parent.Problem)"
            }
            $lastState = "parent=$parentState, child=$($device.Status)/$($device.Problem)"
            if ((Test-PnpDisabled -Device $device) -and -not $childEnableAttempted) {
                Write-Log "Re-enabling exact host interface $($device.InstanceId)."
                $result = Invoke-ProcessTimed `
                    -FileName "pnputil.exe" `
                    -Arguments @("/enable-device", $device.InstanceId) `
                    -TimeoutSeconds 30 `
                    -DisplayCommand "pnputil.exe /enable-device <MediaTek Bluetooth MI_00>"
                if (-not $result.Success -and $result.ExitCode -ne 3010) {
                    throw "Windows could not re-enable MediaTek Bluetooth during rollback (pnputil exit $($result.ExitCode))."
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
                -not $restartAttempted) {
                Write-Log "Restarting the exact host interface once during rollback."
                [void](Invoke-ProcessTimed `
                    -FileName "pnputil.exe" `
                    -Arguments @("/restart-device", $device.InstanceId) `
                    -TimeoutSeconds 30 `
                    -DisplayCommand "pnputil.exe /restart-device <MediaTek Bluetooth MI_00>")
                $restartAttempted = $true
                Start-Sleep -Seconds 3
                continue
            }
            Start-Service -Name "bthserv" -ErrorAction SilentlyContinue
            $service = Get-Service -Name "bthserv" -ErrorAction SilentlyContinue
            if ((Test-PnpHealthy -Device $parent) -and
                (Test-PnpHealthy -Device $device) -and
                $null -ne $service -and
                [string]$service.Status -eq "Running") {
                Write-Log "Laptop Bluetooth rollback succeeded: $lastState, bthserv=Running."
                return
            }
        }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    throw "Laptop Bluetooth rollback did not become healthy within 90 seconds ($lastState)."
}

function Try-AttachBluetoothDevice {
    param(
        [Parameter(Mandatory = $true)]
        $Device,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Write-Log "Trying documented live USB attach for $Description UUID $($Device.Uuid)."
    $attach = Invoke-VBox `
        -Arguments @("controlvm", $VMName, "usbattach", $Device.Uuid) `
        -TimeoutSeconds 20 `
        -AllowFail

    # showvminfo is authoritative. VirtualBox can finish an attach after the
    # command reports a transient error, so verify ownership before deciding.
    $attached = Wait-BluetoothAttachment -TimeoutSeconds 20
    if ($null -ne $attached) {
        Write-Log "Exact MediaTek Bluetooth UUID $($attached.Uuid) is attached to '$VMName'."
        return $attached
    }

    Write-Log "$Description UUID $($Device.Uuid) did not attach (VBoxManage exit $($attach.ExitCode))."
    return $null
}

function Attach-BluetoothToVm {
    # An interrupted prior handoff can leave the temporary global hold behind
    # even though the device is already attached. Always clear it while the
    # cross-process handoff lock is held before taking the idempotent fast path.
    Remove-GlobalBluetoothHoldFilter

    $attached = Get-AttachedBluetoothDevice
    if ($null -ne $attached) {
        Write-Log "Preserving exact Bluetooth UUID already attached to '$VMName': $($attached.Uuid)"
        return $attached
    }

    # Prefer VirtualBox's supported live-attach path. This often captures the
    # adapter without touching PnP at all. A present proxy is preferred; a
    # unique real Available-or-Busy native row is next. Stale Held/Captured
    # aliases are deliberately ignored.
    $readyProxy = Wait-VirtualBoxBluetoothProxyReady -TimeoutSeconds 1
    if ($null -ne $readyProxy) {
        $attached = Try-AttachBluetoothDevice `
            -Device $readyProxy `
            -Description "present VirtualBox Bluetooth proxy"
        if ($null -ne $attached) {
            return $attached
        }
        throw "A present exact VirtualBox Bluetooth proxy could not be attached to '$VMName'."
    }

    $directDevice = Get-DirectAttachBluetoothDevice
    if ($null -ne $directDevice) {
        $attached = Try-AttachBluetoothDevice `
            -Device $directDevice `
            -Description "$($directDevice.State.ToLowerInvariant()) physical Bluetooth record"
        if ($null -ne $attached) {
            return $attached
        }
    }

    # For a Windows-owned Busy adapter, use VirtualBox's normal hot-plug
    # mechanism: make the exact per-VM filter active, then re-enumerate the
    # unchanged parent. This avoids queuing usbattach against a Busy device.
    Set-BluetoothVmFilterActive -Active $true
    try {
        $readyProxy = Rebind-HostBluetoothForVirtualBoxCapture

        # A live usbattach request may complete automatically when the native
        # parent is cycled. Verify VM ownership before requiring a Held proxy.
        $attached = Get-AttachedBluetoothDevice
        if ($null -ne $attached) {
            return $attached
        }

        if ($null -eq $readyProxy) {
            throw "The reversible host rebind returned without a present attachable VirtualBox proxy."
        }

        $attached = Try-AttachBluetoothDevice `
            -Device $readyProxy `
            -Description "newly captured VirtualBox Bluetooth proxy"
        if ($null -ne $attached) {
            return $attached
        }

        throw "VirtualBox created the exact Bluetooth proxy but did not attach it to '$VMName'."
    }
    catch {
        if ($null -eq (Get-AttachedBluetoothDevice)) {
            Set-BluetoothVmFilterActive -Active $false
        }
        throw
    }
}

function Get-GuestCredentials {
    if (-not (Test-Path -LiteralPath $CredentialPath)) {
        throw "Guest credentials were not found: $CredentialPath"
    }
    $content = Get-Content -LiteralPath $CredentialPath
    $username = [regex]::Match(($content -join "`n"), '(?m)^Guest username:\s*(.+)$')
    $password = [regex]::Match(($content -join "`n"), '(?m)^Guest password:\s*(.+)$')
    if (-not $username.Success -or -not $password.Success) {
        throw "Guest credentials do not contain both a username and password."
    }
    return [pscustomobject]@{
        Username = $username.Groups[1].Value.Trim()
        Password = $password.Groups[1].Value.Trim()
    }
}

function Invoke-GuestPowerShell {
    param(
        [string]$Command,
        [int]$TimeoutSeconds,
        [string]$Purpose
    )

    $credentials = Get-GuestCredentials
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($Command)
    )
    $arguments = @(
        "guestcontrol", $VMName, "run",
        "--username", $credentials.Username,
        "--password", $credentials.Password,
        "--timeout=$($TimeoutSeconds * 1000)",
        "--wait-stdout",
        "--wait-stderr",
        "--exe", "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
        "--arg0=powershell.exe",
        "--",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy", "Bypass",
        "-EncodedCommand", $encodedCommand
    )
    $result = Invoke-ProcessTimed `
        -FileName $script:VBoxManage `
        -Arguments $arguments `
        -TimeoutSeconds ($TimeoutSeconds + 10) `
        -DisplayCommand "VBoxManage guestcontrol $VMName run <$Purpose>" `
        -SuppressOutputLog
    $result.Output = $result.Output.Replace($credentials.Password, "<redacted>")
    if ($result.Output -and -not $result.Success) {
        $result.Output.TrimEnd() -split "\r?\n" |
            Select-Object -First 12 |
            ForEach-Object { Write-Log "  guestcontrol: $_" }
    }
    return $result
}

function ConvertFrom-GuestToken {
    param(
        [string]$Output,
        [string]$Marker
    )

    $match = [regex]::Match(
        $Output,
        "(?m)^$([regex]::Escape($Marker))=(?<payload>[A-Za-z0-9+/=]+)\s*$"
    )
    if (-not $match.Success) {
        return $null
    }
    try {
        $json = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($match.Groups["payload"].Value)
        )
        return $json | ConvertFrom-Json
    }
    catch {
        throw "The guest returned a malformed $Marker token."
    }
}

function Get-GuestBluetoothHealth {
    $command = @'
$ErrorActionPreference = "SilentlyContinue"
$minimumVersion = [version]"1.1045.0.566"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
$devices = @(Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*"
})
$device = $devices | Select-Object -First 1
$provider = ""
$versionText = ""
if ($null -ne $device) {
    $providerProperty = Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName "DEVPKEY_Device_DriverProvider"
    $versionProperty = Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName "DEVPKEY_Device_DriverVersion"
    if ($null -ne $providerProperty) { $provider = [string]$providerProperty.Data }
    if ($null -ne $versionProperty) { $versionText = [string]$versionProperty.Data }
}
$version = [version]"0.0"
$versionValid = [version]::TryParse($versionText, [ref]$version)
$service = Get-Service -Name "bthserv"
$serviceStatus = if ($null -eq $service) { "Missing" } else { [string]$service.Status }
$result = [ordered]@{
    DeviceCount = $devices.Count
    InstanceId = if ($null -eq $device) { "" } else { [string]$device.InstanceId }
    DeviceStatus = if ($null -eq $device) { "Missing" } else { [string]$device.Status }
    DriverProvider = $provider
    DriverVersion = $versionText
    BluetoothService = $serviceStatus
    Elevated = $elevated
    Healthy = (
        $devices.Count -eq 1 -and
        [string]$device.Status -eq "OK" -and
        $provider -match "(?i)MediaTek" -and
        $versionValid -and
        $version -ge $minimumVersion -and
        $serviceStatus -eq "Running"
    )
}
$json = $result | ConvertTo-Json -Compress
$token = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
Write-Output "STAYACTIVE_BT_HEALTH_V1=$token"
if ($result.Healthy) { exit 0 }
exit 21
'@

    $invocation = Invoke-GuestPowerShell `
        -Command $command `
        -TimeoutSeconds 35 `
        -Purpose "encoded exact Bluetooth health check"
    $details = ConvertFrom-GuestToken `
        -Output $invocation.Output `
        -Marker "STAYACTIVE_BT_HEALTH_V1"
    if ($null -ne $details) {
        return [pscustomobject]@{ State = "Ready"; Details = $details }
    }

    $credentialRejectedPattern = @(
        "specified user was not able to logon on guest",
        "restricted.+can.?t be used to logon",
        "VERR_AUTHENTICATION_FAILURE"
    ) -join "|"
    if ($invocation.Output -match $credentialRejectedPattern) {
        return [pscustomobject]@{
            State = "GuestCredentialsRejected"
            Details = $null
        }
    }

    $notReadyPattern = @(
        "VERR_GSTCTL_GUEST_ERROR",
        "guest execution service.*not ready",
        "Guest Additions.*not.*ready",
        "VERR_INVALID_STATE",
        "VERR_NOT_AVAILABLE",
        "timed out"
    ) -join "|"
    return [pscustomobject]@{
        State = $(if ($invocation.TimedOut -or
            $invocation.Output -match $notReadyPattern) {
            "GuestNotReady"
        } else { "GuestControlError" })
        Details = $null
    }
}

function Repair-GuestBluetoothDriver {
    $command = @'
$ErrorActionPreference = "Stop"
function Write-RepairToken {
    param($Value)
    $json = $Value | ConvertTo-Json -Compress
    $token = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    Write-Output "STAYACTIVE_BT_REPAIR_V1=$token"
}
$result = [ordered]@{
    Elevated = $false
    Signature = ""
    PnpUtilExitCode = -1
    Healthy = $false
    Error = ""
}
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $result.Elevated = $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
    if (-not $result.Elevated) {
        throw "The VirtualBox guestcontrol token is not elevated."
    }

    $driverRoot = $null
    $infPath = $null
    $catalogPath = $null
    foreach ($candidate in @(
        "W:\drivers\mediatek-bluetooth\mtkbtfilter.inf_amd64_7968e94d48e994b9",
        "\\VBOXSVR\workvm\drivers\mediatek-bluetooth\mtkbtfilter.inf_amd64_7968e94d48e994b9"
    )) {
        $candidateInf = Join-Path $candidate "mtkbtfilter.inf"
        $candidateCatalog = Join-Path $candidate "mtkbtfilterx.cat"
        if ((Test-Path -LiteralPath $candidateInf) -and
            (Test-Path -LiteralPath $candidateCatalog)) {
            $driverRoot = $candidate
            $infPath = $candidateInf
            $catalogPath = $candidateCatalog
            break
        }
    }
    if ($null -eq $driverRoot) {
        throw "The bundled MediaTek driver is unavailable through W: and \\VBOXSVR\workvm."
    }
    $signature = Get-AuthenticodeSignature -FilePath $catalogPath
    $result.Signature = [string]$signature.Status
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "The bundled MediaTek driver catalog does not have a valid signature."
    }

    $pnpOutput = (& pnputil.exe /add-driver $infPath /install 2>&1 | Out-String)
    $result.PnpUtilExitCode = $LASTEXITCODE
    if ($result.PnpUtilExitCode -notin @(0, 3010)) {
        throw "pnputil failed with exit $($result.PnpUtilExitCode): $($pnpOutput.Trim())"
    }
    & pnputil.exe /scan-devices 2>&1 | Out-Null
    $device = Get-PnpDevice -PresentOnly | Where-Object {
        $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*"
    } | Select-Object -First 1
    if ($null -ne $device) {
        & pnputil.exe /restart-device $device.InstanceId 2>&1 | Out-Null
    }
    Start-Service -Name "bthserv" -ErrorAction SilentlyContinue

    $minimumVersion = [version]"1.1045.0.566"
    $deadline = (Get-Date).AddSeconds(35)
    do {
        $devices = @(Get-PnpDevice -PresentOnly | Where-Object {
            $_.InstanceId -like "USB\VID_13D3&PID_3602&MI_00*"
        })
        $device = $devices | Select-Object -First 1
        $provider = ""
        $versionText = ""
        if ($null -ne $device) {
            $providerProperty = Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName "DEVPKEY_Device_DriverProvider"
            $versionProperty = Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName "DEVPKEY_Device_DriverVersion"
            if ($null -ne $providerProperty) { $provider = [string]$providerProperty.Data }
            if ($null -ne $versionProperty) { $versionText = [string]$versionProperty.Data }
        }
        $version = [version]"0.0"
        $versionValid = [version]::TryParse($versionText, [ref]$version)
        $service = Get-Service -Name "bthserv"
        $result.Healthy = (
            $devices.Count -eq 1 -and
            [string]$device.Status -eq "OK" -and
            $provider -match "(?i)MediaTek" -and
            $versionValid -and
            $version -ge $minimumVersion -and
            $null -ne $service -and
            [string]$service.Status -eq "Running"
        )
        if ($result.Healthy) { break }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    if (-not $result.Healthy) {
        throw "The signed driver was installed, but exact Bluetooth health stayed bad."
    }
}
catch {
    $result.Error = $_.Exception.Message
}
Write-RepairToken -Value $result
if ($result.Healthy) { exit 0 }
exit 33
'@

    $invocation = Invoke-GuestPowerShell `
        -Command $command `
        -TimeoutSeconds 90 `
        -Purpose "signed bundled MediaTek driver repair"
    $details = ConvertFrom-GuestToken `
        -Output $invocation.Output `
        -Marker "STAYACTIVE_BT_REPAIR_V1"
    if ($null -eq $details) {
        throw "Guest driver repair returned no valid result token (guestcontrol exit $($invocation.ExitCode))."
    }
    if (-not $details.Elevated) {
        throw "The exact device is attached, but GuestControl has a filtered token, so automatic driver repair is not permitted. In the VM, run the bundled MediaTek driver install once from an elevated PowerShell, then click Open VM again; the device was left attached."
    }
    if (-not $details.Healthy) {
        throw "The signed MediaTek guest driver repair failed: $($details.Error)"
    }
    Write-Log "Signed bundled MediaTek driver repair succeeded."
}

function Format-GuestHealth {
    param($Details)

    return "devices=$($Details.DeviceCount), status=$($Details.DeviceStatus), provider='$($Details.DriverProvider)', version='$($Details.DriverVersion)', bthserv=$($Details.BluetoothService), elevated=$($Details.Elevated)"
}

function Confirm-AttachedBluetoothStable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedUuid,
        [int]$Seconds = 12
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        if ((Get-VmState) -ne "running") {
            throw "WorkVM stopped while Bluetooth was settling."
        }

        $current = Get-AttachedBluetoothDevice
        if ($null -eq $current -or $current.Uuid -ne $ExpectedUuid) {
            throw "The exact MediaTek Bluetooth device did not remain attached while the guest was settling."
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
}

function Ensure-GuestBluetoothHealthy {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AttachedUuid
    )

    $deadline = (Get-Date).AddSeconds(45)
    $lastState = "GuestNotReady"
    do {
        $health = Get-GuestBluetoothHealth
        $lastState = $health.State
        if ($health.State -eq "Ready") {
            Write-Log "Guest Bluetooth health: $(Format-GuestHealth -Details $health.Details)"
            if ($health.Details.Healthy) {
                Write-GuestBluetoothProof -ProofKind "ExactGuestHealth"
                return $true
            }
            if (-not $health.Details.Elevated) {
                throw "The exact device is attached but unhealthy, and GuestControl has a filtered token, so automatic driver repair is not permitted. In the VM, run the bundled MediaTek driver install once from an elevated PowerShell, then click Open VM again; the device was left attached."
            }
            Write-Log "Guest health is exact but bad; attempting one signed bundled-driver repair."
            Repair-GuestBluetoothDriver

            $repairDeadline = (Get-Date).AddSeconds(45)
            do {
                Start-Sleep -Seconds 3
                $afterRepair = Get-GuestBluetoothHealth
                if ($afterRepair.State -eq "Ready") {
                    Write-Log "Health after repair: $(Format-GuestHealth -Details $afterRepair.Details)"
                    if ($afterRepair.Details.Healthy) {
                        Write-GuestBluetoothProof -ProofKind "ExactGuestHealth"
                        return $true
                    }
                }
            } while ((Get-Date) -lt $repairDeadline)
            throw "The signed driver repair ran, but exact guest Bluetooth health did not become good within 45 seconds."
        }

        if ($health.State -eq "GuestCredentialsRejected") {
            Write-Log "GuestControl credentials were rejected; verifying exact USB ownership without treating the optional guest diagnostic as the handoff result."
            Confirm-AttachedBluetoothStable -ExpectedUuid $AttachedUuid
            if (Test-GuestBluetoothProof) {
                Write-Log "Exact MediaTek Bluetooth attachment remained stable and this VM has a matching proof from the current VM session. GuestControl remains unavailable until vm-credentials.txt matches the guest account."
            }
            else {
                Write-Log "Exact MediaTek Bluetooth attachment remained stable. Guest driver/service health was not rechecked because vm-credentials.txt was rejected; USB ownership is nevertheless confirmed."
            }
            return $false
        }

        Write-Log "Waiting for guest validation; guestcontrol state is '$($health.State)'."
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "The exact Bluetooth device remains attached, but guest validation was not ready within 45 seconds (last state: $lastState). Log in to '$VMName', then click Open VM again; no detach was performed."
}

if ($LibraryMode) {
    return
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-Width", "$Width",
        "-Height", "$Height",
        "-BitsPerPixel", "$BitsPerPixel",
        "-NoElevate"
    )
    Write-Host "Requesting administrator elevation for the WorkVM Bluetooth handoff..."
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
    Write-Log "Starting reliable Bluetooth handoff to VM '$VMName'."
    $script:VBoxManage = Get-VBoxManagePath
    Prepare-BluetoothUsbFilter
    Start-WorkVm
    $attached = Attach-BluetoothToVm
    Write-Log "Validating exact attached Bluetooth UUID $($attached.Uuid) inside the guest."
    $guestVerified = Ensure-GuestBluetoothHealthy -AttachedUuid $attached.Uuid
    if ($guestVerified) {
        Write-Log "Bluetooth handoff complete: exact MediaTek device, MediaTek driver, Status OK, and bthserv are confirmed."
    }
    else {
        Write-Log "Bluetooth handoff complete: exact MediaTek device is attached and stable. Saved GuestControl credentials were rejected, so guest health was not rechecked."
    }
}
catch {
    $failed = $true
    $failureMessage = $_.Exception.Message
    if ($null -ne $script:VBoxManage) {
        try {
            $attachedAfterFailure = Get-AttachedBluetoothDevice
            if ($null -eq $attachedAfterFailure) {
                Restore-HostBluetoothAfterFailedHandoff
            }
            else {
                Write-Log "Failure occurred after exact UUID $($attachedAfterFailure.Uuid) attached; leaving it attached for diagnosis."
            }
        }
        catch {
            $failureMessage += " Host rollback also failed: $($_.Exception.Message)"
        }
    }
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
