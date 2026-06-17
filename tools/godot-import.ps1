#!/usr/bin/env pwsh
# Regenerate Godot's GLB import artifacts.
#
#   tools/godot-import.ps1           # import only if something is missing
#   tools/godot-import.ps1 -Force    # always reimport (after editing an existing asset)
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$REPO_ROOT = Split-Path -Parent $PSScriptRoot
$CLIENT    = Join-Path $REPO_ROOT 'client'

. "$REPO_ROOT/scripts/godot-bin.ps1"

function Test-NeedsImport {
    if ($Force) { return $true }
    $glbs = Get-ChildItem "$CLIENT/assets" -Recurse -Filter '*.glb' -ErrorAction SilentlyContinue
    foreach ($glb in $glbs) {
        if (-not (Test-Path "$($glb.FullName).import")) { return $true }
    }
    return $false
}

if (-not (Test-NeedsImport)) {
    Write-Host "[godot-import] assets already imported — nothing to do (pass -Force to reimport)."
    exit 0
}

$GODOT = Resolve-Godot
if (-not $GODOT) {
    # Exit 0 so an auto-run on folder-open doesn't surface as a task failure on machines without Godot.
    exit 0
}

Write-Host "[godot-import] importing assets with: $GODOT"
& $GODOT --headless --import --path $CLIENT
Write-Host "[godot-import] done."
