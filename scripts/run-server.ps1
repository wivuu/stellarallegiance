#!/usr/bin/env pwsh
# Build and run the standalone authoritative sim server (server/).
#
#   scripts/run-server.ps1                          # open, :8090, lobby ready-up
#   scripts/run-server.ps1 --secret hunter2         # require a shared-secret password
#   scripts/run-server.ps1 --autostart              # skip ready-up (bots / benchmarking)
#   $env:SIM_PORT = 9000; scripts/run-server.ps1    # different port
#
# Extra args after -- pass through to the server binary.
$ErrorActionPreference = 'Stop'

$REPO_ROOT = Split-Path -Parent $PSScriptRoot
$SIM_PORT  = if ($env:SIM_PORT) { [int]$env:SIM_PORT } else { 8090 }

function Stop-PortListener([int]$Port) {
    $stalePid = $null
    if ($IsWindows) {
        $stalePid = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
                    Select-Object -First 1 -ExpandProperty OwningProcess
    } elseif (Get-Command lsof -ErrorAction SilentlyContinue) {
        $stalePid = lsof -tnP -iTCP:$Port -sTCP:LISTEN 2>/dev/null | Select-Object -First 1
    }
    if (-not $stalePid) { return }
    Write-Host "[run-server] stopping stale sim server on :$Port (pid $stalePid)"
    Stop-Process -Id $stalePid -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 500
        $still = if ($IsWindows) {
            Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        } else {
            lsof -tnP -iTCP:$Port -sTCP:LISTEN 2>/dev/null
        }
        if (-not $still) { break }
    }
}

Stop-PortListener $SIM_PORT

Write-Host "[run-server] rebuilding server/ (-c Release)"
& dotnet build "$REPO_ROOT/server/SimServer.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "[run-server] starting on :$SIM_PORT"
Set-Location $REPO_ROOT
& dotnet run --project server -c Release --no-build -- --port $SIM_PORT @args
exit $LASTEXITCODE
