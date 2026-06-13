#!/bin/bash
# Launch the NATIVE-SIM path: boot the Phase-1 authoritative sim server (server/) and run the
# Godot client pointed at it via SIM_URI (dev direct-connect, no join token — the credentialed
# lobby handoff uses set_sim_endpoint instead). SpacetimeDB must still be running for
# defs/chat/lobby, same as start-client.sh; pass --maincloud to point the CLIENT's STDB
# connection at Maincloud.
#
# The sim server is RESTARTED fresh every launch (any stale process on the port is killed
# first), so a rebuilt server can never go stale and silently serve old-format snapshots —
# the failure mode the protocol-version handshake now also guards against.
#
# Usage:
#   scripts/start-native-client.sh                # local STDB + local sim server
#   scripts/start-native-client.sh --maincloud    # Maincloud STDB + local sim server
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_PORT="${SIM_PORT:-8090}"
SIM_LOG="${TMPDIR:-/tmp}/simserver.log"

ARGS=""
if [[ "${1:-}" == "--maincloud" ]]; then
  ARGS="--maincloud"
fi

# Always run the current build: stop whatever is on the port, then start a fresh server.
STALE="$(lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN 2>/dev/null || true)"
if [[ -n "${STALE}" ]]; then
  echo "[native] stopping stale sim server on :${SIM_PORT} (pid ${STALE})"
  kill ${STALE} 2>/dev/null || true
  for _ in $(seq 1 10); do
    lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN >/dev/null 2>&1 || break
    sleep 0.5
  done
fi

echo "[native] starting sim server on :${SIM_PORT} (log: ${SIM_LOG})"
(cd "${REPO_ROOT}" && nohup dotnet run --project server -c Release -- --port "${SIM_PORT}" \
    > "${SIM_LOG}" 2>&1 &)
for _ in $(seq 1 30); do
  nc -z localhost "${SIM_PORT}" 2>/dev/null && break
  sleep 1
done
if ! nc -z localhost "${SIM_PORT}" 2>/dev/null; then
  echo "[native] ERROR: sim server did not come up — see ${SIM_LOG}" >&2
  exit 1
fi

export SIM_URI="${SIM_URI:-ws://localhost:${SIM_PORT}/game}"
echo "[native] client -> ${SIM_URI}"

# Build the client C# fresh so godot-mono can't launch a stale assembly against the
# just-rebuilt sim server — the silent protocol-version skew (e.g. old v3 client vs v4 server).
echo "[native] building client C# (Debug)"
dotnet build "${REPO_ROOT}/client/wivuullegiance.csproj" -c Debug

cd "${REPO_ROOT}"
godot-mono --path client/ $ARGS
