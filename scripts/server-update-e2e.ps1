#!/usr/bin/env pwsh
#Requires -Version 7.3
#
# server-update-e2e.ps1 — prove the release image's self-update cycle end to end, against the SERVER
# AUTO-UPDATE CONTRACT (env SIM_AUTO_UPDATE/SIM_UPDATE_*, the `update-state <Name>` log lines,
# GET /version, exit code 85 — see the plan this script was written from): pack v1, boot it, hold a
# player, publish v2 to the feed, prove the update is DEFERRED while occupied, release the player and
# watch it apply + relaunch INSIDE the same container, publish v3 (a delta this time), prove a plain
# `docker restart` does not re-unpack, prove `docker stop` is prompt and graceful, and prove
# SIM_AUTO_UPDATE=warn only logs. Beside it runs a HAND-RUN server - the bare AppImage, FUSE-mounted, no
# supervisor (SIM_UPDATE_RESTART=relaunch) - which must follow the same two releases by starting the new
# build itself, and come back in its own working directory with the old mount released (-SkipHandRun
# where containers get no /dev/fuse). Mirrors scripts/launcher-e2e.ps1's shape (Assert + $Failures, a
# scratch dir under build/, deadline-polling helpers, try/finally cleanup, ALL CHECKS PASSED) for the
# container update path instead of the desktop launcher.
#
# The contract it asserts on is the server's own (server/Update/*, docs/adr/0005): the `update-state …`
# lines are declared in server/Logging/Log.Update.cs, which calls them a contract for exactly this
# reason - reword one there and this script has to follow. The server runs UNLISTED here (no lobby),
# so what rings its doorbell is the safety-net tick, shortened to 30 s; the public lobby's Release
# Advert path is covered by tests/PublicLobbyTest + tests/ServerUpdateTest instead.
#
# Needs Docker, zstd and mksquashfs (macOS: `brew install zstd squashfs`). One NativeAOT publish inside
# Docker (minutes) + three packs + the update cycle: allow ~15 minutes.
#
#   scripts/server-update-e2e.ps1                # full run
#   scripts/server-update-e2e.ps1 -Fast           # skip the restart / stop / warn-mode checks (7-9)
#   scripts/server-update-e2e.ps1 -KeepFiles      # leave build/server-update-e2e + any failed container for inspection
#   scripts/server-update-e2e.ps1 -SkipHandRun    # no hand-run (FUSE) server: for a Docker that cannot give a container /dev/fuse
param(
    [switch]$KeepFiles, # leave build/server-update-e2e (and a failed run's containers/image) in place afterwards
    [switch]$Fast, # skip steps 7-9 (docker restart / docker stop / SIM_AUTO_UPDATE=warn)
    [switch]$SkipHandRun, # skip the hand-run AppImage (steps 2b / 6b); it needs --device /dev/fuse + SYS_ADMIN
    # The three versions to pack, oldest first. v2/v3 are packed into the SAME output dir as v1 so
    # vpk produces deltas for them (see Publish-ToFeed below for why a SNAPSHOT per version, not the
    # live output dir, is what actually gets copied to the feed at each publish step).
    [string[]]$Versions = @('1.0.0', '1.0.1', '1.0.2')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'docker is required' }

# Tests pack under their own id so their package cache / feed never mixes with a real release.
$PackId = 'StellarAllegianceServerE2E'
$PackTitle = 'Stellar Allegiance Server E2E'
$ImageTag = 'stellarallegiance-sim-e2e:local'
$Root = Join-Path $RepoRoot 'build/server-update-e2e'
$PackOut = Join-Path $Root 'packages' # vpk's -OutputDir: v1, v2, v3 all pack in here (cumulative)
$Feed = Join-Path $Root 'feed' # what's mounted into the container as SIM_UPDATE_FEED
$ImageCtx = Join-Path $Root 'image-ctx' # server/Dockerfile.release's staged build context
$Container = 'sa-server-e2e'
$WarnContainer = 'sa-server-e2e-warn'
$HandRunContainer = 'sa-server-e2e-handrun'
$HandRunDir = '/srv/work' # where the hand-run server is started - and must still be after every relaunch
$bot = $null

$Failures = [System.Collections.Generic.List[string]]::new()
if ($Versions.Count -ne 3) { throw '-Versions takes exactly three versions, oldest first' }
$v1, $v2, $v3 = $Versions

function Step([string]$Message) { Write-Host "[e2e] $Message" }

function Assert([bool]$Condition, [string]$What) {
    if ($Condition) { Write-Host "[e2e]   PASS  $What" }
    else { Write-Host "[e2e]   FAIL  $What"; $Failures.Add($What) }
}

# Best-effort: swallows any failure (native or otherwise) — for cleanup calls where "it was already
# gone" is a success, not an error (docker rm -f on a container that never got created, etc).
function Invoke-Quiet([scriptblock]$Action) {
    $prev = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    try { & $Action *> $null } catch { }
    finally { $PSNativeCommandUseErrorActionPreference = $prev }
}

# `update-state <Name> version=<v>` up to the next whitespace or end of line — some of the contract's
# lines have fields after the version (Boot's installed=/mode=, Ready's delta=), others end right
# there (Available, Downloading, Applying, Applied) — this matches either without needing a second
# pattern per line shape. $After overrides the tail for a line that must show a specific value there.
function StatePattern([string]$Name, [string]$Version, [string]$After = '(\s|$)') {
    'update-state ' + $Name + ' version=' + [regex]::Escape($Version) + $After
}

function Get-ContainerLog([string]$Name) { @(docker logs $Name 2>&1) }

# Polls until $Pattern matches a line in $Name's log, or fails the assertion at the deadline.
function Wait-LogMatch([string]$Name, [string]$Pattern, [int]$TimeoutSeconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $log = Get-ContainerLog $Name
        if ($log -match $Pattern) { Assert $true $What; return $log }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    Assert $false "$What (within ${TimeoutSeconds}s)"
    return (Get-ContainerLog $Name)
}

# The inverse: holds for $HoldSeconds proving $Pattern never appears. Used to prove an update stays
# deferred/held rather than sneaking through — a plain "assert false once" would miss a late arrival.
function Assert-NoLogMatch([string]$Name, [string]$Pattern, [int]$HoldSeconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($HoldSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-ContainerLog $Name) -match $Pattern) { Assert $false $What; return }
        Start-Sleep -Milliseconds 1000
    }
    Assert $true $What
}

# Polls $Read until it returns $Expected or the deadline passes; returns the last value either way.
function Wait-Value([scriptblock]$Read, [string]$Expected, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $value = & $Read
        if ($value -eq $Expected) { return $value }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $value
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function Wait-Health([int]$Port, [int]$TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-WebRequest -Uri "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) { return $resp.Content.Trim() }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-RunningVersion([int]$Port) {
    try { return (Invoke-WebRequest -Uri "http://localhost:$Port/version" -UseBasicParsing -TimeoutSec 3).Content.Trim() }
    catch { return $null }
}

# Velopack's own updater log lives INSIDE the container (Program.cs configures it there, not under
# /data) — usually the only witness when something goes wrong mid-download/apply, so on a failure
# it's worth pulling out before the container is removed.
function Save-VelopackLog([string]$Name) {
    Invoke-Quiet { docker cp "${Name}:/tmp/velopack_$PackId.log" (Join-Path $Root "velopack_$Name.log") }
}

# "Publishing" a version to the feed = copying its files + the releases/assets json AS THEY STOOD
# right after packing THAT version. vpk regenerates releases.<channel>.json from everything sitting
# in -OutputDir at pack time, and v1/v2/v3 are all packed into the SAME $PackOut (so v2/v3 get
# deltas) — so by the time v3 has been packed, $PackOut's own releases.json already lists all three.
# A snapshot taken right after each individual pack call is what lets this script reveal v2 (and
# later v3) to the running container on its own schedule instead of all at once; copying a whole
# snapshot into $Feed is safe to repeat because each later snapshot is a strict superset of the
# earlier ones (same cumulative $PackOut, captured later).
function Publish-ToFeed([string]$SnapshotDir) {
    Copy-Item -Path (Join-Path $SnapshotDir '*') -Destination $Feed -Recurse -Force
}

# ONE NativeAOT publish serves all three versions: a packaged server reads its version from the package
# manifest (sq.version, written by vpk at pack time), not from the binary, so re-packing the same tree
# under a higher --packVersion is a legitimate "new release" for everything this test is about - the
# update cycle. It also keeps the run to one native compile (minutes) instead of three.
function Invoke-Pack([string]$Version, [string]$ImgCtx, [string]$PublishedTree) {
    # A HASHTABLE splat, not an array: package-server.ps1 is a .ps1 with a declared param() block, and
    # splatting an ARRAY into that binds every element POSITIONALLY (PowerShell does not re-parse
    # '-Name'-shaped array elements as parameter names for a cmdlet/script target — only for a NATIVE
    # command, whose own CLI parser does that; that's why `dotnet @pack` elsewhere in this repo is fine
    # but this needs a hashtable). Confirmed by hand: array-splatting this call bound $Version to the
    # literal string "-Version" and then failed binding '-Ci' at all.
    $packArgs = @{ Version = $Version; PackId = $PackId; PackTitle = $PackTitle; OutputDir = $PackOut; Ci = $true }
    if ($ImgCtx) { $packArgs.ImageContext = $ImgCtx }
    if ($PublishedTree) { $packArgs.PublishDir = $PublishedTree }
    & (Join-Path $RepoRoot 'scripts/package-server.ps1') @packArgs | Out-Host
    # package-server.ps1 is a .ps1 invoked by path (not a native exe), so the error-action preference
    # above does not apply to its own `exit N` — check $LASTEXITCODE explicitly instead (same pattern
    # scripts/launcher-e2e.ps1 uses around scripts/package-clients.ps1).
    if ($LASTEXITCODE -ne 0) { throw "packaging $Version failed (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------------------------------------------
if (Test-Path -LiteralPath $Root) { Remove-Item -Recurse -Force -LiteralPath $Root }
New-Item -ItemType Directory -Force -Path $PackOut, $Feed | Out-Null

try {
    # ---- 1. pack v1 (+ stage the image context), build the image, seed the feed with v1 only,
    #        then pack v2 and v3 into the SAME output dir WITHOUT publishing them yet ---------------
    Step "packing $v1 and staging the release-image context ..."
    Invoke-Pack $v1 $ImageCtx $null
    # Where package-server.ps1 left the tree it just published (build/package/<channel>/publish).
    $dockerArch = (docker version --format '{{.Server.Arch}}').Trim()
    $PublishedTree = Join-Path $RepoRoot "build/package/server-linux-$($dockerArch -eq 'amd64' ? 'x64' : $dockerArch)/publish"
    if (-not (Test-Path -LiteralPath (Join-Path $PublishedTree 'SimServer'))) { throw "no published tree at $PublishedTree" }
    $SnapV1 = Join-Path $Root 'snapshot-v1'
    Copy-Item -Path $PackOut -Destination $SnapV1 -Recurse
    Publish-ToFeed $SnapV1

    Step "building the release image ($ImageTag) from the staged context ..."
    docker buildx build --load -f (Join-Path $RepoRoot 'server/Dockerfile.release') -t $ImageTag $ImageCtx | Out-Host

    Step "packing $v2 (delta vs $v1) — not published to the feed yet ..."
    Invoke-Pack $v2 $null $PublishedTree
    $SnapV2 = Join-Path $Root 'snapshot-v2'
    Copy-Item -Path $PackOut -Destination $SnapV2 -Recurse

    Step "packing $v3 (delta vs $v2) — not published to the feed yet ..."
    Invoke-Pack $v3 $null $PublishedTree
    $SnapV3 = Join-Path $Root 'snapshot-v3'
    Copy-Item -Path $PackOut -Destination $SnapV3 -Recurse

    # ---- 2. boot v1 ----------------------------------------------------------------------------
    $Port = Get-FreePort
    Step "starting the container on port $Port (feed currently offers only $v1) ..."
    Invoke-Quiet { docker rm -f $Container }
    docker run -d --name $Container -p "${Port}:8090" -v "${Feed}:/feed:ro" `
        -e SIM_UPDATE_FEED=/feed -e SIM_UPDATE_INTERVAL_SECONDS=30 -e SIM_UPDATE_IDLE_SECONDS=10 `
        -e SIM_AUTO_UPDATE=on $ImageTag | Out-Host

    $health = Wait-Health $Port 60
    Assert ($health -eq 'wivuu-sim') "GET /health = wivuu-sim (was '$health')"
    Wait-LogMatch $Container (StatePattern 'Boot' $v1) 30 "update-state Boot version=$v1" | Out-Null
    Assert ((Get-RunningVersion $Port) -eq $v1) "GET /version = $v1"

    # ---- 2b. a second server, HAND-RUN: the bare AppImage, no supervisor -----------------------------
    # The release image only ever exercises SIM_UPDATE_RESTART=exit (its entrypoint relaunches). A server
    # somebody starts by hand has nobody to relaunch it: after the swap it starts the new build itself
    # (server/Update/Relauncher.cs). That path goes through the AppImage runtime and FUSE for real, and
    # the first release rehearsal - then still on Velopack's `UpdateNix start` - found two silent faults
    # in it that nothing else here could see: the new build ran with its working directory inside the OLD
    # version's mount (relative paths in the operator's arguments pointed somewhere else after an update),
    # and every relaunch left the previous version mounted, replaced AppImage and all. It idles through
    # both publishes below and is checked at 6b. Same image, entrypoint bypassed: the AppImage is already
    # in there, next to every native dependency. --init gives it a PID 1 that outlives the server; the
    # server's direct parent (`sleep`) never collects it, so every relaunch also has to see through a ZOMBIE.
    if (-not $SkipHandRun) {
        $HandRunPort = Get-FreePort
        Step "starting a HAND-RUN server (bare AppImage over FUSE, restart=relaunch) on port $HandRunPort ..."
        Invoke-Quiet { docker rm -f $HandRunContainer }
        $handRun = "mkdir -p $HandRunDir && cd $HandRunDir && /opt/stellar/StellarAllegianceServer.AppImage --port 8090 & exec sleep infinity"
        docker run -d --init --name $HandRunContainer -p "${HandRunPort}:8090" -v "${Feed}:/feed:ro" `
            --device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor=unconfined `
            -e SIM_UPDATE_FEED=/feed -e SIM_UPDATE_INTERVAL_SECONDS=30 -e SIM_UPDATE_IDLE_SECONDS=10 `
            -e SIM_AUTO_UPDATE=on -e SIM_UPDATE_RESTART=relaunch `
            --entrypoint /bin/sh $ImageTag -c $handRun | Out-Host
        Wait-LogMatch $HandRunContainer (StatePattern 'Boot' $v1 '.* restart=relaunch') 60 "hand-run: update-state Boot version=$v1 ... restart=relaunch" | Out-Null
    }

    # ---- 3. hold a player -----------------------------------------------------------------------
    Step 'holding a player with tools/simbot ...'
    $botLog = Join-Path $Root 'simbot.log'
    $bot = Start-Process -FilePath 'dotnet' -ArgumentList @(
        'run', '--project', (Join-Path $RepoRoot 'tools/simbot'), '-c', 'Release', '--',
        '--bots', '1', '--url', "ws://localhost:$Port/game", '--seconds', '600'
    ) -RedirectStandardOutput $botLog -RedirectStandardError (Join-Path $Root 'simbot.err.log') -PassThru -NoNewWindow

    $botConnected = $false
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path -LiteralPath $botLog) -and ((Get-Content -LiteralPath $botLog -Raw) -match 'connected=1/1')) { $botConnected = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert $botConnected 'simbot reported one connected player'

    # ---- 4. publish v2 — must be DEFERRED while the bot holds the server occupied -----------------
    Step "publishing $v2 to the feed ..."
    Publish-ToFeed $SnapV2
    # Budgets below: the unlisted server only LOOKS every 30 s (SIM_UPDATE_INTERVAL_SECONDS, plus the
    # coordinator's 30 s minimum spacing between checks), then waits out the 10 s idle window, then a
    # delta rebuild takes several seconds of its own - a slow CI runner needs the slack.
    Wait-LogMatch $Container (StatePattern 'Available' $v2) 120 "update-state Available version=$v2" | Out-Null
    Wait-LogMatch $Container 'update-state Deferred players=' 30 'update-state Deferred (a player is connected)' | Out-Null
    Assert-NoLogMatch $Container 'update-state (Downloading|Applying)' 45 'no Downloading/Applying while the bot stays connected (~45s)'

    # ---- 5. release the player — the update applies and relaunches INSIDE the same container ------
    Step 'stopping simbot so the server goes idle ...'
    if ($bot -and -not $bot.HasExited) { $bot.Kill($true) } # kill the whole tree: `dotnet run` spawns the real app as a child
    $containerIdBefore = (docker inspect -f '{{.Id}}' $Container).Trim()
    Wait-LogMatch $Container (StatePattern 'Applying' $v2) 120 "update-state Applying version=$v2" | Out-Null

    $skip = (Get-ContainerLog $Container).Count # only count a Boot line that arrives AFTER Applying — v1's own Boot would also match a bare version-less search otherwise
    $bootAgain = $false
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ((@(Get-ContainerLog $Container | Select-Object -Skip $skip)) -match (StatePattern 'Boot' $v2)) { $bootAgain = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert $bootAgain "a second update-state Boot version=$v2 appeared after the update"
    Assert ((Get-RunningVersion $Port) -eq $v2) "GET /version = $v2 after the update"
    $containerIdAfter = (docker inspect -f '{{.Id}}' $Container).Trim()
    Assert ($containerIdAfter -eq $containerIdBefore) 'still the same container id — the relaunch happened INSIDE the container'
    $restarts = (docker inspect -f '{{.RestartCount}}' $Container).Trim()
    Assert ($restarts -eq '0') "docker RestartCount is 0 (was $restarts) — Docker itself never restarted the container"

    # ---- 6. publish v3 (a delta this time) ---------------------------------------------------------
    Step "publishing $v3 to the feed ..."
    Publish-ToFeed $SnapV3
    Wait-LogMatch $Container (StatePattern 'Ready' $v3 ' delta=True') 180 "update-state Ready version=$v3 delta=True" | Out-Null
    Wait-LogMatch $Container (StatePattern 'Boot' $v3) 120 "update-state Boot version=$v3" | Out-Null
    Assert ((Get-RunningVersion $Port) -eq $v3) "GET /version = $v3"

    # ---- 6b. the hand-run server followed both releases by RELAUNCHING itself ------------------------
    if (-not $SkipHandRun) {
        Step 'hand-run server: two relaunches of its own, same working directory, no stale mount ...'
        Wait-LogMatch $HandRunContainer (StatePattern 'Boot' $v2) 60 "hand-run: relaunched as $v2" | Out-Null
        Wait-LogMatch $HandRunContainer (StatePattern 'Boot' $v3) 180 "hand-run: relaunched again as $v3 (a relaunched build can relaunch)" | Out-Null
        Wait-LogMatch $HandRunContainer (StatePattern 'Confirmed' $v3) 30 "hand-run: update-state Confirmed version=$v3" | Out-Null
        $handRunVersion = Wait-Value { Get-RunningVersion $HandRunPort } $v3 30
        Assert ($handRunVersion -eq $v3) "hand-run: GET /version = $v3 (was '$handRunVersion')"
        # Zombies (the old builds, never collected by `sleep`) keep their comm but have no cwd: skip them.
        $probe = 'for p in /proc/[0-9]*; do [ "$(cat $p/comm 2>/dev/null)" = SimServer ] && readlink $p/cwd; done; true'
        $cwd = (@(docker exec $HandRunContainer sh -c $probe) -join ',').Trim()
        Assert ($cwd -eq $HandRunDir) "hand-run: the running server's working directory is still $HandRunDir (was '$cwd')"
        # The old mount goes away as soon as the old build has exited - nothing else may hold on to it.
        $mounts = Wait-Value { (docker exec $HandRunContainer sh -c 'grep -c /tmp/.mount_ /proc/mounts; true').Trim() } '1' 20
        Assert ($mounts -eq '1') "hand-run: exactly one AppImage mount is left - the old versions' were released (found $mounts)"
    }

    if ($Fast) {
        Step '-Fast: skipping steps 7-9 (docker restart / docker stop / SIM_AUTO_UPDATE=warn)'
    }
    else {
        # ---- 7. a plain `docker restart` must NOT re-unpack --------------------------------------
        Step 'docker restart (must not re-unpack) ...'
        $unpacksBefore = @(Get-ContainerLog $Container | Where-Object { $_ -match 'unpacking' }).Count
        docker restart $Container | Out-Host
        $health = Wait-Health $Port 60
        Assert ($health -eq 'wivuu-sim') 'health answers again after docker restart'
        Wait-LogMatch $Container (StatePattern 'Boot' $v3) 60 "boots $v3 again after a plain restart" | Out-Null
        $unpacksAfter = @(Get-ContainerLog $Container | Where-Object { $_ -match 'unpacking' }).Count
        Assert ($unpacksAfter -eq $unpacksBefore) "unpack log-line count unchanged by the restart ($unpacksBefore -> $unpacksAfter)"

        # ---- 8. `docker stop` must be prompt and graceful -----------------------------------------
        Step 'docker stop (must be prompt and graceful) ...'
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        docker stop --time 30 $Container | Out-Host
        $sw.Stop()
        Assert ($sw.Elapsed.TotalSeconds -lt 15) "docker stop returned within 15s (took $([math]::Round($sw.Elapsed.TotalSeconds, 1))s)"
        $exitCode = (docker inspect -f '{{.State.ExitCode}}' $Container).Trim()
        Assert ($exitCode -eq '0') "container exit code 0 (was $exitCode)"
        Assert ([bool]((Get-ContainerLog $Container) -match 'Application is shutting down')) 'log shows the host shutting down gracefully'

        # ---- 9. a SEPARATE container with SIM_AUTO_UPDATE=warn only logs, never applies -----------
        Step 'second container: SIM_AUTO_UPDATE=warn against the (now v3) feed — boots v1, should only warn ...'
        $WarnPort = Get-FreePort
        Invoke-Quiet { docker rm -f $WarnContainer }
        docker run -d --name $WarnContainer -p "${WarnPort}:8090" -v "${Feed}:/feed:ro" `
            -e SIM_UPDATE_FEED=/feed -e SIM_UPDATE_INTERVAL_SECONDS=30 -e SIM_UPDATE_IDLE_SECONDS=10 `
            -e SIM_AUTO_UPDATE=warn $ImageTag | Out-Host
        $health = Wait-Health $WarnPort 60
        Assert ($health -eq 'wivuu-sim') 'second container answers /health'
        Wait-LogMatch $WarnContainer 'update-state Warn version=' 150 'update-state Warn appears' | Out-Null
        Assert-NoLogMatch $WarnContainer 'update-state Applying' 60 'never applies with SIM_AUTO_UPDATE=warn (60s)'
    }
}
catch {
    Write-Host "[e2e] EXCEPTION: $_"
    $Failures.Add("unhandled exception: $_")
}
finally {
    if ($bot -and -not $bot.HasExited) { Invoke-Quiet { $bot.Kill($true) } }
    if ($Failures.Count -gt 0) {
        Save-VelopackLog $Container
        Save-VelopackLog $WarnContainer
        Save-VelopackLog $HandRunContainer
        Invoke-Quiet { docker logs $HandRunContainer *> (Join-Path $Root 'handrun.log') }
    }
    Invoke-Quiet { docker rm -f $Container }
    Invoke-Quiet { docker rm -f $WarnContainer }
    Invoke-Quiet { docker rm -f $HandRunContainer }
    if (-not $KeepFiles -and $Failures.Count -eq 0) {
        Invoke-Quiet { docker rmi -f $ImageTag }
        Remove-Item -Recurse -Force -LiteralPath $Root -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($Failures.Count -gt 0) {
    Write-Host "[e2e] FAILED ($($Failures.Count)):"
    $Failures | ForEach-Object { Write-Host "[e2e]   - $_" }
    Write-Host "[e2e] files kept for inspection: $Root"
    exit 1
}
Write-Host '[e2e] ALL CHECKS PASSED'
exit 0
