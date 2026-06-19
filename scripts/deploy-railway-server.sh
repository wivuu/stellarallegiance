#!/usr/bin/env bash
# Deploy (or UPDATE) a wivuullegiance GAME SERVER on Railway, published to the default public lobby.
#
# Prereqs: Railway CLI installed and logged in (`railway login`).
# Usage:
#   scripts/deploy-railway-server.sh [project-name]
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
# Override the lobby by exporting PUBLIC_LOBBY before running. Tear down with `railway delete`.
set -euo pipefail

PROJECT="${1:-wivuu-game-server}"
DOCKERFILE="server/Dockerfile"

cd "$(dirname "$0")/.."   # repo root — server/Dockerfile builds from here (needs shared/)
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
  railway variable set "RAILWAY_DOCKERFILE_PATH=$DOCKERFILE" "SIM_PUBLIC_NAME=$PROJECT" \
    -p "$PROJECT_ID" -s "$PROJECT" -e production --skip-deploys
  railway up -c -p "$PROJECT_ID" -s "$PROJECT" -e production
else
  echo "==> Creating new project '$PROJECT'"
  railway init -n "$PROJECT"
  railway add --service "$PROJECT" \
    --variables "RAILWAY_DOCKERFILE_PATH=$DOCKERFILE" \
    --variables "SIM_PUBLIC_NAME=$PROJECT"
  railway domain --service "$PROJECT"   # provision the public domain up front
  railway up -c --service "$PROJECT"
fi

cat <<'DONE'

Done. It should appear (as DIRECT) in the lobby within ~1 min of the container starting:
  curl -s https://wivuu-public-lobby-production.up.railway.app/servers
DONE
