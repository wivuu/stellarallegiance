#!/bin/bash
# Build and run the standalone authoritative sim server (server/). It hosts the lobby and the
# 20 Hz match; clients connect directly by ip:port and download all content from it.
#
# Rebuilds every launch (and kills any stale process on the port) so the running binary can
# never silently serve old-format snapshots — the failure the protocol-version handshake also
# guards against. Open by default; flags pass through to the server:
#   scripts/run-server.sh                       # open, :8090, lobby ready-up
#   scripts/run-server.sh --secret hunter2      # require a shared-secret password
#   scripts/run-server.sh --autostart           # skip ready-up (bots / benchmarking)
#   SIM_PORT=9000 scripts/run-server.sh         # different port
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_PORT="${SIM_PORT:-8090}"

# Stop whatever is already on the port so the rebuilt binary takes over cleanly.
STALE="$(lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN 2>/dev/null || true)"
if [[ -n "${STALE}" ]]; then
  echo "[run-server] stopping stale sim server on :${SIM_PORT} (pid ${STALE})"
  kill ${STALE} 2>/dev/null || true
  for _ in $(seq 1 10); do
    lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN >/dev/null 2>&1 || break
    sleep 0.5
  done
fi

echo "[run-server] rebuilding server/ (-c Release)"
dotnet build "${REPO_ROOT}/server/SimServer.csproj" -c Release

echo "[run-server] starting on :${SIM_PORT}"
cd "${REPO_ROOT}"
exec dotnet run --project server -c Release --no-build -- --port "${SIM_PORT}" "$@"
