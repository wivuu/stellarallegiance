#!/usr/bin/env pwsh
# Export the Godot client for macOS + Windows + Linux and package for distribution.
#
# macOS note: the ad-hoc re-signing step (codesign/ditto) only works on macOS.
# On Windows/Linux, only the Windows and Linux builds are produced.
$ErrorActionPreference = 'Stop'

$REPO_ROOT = Split-Path -Parent $PSScriptRoot
$CLIENT    = Join-Path $REPO_ROOT 'client'
$OUT       = Join-Path $REPO_ROOT 'build'

. "$PSScriptRoot/godot-bin.ps1"
$GODOT = Resolve-Godot
if (-not $GODOT) { exit 1 }

New-Item -ItemType Directory -Force -Path "$OUT/mac", "$OUT/win", "$OUT/linux" | Out-Null

# Kill stale MSBuild/Roslyn servers to prevent dotnet publish from hanging inside a headless export.
$env:MSBUILDDISABLENODEREUSE      = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
Stop-Process -Name VBCSCompiler -Force -ErrorAction SilentlyContinue
Stop-Process -Name MSBuild       -Force -ErrorAction SilentlyContinue
& dotnet build-server shutdown 2>$null

if ($IsMacOS) {
    $app = "$OUT/mac/wivuullegiance.app"
    Write-Host "[export] macOS .app ..."
    Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
    & $GODOT --headless --path $CLIENT --export-release macOS $app

    Write-Host "[export] re-signing macOS app as plain ad-hoc (no hardened runtime) ..."
    codesign --remove-signature $app 2>$null
    codesign --force --deep --sign - $app
    codesign --verify --deep --strict $app && Write-Host "[export]   signature valid"

    Write-Host "[export] zipping macOS app with ditto ..."
    Remove-Item -Force "$OUT/mac/wivuullegiance-macos.zip" -ErrorAction SilentlyContinue
    & ditto -c -k --keepParent $app "$OUT/mac/wivuullegiance-macos.zip"
} else {
    Write-Host "[export] skipping macOS build (only exportable from macOS — needs codesign/ditto)"
}

Write-Host "[export] Windows .exe ..."
& $GODOT --headless --path $CLIENT --export-release 'Windows Desktop' "$OUT/win/wivuullegiance.exe"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[export] zipping Windows folder (testers need the whole folder) ..."
Remove-Item -Force "$OUT/wivuullegiance-windows.zip" -ErrorAction SilentlyContinue
Compress-Archive -Path "$OUT/win/*" -DestinationPath "$OUT/wivuullegiance-windows.zip"

Write-Host "[export] Linux .x86_64 ..."
& $GODOT --headless --path $CLIENT --export-release Linux "$OUT/linux/wivuullegiance.x86_64"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $IsWindows) { chmod +x "$OUT/linux/wivuullegiance.x86_64" }

Write-Host "[export] zipping Linux folder (testers need the whole folder) ..."
Remove-Item -Force "$OUT/wivuullegiance-linux.zip" -ErrorAction SilentlyContinue
Compress-Archive -Path "$OUT/linux/*" -DestinationPath "$OUT/wivuullegiance-linux.zip"

Write-Host ""
Write-Host "[export] done:"
Write-Host "  macOS:   $OUT/mac/wivuullegiance-macos.zip"
Write-Host "  Windows: $OUT/wivuullegiance-windows.zip"
Write-Host "  Linux:   $OUT/wivuullegiance-linux.zip"
