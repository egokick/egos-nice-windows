[CmdletBinding()]
param(
    [string]$ControllerIPv4 = '213.188.217.227'
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this Taildesk route-task installer as administrator.'
}

$sourceScript = Join-Path $PSScriptRoot 'Set-TaildeskFlyBypassRoute.ps1'
if (-not (Test-Path -LiteralPath $sourceScript)) {
    throw "The route helper was not found at $sourceScript"
}

$installDirectory = Join-Path $env:ProgramData 'Taildesk'
$installedScript = Join-Path $installDirectory 'Set-TaildeskFlyBypassRoute.ps1'
New-Item -Path $installDirectory -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath $sourceScript -Destination $installedScript -Force

$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$installedScript`" -ControllerIPv4 $ControllerIPv4"
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$startup = New-ScheduledTaskTrigger -AtStartup
$logon = New-ScheduledTaskTrigger -AtLogOn
$repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)

Register-ScheduledTask -TaskName 'Taildesk Fly Route' -Action $action -Trigger @($startup, $logon, $repeat) `
    -Principal $principal -Settings $settings -Description 'Keeps the Taildesk Fly control endpoint reachable outside other full-tunnel VPNs after network changes.' -Force | Out-Null
Start-ScheduledTask -TaskName 'Taildesk Fly Route'

$registeredTask = Get-ScheduledTask -TaskName 'Taildesk Fly Route'
$taskInfo = Get-ScheduledTaskInfo -TaskName 'Taildesk Fly Route'
[pscustomobject]@{
    TaskName = $registeredTask.TaskName
    State = $registeredTask.State.ToString()
    Principal = $registeredTask.Principal.UserId
    RunLevel = $registeredTask.Principal.RunLevel.ToString()
    TriggerCount = @($registeredTask.Triggers).Count
    LastTaskResult = $taskInfo.LastTaskResult
} | ConvertTo-Json -Compress

