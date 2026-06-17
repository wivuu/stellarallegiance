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

    # 3. Standard install locations
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
