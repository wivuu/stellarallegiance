#!/usr/bin/env pwsh
#Requires -Version 7.3
# Deploy (or UPDATE) the stellarallegiance PUBLIC LOBBY on Railway (server registry + WebRTC signaling).
#
# Prereqs: Railway CLI installed and logged in (`railway login`).
# Usage:
#   scripts/deploy-railway-lobby.ps1 [-Project <project-name>]
#   scripts/deploy-railway-lobby.ps1 my-lobby
#
# Re-running with the SAME name UPDATES the existing Railway project (redeploys it) instead of
# creating a duplicate lobby. Build: public-lobby/Dockerfile from the repo-root context.
# Set the STUN_URL environment variable to override the public STUN default handed to WebRTC clients.
#
# NOTE: the default lobby URL is baked into the server/client as
# https://wivuu-public-lobby-production.up.railway.app — keep the project name `wivuu-public-lobby`
# (or update that default in LobbyRegistrar.cs / ConnectionManager.cs) so clients find this lobby.
param([string]$Project = 'wivuu-public-lobby')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Dockerfile = 'public-lobby/Dockerfile'

# repo root — public-lobby/Dockerfile builds from here
$RepoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $RepoRoot

if (-not (Get-Command railway -ErrorAction SilentlyContinue)) {
  [Console]::Error.WriteLine('railway CLI not found; install it and run `railway login` first.')
  exit 1
}

# Recursively walk PSCustomObjects / arrays looking for a node named $Project that has an id.
# Native replacement for the old inline python3 JSON walker.
function Find-ProjectId($node) {
  if ($node -is [System.Collections.IEnumerable] -and $node -isnot [string]) {
    foreach ($item in $node) {
      $found = Find-ProjectId $item
      if ($found) { return $found }
    }
    return $null
  }
  if ($node -is [System.Management.Automation.PSCustomObject]) {
    $props = $node.PSObject.Properties
    if ($props['name'] -and $node.name -eq $Project -and $props['id']) {
      return $node.id
    }
    foreach ($prop in $props) {
      $found = Find-ProjectId $prop.Value
      if ($found) { return $found }
    }
  }
  return $null
}

# Idempotency: is a project with this name already there? (so re-runs UPDATE, not duplicate.)
# `railway list` errors (e.g. logged out) must not abort this probe.
$projectList = $null
try {
  $raw = railway list --json 2>$null
  if ($raw) { $projectList = $raw | ConvertFrom-Json }
} catch {
  $projectList = $null
}
$ProjectId = if ($projectList) { Find-ProjectId $projectList } else { $null }

# Conditional STUN_URL: append only when the env var is set (bash `${STUN_URL:+...}`).
if ($ProjectId) {
  Write-Host "==> Updating existing project '$Project' ($ProjectId)"
  $vars = @("RAILWAY_DOCKERFILE_PATH=$Dockerfile")
  if ($env:STUN_URL) { $vars += "STUN_URL=$($env:STUN_URL)" }
  railway variable set @vars -p $ProjectId -s $Project -e production --skip-deploys
  railway up -c -p $ProjectId -s $Project -e production
} else {
  Write-Host "==> Creating new project '$Project'"
  railway init -n $Project
  $addArgs = @('--variables', "RAILWAY_DOCKERFILE_PATH=$Dockerfile")
  if ($env:STUN_URL) { $addArgs += @('--variables', "STUN_URL=$($env:STUN_URL)") }
  railway add --service $Project @addArgs
  railway domain --service $Project
  railway up -c --service $Project
}

Write-Host @"

Done. Show the domain and verify:
  railway domain -s "$Project"
  curl -s https://<that-domain>/health     # -> public-lobby
"@
