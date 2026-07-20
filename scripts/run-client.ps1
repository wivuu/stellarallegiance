#!/usr/bin/env pwsh
#Requires -Version 7.3
# Build and launch the Godot client. By DEFAULT it opens the server browser pointed at the public
# lobby (PUBLIC_LOBBY, default https://wivuu-public-lobby-production.up.railway.app) so you can pick
# a server; pass -Local to skip
# the browser and connect straight to localhost. Builds the client C# fresh so godot-mono can't
# launch a stale assembly against a rebuilt server (silent protocol skew). Other args pass through
# to Godot.
#   scripts/run-client.ps1                          # public lobby server browser
#   scripts/run-client.ps1 -Local                   # connect directly to localhost:8090
#   scripts/run-client.ps1 --host some.host:8090    # connect to a specific server
#   scripts/run-client.ps1 -WriteMovie out.avi      # record Movie Maker video to out.avi
#   $env:PUBLIC_LOBBY="host:port"; scripts/run-client.ps1   # browse a different lobby
#
# Movie Maker perf: recording cost is dominated by per-frame game sim + MJPEG encoding,
# both of which scale with the frame count (render itself is ~free). So when -WriteMovie
# is active we default the capture to 30 fps, which ~halves total record time vs Godot's
# 60 fps default. Override with MOVIE_FPS (e.g. MOVIE_FPS=60 for smoother output, or a
# lower value for faster grabs). MOVIE_RESOLUTION (e.g. 1280x720) additionally shrinks the
# captured frame to cut encoding time further — off by default since it's lossy. An explicit
# --fixed-fps / --resolution passed on the command line always wins over these defaults.
#
# Note: unlike the old bash --write-movie (whose path was optional), -WriteMovie takes a
# required path argument.
param(
    [switch]$Local,
    [string]$WriteMovie,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$GodotArgs
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Capture the caller's working dir up front (before any Set-Location) so we can anchor
# relative -WriteMovie paths against it (replaces bash's $OLDPWD anchoring).
$CallerDir = (Get-Location).Path

$RepoRoot = Split-Path $PSScriptRoot -Parent
$SimPort = if ($env:SIM_PORT) { $env:SIM_PORT } else { '8090' }

if (-not $GodotArgs) { $GodotArgs = @() }

if ($Local) {
    # Direct connect, no lobby. A later user-supplied --host still wins (the client takes the last).
    Write-Host "[run-client] -Local: connecting directly to localhost:$SimPort"
    $GodotArgs = @('--host', "localhost:$SimPort") + $GodotArgs
} else {
    if (-not $env:PUBLIC_LOBBY) { $env:PUBLIC_LOBBY = 'https://wivuu-public-lobby-production.up.railway.app' }
    Write-Host "[run-client] public lobby $($env:PUBLIC_LOBBY) server browser (use -Local for direct localhost)"
}

. "$RepoRoot/scripts/godot-bin.ps1"
$Godot = Resolve-Godot
if (-not $Godot) { exit 1 }

Write-Host "[run-client] building client C# (Debug)"
dotnet build "$RepoRoot/client/stellarallegiance.csproj" -c Debug

Set-Location $RepoRoot
if ($WriteMovie) {
    # Godot resolves relative --write-movie paths against res:// (client/), so anchor
    # a non-absolute path to the caller's original working dir to avoid surprises.
    $moviePath = $WriteMovie
    if (-not [System.IO.Path]::IsPathRooted($moviePath)) {
        $moviePath = Join-Path $CallerDir $moviePath
    }
    Write-Host "[run-client] Movie Maker mode: recording to $moviePath"

    # Perf defaults, only applied when the user hasn't set them explicitly (last wins in Godot,
    # but we skip injecting to keep the command line honest / avoid dupes).
    function Test-HasArg {
        param([string]$Flag, [string[]]$ArgList)
        foreach ($a in $ArgList) {
            if ($a -eq $Flag -or $a -like "$Flag=*") { return $true }
        }
        return $false
    }

    $movieFps = if ($env:MOVIE_FPS) { $env:MOVIE_FPS } else { '30' }
    if ($movieFps -and -not (Test-HasArg -Flag '--fixed-fps' -ArgList $GodotArgs)) {
        Write-Host "[run-client] capturing at $movieFps fps (set MOVIE_FPS to override, or pass --fixed-fps)"
        $GodotArgs = @('--fixed-fps', $movieFps) + $GodotArgs
    }
    if ($env:MOVIE_RESOLUTION -and -not (Test-HasArg -Flag '--resolution' -ArgList $GodotArgs)) {
        Write-Host "[run-client] capturing at $($env:MOVIE_RESOLUTION) (set MOVIE_RESOLUTION= to disable)"
        $GodotArgs = @('--resolution', $env:MOVIE_RESOLUTION) + $GodotArgs
    }

    $GodotArgs = @('--write-movie', $moviePath) + $GodotArgs
}

& $Godot --path client/ @GodotArgs
exit $LASTEXITCODE
