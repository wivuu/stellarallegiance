#!/usr/bin/env pwsh
#Requires -Version 7.3
# verify-game-assets.ps1 — *dot-sourced* by the scripts that produce a game build (package-clients.ps1,
# export-clients.ps1). After `. scripts/verify-game-assets.ps1` call
#
#     $verdict = Test-GameAssets -GameExe <exported game executable> -WorkDir <scratch folder>
#
# which RUNS THE EXPORTED GAME with `--headless --verify-assets=<report>` (client/scripts/AssetVerify.cs)
# and returns { Ok, ExitCode, Lines }: can this build render AND collide with every model it may be
# asked to field? The caller refuses to package a build that cannot.
#
# Why run the artifact instead of inspecting the project: the failure only exists inside a package.
# From source every raw .glb is on disk and the collision loader is happy; in an export Godot replaces
# an imported .glb by its imported scene, so the loader finds nothing and the client silently predicts
# spheres where the server has hulls. v0.0.13 and v0.0.14 shipped exactly like that — the ship
# rubber-banded near every station and it read as server lag. This is the gate that was missing.
#
# The REPORT FILE is the verdict, not the exit code: headless Godot .NET has been seen to die during
# shutdown (exit 139) after doing its job, and neither a pass nor a fail may hinge on that. No report,
# or a first line that is not `ASSET_VERIFY: OK …`, is a failure.

function Test-GameAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$GameExe,
        [Parameter(Mandatory)] [string]$WorkDir,
        [int]$TimeoutSeconds = 300
    )

    if (-not (Test-Path -LiteralPath $GameExe -PathType Leaf)) {
        return [pscustomobject]@{ Ok = $false; ExitCode = $null; Lines = @("game executable not found: $GameExe") }
    }
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $report = Join-Path $WorkDir 'asset-verify.txt'
    $log = Join-Path $WorkDir 'asset-verify.log'
    Remove-Item -Force -LiteralPath $report, $log -ErrorAction SilentlyContinue

    # A plain Process with inherited handles (same reasons as Invoke-Program in package-clients.ps1):
    # PowerShell would not wait for a GUI-subsystem exe on Windows, and nothing the game prints may leak
    # into this function's return value. --log-file keeps the run out of the developer's own
    # user://logs (Godot keeps five, and this must not rotate a real session out of them).
    $info = [System.Diagnostics.ProcessStartInfo]::new($GameExe)
    $info.UseShellExecute = $false
    # The staged game sits inside the launcher's bundle/folder, which is exactly how a Dock-icon start looks
    # to it - and its answer to that is to relaunch THROUGH the launcher (client/scripts/LauncherHandoff.cs).
    # The game stands down on its own under - -verify-assets; this is the belt to that pair of braces (and
    # covers a game build that predates it): never let a packaging check open the launcher's window.
    $info.Environment['SA_NO_LAUNCHER_REDIRECT'] = '1'
    foreach ($a in @('--headless', '--log-file', $log, "--verify-assets=$report")) { $info.ArgumentList.Add($a) }
    $process = [System.Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        return [pscustomobject]@{ Ok = $false; ExitCode = $null; Lines = @("the game did not finish its asset check within $TimeoutSeconds s (log: $log)") }
    }
    $exit = $process.ExitCode

    if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
        return [pscustomobject]@{
            Ok = $false; ExitCode = $exit
            Lines = @("the game wrote no asset report (exit code $exit) - it predates --verify-assets, or died before the check (log: $log)")
        }
    }
    $lines = @(Get-Content -LiteralPath $report)
    $ok = $lines.Count -gt 0 -and $lines[0].StartsWith('ASSET_VERIFY: OK')
    return [pscustomobject]@{ Ok = $ok; ExitCode = $exit; Lines = $lines }
}
