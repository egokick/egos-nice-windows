$ErrorActionPreference = 'Stop'

$sourceZipUrl = 'http://10.0.2.2:8765/stayactive-source.zip'
$installRoot = Join-Path $env:USERPROFILE 'Apps\StayActive'
$sourceRoot = Join-Path $installRoot 'source'
$publishRoot = Join-Path $installRoot 'publish'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'StayActive.lnk'
$edgeBookmarkUrl = 'https://windows365.microsoft.com/ent#/devices'
$edgeBookmarkName = 'Windows 365 Devices'
$dotnetRoot = Join-Path $env:USERPROFILE '.dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "== $message =="
}

function Ensure-DotNet {
    $candidate = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($candidate) {
        $script:dotnetExe = $candidate.Source
        & $script:dotnetExe --version
        return
    }

    Write-Step "Installing per-user .NET SDK"
    New-Item -ItemType Directory -Path $dotnetRoot -Force | Out-Null
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Channel '10.0' -InstallDir $dotnetRoot
    if (-not (Test-Path $dotnetExe)) {
        throw "dotnet.exe was not installed to $dotnetExe"
    }

    $env:PATH = "$dotnetRoot;$env:PATH"
    & $dotnetExe --version
}

function Install-EdgeBookmark {
    Write-Step "Adding Edge bookmark"
    Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $edgeDefault = Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data\Default'
    New-Item -ItemType Directory -Path $edgeDefault -Force | Out-Null
    $bookmarksPath = Join-Path $edgeDefault 'Bookmarks'

    if (Test-Path $bookmarksPath) {
        $bookmarks = Get-Content -LiteralPath $bookmarksPath -Raw | ConvertFrom-Json
    }
    else {
        $bookmarks = [pscustomobject]@{
            checksum = ''
            roots = [pscustomobject]@{
                bookmark_bar = [pscustomobject]@{
                    children = @()
                    date_added = '0'
                    date_last_used = '0'
                    date_modified = '0'
                    guid = [guid]::NewGuid().ToString()
                    id = '1'
                    name = 'Bookmarks bar'
                    type = 'folder'
                }
                other = [pscustomobject]@{
                    children = @()
                    date_added = '0'
                    date_last_used = '0'
                    date_modified = '0'
                    guid = [guid]::NewGuid().ToString()
                    id = '2'
                    name = 'Other favorites'
                    type = 'folder'
                }
                synced = [pscustomobject]@{
                    children = @()
                    date_added = '0'
                    date_last_used = '0'
                    date_modified = '0'
                    guid = [guid]::NewGuid().ToString()
                    id = '3'
                    name = 'Mobile favorites'
                    type = 'folder'
                }
            }
            version = 1
        }
    }

    $bar = $bookmarks.roots.bookmark_bar
    $children = @($bar.children)
    if (-not ($children | Where-Object { $_.url -eq $edgeBookmarkUrl })) {
        $maxId = 10
        $allText = $bookmarks | ConvertTo-Json -Depth 100
        [regex]::Matches($allText, '"id"\s*:\s*"(?<id>\d+)"') | ForEach-Object {
            $maxId = [Math]::Max($maxId, [int]$_.Groups['id'].Value)
        }

        $now = ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() * 1000000 + 11644473600000000).ToString()
        $children += [pscustomobject]@{
            date_added = $now
            date_last_used = '0'
            guid = [guid]::NewGuid().ToString()
            id = ($maxId + 1).ToString()
            name = $edgeBookmarkName
            type = 'url'
            url = $edgeBookmarkUrl
        }
        $bar.children = $children
        $bar.date_modified = $now
        $bookmarks | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $bookmarksPath -Encoding UTF8
    }
}

Write-Step "Preparing folders"
Remove-Item -LiteralPath $sourceRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null

Write-Step "Downloading StayActive source"
$zipPath = Join-Path $env:TEMP 'stayactive-source.zip'
Invoke-WebRequest -Uri $sourceZipUrl -OutFile $zipPath
Expand-Archive -LiteralPath $zipPath -DestinationPath $sourceRoot -Force

Ensure-DotNet

Write-Step "Building StayActive in VM"
& $dotnetExe build (Join-Path $sourceRoot 'stayactive.csproj') -p:UseSharedCompilation=false

Write-Step "Publishing StayActive"
Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
& $dotnetExe publish (Join-Path $sourceRoot 'stayactive.csproj') -c Release -r win-x64 --self-contained true -o $publishRoot -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

$exe = Join-Path $publishRoot 'stayactive.exe'
if (-not (Test-Path $exe)) {
    throw "Published executable not found: $exe"
}

Write-Step "Creating Startup shortcut"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startupShortcut)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $publishRoot
$shortcut.Description = 'StayActive'
$shortcut.Save()

Install-EdgeBookmark

Write-Step "Launching StayActive"
Get-Process stayactive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $exe -WorkingDirectory $publishRoot
Start-Sleep -Seconds 3

$running = Get-Process stayactive -ErrorAction SilentlyContinue
if (-not $running) {
    throw 'StayActive did not remain running after launch.'
}

Write-Step "Done"
Write-Host "StayActive installed at $publishRoot"
Write-Host "Startup shortcut: $startupShortcut"
Write-Host "Edge bookmark: $edgeBookmarkName -> $edgeBookmarkUrl"
Write-Host "Running PID(s): $($running.Id -join ', ')"
