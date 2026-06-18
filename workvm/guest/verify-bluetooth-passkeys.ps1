#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Bluetooth devices visible to this guest:"
Write-Host ""

$devices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Class -eq "Bluetooth" -or
        $_.FriendlyName -match "Bluetooth|BLE"
    } |
    Select-Object Status, Class, FriendlyName, InstanceId

if ($devices) {
    $devices | Format-Table -AutoSize
}
else {
    Write-Warning "No Bluetooth devices are visible to the guest. The VM probably has not captured the USB Bluetooth dongle."
}

Write-Host ""
Write-Host "Recommended checks:"
Write-Host "  1. If no dongle appears here, unplug/replug the dedicated USB Bluetooth dongle while the VM is running."
Write-Host "  2. In the VirtualBox VM window, check Devices > USB and confirm the dongle is selected."
Write-Host "  3. Open Chrome and test the real password-manager/passkey flow, or use https://webauthn.io for a basic WebAuthn check."
Write-Host "  4. Keep the phone nearby with Bluetooth enabled."
