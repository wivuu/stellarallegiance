#!/usr/bin/env pwsh
#Requires -Version 7.3
#
# package-server.ps1 — build one Velopack package for the native sim server: a single self-updating
# .AppImage (see server/Update/*, server/docker-entrypoint.sh, docs on the auto-update contract). The
# single packaging entry point for CI (.github/workflows/release.yml, server-update-dryrun.yml) AND
# for local runs, so the two can never drift apart.
#
#   scripts/package-server.ps1 -Version 1.0.0                                       # AppImage for the Docker daemon's own arch
#   scripts/package-server.ps1 -Version 1.0.0 -Arch arm64                           # explicit arch (must match the daemon's — NativeAOT cannot cross-compile)
#   scripts/package-server.ps1 -Version 1.0.1 -OutputDir build/releases/server-linux-arm64   # pack again into the same folder -> a delta comes out too
#   scripts/package-server.ps1 -Version 1.0.0 -PublishDir build/package/server-linux-arm64/publish   # already-published tree; skips the Docker publish
#   scripts/package-server.ps1 -Version 1.0.0 -ImageContext build/server-image       # also stage server/Dockerfile.release's build context (see -ImageContext below)
#
# Runs on any host with Docker (macOS included): the actual `dotnet publish` happens INSIDE the SDK
# image (server/Dockerfile's `publish-output` stage, lifted onto the host with
# `docker buildx build --output type=local`), never on the host directly. That keeps ONE publish path
# for CI and a Mac dev box, and pins the glibc floor to the SDK image instead of whatever the runner
# or dev machine happens to have — only vpk (packing the AppImage around that published tree) runs on
# the host. NativeAOT itself still cannot cross-compile, so the produced AppImage always matches the
# Docker DAEMON's architecture (see -Arch below), regardless of what OS is driving this script.
#
# Output (-OutputDir, default build/releases/server-linux-<arch>): <id>-<ver>-server-linux-<arch>-full.nupkg,
# a -delta.nupkg once a previous full package is already in that folder, <id>-server-linux-<arch>.AppImage,
# releases.server-linux-<arch>.json, assets.server-linux-<arch>.json.
#
# Channels are server-linux-x64 / server-linux-arm64 — NEVER the client's `linux` channel and NEVER
# shared between the two server arches — because a Velopack feed is keyed by CHANNEL alone (not
# filtered by package id or runtime): reusing `linux` would merge the game client's releases.json
# with the server's, and sharing one channel between arches would offer an arm64 delta to an x64
# install (and vice versa).
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$OutputDir,
    [string]$ReleaseNotes,
    # Tests (scripts/server-update-e2e.ps1) pack under a different id so their package cache / feed
    # never mixes with a real release.
    [string]$PackId = 'StellarAllegianceServer',
    [string]$PackTitle = 'Stellar Allegiance Server',
    # CI mode: never answer vpk's "this version already exists — overwrite?" prompt with yes.
    [switch]$Ci,
    # Must match the Docker daemon's own arch (NativeAOT cannot cross-compile); default = the
    # daemon's arch, amd64 mapped to the Velopack/.NET RID spelling x64.
    [string]$Arch,
    # A pre-published server tree (the layout server/Dockerfile's publish-output stage produces:
    # SimServer + assets/ + sim-cache/ at its root) — skips the Docker publish entirely. Lets a
    # caller pack several versions from ONE publish (Velopack's runtime reads its version from
    # sq.version, which vpk writes at pack time — not from the binary itself — so the same publish
    # can legitimately be packed under different --packVersion values); scripts/server-update-e2e.ps1
    # uses this for its v2/v3 packs so only the first version pays for a NativeAOT publish.
    [string]$PublishDir,
    # Also stage server/Dockerfile.release's build context at this directory: the AppImage under
    # <ctx>/<amd64|arm64>/StellarAllegianceServer.AppImage (Docker's arch names, not Velopack's) plus
    # <ctx>/docker-entrypoint.sh. Matches what release.yml's `server-image` job assembles from two
    # per-arch CI artifacts, and what scripts/server-update-e2e.ps1 builds from directly.
    [string]$ImageContext
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path $PSScriptRoot -Parent

function Step([string]$Message) { Write-Host "[package-server] $Message" }

function Fail([string]$Message) {
    [Console]::Error.WriteLine("[package-server] ERROR: $Message")
    exit 1
}

# Runs a program to completion with its output going STRAIGHT to the console (see
# scripts/package-clients.ps1 for the full rationale: un-piped native stdout leaks into a captured
# function's return value, and BuildKit's own progress UI wants a real inherited console, not
# PowerShell's pipeline). Used for `docker buildx build`, whose TTY progress output does not survive
# a round trip through PowerShell's object pipeline intact.
function Invoke-Program([string]$Exe, [string[]]$Arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    foreach ($a in $Arguments) { $info.ArgumentList.Add($a) }
    $process = [System.Diagnostics.Process]::Start($info)
    $process.WaitForExit()
    return $process.ExitCode
}

# ---------------------------------------------------------------------------------------------------
Set-Location $RepoRoot
if ($IsWindows) { Fail 'run this from macOS or Linux — the sim server only ever publishes linux-*, and vpk needs mksquashfs' }
if ($ReleaseNotes -and -not (Test-Path -LiteralPath $ReleaseNotes)) { Fail "release notes file not found: $ReleaseNotes" }
if ($PublishDir -and -not (Test-Path -LiteralPath (Join-Path $PublishDir 'SimServer'))) { Fail "no SimServer binary at $PublishDir" }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail 'docker is required' }
if (-not (Get-Command mksquashfs -ErrorAction SilentlyContinue)) { Fail 'mksquashfs is required (macOS: brew install squashfs; Linux: apt-get install squashfs-tools)' }
# Without a system zstd, vpk silently falls back to bsdiff deltas — which the 1.2.0 updater REJECTS
# ("Unsupported patch format"). Every update would then quietly be a full download.
if (-not (Get-Command zstd -ErrorAction SilentlyContinue)) { Fail 'zstd is required (brew install zstd / apt-get install zstd)' }

# NativeAOT cannot cross-compile, so the produced binary always matches whatever arch the Docker
# DAEMON actually builds on (buildx picks that by default with no --platform override) — never the
# host script's own arch, which matters on e.g. Rosetta-emulated shells. amd64/arm64 are Docker's
# names for that arch; x64/arm64 are the Velopack/.NET RID spelling used everywhere else below.
$dockerArch = (docker version --format '{{.Server.Arch}}').Trim()
$daemonArch = switch ($dockerArch) {
    'amd64' { 'x64' }
    'arm64' { 'arm64' }
    default { Fail "unrecognised Docker daemon arch '$dockerArch' (expected amd64 or arm64)" }
}
if ($Arch -and $Arch -notin 'x64', 'arm64') { Fail "-Arch must be x64 or arm64 (was '$Arch')" }
if ($Arch -and $Arch -ne $daemonArch) { Fail "-Arch $Arch was requested but the Docker daemon builds $daemonArch ($dockerArch) — NativeAOT cannot cross-compile" }
if (-not $Arch) { $Arch = $daemonArch }

$Channel = "server-linux-$Arch"
$Rid = "linux-$Arch"
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "build/releases/$Channel" }
$OutputDir = [System.IO.Path]::GetFullPath((New-Item -ItemType Directory -Force -Path $OutputDir).FullName)

Step "version $Version . channel $Channel . packId $PackId"
dotnet tool restore | Out-Host

# ---- 1. publish ---------------------------------------------------------------------------------
function Publish-Server([string]$OutDir) {
    Step 'publishing inside the SDK image (docker buildx --target publish-output) ...'
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    # An absolute context path (not '.') so this call never depends on the shell's current
    # directory matching $RepoRoot at the moment it runs.
    $buildArgs = @(
        'buildx', 'build', '--target', 'publish-output',
        '--build-arg', "VERSION=$Version",
        '--output', "type=local,dest=$OutDir",
        '-f', (Join-Path $RepoRoot 'server/Dockerfile'),
        $RepoRoot
    )
    $exit = Invoke-Program 'docker' $buildArgs
    if ($exit -ne 0) { Fail "docker buildx build exited $exit" }
    chmod +x (Join-Path $OutDir 'SimServer')
}

if ($PublishDir) {
    $Publish = (Resolve-Path -LiteralPath $PublishDir).Path
    Step "using the pre-published tree at $Publish"
    chmod +x (Join-Path $Publish 'SimServer')
}
else {
    $Work = Join-Path $RepoRoot "build/package/$Channel"
    if (Test-Path -LiteralPath $Work) { Remove-Item -Recurse -Force -LiteralPath $Work }
    $Publish = Join-Path $Work 'publish'
    Publish-Server $Publish
}

# ---- 2. vpk pack ----------------------------------------------------------------------------------
$hadPrevious = [bool](Get-ChildItem -LiteralPath $OutputDir -Filter '*-full.nupkg' -ErrorAction SilentlyContinue)

# The `[linux]` directive MUST be the very first token after 'vpk' — before even `-x` — or vpk
# silently ignores it and packs for the HOST os instead (verified: `vpk -x [linux] pack` and
# `vpk pack [linux]` both fall back to an osx-shaped package on a macOS host with no warning).
$pack = @(
    'vpk', '[linux]', 'pack', '-x',
    '--packId', $PackId,
    '--packVersion', $Version,
    '--packDir', $Publish,
    '--mainExe', 'SimServer',
    '--packTitle', $PackTitle,
    '--packAuthors', 'Wivuu',
    '--channel', $Channel,
    '--runtime', $Rid,
    '--outputDir', $OutputDir,
    '--categories', 'Game'
)
if (-not $Ci) { $pack += '-y' }
if ($ReleaseNotes) { $pack += @('--releaseNotes', (Resolve-Path -LiteralPath $ReleaseNotes).Path) }
$icon = Join-Path $RepoRoot 'launcher/App/Assets/icon-256.png'
if (Test-Path -LiteralPath $icon) { $pack += @('--icon', $icon) }

Step 'vpk pack ...'
dotnet @pack

# ---- 3. post-checks -------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$full = Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Version-*full.nupkg" | Select-Object -First 1
if (-not $full) { $full = Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Version-full.nupkg" | Select-Object -First 1 }
if (-not $full) { Fail "vpk produced no full package for $Version in $OutputDir" }
foreach ($required in "releases.$Channel.json", "assets.$Channel.json") {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDir $required))) { Fail "missing $required" }
}

$appImage = Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Channel.AppImage" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $appImage) { Fail "vpk produced no $PackId-$Channel.AppImage in $OutputDir" }
# Velopack diffs a delta byte-for-byte against the previous full package; above ~1 GiB that stops
# being practical and it falls back silently to shipping a full download every time.
$appImageGiB = $appImage.Length / 1GB
if ($appImageGiB -ge 1) { Fail "AppImage is $([math]::Round($appImageGiB, 2)) GiB — Velopack cannot delta a single file >= 1 GiB" }

$delta = Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Version-*delta.nupkg" | Select-Object -First 1
if ($hadPrevious -and -not $delta) { Fail 'a previous full package was present but no delta was produced' }
if ($delta) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($delta.FullName)
    try {
        $names = $zip.Entries.FullName
        if ($names | Where-Object { $_ -like '*.bsdiff' }) { Fail 'delta contains bsdiff patches — the updater cannot apply them (is zstd installed?)' }
        $zs = @($names | Where-Object { $_ -like '*.zsdiff' }).Count
        Step "delta ok: $([math]::Round($delta.Length / 1MB, 2)) MiB, $zs zstd patch(es)"
    }
    finally { $zip.Dispose() }
}

# ---- 4. -ImageContext staging ----------------------------------------------------------------------
if ($ImageContext) {
    # Docker's arch spelling (amd64/arm64), not Velopack/.NET's (x64/arm64) — server/Dockerfile.release
    # and its ${TARGETARCH} build-arg both key off Docker's names.
    $dockerArchDir = if ($Arch -eq 'x64') { 'amd64' } else { 'arm64' }
    $archDir = New-Item -ItemType Directory -Force -Path (Join-Path $ImageContext $dockerArchDir)
    # Renamed to the fixed name server/Dockerfile.release's COPY expects — $PackId varies (tests pack
    # under StellarAllegianceServerE2E), the staged filename must not.
    Copy-Item -LiteralPath $appImage.FullName -Destination (Join-Path $archDir.FullName 'StellarAllegianceServer.AppImage') -Force
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'server/docker-entrypoint.sh') -Destination (Join-Path $ImageContext 'docker-entrypoint.sh') -Force
    Step "staged release-image context at $ImageContext ($dockerArchDir/)"
}

# ---- 5. done ----------------------------------------------------------------------------------------
Step 'done:'
Get-ChildItem -LiteralPath $OutputDir -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,10:N1} MiB  {1}" -f ($_.Length / 1MB), $_.Name)
}
