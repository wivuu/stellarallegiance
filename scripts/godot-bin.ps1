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
#   2. the per-workstation `dotnet user-secrets` store (id `stellarallegiance`, key
#      `godot.executablePath`) — set via the "Godot: set executable path" VS Code task or
#        dotnet user-secrets set godot.executablePath "<path>" --id stellarallegiance
#      Lives outside the repo (%APPDATA%\Microsoft\UserSecrets / ~/.microsoft/usersecrets),
#      so it can never dirty a committed file. Read directly from secrets.json (no dotnet
#      invocation) to keep resolution instant.
#   3. `godot-mono` / `godot4` / `godot` on PATH.
#   4. standard install locations (macOS .app bundles, Windows, scoop/winget, Linux).
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

    # 2. per-workstation `dotnet user-secrets` store (see header). secrets.json is read
    #    directly — `dotnet user-secrets list` would add a full dotnet invocation per run.
    $secretsRoot = if ($IsWindows) { "$env:APPDATA\Microsoft\UserSecrets" } else { "$HOME/.microsoft/usersecrets" }
    $secretsFile = Join-Path $secretsRoot 'stellarallegiance' 'secrets.json'
    if (Test-Path -LiteralPath $secretsFile -PathType Leaf) {
        $secret = $null
        try { $secret = (Get-Content -LiteralPath $secretsFile -Raw | ConvertFrom-Json).'godot.executablePath' }
        catch { [Console]::Error.WriteLine("[godot] Could not parse '$secretsFile' — ignoring it.") }
        if ($secret) {
            $cmd = Get-Command $secret -ErrorAction SilentlyContinue
            if ($cmd) { return $cmd.Source }
            if (Test-Path -LiteralPath $secret -PathType Leaf) { return $secret }
            [Console]::Error.WriteLine("[godot] user-secret godot.executablePath='$secret' is not an executable — falling back to auto-detect.")
        }
    }

    # 3. on PATH
    foreach ($c in 'godot-mono', 'godot4', 'godot') {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }

    # 4. standard install locations, guarded by platform. Globs that don't match expand to
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
        $candidates += "$env:LOCALAPPDATA\Programs\Godot*\Godot*mono*\Godot*.exe"
        $candidates += "$HOME\scoop\apps\godot-mono\current\Godot*.exe"
        $candidates += "$HOME\scoop\apps\godot\current\Godot*.exe"
    }
    if ($IsLinux) {
        $candidates += "$HOME/.local/bin/godot*"
        $candidates += "/usr/local/bin/godot*"
        $candidates += "/opt/godot*/Godot*"
    }
    foreach ($p in $candidates) {
        $hit = Get-Item -Path $p -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    [Console]::Error.WriteLine("[godot] No Godot .NET (mono) executable found.")
    [Console]::Error.WriteLine("[godot] Run the 'Godot: set executable path' VS Code task, or equivalently:")
    [Console]::Error.WriteLine("[godot]   dotnet user-secrets set godot.executablePath `"/path/to/Godot`" --id stellarallegiance")
    [Console]::Error.WriteLine("[godot] (or export GODOT=/path/to/Godot for a one-off).")
    return $null
}
