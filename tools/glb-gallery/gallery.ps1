#!/usr/bin/env pwsh
#Requires -Version 7.3
# Render a labeled contact sheet of every GLB in a source folder.
#
#   ./gallery.ps1 [-SrcDir <dir>] [-OutPng <png>] [-Size <n>] [-Limit <n>]
#   ./gallery.ps1 path/to/glbs out.png 320 0
#
# Defaults to the repo's pick-assets folder.
param(
  [string]$SrcDir,
  [string]$OutPng,
  [int]$Size = 320,
  [int]$Limit = 0
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Here = $PSScriptRoot
$Repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

if (-not $SrcDir) { $SrcDir = Join-Path $Repo 'pick-assets' }
if (-not $OutPng) { $OutPng = Join-Path $Here 'glb-gallery.png' }

$SrcDir = (Resolve-Path $SrcDir).Path
$Thumbs = Join-Path $Here 'thumbs'

# Resolve the Godot binary via the shared helper (returns a path string, or $null).
. (Join-Path $Repo 'scripts/godot-bin.ps1')
$Godot = Resolve-Godot
if (-not $Godot) { exit 1 }

Write-Host '== rendering thumbnails with Godot =='
New-Item -ItemType Directory -Force -Path $Thumbs | Out-Null

$godotArgs = @('--path', $Here, '--resolution', '400x400')
if ($IsMacOS) { $godotArgs += @('--rendering-driver', 'metal') }
$godotArgs += @('--', '--src', $SrcDir, '--out', $Thumbs, '--size', "$Size", '--limit', "$Limit")
& $Godot @godotArgs

Write-Host '== composing grid =='
$python = Get-Command python3 -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python -ErrorAction SilentlyContinue }
if (-not $python) {
  [Console]::Error.WriteLine('python3 (or python) not found on PATH; needed to compose the grid.')
  exit 1
}
& $python.Source (Join-Path $Here 'compose.py') --thumbs $Thumbs --out $OutPng --cols 10 --cell "$Size"

Write-Host "== done: $OutPng =="
