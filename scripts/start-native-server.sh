#!/bin/bash
# Launch the Phase-1 authoritative sim server (server/) standalone — the HOST side of the
# two-machine playtest. Clients reach it over the lobby handoff (set_sim_endpoint + JoinToken)
# or a dev SIM_URI; this script just owns the server process.
#
# Every launch CLEANS UP and REBUILDS before starting, so the running binary can never go
# stale and silently serve old-format snapshots (the failure mode the protocol-version
# handshake also guards against — e.g. after the v4 quantized-record change):
#   1. kill whatever is already listening on the sim port,
#   2. rebuild server/ (-c Release), failing fast if it doesn't compile,
#   3. run the fresh server in the foreground (logs to this terminal; Ctrl-C stops it).
#
# Credentialed mode is on by default (--secret), so only clients whose JoinToken the STDB
# module minted for the same secret may connect; override the secret via SIM_SECRET. Any extra
# arguments are passed through to the server (e.g. --seed 42, --port 9000).
#
# Usage:
#   scripts/start-native-server.sh                     # :8090, secret "S"
#   SIM_SECRET=hunter2 scripts/start-native-server.sh  # custom secret
#   scripts/start-native-server.sh --seed 42           # extra server args pass through
#   SIM_PORT=9000 scripts/start-native-server.sh       # different port
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_PORT="${SIM_PORT:-8090}"
SIM_SECRET="${SIM_SECRET:-S}"

# 1. Stop whatever is already on the port so the rebuilt binary takes over cleanly.
STALE="$(lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN 2>/dev/null || true)"
if [[ -n "${STALE}" ]]; then
  echo "[native-server] stopping stale sim server on :${SIM_PORT} (pid ${STALE})"
  kill ${STALE} 2>/dev/null || true
  for _ in $(seq 1 10); do
    lsof -tnP -iTCP:"${SIM_PORT}" -sTCP:LISTEN >/dev/null 2>&1 || break
    sleep 0.5
  done
fi

# 2. Rebuild — fail fast before we claim the port if the server doesn't compile.
echo "[native-server] rebuilding server/ (-c Release)"
dotnet build "${REPO_ROOT}/server/SimServer.csproj" -c Release

# 3. Run the fresh server in the foreground.
echo "[native-server] starting on :${SIM_PORT} (credentialed, secret set)"
cd "${REPO_ROOT}"
exec dotnet run --project server -c Release --no-build -- \
  --port "${SIM_PORT}" --secret "${SIM_SECRET}" "$@"
