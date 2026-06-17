#!/usr/bin/env pwsh
# Build the generator image and produce every catalog asteroid into ./build.
#
#   tools/asteroid-gen/build.ps1                # build all
#   tools/asteroid-gen/build.ps1 one --seed 4242
$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

$IMAGE = if ($env:IMAGE) { $env:IMAGE } else { 'asteroid-gen' }
$OUT   = if ($env:OUT)   { $env:OUT }   else { Join-Path $PSScriptRoot 'build' }

Remove-Item -Recurse -Force $OUT -ErrorAction SilentlyContinue
& docker build -t $IMAGE .
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force $OUT | Out-Null

if ($args.Count -gt 0) {
    & docker run --rm -v "${OUT}:/out" $IMAGE @args --out /out
} else {
    & docker run --rm -v "${OUT}:/out" $IMAGE
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Done. Artifacts in $OUT"
