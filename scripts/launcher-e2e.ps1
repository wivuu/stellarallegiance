#!/usr/bin/env pwsh
#Requires -Version 7.3
#
# launcher-e2e.ps1 — prove the whole install → update → restart → play cycle on THIS machine, in about a
# minute, with the real Velopack updater and the real launcher binary. Used locally and by the
# package-dryrun CI workflow (which is how Windows gets verified without a Windows machine).
#
# It packs three versions of the launcher around the stub game (tools/launcher-stubgame) into a local
# folder feed, installs the first the way a player would, then drives the installed launcher HEADLESS
# (`--launcher-selftest=…`, no window, works on display-less runners):
#
#   pass 1  selftest=update   1.0.0 → 1.0.1   first update of a fresh install (full package on macOS/Linux)
#   pass 2  selftest=play     1.0.1 → 1.0.2   the game exits 85 ("UPDATE NOW" pressed in-game) → the launcher
#                                             updates (delta this time), restarts and goes straight back
#                                             into the game
#
# Everything is asserted from the launcher's LAUNCHER_E2E_STATE log markers and the stub game's report
# (how it was started: args, SA_LAUNCHER, SA_LAUNCHER_UPDATE). Exit code 0 = every assertion held.
#
# All state lives under build/launcher-e2e (gitignored) plus Velopack's own per-app cache, which is why
# the test packs under its own id (StellarAllegianceE2E) — a real install is never touched.
param(
    [switch]$KeepFiles, # leave build/launcher-e2e in place afterwards for inspection
    # The three versions to pack, oldest first. The default is what CI runs; pass pre-release strings to
    # rehearse a release candidate's numbering, e.g. -Versions 0.0.13-ci.1, 0.0.13-ci.2, 0.0.13
    [string[]]$Versions = @('1.0.0', '1.0.1', '1.0.2')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path $PSScriptRoot -Parent
$PackId = 'StellarAllegianceE2E'
$PackTitle = 'Stellar Allegiance E2E'
$Channel = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$Root = Join-Path $RepoRoot 'build/launcher-e2e'
$Feed = Join-Path $Root 'feed'
$Install = Join-Path $Root 'install'
$Data = Join-Path $Root 'data'
$Report = Join-Path $Root 'stub-report.txt'
$Log = Join-Path $Data 'logs/launcher.log'
$Failures = [System.Collections.Generic.List[string]]::new()
if ($Versions.Count -ne 3) { throw '-Versions takes exactly three versions, oldest first' }
$v0, $v1, $v2 = $Versions
$r0, $r1, $r2 = $Versions | ForEach-Object { [regex]::Escape($_) }

function Step([string]$Message) { Write-Host "[e2e] $Message" }

function Assert([bool]$Condition, [string]$What) {
    if ($Condition) { Write-Host "[e2e]   PASS  $What" }
    else { Write-Host "[e2e]   FAIL  $What"; $Failures.Add($What) }
}

# Start-Process -ArgumentList does not quote elements, so a repo path with a space in it would split an
# argument in two. ProcessStartInfo.ArgumentList quotes correctly on every OS.
function Start-Native([string]$Exe, [string[]]$Arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    foreach ($a in $Arguments) { $info.ArgumentList.Add($a) }
    return [System.Diagnostics.Process]::Start($info)
}

# Velopack's updater (Update.exe / UpdateMac / UpdateNix) writes its own log outside the install dir. When
# something goes wrong mid-update that log is usually the only witness, so keep a copy next to ours.
function Save-VelopackLog {
    $log = if ($IsMacOS) { "$HOME/Library/Logs/velopack_$PackId.log" }
    elseif ($IsWindows) { "$env:LOCALAPPDATA\velopack\velopack_$PackId.log" }
    else { "/tmp/velopack_$PackId.log" }
    if ((Test-Path -LiteralPath $log) -and (Test-Path -LiteralPath $Root)) {
        Copy-Item -LiteralPath $log -Destination (Join-Path $Root 'velopack-updater.log') -Force -ErrorAction SilentlyContinue
    }
}

# Velopack keeps a per-app package cache + log outside the install dir; clear the test app's.
function Clear-VelopackState {
    $stale = if ($IsMacOS) { @("$HOME/Library/Caches/velopack/$PackId", "$HOME/Library/Logs/velopack_$PackId.log") }
    elseif ($IsWindows) { @("$env:LOCALAPPDATA\velopack\velopack_$PackId.log") }
    else { @("/var/tmp/velopack/$PackId", "/tmp/velopack_$PackId.log") }
    foreach ($path in $stale) {
        if (Test-Path -LiteralPath $path) { Remove-Item -Recurse -Force -LiteralPath $path -ErrorAction SilentlyContinue }
    }
}

function New-Package([string]$Version) {
    $notes = Join-Path $Root "notes-$Version.md"
    "# Stellar Allegiance $Version`n`n* e2e build **$Version**`n" | Set-Content -LiteralPath $notes
    & (Join-Path $RepoRoot 'scripts/package-clients.ps1') -Version $Version -FakeGame -HostArchOnly `
        -PackId $PackId -PackTitle $PackTitle -OutputDir $Feed -ReleaseNotes $notes | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "packaging $Version failed" }
}

# Returns the installed launcher's path.
function Install-First {
    New-Item -ItemType Directory -Force -Path $Install | Out-Null
    if ($IsMacOS) {
        ditto -x -k (Join-Path $Feed "$PackId-$Channel-Portable.zip") $Install
        return Join-Path $Install "$PackTitle.app/Contents/MacOS/StellarLauncher"
    }
    if ($IsWindows) {
        # The real thing: Setup.exe runs the launcher with --veloapp-install and needs it to exit at once.
        $setupLog = Join-Path $Root 'setup.log'
        $setup = Start-Native (Join-Path $Feed "$PackId-$Channel-Setup.exe") @('--silent', '--log', $setupLog, '--installto', $Install)
        $setup.WaitForExit()
        Assert ($setup.ExitCode -eq 0) "Setup.exe --silent exited 0 (was $($setup.ExitCode))"
        $text = if (Test-Path -LiteralPath $setupLog) { Get-Content -LiteralPath $setupLog -Raw } else { '' }
        Assert ($text -match 'Hook executed successfully') 'the --veloapp-install hook ran and exited cleanly'
        Assert ($text -notmatch 'timed out') 'no Velopack hook timed out'
        Assert (Test-Path -LiteralPath (Join-Path $Install 'current/game/stellarallegiance.exe')) 'current\game\ holds the game after install'
        return Join-Path $Install 'current/StellarLauncher.exe'
    }
    $appImage = Join-Path $Install "$PackId.AppImage"
    Copy-Item -LiteralPath (Join-Path $Feed "$PackId.AppImage") -Destination $appImage
    chmod +x $appImage
    return $appImage
}

function Get-Markers {
    if (-not (Test-Path -LiteralPath $Log)) { return @() }
    return @(Get-Content -LiteralPath $Log | Where-Object { $_ -match 'LAUNCHER_E2E_STATE:' } | ForEach-Object { ($_ -split 'LAUNCHER_E2E_STATE:\s*', 2)[1] })
}

# Starts the installed launcher and waits until the log shows `$Until` (a regex over ONE marker line) among
# the markers written after `$Skip`. The launcher restarts itself through Velopack mid-way, so the process
# we start is NOT the one that finishes — hence polling the log rather than waiting on a handle.
function Invoke-Launcher([string]$Exe, [string[]]$LauncherArgs, [string]$Until, [int]$Skip, [int]$TimeoutSeconds = 180) {
    Start-Native $Exe $LauncherArgs | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $new = @(Get-Markers | Select-Object -Skip $Skip)
        if ($new | Where-Object { $_ -match $Until }) {
            Start-Sleep -Milliseconds 1500 # let the final process write its last lines and exit
            return @(Get-Markers | Select-Object -Skip $Skip)
        }
        if ($new | Where-Object { $_ -match '^(Error|UpdateFailed|NotInstalled|GameMissing)\b' }) { break }
        Start-Sleep -Milliseconds 500
    }
    return @(Get-Markers | Select-Object -Skip $Skip)
}

function Get-InstalledVersion {
    $manifest = if ($IsMacOS) { Join-Path $Install "$PackTitle.app/Contents/Resources/sq.version" }
    elseif ($IsWindows) { Join-Path $Install 'current/sq.version' }
    else { $null } # inside the AppImage — the markers are the evidence on Linux
    if (-not $manifest -or -not (Test-Path -LiteralPath $manifest)) { return $null }
    if ((Get-Content -LiteralPath $manifest -Raw) -match '<version>([^<]+)</version>') { return $Matches[1] }
    return $null
}

# ------------------------------------------------------------------------------------------------------
# CI runners have no FUSE, which an AppImage normally mounts itself with. The AppImage runtime then
# unpacks to a temp dir instead; Velopack still finds $APPIMAGE and replaces that file on update, and the
# variable is inherited by the restarted launcher.
if ($IsLinux) { $env:APPIMAGE_EXTRACT_AND_RUN = '1' }

if (Test-Path -LiteralPath $Root) { Remove-Item -Recurse -Force -LiteralPath $Root }
New-Item -ItemType Directory -Force -Path $Root, $Data | Out-Null
Clear-VelopackState

try {
    Step "packing $v0 and installing it ..."
    New-Package $v0
    $launcher = Install-First
    Assert (Test-Path -LiteralPath $launcher) "installed launcher exists ($launcher)"

    # The self-test passes below are headless by design, so on their own they would never notice a launcher
    # whose WINDOW cannot come up (a missing native Skia/HarfBuzz library, a font that did not get embedded,
    # UI code lost to trimming/AOT). So render the real window of the INSTALLED build off-screen once — no
    # display needed — and keep the PNG: on CI it is also the only look anyone gets at the Windows and Linux UI.
    Step 'UI smoke: rendering the installed launcher off-screen ...'
    $shot = Join-Path $Root 'launcher.png'
    $ui = Start-Native $launcher @('--launcher-fake=available', "--launcher-shot=$shot", "--launcher-data=$(Join-Path $Root 'data-ui')")
    if (-not $ui.WaitForExit(120000)) {
        $ui.Kill($true)
        Assert $false 'the launcher window rendered within two minutes'
    }
    else {
        Assert ($ui.ExitCode -eq 0) "the installed launcher built and rendered its window (exit code $($ui.ExitCode))"
        Assert ((Test-Path -LiteralPath $shot) -and (Get-Item -LiteralPath $shot).Length -gt 50000) 'the rendered window is a real image (launcher.png > 50 KB)'
    }

    # ...and once more with the REAL flow behind the window. The fake view above has no flow and the self-test
    # passes below have no window, so nothing else runs the path a player takes on every quit: window closing
    # -> flow -> host exit -> lifetime shutdown. v0.0.13 shipped recursing there until the stack ran out (a
    # crash dialog on every exit) with this whole script green. The shot mode ends in that same shutdown.
    Step 'UI smoke: the real flow behind the window shuts down cleanly ...'
    $uiFlow = Start-Native $launcher @("--launcher-feed=$Feed", '--launcher-no-autolaunch', "--launcher-shot=$(Join-Path $Root 'launcher-flow.png')", "--launcher-data=$(Join-Path $Root 'data-ui-flow')")
    if (-not $uiFlow.WaitForExit(120000)) {
        $uiFlow.Kill($true)
        Assert $false 'the launcher with a real flow exited within two minutes'
    }
    else {
        Assert ($uiFlow.ExitCode -eq 0) "the launcher with a real flow shut down cleanly (exit code $($uiFlow.ExitCode))"
    }

    Step "packing $v1 ..."
    New-Package $v1

    # Flags before the bare `--` and everything after it must reach the game verbatim and in order.
    $gameArgs = @("--stub-report=$Report", '--host', '127.0.0.1:8090', '--', '--ui-x')
    $common = @("--launcher-feed=$Feed", "--launcher-data=$Data")

    Step "pass 1: selftest=update ($v0 → $v1) ..."
    $m = Invoke-Launcher $launcher (@('--launcher-selftest=update') + $common + $gameArgs) -Until '^GameExited code=0' -Skip 0
    $m | ForEach-Object { Write-Host "[e2e]     $_" }
    Assert ([bool]($m -match "^UpdateAvailable version=$r1 ")) "the feed offered $v1"
    Assert ([bool]($m -match "^UpdatedJustNow version=$r1 from=$r0 notes=1")) "restarted as $v1 and recognised the finished update (with its release notes)"
    Assert ([bool]($m -match '^GameExited code=0 kind=Quit')) 'the game ran after the update and quit cleanly'
    $installed = Get-InstalledVersion
    if ($null -ne $installed) { Assert ($installed -eq $v1) "installed manifest says $v1 (was $installed)" }
    $runs = @(Get-Content -LiteralPath $Report -ErrorAction SilentlyContinue)
    Assert ($runs.Count -eq 1) "the stub game ran exactly once (ran $($runs.Count)x)"
    Assert ([bool]($runs[-1] -match 'SA_LAUNCHER=1 ')) 'the game saw SA_LAUNCHER=1'
    Assert ([bool]($runs[-1] -match 'SA_LAUNCHER_UPDATE=none ')) 'the game was told no further update exists'
    Assert ([bool]($runs[-1] -match [regex]::Escape('|--host|127.0.0.1:8090|--|--ui-x]'))) 'game args passed through verbatim, in order, including the bare --'
    Assert (-not ($runs[-1] -match '--launcher-')) 'no --launcher-* flag leaked into the game'

    Step "packing $v2 ..."
    New-Package $v2

    Step "pass 2: selftest=play, the game asks for the update with exit code 85 ($v1 → $v2) ..."
    $skip = (Get-Markers).Count
    $m = Invoke-Launcher $launcher (@('--launcher-selftest=play') + $common + @('--stub-exit=85') + $gameArgs) -Until '^GameExited code=0' -Skip $skip
    $m | ForEach-Object { Write-Host "[e2e]     $_" }
    Assert ([bool]($m -match "^UpdateAvailable version=$r2 delta=True")) 'the second update is a DELTA (the first one seeded the package cache)'
    Assert ([bool]($m -match '^GameExited code=85 kind=UpdateRequested')) 'the game exited 85 and it was read as "update requested"'
    Assert ([bool]($m -match "^UpdateRequestedByGame version=$r2(\s|$)")) 'the launcher started the update the game asked for'
    Assert ([bool]($m -match "^UpdatedJustNow version=$r2 ")) "restarted as $v2"
    Assert ([bool]($m -match '^GameExited code=0 kind=Quit')) 'went straight back into the game after the update'
    $installed = Get-InstalledVersion
    if ($null -ne $installed) { Assert ($installed -eq $v2) "installed manifest says $v2 (was $installed)" }
    $runs = @(Get-Content -LiteralPath $Report -ErrorAction SilentlyContinue)
    Assert ($runs.Count -eq 3) "the stub game ran three times in total (ran $($runs.Count)x)"
    Assert ([bool]($runs[1] -match "SA_LAUNCHER_UPDATE=$r2 ")) "before the update the game was told $v2 is available"
    Assert ([bool]($runs[2] -match 'SA_LAUNCHER_UPDATE=none ')) 'after the update the game was told it is current'
}
finally {
    Save-VelopackLog
    Clear-VelopackState
    if (-not $KeepFiles -and $Failures.Count -eq 0 -and (Test-Path -LiteralPath $Root)) { Remove-Item -Recurse -Force -LiteralPath $Root -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($Failures.Count -gt 0) {
    Write-Host "[e2e] FAILED ($($Failures.Count)):"
    $Failures | ForEach-Object { Write-Host "[e2e]   - $_" }
    Write-Host "[e2e] files kept for inspection: $Root  (launcher log: $Log)"
    exit 1
}
Write-Host '[e2e] ALL CHECKS PASSED'
exit 0
