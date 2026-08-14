[CmdletBinding()]
param(
    [string]$LogPath = '',
    [switch]$Elevated
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$MaximumTreeEntries = 100000

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path (Split-Path -Parent $PSCommandPath) 'Reset-Opticon-ForReinstall.log'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    if ($Elevated) {
        throw 'The elevated cleanup process did not receive an administrator token.'
    }

    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-LogPath', ('"{0}"' -f $LogPath),
        '-Elevated'
    )
    $child = Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden `
        -ArgumentList $arguments -Wait -PassThru
    exit $child.ExitCode
}

$logDirectory = Split-Path -Parent $LogPath
if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
    [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
}

function Write-Log([string]$Message) {
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Get-FixedChild([string]$Parent, [string]$ChildName) {
    $canonicalParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [IO.Directory]::Exists($canonicalParent)) {
        throw "Required Windows parent does not exist: $canonicalParent"
    }
    $parentItem = Get-Item -LiteralPath $canonicalParent -Force
    if (($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Required Windows parent is a link or junction: $canonicalParent"
    }
    if ([string]::IsNullOrWhiteSpace($ChildName) -or [IO.Path]::GetFileName($ChildName) -ne $ChildName) {
        throw 'The fixed cleanup child name is invalid.'
    }
    $result = [IO.Path]::GetFullPath((Join-Path $canonicalParent $ChildName))
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($result), $canonicalParent,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The fixed cleanup root escaped its expected Windows parent.'
    }
    return $result
}

function Invoke-Native(
    [string]$FilePath,
    [string[]]$Arguments,
    [int[]]$AllowedExitCodes = @(0),
    [switch]$Quiet
) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = [int]$LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if (-not $Quiet) {
        foreach ($line in $output) {
            if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                Write-Log ([string]$line)
            }
        }
    }
    if ($AllowedExitCodes -notcontains $exitCode) {
        if ($Quiet) {
            foreach ($line in ($output | Select-Object -Last 20)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                    Write-Log ([string]$line)
                }
            }
        }
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $exitCode."
    }
    return $exitCode
}

function Test-TaskPresent([string]$TaskName) {
    return (Invoke-Native $script:SchtasksPath @('/Query', '/TN', $TaskName) @(0, 1) -Quiet) -eq 0
}

function Stop-OpticonProcesses([string]$InstallRoot, [string]$MachineDataRoot) {
    $installPrefix = $InstallRoot.TrimEnd('\') + '\'
    foreach ($process in Get-CimInstance -ClassName Win32_Process) {
        $image = [string]$process.ExecutablePath
        $commandLine = [string]$process.CommandLine
        $insideInstall = -not [string]::IsNullOrWhiteSpace($image) -and
            $image.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)
        $opticonSshd = $process.Name -ieq 'sshd.exe' -and
            -not [string]::IsNullOrWhiteSpace($commandLine) -and
            $commandLine.IndexOf($MachineDataRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($insideInstall -or $opticonSshd) {
            Write-Log "Stopping Opticon process $($process.ProcessId): $([IO.Path]::GetFileName($image))"
            Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
        }
    }
}

function Assert-RegularBoundedTree([string]$Root) {
    if ([IO.File]::Exists($Root)) {
        throw "Cleanup root is a file rather than a directory: $Root"
    }
    if (-not [IO.Directory]::Exists($Root)) { return }

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Cleanup root is a link or junction: $Root"
    }

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)
    $count = 0
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $current -Force) {
            $count++
            if ($count -gt $script:MaximumTreeEntries) {
                throw "Cleanup refused an unexpectedly large tree beneath: $Root"
            }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Cleanup refuses a link or junction: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
    Write-Log "Validated $count regular entries beneath fixed root: $Root"
}

function Repair-AndRemoveFixedTree([string]$Root, [string[]]$AllowedRoots) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($AllowedRoots -notcontains $fullRoot) {
        throw "Refusing an unapproved cleanup root: $fullRoot"
    }
    if (-not [IO.Directory]::Exists($fullRoot) -and -not [IO.File]::Exists($fullRoot)) {
        Write-Log "Fixed root is already absent: $fullRoot"
        return
    }
    if ([IO.File]::Exists($fullRoot)) {
        throw "Fixed Opticon root is a file rather than a directory: $fullRoot"
    }

    $rootItem = Get-Item -LiteralPath $fullRoot -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Fixed Opticon root is a link or junction: $fullRoot"
    }

    Write-Log "Taking Administrators ownership without following symbolic links: $fullRoot"
    [void](Invoke-Native $script:TakeownPath @('/F', $fullRoot, '/A', '/R', '/D', 'Y', '/SKIPSL') @(0) -Quiet)
    Write-Log "Granting SYSTEM and Administrators explicit per-object full control: $fullRoot"
    [void](Invoke-Native $script:IcaclsPath @(
        $fullRoot, '/grant:r', '*S-1-5-18:F', '*S-1-5-32-544:F', '/T', '/C', '/L', '/Q'
    ) @(0) -Quiet)

    Assert-RegularBoundedTree $fullRoot
    $entries = New-Object 'System.Collections.Generic.List[System.IO.FileSystemInfo]'
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($fullRoot)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $current -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Cleanup refuses a link or junction: $($entry.FullName)"
            }
            $entries.Add($entry)
            if ($entries.Count -gt $script:MaximumTreeEntries) {
                throw "Cleanup refused an unexpectedly large tree beneath: $fullRoot"
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }

    Write-Log "Deleting $($entries.Count) entries beneath fixed root: $fullRoot"
    foreach ($entry in ($entries | Sort-Object { $_.FullName.Length } -Descending)) {
        $current = Get-Item -LiteralPath $entry.FullName -Force -ErrorAction SilentlyContinue
        if ($null -eq $current) { continue }
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cleanup refuses a changed link or junction: $($current.FullName)"
        }
        if ($current.PSIsContainer) {
            [IO.Directory]::Delete($current.FullName, $false)
        }
        else {
            [IO.File]::SetAttributes($current.FullName, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($current.FullName)
        }
    }
    [IO.Directory]::Delete($fullRoot, $false)
    if ([IO.Directory]::Exists($fullRoot) -or [IO.File]::Exists($fullRoot)) {
        throw "Fixed Opticon root remains after removal: $fullRoot"
    }
}

try {
    Set-Content -LiteralPath $LogPath -Value '' -Encoding UTF8
    Write-Log 'Starting elevated Opticon reset for a clean reinstall.'

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $roamingAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    $installRoot = Get-FixedChild $programFiles 'Taildesk'
    $machineDataRoot = Get-FixedChild $programData 'Taildesk'
    $fixedRoots = @(
        $installRoot,
        $machineDataRoot,
        (Get-FixedChild $programData 'OpticonProvenance'),
        (Get-FixedChild $programData 'OpticonBootstrap'),
        (Get-FixedChild $programData 'OpticonBootstrapUnvalidated'),
        (Get-FixedChild $localAppData 'Taildesk'),
        (Get-FixedChild $roamingAppData 'Taildesk'),
        (Get-FixedChild (Get-FixedChild $localAppData 'Programs') 'Opticon')
    ) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }

    $systemDirectory = [Environment]::SystemDirectory
    $script:SchtasksPath = Join-Path $systemDirectory 'schtasks.exe'
    $script:TakeownPath = Join-Path $systemDirectory 'takeown.exe'
    $script:IcaclsPath = Join-Path $systemDirectory 'icacls.exe'
    $scPath = Join-Path $systemDirectory 'sc.exe'
    $netPath = Join-Path $systemDirectory 'net.exe'
    $netshPath = Join-Path $systemDirectory 'netsh.exe'
    foreach ($utilityPath in @($script:SchtasksPath, $script:TakeownPath, $script:IcaclsPath,
            $scPath, $netPath, $netshPath)) {
        if (-not [IO.File]::Exists($utilityPath)) {
            throw "Required Windows utility is absent: $utilityPath"
        }
    }

    $fixedTasks = @(
        'Taildesk Update Guardian Watchdog',
        'Taildesk Update Guardian',
        'Taildesk Opticon SSH Supervisor',
        'Taildesk SSH Supervisor',
        'Taildesk Agent',
        'Taildesk Fly Route',
        'Opticon Command Center',
        'Taildesk Setup Resume'
    )
    $fixedFirewallRules = @(
        'Opticon Agent (Tailscale only)',
        'Taildesk Agent (Tailscale only)',
        'Opticon RustDesk (Tailscale only)',
        'RustDesk Direct (Tailscale only)',
        'RustDesk External IPv4 Block',
        'RustDesk External IPv6 Block'
    )

    Write-Log 'Stopping the fixed Opticon Agent service if present.'
    [void](Invoke-Native $scPath @('stop', 'OpticonAgent') @(0, 1060, 1062) -Quiet)
    Write-Log 'Stopping fixed Opticon scheduled tasks if present.'
    foreach ($taskName in $fixedTasks) {
        if (Test-TaskPresent $taskName) {
            [void](Invoke-Native $script:SchtasksPath @('/End', '/TN', $taskName) @(0, 1) -Quiet)
            Write-Log "Stopped or found inactive scheduled task: $taskName"
        }
    }
    Stop-OpticonProcesses $installRoot $machineDataRoot

    Write-Log 'Deleting the fixed Opticon Agent service registration.'
    [void](Invoke-Native $scPath @('delete', 'OpticonAgent') @(0, 1060, 1072) -Quiet)
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ((Invoke-Native $scPath @('query', 'OpticonAgent') @(0, 1060) -Quiet) -eq 1060) { break }
        Start-Sleep -Milliseconds 500
        if ($attempt -eq 29) { throw 'The OpticonAgent service was not deleted within 15 seconds.' }
    }

    foreach ($root in $fixedRoots) {
        Repair-AndRemoveFixedTree $root $fixedRoots
    }

    Write-Log 'Deleting fixed Opticon scheduled task definitions.'
    foreach ($taskName in $fixedTasks) {
        if (Test-TaskPresent $taskName) {
            [void](Invoke-Native $script:SchtasksPath @('/Delete', '/TN', $taskName, '/F'))
        }
    }
    Write-Log 'Deleting firewall rules created by Opticon.'
    foreach ($ruleName in $fixedFirewallRules) {
        [void](Invoke-Native $netshPath @('advfirewall', 'firewall', 'delete', 'rule', "name=$ruleName") @(0, 1) -Quiet)
    }
    Write-Log 'Deleting the temporary Opticon remote-administration account if present.'
    [void](Invoke-Native $netPath @('user', 'OpticonRemoteAdmin', '/delete') @(0, 2) -Quiet)

    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Opticon'
    if (Test-Path -LiteralPath $uninstallKey) {
        Write-Log 'Deleting the fixed Opticon uninstall registration.'
        Remove-Item -LiteralPath $uninstallKey -Recurse -Force
    }

    foreach ($root in $fixedRoots) {
        if ([IO.Directory]::Exists($root) -or [IO.File]::Exists($root)) {
            throw "Verification failed; fixed root remains: $root"
        }
    }
    foreach ($taskName in $fixedTasks) {
        if (Test-TaskPresent $taskName) {
            throw "Verification failed; fixed scheduled task remains: $taskName"
        }
    }
    if ((Invoke-Native $scPath @('query', 'OpticonAgent') @(0, 1060) -Quiet) -ne 1060) {
        throw 'Verification failed; the OpticonAgent service remains.'
    }

    Write-Log 'SUCCESS: Opticon roots, service, tasks, firewall rules, account, and registration are absent.'
    Write-Log 'Tailscale and RustDesk installations and identities were left unchanged.'
    exit 0
}
catch {
    Write-Log ('ERROR: ' + $_.Exception.Message)
    Write-Log $_.ScriptStackTrace
    exit 1
}
