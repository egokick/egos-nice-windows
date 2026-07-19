[CmdletBinding()]
param(
    [switch]$ExitCapable,

    [string]$EnvironmentFile = 'C:\source\babelfish\.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FlyAdmin.Common.ps1')

$ticket = $null
$joinCommand = $null
try {
    $ticket = New-StayActiveEnrollmentKey $ExitCapable $EnvironmentFile
    # This is the sole intentional disclosure. The key is one-use, expires in
    # 15 minutes, and is not copied or persisted by this script.
    $joinCommand = "tailscale up --login-server https://headscale.stayactive.test --auth-key $($ticket.Key)"
    Write-Output $joinCommand
    Write-Host "Enrollment ticket ID: $($ticket.Id). It expires at $($ticket.ExpiresAtUtc.ToString('u')). Revoke before use with Revoke-FlyEnrollment.ps1 -EnrollmentId $($ticket.Id)."
    Write-Host 'If you copy the command, this window will clear the matching clipboard value after 60 seconds.'
    Start-Sleep -Seconds 60
    try {
        $clipboard = Get-Clipboard -Raw -ErrorAction Stop
        if (-not [string]::IsNullOrEmpty($clipboard) -and
            ($clipboard.Contains($joinCommand) -or $clipboard.Contains($ticket.Key))) {
            Set-Clipboard -Value ''
            Write-Host 'The enrollment command was cleared from the clipboard.'
        }
    }
    catch {
        Write-Warning 'Clipboard cleanup could not be verified. Clear the clipboard manually after transfer.'
    }
}
finally {
    $joinCommand = $null
    if ($null -ne $ticket) { $ticket.Key = $null }
}
