function Get-OpticonSourceFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceRoot)

    $root = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    $relativeInputs = @(
        'src',
        'assets',
        'installer',
        'Taildesk.sln',
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'build.ps1'
    )
    $files = @{}
    foreach ($relativeInput in $relativeInputs) {
        $inputPath = [IO.Path]::GetFullPath((Join-Path $root $relativeInput))
        if (-not $inputPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Opticon fingerprint input escaped the source root: $relativeInput"
        }
        if (-not (Test-Path -LiteralPath $inputPath)) { continue }
        $item = Get-Item -LiteralPath $inputPath -Force
        $candidates = if ($item.PSIsContainer) {
            Get-ChildItem -LiteralPath $inputPath -File -Recurse -Force |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        } else {
            @($item)
        }
        foreach ($candidate in $candidates) { $files[$candidate.FullName] = $candidate }
    }

    $records = foreach ($file in @($files.Values | Sort-Object -Property FullName)) {
        $relative = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative`0$($file.Length)`0$hash"
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($records -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
