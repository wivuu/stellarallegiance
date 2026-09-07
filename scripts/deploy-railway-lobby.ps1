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
# The lobby needs POSTGRES (identity, sessions, matches, ladder — .PLAN/LobbyRankingService.md).
# One-time, in the Railway dashboard for this project:
#   1. Add a Postgres database service (Railway Postgres). Railway exposes its connection string
#      as ${{Postgres.DATABASE_URL}} on the database service; copy it into the lobby service as
#      ConnectionStrings__postgres-database (the lobby normalizes the postgres:// URL form — Hosting/PostgresConnectionString.cs).
#   2. Set the lobby's pre-deploy command to: dotnet PublicLobby.dll --migrate
#      (Service → Settings → Deploy → Pre-deploy command). It applies EF Core + Orleans migrations
#      and exits 0 (idempotent), so every deploy migrates before the new instance starts.
#   3. Set LOBBY_PUBLIC_URL=https://<lobby-domain> (join-token issuer, device-code links, passkey
#      relying party) and LOBBY_ADMINS=name:<your display name> (or github:<login> etc.).
#   4. Optional: AUTH_GOOGLE_CLIENT_ID/SECRET, AUTH_GITHUB_CLIENT_ID/SECRET, AUTH_STEAM_API_KEY,
#      RANKED_RESULTS (flagged|authenticated), ALLOW_UNVERIFIED_SERVERS (true|false).
#      NEVER set AUTH_DEV_LOGIN in production.
# This script sets the non-secret defaults it can (RAILWAY_DOCKERFILE_PATH, LOBBY_PUBLIC_URL when
# LOBBY_PUBLIC_URL is exported, STUN_URL) — database attachment and secrets stay manual.
#
# NOTE: the default lobby URL baked into the server/client is https://stellarlobby.wivuu.com — a
# custom domain attached to the `wivuu-public-lobby` service (Railway → Settings → Networking).
# Keep that domain attached (or update the default in LobbyRegistrar.cs / ConnectionManager.cs)
# so clients and servers find this lobby; LOBBY_PUBLIC_URL must be the same URL.
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
  if ($env:LOBBY_PUBLIC_URL) { $vars += "LOBBY_PUBLIC_URL=$($env:LOBBY_PUBLIC_URL)" }
  railway variable set @vars -p $ProjectId -s $Project -e production --skip-deploys
  railway up -c -p $ProjectId -s $Project -e production
} else {
  Write-Host "==> Creating new project '$Project'"
  railway init -n $Project
  $addArgs = @('--variables', "RAILWAY_DOCKERFILE_PATH=$Dockerfile")
  if ($env:STUN_URL) { $addArgs += @('--variables', "STUN_URL=$($env:STUN_URL)") }
  if ($env:LOBBY_PUBLIC_URL) { $addArgs += @('--variables', "LOBBY_PUBLIC_URL=$($env:LOBBY_PUBLIC_URL)") }
  railway add --service $Project @addArgs
  railway domain --service $Project
  railway up -c --service $Project
}

Write-Host @"

Done. Show the domain and verify:
  railway domain -s "$Project"
  curl -s https://<that-domain>/health          # -> public-lobby
  curl -s https://<that-domain>/health/orleans  # -> orleans:ok   (silo up; needs the Postgres attached)
  open  https://<that-domain>/login             # passkey sign-up works with zero provider config

If /health/orleans fails or the deploy log shows "connection string 'postgres-database' is not set",
attach the Postgres service + pre-deploy command as described at the top of this script.
"@
