#!/usr/bin/env pwsh
#Requires -Version 7.3
# Build the generator image and produce every catalog asteroid into ./build.
# Pass extra args to override the default `all` command, e.g.:
#   ./build.ps1 one --seed 4242
param([Parameter(ValueFromRemainingArguments)][string[]]$GenArgs)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Set-Location $PSScriptRoot

$Image = if ($env:IMAGE) { $env:IMAGE } else { 'asteroid-gen' }
$Out = if ($env:OUT) { $env:OUT } else { Join-Path $PSScriptRoot 'build' }

# Clean out the old build artifacts, if any.
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }

docker build -t $Image .
New-Item -ItemType Directory -Force -Path $Out | Out-Null

if ($GenArgs.Count -gt 0) {
  docker run --rm -v "${Out}:/out" $Image @GenArgs --out /out
} else {
  docker run --rm -v "${Out}:/out" $Image
}

Write-Host "Done. Artifacts in $Out"
