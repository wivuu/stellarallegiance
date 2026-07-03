#!/usr/bin/env bash
# Deploy (or UPDATE) the stellarallegiance PUBLIC LOBBY on Railway (server registry + WebRTC signaling).
#
# Prereqs: Railway CLI installed and logged in (`railway login`).
# Usage:
#   scripts/deploy-railway-lobby.sh [project-name]
#
# Re-running with the SAME name UPDATES the existing Railway project (redeploys it) instead of
# creating a duplicate lobby. Build: public-lobby/Dockerfile from the repo-root context.
# Export STUN_URL to override the public STUN default handed to WebRTC clients.
#
# NOTE: the default lobby URL is baked into the server/client as
# https://wivuu-public-lobby-production.up.railway.app — keep the project name `wivuu-public-lobby`
# (or update that default in LobbyRegistrar.cs / ConnectionManager.cs) so clients find this lobby.
set -euo pipefail

PROJECT="${1:-wivuu-public-lobby}"
DOCKERFILE="public-lobby/Dockerfile"

cd "$(dirname "$0")/.."   # repo root — public-lobby/Dockerfile builds from here
command -v railway >/dev/null || { echo "railway CLI not found; install it and run \`railway login\` first." >&2; exit 1; }

# Idempotency: is a project with this name already there? (so re-runs UPDATE, not duplicate.)
# Code via -c so stdin stays free for the piped `railway list --json`.
PROJECT_ID="$(railway list --json 2>/dev/null | python3 -c '
import sys, json
target = sys.argv[1]
try: data = json.load(sys.stdin)
except Exception: sys.exit(0)
hits = []
def walk(o):
    if isinstance(o, dict):
        if o.get("name") == target and "id" in o: hits.append(o["id"])
        for v in o.values(): walk(v)
    elif isinstance(o, list):
        for v in o: walk(v)
walk(data)
print(hits[0] if hits else "")
' "$PROJECT")"

if [ -n "$PROJECT_ID" ]; then
  echo "==> Updating existing project '$PROJECT' ($PROJECT_ID)"
  railway variable set "RAILWAY_DOCKERFILE_PATH=$DOCKERFILE" ${STUN_URL:+"STUN_URL=$STUN_URL"} \
    -p "$PROJECT_ID" -s "$PROJECT" -e production --skip-deploys
  railway up -c -p "$PROJECT_ID" -s "$PROJECT" -e production
else
  echo "==> Creating new project '$PROJECT'"
  railway init -n "$PROJECT"
  railway add --service "$PROJECT" \
    --variables "RAILWAY_DOCKERFILE_PATH=$DOCKERFILE" \
    ${STUN_URL:+--variables "STUN_URL=$STUN_URL"}
  railway domain --service "$PROJECT"
  railway up -c --service "$PROJECT"
fi

cat <<DONE

Done. Show the domain and verify:
  railway domain -s "$PROJECT"
  curl -s https://<that-domain>/health     # -> public-lobby
DONE
