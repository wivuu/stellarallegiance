#!/usr/bin/env pwsh
# Solo turret feel rig: one command puts you in a bomber's dorsal turret with a live aim readout.
#
#   pwsh scripts/turret-test.ps1                  # you aim; close the window (or Esc menu → Quit) to end
#   pwsh scripts/turret-test.ps1 -Auto            # scripted mouse at 3 hand speeds + spin + chat; exit code = verdict
#   pwsh scripts/turret-test.ps1 -SlewDeg 110     # feel a heavy (Devastator-weight) mount on the same seat
#   pwsh scripts/turret-test.ps1 -Gain 0.2 -Window 0.25 -Sens 0.015
#   pwsh scripts/turret-test.ps1 -Pigs            # leave the AI drones on (moving targets; they WILL shoot you)
#
# A gunner needs a captain, so this starts THREE processes and tears them all down when the gunner
# window exits: a private sim server on its own port, a small CAPTAIN client that launches the bomber
# and sits still (--turret-test=captain), and YOUR client, which claims station T1 and hands you the
# mouse (--turret-test=gunner|auto). The server runs a throwaway copy of the stock content with the
# bomber pre-unlocked (faction `base-techs: [bomber]`), skipping the 120 s research.
#
# The on-screen readout / [turret-stats] log line (once a second while the mouse is captured):
#   hand px/s · want °/s vs got °/s · limited % (frames the slew bucket scaled) · dropped ° ·
#   budget (bucket fill) · the station's slew · the live gain in °/px · fps
# A healthy feel keeps `limited` at 0 outside a deliberate hard spin.
#
# Tuning overrides are CLIENT env vars (TurretController reads them): TURRET_GAIN (rad per stick
# unit, stock 0.12), TURRET_SLEW_DEG (every station's sustained rate; 0 = unlimited),
# TURRET_SLEW_WINDOW (seconds of traverse the bucket holds, stock 0.15), STDB_MOUSE_SENS.
param(
    [switch]$Auto,
    [switch]$Pigs,
    [double]$Gain,
    [double]$SlewDeg = -1,
    [double]$Window = -1,
    [double]$Sens,
    [int]$Port = 8097,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $RepoRoot

. "$RepoRoot/scripts/godot-bin.ps1"
$Godot = Resolve-Godot
if (-not $Godot) { exit 1 }

if (-not $NoBuild) {
    Write-Host "[turret-test] building server (Release) + client (Debug)"
    dotnet build "$RepoRoot/server/SimServer.csproj" -c Release -v q --nologo | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build "$RepoRoot/client/stellarallegiance.csproj" -v q --nologo | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Throwaway content: stock bundle + the bomber pre-unlocked for every team.
$Work = Join-Path ([System.IO.Path]::GetTempPath()) "turret-test-$PID"
$Content = Join-Path $Work 'core'
New-Item -ItemType Directory -Force $Work | Out-Null
Copy-Item "$RepoRoot/server/Content/core" $Content -Recurse
foreach ($f in Get-ChildItem "$Content/factions/*.yaml") {
    $text = Get-Content $f -Raw
    if ($text -match '(?m)^base-techs:') {
        Write-Warning "[turret-test] $($f.Name) already authors base-techs — not patched; the captain will research the bomber if it is locked (~120 s)"
    } else {
        Add-Content $f "`nbase-techs: [bomber]   # turret-test: skip the bomber research`n"
    }
}

$procs = @()
# Children inherit this process's environment, so Env is applied around the spawn and restored.
function Start-Logged([string]$Name, [string]$Exe, [string[]]$ArgList, [hashtable]$Env) {
    $saved = @{}
    foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$Env[$k]) }
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList $ArgList -WorkingDirectory $RepoRoot -PassThru `
            -RedirectStandardOutput (Join-Path $Work "$Name.log") -RedirectStandardError (Join-Path $Work "$Name.err.log")
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    Write-Host "[turret-test] $Name pid $($p.Id) → $Work/$Name.log"
    return $p
}

# Echo the gunner's harness lines as they land (the log is the full record).
$script:Echoed = 0
function Show-GunnerLines {
    $log = Join-Path $Work 'gunner.log'
    if (-not (Test-Path $log)) { return }
    $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
    for ($i = $script:Echoed; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'TURRET_TEST|\[turret-stats\]|CREW_DEMO|HANGAR_DEMO') { Write-Host "[gunner] $($lines[$i])" }
    }
    $script:Echoed = $lines.Count
}

$exit = 1
try {
    $serverEnv = @{ SIM_PUBLIC_NAME = ''; SIM_PIGS = $(if ($Pigs) { '1' } else { '0' }) }
    $procs += Start-Logged 'server' 'dotnet' @(
        "$RepoRoot/server/bin/Release/net10.0/SimServer.dll", '--port', "$Port", '--autostart',
        '--content', "$Content/core.manifest.yaml", '--world', "$Content/world.yaml"
    ) $serverEnv

    # Wait for the listener (asset pre-bake on a cold sim-cache can take a while).
    $up = $false
    for ($i = 0; $i -lt 240 -and -not $up; $i++) {
        if ($procs[0].HasExited) { throw "server exited early — see $Work/server.log" }
        try { $c = [System.Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', $Port); $up = $true } catch { Start-Sleep -Milliseconds 500 } finally { $c.Dispose() }
    }
    if (-not $up) { throw "server never listened on :$Port — see $Work/server.log" }

    $clientEnv = @{ AUTOFLY_TEAM = '0'; TURRET_TEST_DIR = $Work }
    if ($Gain -gt 0) { $clientEnv.TURRET_GAIN = $Gain.ToString([cultureinfo]::InvariantCulture) }
    if ($SlewDeg -ge 0) { $clientEnv.TURRET_SLEW_DEG = $SlewDeg.ToString([cultureinfo]::InvariantCulture) }
    if ($Window -ge 0) { $clientEnv.TURRET_SLEW_WINDOW = $Window.ToString([cultureinfo]::InvariantCulture) }
    if ($Sens -gt 0) { $clientEnv.STDB_MOUSE_SENS = $Sens.ToString([cultureinfo]::InvariantCulture) }

    # The captain needs a real window (the hangar harness drives real widgets), just not a big one.
    $procs += Start-Logged 'captain' $Godot @(
        '--path', "$RepoRoot/client", '--windowed', '--resolution', '1280x720', '--position', '40,40',
        '--host', "localhost:$Port", '--', '--turret-test=captain'
    ) $clientEnv
    Start-Sleep -Seconds 2
    $role = if ($Auto) { 'auto' } else { 'gunner' }
    $gunner = Start-Logged 'gunner' $Godot @(
        '--path', "$RepoRoot/client", '--host', "localhost:$Port", '--', "--turret-test=$role"
    ) $clientEnv
    $procs += $gunner

    Write-Host "[turret-test] waiting for the gunner client to exit (the seat takes ~15–20 s to reach)…"
    while (-not $gunner.HasExited) {
        if ($procs[0].HasExited) { throw "server died — see $Work/server.log" }
        Show-GunnerLines
        Start-Sleep -Milliseconds 300
    }
    $gunner.WaitForExit()
    Show-GunnerLines
    $exit = $gunner.ExitCode
}
finally {
    foreach ($p in $procs) { if ($p -and -not $p.HasExited) { try { $p.Kill($true) } catch {} } }
    Write-Host "[turret-test] logs kept in $Work"
}
exit $exit
