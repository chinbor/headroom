# Headroom installer for Windows 10 / 11 (x64), no admin rights needed.
#
#   irm https://raw.githubusercontent.com/chinbor/headroom/main/install.ps1 | iex
#
# Downloads the latest release from GitHub, checks it against the release's SHA256SUMS.txt,
# installs to %LOCALAPPDATA%\Programs\Headroom and adds a Start menu shortcut.
# Running it again updates in place; the data folder is kept. A copy installed by this script
# before the rename (Programs\codeusagemonit, up to 2.0.1) is moved over with its data.
# Optional: $env:HEADROOM_DIR to install elsewhere, $env:HEADROOM_VERSION = '2.1.0'.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $repo = 'chinbor/headroom'
    $dir = if ($env:HEADROOM_DIR) { $env:HEADROOM_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\Headroom' }
    $legacy = if ($env:HEADROOM_DIR) { '' } else { Join-Path $env:LOCALAPPDATA 'Programs\codeusagemonit' }
    $names = @('headroom', 'codeusagemonit')
    $headers = @{ 'User-Agent' = 'headroom-installer' }

    $api = if ($env:HEADROOM_VERSION) { "https://api.github.com/repos/$repo/releases/tags/v$($env:HEADROOM_VERSION)" } else { "https://api.github.com/repos/$repo/releases/latest" }
    Write-Host 'Headroom: looking up the release...'
    $release = Invoke-RestMethod -Uri $api -Headers $headers
    $zip = $release.assets | Where-Object { $_.name -like 'headroom-*-win-x64.zip' } | Select-Object -First 1
    if (-not $zip) { throw "No Windows x64 zip in release $($release.tag_name)." }
    $sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' -or $_.name -eq "$($zip.name).sha256" } | Select-Object -First 1

    $temp = Join-Path ([IO.Path]::GetTempPath()) ('headroom-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $file = Join-Path $temp $zip.name
        Write-Host "Headroom: downloading $($zip.name) ($([Math]::Round($zip.size / 1MB, 2)) MB)..."
        Invoke-WebRequest -Uri $zip.browser_download_url -OutFile $file -Headers $headers -UseBasicParsing
        if ($sums) {
            $list = (Invoke-WebRequest -Uri $sums.browser_download_url -Headers $headers -UseBasicParsing).Content
            if ($list -is [byte[]]) { $list = [Text.Encoding]::UTF8.GetString($list) }
            $lines = @(($list -split "`r?`n") | Where-Object { $_.Trim() })
            $line = $lines | Where-Object { $_ -match [Regex]::Escape($zip.name) } | Select-Object -First 1
            if (-not $line -and $lines.Count -eq 1) { $line = $lines[0] }
            if (-not $line) { throw "$($sums.name) lists no hash for $($zip.name)." }
            $expected = $line.Trim().Split(' ')[0].ToLowerInvariant()
            $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($expected -ne $actual) { throw "SHA-256 mismatch: expected $expected, got $actual." }
            Write-Host "Headroom: SHA-256 verified ($actual)."
        } else {
            Write-Warning 'This release publishes no SHA-256 list; the download was not verified.'
        }

        # A running copy locks its files; ask it to quit first.
        $folders = @($dir, $legacy) | Where-Object { $_ }
        $running = Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object { $p = $_.Path; $p -and @($folders | Where-Object { $p.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 }
        if ($running) { Write-Host 'Headroom: closing the running copy...'; $running | Stop-Process -Force; Start-Sleep -Milliseconds 600 }
        # The pre-rename folder becomes the new one, data included.
        if ($legacy -and (Test-Path -LiteralPath $legacy) -and -not (Test-Path -LiteralPath $dir)) {
            Write-Host "Headroom: moving $legacy to $dir..."
            Move-Item -LiteralPath $legacy -Destination $dir
        }

        $unpacked = Join-Path $temp 'unpacked'
        Expand-Archive -LiteralPath $file -DestinationPath $unpacked -Force
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        # Everything except data\ (settings, keys, caches) is replaced.
        Get-ChildItem -LiteralPath $unpacked | Where-Object { $_.Name -ne 'data' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dir -Recurse -Force }
        Remove-Item -LiteralPath (Join-Path $dir 'codeusagemonit.exe') -Force -ErrorAction SilentlyContinue
    } finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Start menu shortcut.
    $programs = [Environment]::GetFolderPath('Programs')
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'Headroom.lnk'))
    $link.TargetPath = Join-Path $dir 'headroom.exe'; $link.WorkingDirectory = $dir; $link.IconLocation = (Join-Path $dir 'headroom.exe') + ',0'; $link.Save()

    Write-Host ''
    Write-Host "Headroom $($release.tag_name) installed to $dir" -ForegroundColor Green
    Write-Host '  Start it from the Start menu (Headroom).'
    Write-Host '  Update: run this command again.'
}
