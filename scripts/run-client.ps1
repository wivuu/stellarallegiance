#!/usr/bin/env pwsh
# Build and launch the Godot client.
#
#   scripts/run-client.ps1                          # show the address-input screen
#   scripts/run-client.ps1 --host localhost:8090    # connect immediately
#
# Extra args pass through to Godot.
$ErrorActionPreference = 'Stop'

$REPO_ROOT = Split-Path -Parent $PSScriptRoot

. "$PSScriptRoot/godot-bin.ps1"
$GODOT = Resolve-Godot
if (-not $GODOT) { exit 1 }

Write-Host "[run-client] building client C# (Debug)"
& dotnet build "$REPO_ROOT/client/wivuullegiance.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Set-Location $REPO_ROOT
& $GODOT --path client/ @args
exit $LASTEXITCODE
