#!/usr/bin/env pwsh
#Requires -Version 7.3
# Build and run the standalone authoritative sim server (server/). It hosts the lobby and the
# 20 Hz match; clients connect directly by ip:port and download all content from it.
#
# By DEFAULT it publishes itself to the hosted public lobby (PUBLIC_LOBBY, default
# https://wivuu-public-lobby-production.up.railway.app)
# so clients can discover and WebRTC-join it; pass -Local to stay private (direct ws:// only,
# no lobby registration).
#
# Rebuilds every launch (and kills any stale process on the port) so the running binary can
# never silently serve old-format snapshots — the failure the protocol-version handshake also
# guards against. Other flags pass through to the server:
#   scripts/run-server.ps1                          # publish to the public lobby, :8090
#   scripts/run-server.ps1 -Local                   # private: local/LAN only, no lobby
#   scripts/run-server.ps1 --secret hunter2         # require a shared-secret password
#   scripts/run-server.ps1 --autostart              # skip ready-up (bots / benchmarking)
#   $env:SIM_PUBLIC_NAME="My Server"; scripts/run-server.ps1   # custom lobby name (else hostname)
#   $env:SIM_HOSTED_BY="Vex"; scripts/run-server.ps1   # "hosted by ..." attribution in the browser
#   $env:SIM_PORT=9000; scripts/run-server.ps1      # different port
param(
    [switch]$Local,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ServerArgs
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$RepoRoot = Split-Path $PSScriptRoot -Parent
$SimPort = if ($env:SIM_PORT) { [int]$env:SIM_PORT } else { 8090 }

if ($Local) {
    # Private: empty SIM_PUBLIC_NAME means the server never registers with any lobby.
    $env:SIM_PUBLIC_NAME = ''
    Write-Host "[run-server] -Local: private (not registering with the public lobby)"
} else {
    # Public: default the lobby + a name (hostname, trimmed to the 50-char cap) so it registers.
    if (-not $env:PUBLIC_LOBBY) { $env:PUBLIC_LOBBY = 'https://wivuu-public-lobby-production.up.railway.app' }
    if (-not $env:SIM_PUBLIC_NAME) {
        $name = [System.Net.Dns]::GetHostName()
        if ($name.Length -gt 50) { $name = $name.Substring(0, 50) }
        $env:SIM_PUBLIC_NAME = $name
    }
    Write-Host "[run-server] publishing to public lobby $($env:PUBLIC_LOBBY) as `"$($env:SIM_PUBLIC_NAME)`" (use -Local to stay private)"
}

# Cross-platform listener-PID lookup on a TCP port (replaces bash's lsof).
function Get-PortListenerPids {
    param([int]$Port)
    if ($IsWindows) {
        return (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).OwningProcess |
            Select-Object -Unique
    }
    # unix: lsof, non-fatal when nothing matches.
    $prev = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $out = lsof -tnP -iTCP:$Port -sTCP:LISTEN 2>$null
    } finally {
        $PSNativeCommandUseErrorActionPreference = $prev
    }
    return $out | Where-Object { $_ } | ForEach-Object { [int]$_ } | Select-Object -Unique
}

# Stop whatever is already on the port so the rebuilt binary takes over cleanly.
$stale = Get-PortListenerPids -Port $SimPort
if ($stale) {
    Write-Host "[run-server] stopping stale sim server on :$SimPort (pid $($stale -join ' '))"
    foreach ($procId in $stale) { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue }
    for ($i = 0; $i -lt 10; $i++) {
        if (-not (Get-PortListenerPids -Port $SimPort)) { break }
        Start-Sleep -Milliseconds 500
    }
}

Write-Host "[run-server] rebuilding server/ (-c Release)"
dotnet build "$RepoRoot/server/SimServer.csproj" -c Release

Write-Host "[run-server] starting on :$SimPort"
Set-Location $RepoRoot
& dotnet run --project server -c Release --no-build -- --port $SimPort @ServerArgs
exit $LASTEXITCODE
