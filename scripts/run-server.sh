#!/bin/bash
# Build and run the standalone authoritative sim server (server/). It hosts the lobby and the
# 20 Hz match; clients connect directly by ip:port and download all content from it.
#
# By DEFAULT it publishes itself to the hosted public lobby (PUBLIC_LOBBY, default
# https://wivuu-public-lobby-production.up.railway.app)
# so clients can discover and WebRTC-join it; pass --local to stay private (direct ws:// only,
# no lobby registration).
#
# Rebuilds every launch (and kills any stale process on the port) so the running binary can
# never silently serve old-format snapshots — the failure the protocol-version handshake also
# guards against. Other flags pass through to the server:
#   scripts/run-server.sh                       # publish to the public lobby, :8090
#   scripts/run-server.sh --local               # private: local/LAN only, no lobby
#   scripts/run-server.sh --secret hunter2      # require a shared-secret password
#   scripts/run-server.sh --autostart           # skip ready-up (bots / benchmarking)
#   SIM_PUBLIC_NAME="My Server" scripts/run-server.sh   # custom lobby name (else hostname)
#   SIM_PORT=9000 scripts/run-server.sh         # different port
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_PORT="${SIM_PORT:-8090}"

# Pull our own --local switch out of the args; everything else passes through to the server.
LOCAL=0
new_args=()
for a in "$@"; do
  case "$a" in
    --local) LOCAL=1 ;;
    *) new_args+=("$a") ;;
  esac
done
set -- ${new_args[@]+"${new_args[@]}"}

if [[ "${LOCAL}" == "1" ]]; then
  # Private: empty SIM_PUBLIC_NAME means the server never registers with any lobby.
  export SIM_PUBLIC_NAME=""
  echo "[run-server] --local: private (not registering with the public lobby)"
else
  # Public: default the lobby + a name (hostname, trimmed to the 50-char cap) so it registers.
  : "${PUBLIC_LOBBY:=https://wivuu-public-lobby-production.up.railway.app}"
  : "${SIM_PUBLIC_NAME:=$(hostname | cut -c1-50)}"
  export PUBLIC_LOBBY SIM_PUBLIC_NAME
  echo "[run-server] publishing to public lobby ${PUBLIC_LOBBY} as \"${SIM_PUBLIC_NAME}\" (use --local to stay private)"
fi

# Stop whatever is already on the port so the rebuilt binary takes over cleanly.
if command -v lsof >/dev/null 2>&1; then
  STALE="$(lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN 2>/dev/null || true)"
else
  STALE="$(netstat -ano 2>/dev/null | grep ":${SIM_PORT}.*LISTENING" | awk '{print $NF}' | head -1 || true)"
fi
if [[ -n "${STALE}" ]]; then
  echo "[run-server] stopping stale sim server on :${SIM_PORT} (pid ${STALE})"
  kill "${STALE}" 2>/dev/null || taskkill /PID "${STALE}" /F >/dev/null 2>&1 || true
  for _ in $(seq 1 10); do
    if command -v lsof >/dev/null 2>&1; then
      lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN >/dev/null 2>&1 || break
    else
      netstat -ano 2>/dev/null | grep -q ":${SIM_PORT}.*LISTENING" || break
    fi
    sleep 0.5
  done
fi

echo "[run-server] rebuilding server/ (-c Release)"
dotnet build "${REPO_ROOT}/server/SimServer.csproj" -c Release

echo "[run-server] starting on :${SIM_PORT}"
cd "${REPO_ROOT}"
exec dotnet run --project server -c Release --no-build -- --port "${SIM_PORT}" "$@"
