#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\Taildesk\Admin"
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'App'
if (-not (Test-Path (Join-Path $source 'Opticon.exe'))) {
    throw 'The App folder is missing. Extract the complete Opticon command-center ZIP first.'
}

function Assert-ValidPublisher {
    param([string]$Path, [string[]]$PublisherTerms)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "Invalid Authenticode signature on $([IO.Path]::GetFileName($Path)): $($signature.Status)"
    }
    $subject = $signature.SignerCertificate.Subject
    if (-not ($PublisherTerms | Where-Object { $subject -match [regex]::Escape($_) })) {
        throw "Unexpected publisher on $([IO.Path]::GetFileName($Path)): $subject"
    }
}

function Get-PinnedArtifact {
    param([Parameter(Mandatory)][ValidateSet('Tailscale','RustDesk')][string]$Name)
    $arm64 = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64'
    if ($Name -eq 'Tailscale') {
        if ($arm64) { return [PSCustomObject]@{ Name='Tailscale'; Version='1.102.1'; FileName='tailscale-setup-1.102.1-arm64.msi'; Size=36000256L; Sha256='f81002c5b971fe2de197703606e81107eacc83c6ea40478976fe5de154aed177'; Vendor='https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-arm64.msi'; Publishers=@('Tailscale') } }
        return [PSCustomObject]@{ Name='Tailscale'; Version='1.102.1'; FileName='tailscale-setup-1.102.1-amd64.msi'; Size=38354432L; Sha256='988a38ab854ad176778955b0c92b27b1af14bf5e0146ea43076d829496d7ac77'; Vendor='https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-amd64.msi'; Publishers=@('Tailscale') }
    }
    if ($arm64) { return [PSCustomObject]@{ Name='RustDesk'; Version='1.4.9'; FileName='rustdesk-1.4.9-aarch64.msi'; Size=22855680L; Sha256='30bc8925e62c7ade52371758c2b944036ed2386f6c554e9e59f3bcfef06c7cd9'; Vendor='https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-aarch64.msi'; Publishers=@('RustDesk','PURSLANE') } }
    return [PSCustomObject]@{ Name='RustDesk'; Version='1.4.9'; FileName='rustdesk-1.4.9-x86_64.msi'; Size=24825856L; Sha256='c87d2f4cef2a5acd6003b6507dcfbf5d5168a256db082cd90b54d35193224aaa'; Vendor='https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-x86_64.msi'; Publishers=@('RustDesk','PURSLANE') }
}

function Get-VerifiedArtifact {
    param([Parameter(Mandatory)][object]$Artifact)
    $destination = Join-Path $env:TEMP ("opticon-" + $Artifact.FileName)
    $primary = "https://taildesk-egokick-control.fly.dev/opticon/artifacts/v1/$($Artifact.FileName)"
    $errors = @()
    foreach ($uri in @($primary, $Artifact.Vendor)) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        try {
            Write-Host "Downloading pinned $($Artifact.Name) $($Artifact.Version) from $(([uri]$uri).Host)..."
            Invoke-WebRequest $uri -OutFile $destination -UseBasicParsing
            $actualSize = (Get-Item -LiteralPath $destination).Length
            if ($actualSize -ne $Artifact.Size) { throw "size $actualSize does not match $($Artifact.Size)" }
            $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $Artifact.Sha256) { throw "SHA-256 $actualHash does not match the pinned hash" }
            Assert-ValidPublisher $destination $Artifact.Publishers
            return $destination
        } catch { $errors += "$uri : $($_.Exception.Message)" }
    }
    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
    throw "Both verified download sources failed for $($Artifact.Name): $($errors -join '; ')"
}
function Install-Tailscale {
    $artifact = Get-PinnedArtifact Tailscale
    $cli = "$env:ProgramFiles\Tailscale\tailscale.exe"
    if (Test-Path $cli) {
        $installed = ((& $cli version 2>$null | Select-Object -First 1) -as [string]).Trim()
        if ($installed -eq $artifact.Version) { return $cli }
    }
    $installer = Get-VerifiedArtifact $artifact
    try {
        $process = Start-Process msiexec.exe -ArgumentList @('/i', $installer, '/qn', '/norestart') -Wait -PassThru
        if ($process.ExitCode -notin @(0, 3010)) { throw "Tailscale installer returned $($process.ExitCode)." }
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $cli)) { throw 'Tailscale installed, but tailscale.exe was not found.' }
    $installed = ((& $cli version 2>$null | Select-Object -First 1) -as [string]).Trim()
    if ($installed -ne $artifact.Version) { throw "Tailscale version $installed was installed instead of pinned version $($artifact.Version)." }
    return $cli
}

function Install-RustDesk {
    $artifact = Get-PinnedArtifact RustDesk
    $client = "$env:ProgramFiles\RustDesk\rustdesk.exe"
    if (Test-Path $client) {
        $installed = (Get-Item -LiteralPath $client).VersionInfo.ProductVersion
        if ($installed -like "$($artifact.Version)*") { return $client }
    }
    $installer = Get-VerifiedArtifact $artifact
    try {
        $process = Start-Process msiexec.exe -ArgumentList @('/i', $installer, '/qn', '/norestart') -Wait -PassThru
        if ($process.ExitCode -notin @(0, 3010)) { throw "RustDesk installer returned $($process.ExitCode)." }
    } finally { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path $client)) { throw 'RustDesk installed, but rustdesk.exe was not found.' }
    $installed = (Get-Item -LiteralPath $client).VersionInfo.ProductVersion
    if ($installed -notlike "$($artifact.Version)*") { throw "RustDesk version $installed was installed instead of pinned version $($artifact.Version)." }
    return $client
}

function Configure-PrivateRustDeskController {
    param([Parameter(Mandatory)][string]$Client, [Parameter(Mandatory)][string]$ProfilePath)
    Write-Host 'Restricting the remote-session engine to Opticon and the private Tailscale mesh...'
    $options = @(@('direct-server','N'),@('custom-rendezvous-server','127.0.0.1'),@('relay-server','127.0.0.1'),@('enable-lan-discovery','N'),@('hide-tray','Y'),@('hide-stop-service','Y'),@('disable-discovery-panel','Y'),@('allow-auto-update','N'),@('enable-udp-punch','N'),@('enable-ipv6-punch','N'))
    foreach ($option in $options) {
        $process = Start-Process $Client -ArgumentList @('--option',$option[0],$option[1]) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            throw "RustDesk timed out applying private option $($option[0])."
        }
        if ($process.ExitCode -ne 0) { throw "RustDesk rejected private option $($option[0])." }
    }
    Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue
    Get-Service -Name 'RustDesk' -ErrorAction SilentlyContinue | Set-Service -StartupType Disabled
    Get-Process -Name 'RustDesk' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $configRoots = @((Join-Path $ProfilePath 'AppData\Roaming\RustDesk\config'),(Join-Path $env:APPDATA 'RustDesk\config'),(Join-Path $env:WINDIR 'System32\config\systemprofile\AppData\Roaming\RustDesk\config')) | Select-Object -Unique
    foreach ($configRoot in $configRoots) {
        if (-not (Test-Path -LiteralPath $configRoot)) { continue }
        foreach ($configFile in Get-ChildItem -LiteralPath $configRoot -File -Filter '*.toml' -ErrorAction SilentlyContinue) {
            $content = Get-Content -LiteralPath $configFile.FullName -Raw
            $content = [regex]::Replace($content,'(?m)^\s*rendezvous-server\s*=.*(?:\r?\n)?','')
            if ($content -match '(?m)^\s*rendezvous_server\s*=') { $content = [regex]::Replace($content,'(?m)^\s*rendezvous_server\s*=.*$',"rendezvous_server = '127.0.0.1:21116'") } else { $content = "rendezvous_server = '127.0.0.1:21116'`r`n" + $content }
            [IO.File]::WriteAllText($configFile.FullName,$content,[Text.UTF8Encoding]::new($false))
        }
    }
    $commonStartup=[Environment]::GetFolderPath('CommonStartup');$commonDesktop=[Environment]::GetFolderPath('CommonDesktopDirectory');$commonPrograms=[Environment]::GetFolderPath('CommonPrograms')
    foreach($shortcut in @((Join-Path $commonStartup 'RustDesk Tray.lnk'),(Join-Path $commonDesktop 'RustDesk.lnk'))){Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue}
    $rustDeskPrograms=Join-Path $commonPrograms 'RustDesk';if(Test-Path -LiteralPath $rustDeskPrograms){Remove-Item -LiteralPath $rustDeskPrograms -Recurse -Force}
    & netsh.exe advfirewall firewall delete rule 'name=all' 'dir=in' "program=$Client" | Out-Null
    foreach($rule in @('RustDesk External IPv4 Block','RustDesk External IPv6 Block')){& netsh.exe advfirewall firewall delete rule "name=$rule" | Out-Null}
    & netsh.exe advfirewall firewall add rule 'name=RustDesk External IPv4 Block' 'dir=out' 'action=block' 'remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255' "program=$Client" 'profile=any' 'enable=yes' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not restrict RustDesk to Tailscale IPv4 destinations.'}
    & netsh.exe advfirewall firewall add rule 'name=RustDesk External IPv6 Block' 'dir=out' 'action=block' 'remoteip=::/1,8000::/1' "program=$Client" 'profile=any' 'enable=yes' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Windows could not block external RustDesk IPv6 destinations.'}
}

function New-Shortcut {
    param([string]$Target, [string]$Path)
    $directory = Split-Path $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target
    $shortcut.Description = 'Opticon command center'
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Save()
}

function Expand-InteractivePath {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$ProfilePath,
        [Parameter(Mandatory)][string]$FallbackRelativePath,
        [Parameter(Mandatory)][hashtable]$Variables
    )

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return Join-Path $ProfilePath $FallbackRelativePath
    }

    $expanded = [string]$Value
    for ($pass = 0; $pass -lt 4; $pass++) {
        $before = $expanded
        foreach ($entry in $Variables.GetEnumerator()) {
            $pattern = [regex]::Escape("%$($entry.Key)%")
            $replacement = [string]$entry.Value
            $expanded = [regex]::Replace(
                $expanded,
                $pattern,
                [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement },
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
        $expanded = [Environment]::ExpandEnvironmentVariables($expanded)
        if ($expanded -eq $before) { break }
    }

    if ($expanded.Contains('%')) {
        return Join-Path $ProfilePath $FallbackRelativePath
    }
    return $expanded
}

function Resolve-InteractiveUserProfile {
    # With over-the-shoulder UAC, WindowsIdentity and the process environment
    # describe the administrator whose credentials were entered, not the user
    # who launched Setup. Resolve the Explorer owner in this session instead.
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $accountName = $null
    $sid = $null
    try {
        $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe' AND SessionId=$sessionId" |
            Select-Object -First 1
        if ($null -ne $explorer) {
            $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner
            $ownerSid = Invoke-CimMethod -InputObject $explorer -MethodName GetOwnerSid
            if ($owner.ReturnValue -eq 0 -and -not [string]::IsNullOrWhiteSpace($owner.User)) {
                $accountName = if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
                    $owner.User
                } else {
                    "$($owner.Domain)\$($owner.User)"
                }
            }
            if ($ownerSid.ReturnValue -eq 0) { $sid = $ownerSid.Sid }
        }
    } catch {
        # The Win32_ComputerSystem fallback below covers systems where CIM is
        # unavailable, while still preferring the same signed-in user.
    }

    if ([string]::IsNullOrWhiteSpace($accountName)) {
        try { $accountName = (Get-CimInstance Win32_ComputerSystem).UserName } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($accountName)) {
        throw 'No signed-in interactive Windows user was found. Run this installer from the desktop session that will use Opticon.'
    }
    if ([string]::IsNullOrWhiteSpace($sid)) {
        $account = New-Object -TypeName System.Security.Principal.NTAccount -ArgumentList $accountName
        $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
    }

    $profileKeyPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
    $profileValue = (Get-ItemProperty -LiteralPath $profileKeyPath -ErrorAction Stop).ProfileImagePath
    if ([string]::IsNullOrWhiteSpace($profileValue)) {
        throw "The Windows profile for $accountName could not be found."
    }
    $profilePath = [Environment]::ExpandEnvironmentVariables([string]$profileValue)

    $profileRoot = [IO.Path]::GetPathRoot($profilePath).TrimEnd('\')
    $variables = @{
        USERPROFILE = $profilePath
        HOMEDRIVE = $profileRoot
        HOMEPATH = $profilePath.Substring($profileRoot.Length)
    }
    $accountParts = $accountName -split '\\', 2
    if ($accountParts.Length -eq 2) {
        $variables['USERDOMAIN'] = $accountParts[0]
        $variables['USERNAME'] = $accountParts[1]
    } else {
        $variables['USERNAME'] = $accountName
    }

    $environmentKey = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\Environment")
    $shellKey = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders")
    try {
        if ($null -ne $environmentKey) {
            foreach ($name in $environmentKey.GetValueNames()) {
                $value = $environmentKey.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($null -ne $value) { $variables[$name] = [string]$value }
            }
        }

        $appDataValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('AppData', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }
        $localAppDataValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('Local AppData', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }
        $variables['APPDATA'] = Expand-InteractivePath $appDataValue $profilePath 'AppData\Roaming' $variables
        $variables['LOCALAPPDATA'] = Expand-InteractivePath $localAppDataValue $profilePath 'AppData\Local' $variables

        $desktopValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('Desktop', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }
        $startupValue = if ($null -ne $shellKey) {
            $shellKey.GetValue('Startup', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else { $null }

        return [PSCustomObject]@{
            AccountName = $accountName
            ProfilePath = $profilePath
            Sid = $sid
            Desktop = Expand-InteractivePath $desktopValue $profilePath 'Desktop' $variables
            Startup = Expand-InteractivePath $startupValue $profilePath 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup' $variables
            Programs = Join-Path $variables['APPDATA'] 'Microsoft\Windows\Start Menu\Programs'
        }
    } finally {
        if ($null -ne $environmentKey) { $environmentKey.Dispose() }
        if ($null -ne $shellKey) { $shellKey.Dispose() }
    }
}

Write-Host 'Installing Opticon command center...' -ForegroundColor Cyan
$interactiveProfile = Resolve-InteractiveUserProfile
Write-Host "Installing for signed-in user $($interactiveProfile.AccountName)."
$tailscale = Install-Tailscale
$rustDesk = Install-RustDesk
Configure-PrivateRustDeskController $rustDesk $interactiveProfile.ProfilePath

$statusText = (& $tailscale status --json 2>$null) -join "`n"
$running = $false
if ($statusText) {
    try {
        $statusObject = $statusText | ConvertFrom-Json
        $running = $statusObject.BackendState -eq 'Running' -and @($statusObject.Self.TailscaleIPs | Where-Object { $_ -match '^100\.' }).Count -gt 0
    } catch { $running = $false }
}
if (-not $running) {
    Write-Host 'A browser window will open so you can sign this laptop into Tailscale.' -ForegroundColor Yellow
    & $tailscale login
}
if (Test-Path $InstallDirectory) {
    Get-Process 'Taildesk.Admin','Opticon' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $InstallDirectory 'Taildesk.Admin.exe') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $InstallDirectory 'Payload\Admin\Taildesk.Admin.exe') -Force -ErrorAction SilentlyContinue
}
New-Item $InstallDirectory -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $source '*') $InstallDirectory -Recurse -Force

$admin = Join-Path $InstallDirectory 'Opticon.exe'
$desktopLink = Join-Path $interactiveProfile.Desktop 'Opticon.lnk'
$startupLink = Join-Path $interactiveProfile.Startup 'Opticon.lnk'
$startMenuLink = Join-Path $interactiveProfile.Programs 'Opticon.lnk'
$legacyLinks = @((Join-Path $interactiveProfile.Desktop 'Taildesk.lnk'), (Join-Path $interactiveProfile.Startup 'Taildesk.lnk'), (Join-Path $interactiveProfile.Programs 'Taildesk.lnk'))
foreach ($legacyLink in $legacyLinks) { Remove-Item -LiteralPath $legacyLink -Force -ErrorAction SilentlyContinue }
New-Shortcut $admin $desktopLink
New-Shortcut $admin $startupLink
New-Shortcut $admin $startMenuLink
$ipValue = & $tailscale ip -4 | Select-Object -First 1
if (-not $ipValue) { throw 'Tailscale did not assign an IPv4 address after login.' }
$ip = $ipValue.Trim()
if ($ip -match '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.') {
    $deleteRuleArguments = @('advfirewall', 'firewall', 'delete', 'rule', 'name=Opticon Coordinator (Tailscale only)')
    & netsh.exe @deleteRuleArguments | Out-Null
    $deleteLegacyRuleArguments = @('advfirewall', 'firewall', 'delete', 'rule', 'name=Taildesk Coordinator (Tailscale only)')
    & netsh.exe @deleteLegacyRuleArguments | Out-Null
    $addRuleArguments = @(
        'advfirewall', 'firewall', 'add', 'rule',
        'name=Opticon Coordinator (Tailscale only)', 'dir=in', 'action=allow',
        'protocol=TCP', 'localport=45830', "localip=$ip", 'remoteip=100.64.0.0/10',
        'profile=any', 'enable=yes'
    )
    & netsh.exe @addRuleArguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows could not create the Tailscale-only coordinator firewall rule.' }
} else {
    throw "Tailscale returned an address outside 100.64.0.0/10: $ip"
}

$routeTaskInstaller = Join-Path $PSScriptRoot 'Tools\Install-TaildeskFlyRouteTask.ps1'
if (-not (Test-Path -LiteralPath $routeTaskInstaller)) {
    throw 'The Opticon roaming-route task installer is missing from the extracted package.'
}
& $routeTaskInstaller -ControllerIPv4 '213.188.217.227' | Out-Null

Write-Host "Installed for $($interactiveProfile.AccountName)." -ForegroundColor Green
Write-Host 'Close this elevated installer, then open Opticon from that user''s desktop shortcut.' -ForegroundColor Green
Write-Host 'The command center starts at sign-in and remains available while that user stays signed in; locking the screen is fine.' -ForegroundColor Yellow
