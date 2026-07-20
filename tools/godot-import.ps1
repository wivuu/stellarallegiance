#!/usr/bin/env pwsh
#Requires -Version 7.3
#
# godot-import.ps1 — regenerate Godot's GLB import artifacts.
#
# Each asset's `.glb` is the ONLY committed file (it embeds its own textures). Godot's
# import sidecars (`*.glb.import`, extracted `*_N.png`, `*.png.import`) are gitignored
# and regenerated here, so a fresh clone (or a newly-added `.glb`) needs an import before
# `res://...glb` resolves — otherwise meshes silently fall back to procedural placeholders.
#
#   tools/godot-import.ps1           # import only if something is missing (fresh clone / new asset)
#   tools/godot-import.ps1 -Force    # always reimport (after editing an existing asset)
#
# The Godot (.NET/"mono") executable is resolved by scripts/godot-bin.ps1 — $env:GODOT, then PATH,
# then standard install locations. Set the `godot.executablePath` VS Code setting to override.
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Repo = Split-Path $PSScriptRoot -Parent
$Client = "$Repo/client"

. "$Repo/scripts/godot-bin.ps1"

function Test-NeedsImport {
    if ($Force) { return $true }
    # Import needed if any .glb lacks its sibling .glb.import (fresh clone or a new asset).
    $missing = Get-ChildItem -Path "$Client/assets" -Recurse -Filter *.glb -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-Path -LiteralPath "$($_.FullName).import") } |
        Select-Object -First 1
    return [bool]$missing
}

if (-not (Test-NeedsImport)) {
    Write-Host "[godot-import] assets already imported — nothing to do (pass -Force to reimport)."
    exit 0
}

$Godot = Resolve-Godot
if (-not $Godot) {
    # Exit 0 so an auto-run on folder-open doesn't surface as a task failure on machines
    # without Godot (Resolve-Godot already printed how to set the path).
    exit 0
}

Write-Host "[godot-import] importing assets with: $Godot"
& $Godot --headless --import --path $Client
Write-Host "[godot-import] done."
