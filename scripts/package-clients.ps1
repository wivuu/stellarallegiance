#!/usr/bin/env pwsh
#Requires -Version 7.3
#
# package-clients.ps1 — build the HOST OS's Velopack package: Game Launcher + game in one installable,
# self-updating bundle. The single packaging entry point for CI (.github/workflows/release.yml,
# package-dryrun.yml) AND for local runs, so the two can never drift apart.
#
#   scripts/package-clients.ps1 -Version 0.0.13                      # real Godot export + launcher
#   scripts/package-clients.ps1 -Version 1.0.0 -FakeGame             # stub game (seconds, not minutes)
#   scripts/package-clients.ps1 -Version 0.0.13 -GameDir build/mac/"Stellar Allegiance.app"
#
# Host-OS only, by necessity: macOS packages need codesign/pkgbuild, and the launcher is NativeAOT on
# Windows + macOS, which cannot cross-compile between OSes. CI runs this once per runner.
#
# What one package looks like (the launcher is Velopack's main exe everywhere; the Godot export ships
# UNTOUCHED — see launcher/Core/Game/GameLocator.cs, which resolves exactly these paths):
#
#   Windows  <stage>\StellarLauncher.exe + native dlls        <stage>\game\stellarallegiance.exe …
#   Linux    <stage>/StellarLauncher (self-contained JIT)     <stage>/game/stellarallegiance.x86_64 …
#   macOS    <stage>/Stellar Allegiance.app                   = the launcher (com.stellarallegiance.launcher)
#              Contents/MacOS/StellarLauncher + dylibs        (universal: lipo of osx-arm64 + osx-x64)
#              Contents/Helpers/Stellar Allegiance.app        = the pristine Godot export, nested
#
# Output (-OutputDir, default build/releases/<channel>): <id>-<ver>-<ch>-full.nupkg, a -delta.nupkg when
# a previous full package is already in that folder (CI fetches it with `vpk download github` first),
# the installer (Setup.exe / .pkg / .AppImage), Portable.zip, releases.<ch>.json, assets.<ch>.json.
#
# Pointing a launcher at that folder is a complete local update test:
#   StellarLauncher --launcher-feed=<OutputDir>      (see scripts/launcher-e2e.ps1)
#
# Signing is optional and driven by env so it can be switched on without touching this script:
#   MAC_APP_IDENTITY (default "-" = ad-hoc), MAC_INSTALL_IDENTITY, MAC_NOTARY_PROFILE,
#   WIN_SIGN_PARAMS or AZURE_TRUSTED_SIGN_FILE.
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$OutputDir,
    [string]$ReleaseNotes,
    # A pre-exported game: a folder holding the exe on Windows/Linux, the .app on macOS. Skips the Godot export.
    [string]$GameDir,
    # Use tools/launcher-stubgame instead of a Godot export (CI dry-run, launcher e2e).
    [switch]$FakeGame,
    # Tests pack under a different id so their package cache / logs never mix with a real install.
    [string]$PackId = 'StellarAllegiance',
    [string]$PackTitle = 'Stellar Allegiance',
    # macOS only: build the launcher for the host architecture only (halves the AOT time for local e2e).
    [switch]$HostArchOnly,
    # CI mode: never answer vpk's "this version already exists — overwrite?" prompt with yes.
    [switch]$Ci
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Client = Join-Path $RepoRoot 'client'
$LauncherProj = Join-Path $RepoRoot 'launcher/App/StellarLauncher.csproj'
$StubProj = Join-Path $RepoRoot 'tools/launcher-stubgame/StubGame.csproj'
$MacGameBundle = 'Stellar Allegiance.app' # must match GameLocator.MacGameBundle
$GameExeBase = 'stellarallegiance' # must match GameLocator.GameExeBase (= project.godot config/name)

$Channel = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "build/releases/$Channel" }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$Work = Join-Path $RepoRoot "build/package/$Channel"
$Stage = Join-Path $Work 'stage'

function Step([string]$Message) { Write-Host "[package] $Message" }

function Fail([string]$Message) {
    [Console]::Error.WriteLine("[package] ERROR: $Message")
    exit 1
}

# Runs a program to completion with its output going STRAIGHT to the console, and returns its exit code.
# Used for Godot instead of `& $exe … | Out-Host`, because:
#   - inside a function whose result is captured, un-piped native stdout leaks into the return value;
#   - piping makes PowerShell read the child's stdout until EOF, and a build server that Godot's own
#     `dotnet publish` leaves behind inherits that pipe and can hold it open long after Godot has exited;
#   - on Windows Godot is a GUI-subsystem exe, which PowerShell only waits for when its output is redirected.
# A plain Process with inherited handles has none of these problems (same idea as Start-Native in
# scripts/launcher-e2e.ps1). ArgumentList quotes correctly on every OS.
function Invoke-Program([string]$Exe, [string[]]$Arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    foreach ($a in $Arguments) { $info.ArgumentList.Add($a) }
    $process = [System.Diagnostics.Process]::Start($info)
    $process.WaitForExit()
    return $process.ExitCode
}

# vpk strips these itself — but only AFTER the bundle has been copied, which on macOS is after we have
# sealed the nested game bundle, and removing a sealed file breaks its signature. So strip them first.
# (createdump + *.pdb really are inside a Godot .NET export's data_* folder.)
function Remove-DebugFiles([string]$Dir) {
    Get-ChildItem -LiteralPath $Dir -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'createdump*' -or $_.Extension -in '.pdb', '.dbg' } |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $Dir -Recurse -Force -Directory -Filter '*.dSYM' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
}

function Publish-Launcher([string]$Rid, [string]$OutDir) {
    Step "publishing launcher for $Rid ..."
    $publishArgs = @('publish', $LauncherProj, '-c', 'Release', '-r', $Rid, '-o', $OutDir, "-p:Version=$Version", '--nologo', '-v', 'q')
    if ($Rid -like 'linux-*') {
        # Linux = self-contained JIT, not NativeAOT: there are no Velopack hooks to exit fast for, and an
        # AOT binary built on a current runner would raise the glibc floor above the game's own.
        #
        # Deliberately NOT trimmed. ILLink (unlike the AOT compiler, which only looks at reachable code)
        # analyses whole assemblies: `TrimMode=partial` keeps Avalonia.DesignerSupport entirely, its
        # reflection-heavy previewer code raises IL2026/IL2072/IL2075, and this project promotes those to
        # errors. `TrimMode=full` passes cleanly and is smaller (measured: 28 MB vs 51 MB gzipped), but Linux is
        # the one platform whose real windowing backend no test here ever starts — so it ships "boring and
        # works". Revisit once someone has run a trimmed build on a Linux desktop.
        $publishArgs += @('-p:PublishAot=false', '--self-contained', 'true', '-p:PublishReadyToRun=true')
    }
    dotnet @publishArgs
    Remove-DebugFiles $OutDir
}

function Publish-StubGame([string]$Rid, [string]$OutDir) {
    Step "publishing stub game for $Rid ..."
    $publishArgs = @('publish', $StubProj, '-c', 'Release', '-r', $Rid, '-o', $OutDir, '--nologo', '-v', 'q')
    if ($Rid -like 'linux-*') { $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=true') }
    else { $publishArgs += '-p:PublishAot=true' }
    dotnet @publishArgs
    Remove-DebugFiles $OutDir
}

# Runs the real Godot export for this OS and returns the exported path (a folder, or the .app on macOS).
# BuildInfo.cs (the in-game version string) and the macOS preset's bundle versions are stamped for the
# duration of the export and restored afterwards, so a local run never leaves the tree dirty.
function Export-Game {
    . (Join-Path $RepoRoot 'scripts/godot-bin.ps1')
    $godot = Resolve-Godot
    if (-not $godot) { Fail 'no Godot 4 .NET executable found (see scripts/godot-bin.ps1)' }

    # This function's OUTPUT is its return value (`$gameSource = Export-Game`), and whatever a native command
    # prints to stdout rides along in it — Godot's log lines would come back as extra "paths". So nothing
    # chatty in here may write to the pipeline: the import script goes to the host, Godot runs through
    # Invoke-Program.
    & (Join-Path $RepoRoot 'tools/godot-import.ps1') | Out-Host
    if (-not (Test-Path -LiteralPath (Join-Path $Client 'assets/bases/garrison.glb.import'))) {
        Fail 'GLB import sidecars missing — an export now would ship placeholder meshes'
    }

    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

    $buildInfo = Join-Path $Client 'scripts/BuildInfo.cs'
    $presets = Join-Path $Client 'export_presets.cfg'
    $buildInfoOriginal = Get-Content -LiteralPath $buildInfo -Raw
    $presetsOriginal = Get-Content -LiteralPath $presets -Raw
    try {
        Set-Content -LiteralPath $buildInfo -NoNewline -Value ($buildInfoOriginal.Replace('0.0.0-dev', $Version))
        $stamped = $presetsOriginal -replace 'application/short_version="[^"]*"', "application/short_version=`"$Version`""
        $stamped = $stamped -replace 'application/version="[^"]*"', "application/version=`"$Version`""
        Set-Content -LiteralPath $presets -NoNewline -Value $stamped

        $preset, $target = if ($IsWindows) { 'Windows Desktop', (Join-Path $Work "export/$GameExeBase.exe") }
        elseif ($IsMacOS) { 'macOS', (Join-Path $Work "export/$MacGameBundle") }
        else { 'Linux', (Join-Path $Work "export/$GameExeBase.x86_64") }
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        if (Test-Path -LiteralPath $target) { Remove-Item -Recurse -Force -LiteralPath $target }

        Step "exporting the game ('$preset') — this takes a few minutes ..."
        # Headless Godot .NET can segfault during SHUTDOWN after a successful export (exit 139, or an
        # access violation on Windows), so the exit code is only reported; the artifact check is the gate.
        Step "  $godot"
        $exportExit = Invoke-Program $godot @('--headless', '--path', $Client, '--export-release', $preset, $target)
        Step "  export exit code: $exportExit"
        if (-not (Test-Path -LiteralPath $target)) { Fail "export produced no output at $target (exit code $exportExit)" }
        if ($IsMacOS) { return $target }
        return (Split-Path $target -Parent)
    }
    finally {
        Set-Content -LiteralPath $buildInfo -NoNewline -Value $buildInfoOriginal
        Set-Content -LiteralPath $presets -NoNewline -Value $presetsOriginal
    }
}

# ---------------------------------------------------------------------------------------------------
Set-Location $RepoRoot
if ($FakeGame -and $GameDir) { Fail '-FakeGame and -GameDir are mutually exclusive' }
if ($ReleaseNotes -and -not (Test-Path -LiteralPath $ReleaseNotes)) { Fail "release notes file not found: $ReleaseNotes" }

Step "version $Version · channel $Channel · packId $PackId"
dotnet tool restore | Out-Null
if (-not $IsWindows) {
    # Without a system zstd, vpk silently falls back to bsdiff deltas — which the 1.2.0 updater REJECTS
    # ("Unsupported patch format"). Every update would then quietly be a full download.
    if (-not (Get-Command zstd -ErrorAction SilentlyContinue)) { Fail 'zstd is required (brew install zstd / apt-get install zstd)' }
}

if (Test-Path -LiteralPath $Work) { Remove-Item -Recurse -Force -LiteralPath $Work }
New-Item -ItemType Directory -Force -Path $Stage, $OutputDir | Out-Null

# ---- 1. the game ------------------------------------------------------------------------------------
$hostRid = if ($IsWindows) { 'win-x64' }
elseif ($IsMacOS) { if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' } }
else { 'linux-x64' }

$gameSource = $null
if ($FakeGame) {
    $stubOut = Join-Path $Work 'stub'
    Publish-StubGame $hostRid $stubOut
    if ($IsMacOS) {
        # A minimal stand-in for the Godot .app, at the same nested path and with the game's bundle id.
        $gameSource = Join-Path $Work "fake/$MacGameBundle"
        New-Item -ItemType Directory -Force -Path (Join-Path $gameSource 'Contents/MacOS') | Out-Null
        Copy-Item -LiteralPath (Join-Path $stubOut $GameExeBase) -Destination (Join-Path $gameSource "Contents/MacOS/$GameExeBase")
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Stellar Allegiance</string>
  <key>CFBundleIdentifier</key><string>com.stellarallegiance.game</string>
  <key>CFBundleExecutable</key><string>$GameExeBase</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>CFBundleVersion</key><string>$Version</string>
</dict></plist>
"@ | Set-Content -LiteralPath (Join-Path $gameSource 'Contents/Info.plist')
    }
    else {
        $gameSource = Join-Path $Work 'fake'
        New-Item -ItemType Directory -Force -Path $gameSource | Out-Null
        $stubName = if ($IsWindows) { "$GameExeBase.exe" } else { $GameExeBase }
        $gameName = if ($IsWindows) { "$GameExeBase.exe" } else { "$GameExeBase.x86_64" }
        Copy-Item -LiteralPath (Join-Path $stubOut $stubName) -Destination (Join-Path $gameSource $gameName)
    }
}
elseif ($GameDir) {
    $gameSource = (Resolve-Path -LiteralPath $GameDir).Path
    Step "using the pre-exported game at $gameSource"
}
else {
    $gameSource = Export-Game
}
# Exactly one existing path. (A function that leaks output into its return value hands back an ARRAY,
# and every copy below would then fail on its first element — far from the cause.)
if ($gameSource -isnot [string] -or -not (Test-Path -LiteralPath $gameSource)) {
    Fail "the game did not resolve to one existing path (got $(@($gameSource).Count): $(@($gameSource) -join ' | '))"
}

# ---- 2. the launcher + 3. assemble ---------------------------------------------------------------------
if ($IsMacOS) {
    $app = Join-Path $Stage "$PackTitle.app"
    $macosDir = Join-Path $app 'Contents/MacOS'
    $resources = Join-Path $app 'Contents/Resources'
    $helpers = Join-Path $app 'Contents/Helpers'
    New-Item -ItemType Directory -Force -Path $macosDir, $resources, $helpers | Out-Null

    # [string[]]: an `if` EXPRESSION unrolls a one-element array into a bare string, and `$rids[0]` would then be 'o'.
    [string[]]$rids = if ($HostArchOnly) { @($hostRid) } else { @('osx-arm64', 'osx-x64') }
    foreach ($rid in $rids) { Publish-Launcher $rid (Join-Path $Work "launcher/$rid") }
    $first = Join-Path $Work "launcher/$($rids[0])"
    # The Skia / HarfBuzz / AvaloniaNative dylibs from NuGet are already universal and identical in both
    # publishes, so only the launcher binary itself needs lipo. Everything else is copied from the first.
    # Contents/MacOS must end up holding ONLY Mach-O files: vpk's nupkg does not preserve the xattr-based
    # signatures that non-Mach-O files in that folder would need.
    Get-ChildItem -LiteralPath $first -File | Where-Object { $_.Name -ne 'StellarLauncher' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $macosDir }
    if ($rids.Count -gt 1) {
        lipo -create (Join-Path $Work 'launcher/osx-arm64/StellarLauncher') (Join-Path $Work 'launcher/osx-x64/StellarLauncher') -output (Join-Path $macosDir 'StellarLauncher')
    }
    else {
        Copy-Item -LiteralPath (Join-Path $first 'StellarLauncher') -Destination $macosDir
    }
    chmod +x (Join-Path $macosDir 'StellarLauncher')

    $plist = (Get-Content -LiteralPath (Join-Path $RepoRoot 'launcher/macos/Info.plist') -Raw).Replace('__VERSION__', $Version).Replace('__BUILD__', $Version)
    Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -NoNewline -Value $plist

    Step 'nesting the game bundle (Contents/Helpers) ...'
    $inner = Join-Path $helpers $MacGameBundle
    ditto $gameSource $inner # ditto, not Copy-Item: preserves symlinks, modes and resource forks
    chmod +x (Join-Path $inner "Contents/MacOS/$GameExeBase")
    # The launcher wears the game's icon: reuse the .icns Godot generated instead of a second art pipeline.
    $icns = Join-Path $inner 'Contents/Resources/icon.icns'
    if (Test-Path -LiteralPath $icns) { Copy-Item -LiteralPath $icns -Destination (Join-Path $resources 'icon.icns') }

    Remove-DebugFiles $app

    # Sign INSIDE-OUT. vpk is then told --signDisableDeep: it signs only what it adds (UpdateMac) and seals
    # the outer bundle. `codesign --deep` is not good enough on its own — it never re-signs Mach-O files
    # under Contents/Resources, which is exactly where Godot keeps the .NET runtime dylibs.
    $identity = if ($env:MAC_APP_IDENTITY) { $env:MAC_APP_IDENTITY } else { '-' }
    Step "signing inside-out (identity '$identity') ..."
    # Inner game bundle: today's known-good recipe from export-clients.ps1 — strip Godot's built-in
    # signature (it turns on the hardened runtime, which SIGKILLs an un-notarized .NET game) and re-sign
    # plain ad-hoc. A Developer ID build signs it with launcher/macos/game.entitlements instead.
    $PSNativeCommandUseErrorActionPreference = $false
    codesign --remove-signature $inner 2>$null
    $PSNativeCommandUseErrorActionPreference = $true
    if ($identity -eq '-') { codesign --force --deep --sign - $inner }
    else { codesign --force --deep --timestamp --options runtime --entitlements (Join-Path $RepoRoot 'launcher/macos/game.entitlements') --sign $identity $inner }
    # Outer dylibs: each nested Mach-O needs its own signature before the bundle can be sealed.
    Get-ChildItem -LiteralPath $macosDir -File -Filter '*.dylib' | ForEach-Object {
        if ($identity -eq '-') { codesign --force --sign - $_.FullName }
        else { codesign --force --timestamp --options runtime --sign $identity $_.FullName }
    }
    $packDir = $app
    $mainExe = 'StellarLauncher'
}
else {
    Publish-Launcher $hostRid (Join-Path $Work 'launcher')
    Copy-Item -Path (Join-Path $Work 'launcher/*') -Destination $Stage -Recurse
    $gameStage = Join-Path $Stage 'game'
    New-Item -ItemType Directory -Force -Path $gameStage | Out-Null
    Copy-Item -Path (Join-Path $gameSource '*') -Destination $gameStage -Recurse
    Remove-DebugFiles $Stage
    if (-not $IsWindows) {
        chmod +x (Join-Path $Stage 'StellarLauncher')
        chmod +x (Join-Path $gameStage "$GameExeBase.x86_64")
    }
    $packDir = $Stage
    $mainExe = if ($IsWindows) { 'StellarLauncher.exe' } else { 'StellarLauncher' }
}

# ---- 4. vpk pack ------------------------------------------------------------------------------------------
$hadPrevious = [bool](Get-ChildItem -LiteralPath $OutputDir -Filter '*-full.nupkg' -ErrorAction SilentlyContinue)
$pack = @('vpk', 'pack', '-x', '--packId', $PackId, '--packVersion', $Version, '--packDir', $packDir, '--mainExe', $mainExe,
    '--packTitle', $PackTitle, '--packAuthors', 'Wivuu', '--channel', $Channel, '--outputDir', $OutputDir)
if (-not $Ci) { $pack += '-y' }
if ($ReleaseNotes) { $pack += @('--releaseNotes', (Resolve-Path -LiteralPath $ReleaseNotes).Path) }

if ($IsMacOS) {
    # NEVER omit --signAppIdentity: vpk injects UpdateMac + sq.version into the bundle, and without a
    # re-seal a quarantined download shows "damaged" instead of "unidentified developer".
    $pack += @('--signAppIdentity', $identity, '--signEntitlements', (Join-Path $RepoRoot 'launcher/macos/launcher.entitlements'), '--signDisableDeep')
    if ($env:MAC_INSTALL_IDENTITY) { $pack += @('--signInstallIdentity', $env:MAC_INSTALL_IDENTITY) }
    if ($env:MAC_NOTARY_PROFILE) { $pack += @('--notaryProfile', $env:MAC_NOTARY_PROFILE) }
}
elseif ($IsWindows) {
    $ico = Join-Path $RepoRoot 'launcher/App/Assets/app.ico'
    if (Test-Path -LiteralPath $ico) { $pack += @('--icon', $ico) }
    # When signing is switched on: never sign the ~200 .NET runtime dlls inside the game's data_* folder.
    if ($env:AZURE_TRUSTED_SIGN_FILE) { $pack += @('--azureTrustedSignFile', $env:AZURE_TRUSTED_SIGN_FILE, '--signExclude', 'game\\data_.*') }
    elseif ($env:WIN_SIGN_PARAMS) { $pack += @('--signParams', $env:WIN_SIGN_PARAMS, '--signExclude', 'game\\data_.*') }
}
else {
    $png = Join-Path $RepoRoot 'launcher/App/Assets/icon-256.png'
    if (Test-Path -LiteralPath $png) { $pack += @('--icon', $png) }
    $pack += @('--categories', 'Game')
}

Step 'vpk pack ...'
dotnet @pack

# ---- 5. post-checks ---------------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$full = Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Version-*full.nupkg" | Select-Object -First 1
if (-not $full) { $full = Get-ChildItem -LiteralPath $OutputDir -Filter "$PackId-$Version-full.nupkg" | Select-Object -First 1 }
if (-not $full) { Fail "vpk produced no full package for $Version in $OutputDir" }
foreach ($required in "releases.$Channel.json", "assets.$Channel.json") {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDir $required))) { Fail "missing $required" }
}

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

if ($IsMacOS) {
    # Verify the ARTIFACT, not the stage: unpack the portable zip the way a player would.
    $verify = Join-Path $Work 'verify'
    New-Item -ItemType Directory -Force -Path $verify | Out-Null
    ditto -x -k (Join-Path $OutputDir "$PackId-$Channel-Portable.zip") $verify
    $packed = Join-Path $verify "$PackTitle.app"
    codesign --verify --deep --strict $packed
    Step 'codesign --verify --deep --strict: ok'
    if (-not $HostArchOnly) {
        # (vpk adds a `sq.version` symlink to Contents/MacOS — not a Mach-O, so lipo is allowed to refuse it.)
        $PSNativeCommandUseErrorActionPreference = $false
        Get-ChildItem -LiteralPath (Join-Path $packed 'Contents/MacOS') -File | Where-Object { -not $_.LinkType } | ForEach-Object {
            $archs = (lipo -archs $_.FullName 2>$null) -join ' '
            if ($LASTEXITCODE -eq 0 -and ($archs -notmatch 'x86_64' -or $archs -notmatch 'arm64')) { Fail "$($_.Name) is not universal ($archs)" }
        }
        $PSNativeCommandUseErrorActionPreference = $true
        Step 'every Mach-O in Contents/MacOS is universal'
    }
}

Step 'done:'
Get-ChildItem -LiteralPath $OutputDir -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,10:N1} MiB  {1}" -f ($_.Length / 1MB), $_.Name)
}
Write-Host ''
Write-Host "[package] test an update against this folder:  StellarLauncher --launcher-feed=`"$OutputDir`""
