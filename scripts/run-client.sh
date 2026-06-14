#!/bin/bash
# Build and launch the Godot client. With no --host it opens the server-address input screen
# first; pass --host ip-or-hostname:port to connect straight to a server. Builds the client C#
# fresh so godot-mono can't launch a stale assembly against a rebuilt server (silent protocol
# skew). Extra args pass through to Godot.
#   scripts/run-client.sh                          # show the address-input screen
#   scripts/run-client.sh --host localhost:8090    # connect immediately
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "[run-client] building client C# (Debug)"
dotnet build "${REPO_ROOT}/client/wivuullegiance.csproj" -c Debug

cd "${REPO_ROOT}"
exec godot-mono --path client/ "$@"
