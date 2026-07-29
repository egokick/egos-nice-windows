#requires -Version 5.1

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Disable", "Enable")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [ValidateScript({
        $_ -match '(?i)\AUSB\\VID_13D3&PID_3602\\[^\\]+\z'
    })]
    [string]$InstanceId,

    [uint32]$Flags = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Action -eq "Disable" -and $Flags -notin @([uint32]0x4, [uint32]0x5)) {
    throw "Disable supports only the temporary Config Manager flags 0x4 and 0x5."
}
if ($Action -eq "Enable" -and $Flags -ne 0) {
    throw "Enable does not accept Config Manager flags."
}

# This worker intentionally contains the blocking native boundary. The parent
# handoff process starts it with a hard timeout and can terminate this process
# if a driver stack never returns from cfgmgr32.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace StayActive.IsolatedNative
{
    public static class ConfigManager
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 CM_Locate_DevNodeW(
            out UInt32 deviceInstance,
            string deviceId,
            UInt32 flags);

        [DllImport("cfgmgr32.dll")]
        public static extern UInt32 CM_Disable_DevNode(
            UInt32 deviceInstance,
            UInt32 flags);

        [DllImport("cfgmgr32.dll")]
        public static extern UInt32 CM_Enable_DevNode(
            UInt32 deviceInstance,
            UInt32 flags);
    }
}
'@

[uint32]$deviceInstance = 0
$locateResult = [StayActive.IsolatedNative.ConfigManager]::CM_Locate_DevNodeW(
    [ref]$deviceInstance,
    $InstanceId,
    0
)
$actionResult = $null
if ($locateResult -eq 0) {
    $actionResult = if ($Action -eq "Disable") {
        [StayActive.IsolatedNative.ConfigManager]::CM_Disable_DevNode(
            $deviceInstance,
            $Flags
        )
    }
    else {
        [StayActive.IsolatedNative.ConfigManager]::CM_Enable_DevNode(
            $deviceInstance,
            0
        )
    }
}

[ordered]@{
    SchemaVersion = 1
    Action = $Action
    LocateConfigRet = [uint32]$locateResult
    ActionConfigRet = $actionResult
} | ConvertTo-Json -Compress
