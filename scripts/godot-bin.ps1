#!/usr/bin/env pwsh
#Requires -Version 7.3
# godot-bin.ps1 — shared Godot-executable resolver, *dot-sourced* by the other scripts.
#
# After `. scripts/godot-bin.ps1` call `Resolve-Godot`, which **returns** a runnable
# Godot 4 .NET ("mono") binary path as a string, or `$null` if none is found. Callers:
#   `$Godot = Resolve-Godot; if (-not $Godot) { exit 1 }`
# Resolution order:
#   1. a preset $env:GODOT (the VS Code tasks set this from the `godot.executablePath` setting;
#      CI / shells can export it directly). Empty string = treated as unset.
#   2. `godot-mono` / `godot4` / `godot` on PATH.
#   3. standard install locations (macOS .app bundles, Windows, scoop).
# Returns `$null` (and prints guidance to stderr) if nothing usable is found, so callers can bail.
#
# NOTE: $env:GODOT must be the Godot *executable*, not the macOS `.app` folder that the
# godot-tools extension's `godotTools.editorPath.godot4` setting points at.
#
# Warnings/guidance go to the warning/error stream (never stdout), because callers capture
# this function's return value as the resolved path.

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Resolve-Godot {
    [CmdletBinding()]
    param()

    # 1. explicit override (skip if empty, e.g. the task passed an unset config value)
    if ($env:GODOT) {
        $cmd = Get-Command $env:GODOT -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
        if (Test-Path -LiteralPath $env:GODOT -PathType Leaf) { return $env:GODOT }
        [Console]::Error.WriteLine("[godot] GODOT='$($env:GODOT)' is not an executable — falling back to auto-detect.")
    }

    # 2. on PATH
    foreach ($c in 'godot-mono', 'godot4', 'godot') {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }

    # 3. standard install locations, guarded by platform. Globs that don't match expand to
    #    nothing, so Get-Item over the pattern simply yields no results.
    $candidates = @()
    if ($IsMacOS) {
        $candidates += "/Applications/Godot_mono.app/Contents/MacOS/Godot"
        $candidates += "/Applications/Godot.app/Contents/MacOS/Godot"
        $candidates += "$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot"
    }
    if ($IsWindows) {
        $candidates += "$env:ProgramFiles\Godot\*mono*\Godot*.exe"
        $candidates += "$env:ProgramFiles\Godot_mono\Godot*.exe"
        $candidates += "$HOME\scoop\apps\godot-mono\current\Godot*.exe"
        $candidates += "$HOME\scoop\apps\godot\current\Godot*.exe"
    }
    foreach ($p in $candidates) {
        $hit = Get-Item -Path $p -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    [Console]::Error.WriteLine("[godot] No Godot .NET (mono) executable found.")
    [Console]::Error.WriteLine("[godot] Set the 'godot.executablePath' VS Code setting (User scope), or export GODOT=/path/to/Godot.")
    return $null
}
