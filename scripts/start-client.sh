#!/bin/bash
# Launch the Godot client against SpacetimeDB (lobby/defs/chat + the in-module sim).
#
# Pass --native to instead use the Phase-1 native sim server (server/): this hands off to
# scripts/start-native-client.sh, which boots a fresh sim server and points the client at it
# via SIM_URI. (Setting SIM_URI yourself before running also switches the client to native
# mode — the client reads it from the environment.)
#
# Usage:
#   scripts/start-client.sh                    # local STDB
#   scripts/start-client.sh --maincloud        # Maincloud STDB
#   scripts/start-client.sh --native           # local STDB + native sim server
#   scripts/start-client.sh --native --maincloud
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

NATIVE=0
ARGS=""
for arg in "$@"; do
  case "$arg" in
    --native)    NATIVE=1 ;;
    --maincloud) ARGS="--maincloud" ;;
    *) echo "[client] unknown argument: $arg" >&2; exit 1 ;;
  esac
done

# --native: delegate to the dedicated native launcher (boots the sim server, sets SIM_URI).
if [[ "${NATIVE}" == "1" ]]; then
  exec "${SCRIPT_DIR}/start-native-client.sh" ${ARGS}
fi

# Build the client C# fresh so godot-mono can't launch a stale assembly — the cause of a
# silent protocol-version skew (e.g. an old v3 client against an updated v4 sim server).
echo "[client] building client C# (Debug)"
dotnet build "${REPO_ROOT}/client/wivuullegiance.csproj" -c Debug

cd "${REPO_ROOT}"
godot-mono --path client/ $ARGS
