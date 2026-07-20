#!/usr/bin/env pwsh
#Requires -Version 7.3
# Deploy (or UPDATE) a stellarallegiance GAME SERVER on Railway, published to the default public lobby.
#
# Prereqs: Railway CLI installed and logged in (`railway login`).
# Usage:
#   scripts/deploy-railway-server.ps1 [-Project <project-name>]
#   scripts/deploy-railway-server.ps1 my-server
#
# The project name doubles as the server's public name (SIM_PUBLIC_NAME) for now. Re-running with
# the SAME name UPDATES the existing Railway project (redeploys it) instead of spinning up another
# server — so the same server won't get advertised multiple times in the lobby.
#
# Build: server/Dockerfile from the repo-root context (RAILWAY_DOCKERFILE_PATH=server/Dockerfile).
# PUBLIC_LOBBY is left unset, so the server uses its compiled-in default (the hosted public lobby).
# The server auto-derives its public endpoint from RAILWAY_PUBLIC_DOMAIN and self-heals to a DIRECT
# wss:// listing once Railway's edge finishes provisioning (the first minute after a fresh deploy).
#
# Override the lobby by setting the PUBLIC_LOBBY environment variable before running. Tear down with `railway delete`.
param([string]$Project = 'wivuu-game-server')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Dockerfile = 'server/Dockerfile'

# repo root — server/Dockerfile builds from here (needs shared/)
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

if ($ProjectId) {
  Write-Host "==> Updating existing project '$Project' ($ProjectId)"
  $vars = @("RAILWAY_DOCKERFILE_PATH=$Dockerfile", "SIM_PUBLIC_NAME=$Project")
  railway variable set @vars -p $ProjectId -s $Project -e production --skip-deploys
  railway up -c -p $ProjectId -s $Project -e production
} else {
  Write-Host "==> Creating new project '$Project'"
  railway init -n $Project
  $addArgs = @('--variables', "RAILWAY_DOCKERFILE_PATH=$Dockerfile", '--variables', "SIM_PUBLIC_NAME=$Project")
  railway add --service $Project @addArgs
  railway domain --service $Project   # provision the public domain up front
  railway up -c --service $Project
}

Write-Host @'

Done. It should appear (as DIRECT) in the lobby within ~1 min of the container starting:
  curl -s https://wivuu-public-lobby-production.up.railway.app/servers
'@
