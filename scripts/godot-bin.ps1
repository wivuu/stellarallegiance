# godot-bin.ps1 — dot-sourced by other scripts to resolve the Godot .NET executable.
#
# Usage:
#   . "$PSScriptRoot/godot-bin.ps1"
#   $GODOT = Resolve-Godot
#   if (-not $GODOT) { exit 1 }
#
# Resolution order:
#   1. $env:GODOT (set by the VS Code tasks via godot.executablePath, or exported by the caller)
#   2. godot-mono / godot4 / godot on PATH
#   3. Standard install locations (macOS .app bundles, Windows, Scoop)

function Resolve-Godot {
    # 1. Explicit override
    if ($env:GODOT) {
        if ((Get-Command $env:GODOT -ErrorAction SilentlyContinue) -or (Test-Path $env:GODOT)) {
            return $env:GODOT
        }
        Write-Warning "[godot] GODOT='$env:GODOT' is not an executable — falling back to auto-detect."
    }

    # 2. On PATH
    foreach ($c in 'godot-mono', 'godot4', 'godot') {
        if (Get-Command $c -ErrorAction SilentlyContinue) { return $c }
    }

    # 3. VS Code user settings — godot.executablePath (used when running from a terminal,
    #    not a VS Code task, which injects $env:GODOT via tasks.json)
    $vscodeSettings = @(
        (Join-Path $env:APPDATA      'Code\User\settings.json')                          # Windows
        (Join-Path $HOME             'Library/Application Support/Code/User/settings.json') # macOS
        (Join-Path $HOME             '.config/Code/User/settings.json')                  # Linux
    )
    foreach ($sp in $vscodeSettings) {
        if (-not (Test-Path $sp)) { continue }
        $m = Select-String -Path $sp -Pattern '"godot\.executablePath"\s*:\s*"([^"]+)"' |
             Select-Object -First 1
        if ($m) {
            $p = $m.Matches[0].Groups[1].Value -replace '\\\\', '\'
            if ($p -and (Test-Path $p)) { return $p }
        }
        break
    }

    # 4. Standard install locations
    $candidates = @(
        # macOS
        '/Applications/Godot_mono.app/Contents/MacOS/Godot'
        '/Applications/Godot.app/Contents/MacOS/Godot'
        (Join-Path $HOME 'Applications/Godot_mono.app/Contents/MacOS/Godot')
        # Windows — explicit known path first
        'C:\Program Files\Godot_mono\Godot_v4.6.3-stable_mono_win64.exe'
    )
    foreach ($glob in @(
        'C:\Program Files\Godot*\Godot*mono*.exe'
        "$env:USERPROFILE\scoop\apps\godot-mono\current\Godot*.exe"
        "$env:USERPROFILE\scoop\apps\godot\current\Godot*.exe"
    )) {
        $candidates += Get-Item $glob -ErrorAction SilentlyContinue |
                       Select-Object -ExpandProperty FullName
    }

    foreach ($p in $candidates) {
        if ($p -and (Test-Path $p -PathType Leaf)) { return $p }
    }

    Write-Error "[godot] No Godot .NET (mono) executable found."
    Write-Error "[godot] Set the 'godot.executablePath' VS Code setting (User scope), or set `$env:GODOT to the executable path."
    return $null
}
