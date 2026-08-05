$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Runtime.WindowsRuntime

$mediaDeviceType = [Windows.Media.Devices.MediaDevice, Windows.Media, ContentType = WindowsRuntime]
$role = [Windows.Media.Devices.AudioDeviceRole, Windows.Media, ContentType = WindowsRuntime]::Default
$deviceId = $mediaDeviceType::GetDefaultAudioCaptureId($role)
if ([string]::IsNullOrWhiteSpace($deviceId)) {
    throw 'Windows does not have a default audio capture device.'
}

$deviceInformationType = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime]
$operation = $deviceInformationType::CreateFromIdAsync($deviceId)
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and
        $_.ReturnType.Name -eq 'Task`1'
    } |
    Select-Object -First 1
if ($null -eq $asTask) {
    throw 'Windows could not create an async device-information task.'
}

$task = $asTask.MakeGenericMethod([Windows.Devices.Enumeration.DeviceInformation]).Invoke($null, @($operation))
$device = $task.GetAwaiter().GetResult()
if ($null -eq $device -or [string]::IsNullOrWhiteSpace($device.Name)) {
    throw 'Windows could not read the default microphone name.'
}

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Write-Output $device.Name
