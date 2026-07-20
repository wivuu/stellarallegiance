#!/usr/bin/env pwsh
#Requires -Version 7.3
#
# export-clients.ps1 — export the Godot client for macOS + Windows + Linux and
# package them for tester distribution.
#
# macOS gotcha: Godot's built-in macOS signing always enables the *hardened
# runtime* (codesign flags 0x10002). That's only meaningful once you notarize;
# on an un-notarized ad-hoc build it makes the kernel SIGKILL the app at launch
# ("application can't be opened", no crash log). So we export to a .app, strip
# Godot's signature, and re-sign plain ad-hoc (flags 0x2) — which Apple Silicon
# still requires to run at all — then zip with `ditto` to preserve the signature
# and framework symlinks (plain `zip` corrupts them).
#
# Testers still need to clear quarantine once on the downloaded zip:
#   xattr -dr com.apple.quarantine stellarallegiance.app
#
# Usage: scripts/export-clients.ps1   (run from anywhere; needs godot-mono on PATH)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Client = "$RepoRoot/client"
$Out = "$RepoRoot/build"

. "$RepoRoot/scripts/godot-bin.ps1"
$Godot = Resolve-Godot
if (-not $Godot) { exit 1 }

New-Item -ItemType Directory -Force -Path "$Out/mac", "$Out/win", "$Out/linux" | Out-Null

# Make sure GLB import sidecars exist before exporting — un-imported assets export "successfully"
# but silently fall back to procedural placeholders at runtime. No-op when already imported.
& "$RepoRoot/tools/godot-import.ps1"
if (-not (Test-Path -LiteralPath "$Client/assets/bases/base.glb.import")) {
    [Console]::Error.WriteLine("[export] ERROR: GLB import sidecars missing — export would ship placeholder meshes")
    exit 1
}

# Godot's export rebuilds the C# project with `dotnet` under the hood. MSBuild
# node reuse can leave wedged worker processes (e.g. from a different SDK or the
# IDE's C# Dev Kit) that the next build connects to and hangs on. Disabling node
# reuse and clearing any stale servers up front keeps the export from hanging.
# UseSharedCompilation=false tells MSBuild not to spin up / connect to a Roslyn
# compiler server (VBCSCompiler), which is the most common cause of dotnet
# publish hanging inside a Godot headless export.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
Get-Process VBCSCompiler, MSBuild -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
# Native cleanups expected to no-op / fail — mirror bash `|| true`.
$prevNative = $PSNativeCommandUseErrorActionPreference
$PSNativeCommandUseErrorActionPreference = $false
try {
    if (-not $IsWindows) {
        pkill -9 -f "MSBuild.dll" 2>$null
    }
    dotnet build-server shutdown 2>$null | Out-Null
} finally {
    $PSNativeCommandUseErrorActionPreference = $prevNative
}

# The macOS .app export + ad-hoc re-signing only works on macOS (codesign/ditto are
# macOS-only). On Windows/Linux we skip it and still produce the Windows + Linux builds.
if ($IsMacOS) {
    Write-Host "[export] macOS .app ..."
    $App = "$Out/mac/stellarallegiance.app"
    Remove-Item -Recurse -Force -LiteralPath $App -ErrorAction SilentlyContinue
    & $Godot --headless --path $Client --export-release "macOS" $App

    Write-Host "[export] re-signing macOS app as plain ad-hoc (no hardened runtime) ..."
    $prevNative = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    codesign --remove-signature $App 2>$null
    $PSNativeCommandUseErrorActionPreference = $prevNative
    codesign --force --deep --sign - $App
    codesign --verify --deep --strict $App
    Write-Host "[export]   signature valid"

    Write-Host "[export] zipping macOS app with ditto ..."
    Remove-Item -Force -LiteralPath "$Out/mac/stellarallegiance-macos.zip" -ErrorAction SilentlyContinue
    ditto -c -k --keepParent $App "$Out/mac/stellarallegiance-macos.zip"
} else {
    Write-Host "[export] skipping macOS build (only exportable from macOS — needs codesign/ditto)"
}

Write-Host "[export] Windows .exe ..."
& $Godot --headless --path $Client --export-release "Windows Desktop" "$Out/win/stellarallegiance.exe"

Write-Host "[export] zipping Windows folder (testers need the whole folder) ..."
Remove-Item -Force -LiteralPath "$Out/stellarallegiance-windows.zip" -ErrorAction SilentlyContinue
Compress-Archive -Path "$Out/win/*" -DestinationPath "$Out/stellarallegiance-windows.zip" -Force

Write-Host "[export] Linux .x86_64 ..."
& $Godot --headless --path $Client --export-release "Linux" "$Out/linux/stellarallegiance.x86_64"

Write-Host "[export] zipping Linux folder (testers need the whole folder) ..."
Remove-Item -Force -LiteralPath "$Out/stellarallegiance-linux.zip" -ErrorAction SilentlyContinue
if (-not $IsWindows) {
    # Executable bit matters on Linux — set it and use native zip to preserve it.
    chmod +x "$Out/linux/stellarallegiance.x86_64"
    Push-Location "$Out/linux"
    try {
        zip -rq "$Out/stellarallegiance-linux.zip" .
    } finally {
        Pop-Location
    }
} else {
    # No chmod/zip on Windows — fall back to Compress-Archive. The executable bit is lost,
    # so testers must `chmod +x stellarallegiance.x86_64` after unzipping on Linux.
    Compress-Archive -Path "$Out/linux/*" -DestinationPath "$Out/stellarallegiance-linux.zip" -Force
    Write-Host "[export]   NOTE: built on Windows — Linux testers must 'chmod +x stellarallegiance.x86_64' after unzip"
}

Write-Host ""
Write-Host "[export] done:"
Write-Host "  macOS:   $Out/mac/stellarallegiance-macos.zip"
Write-Host "  Windows: $Out/stellarallegiance-windows.zip"
Write-Host "  Linux:   $Out/stellarallegiance-linux.zip"
