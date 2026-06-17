#!/bin/bash
# Build and launch the Godot client. By DEFAULT it opens the server browser pointed at the public
# lobby (PUBLIC_LOBBY, default 192.168.1.101:8091) so you can pick a server; pass --local to skip
# the browser and connect straight to localhost. Builds the client C# fresh so godot-mono can't
# launch a stale assembly against a rebuilt server (silent protocol skew). Other args pass through
# to Godot.
#   scripts/run-client.sh                          # public lobby server browser
#   scripts/run-client.sh --local                  # connect directly to localhost:8090
#   scripts/run-client.sh --host some.host:8090    # connect to a specific server
#   PUBLIC_LOBBY=host:port scripts/run-client.sh   # browse a different lobby
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Pull our own --local switch out of the args; everything else passes through to Godot.
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
  # Direct connect, no lobby. A later user-supplied --host still wins (the client takes the last).
  echo "[run-client] --local: connecting directly to localhost:${SIM_PORT:-8090}"
  set -- --host "localhost:${SIM_PORT:-8090}" "$@"
else
  : "${PUBLIC_LOBBY:=192.168.1.101:8091}"
  export PUBLIC_LOBBY
  echo "[run-client] public lobby ${PUBLIC_LOBBY} server browser (use --local for direct localhost)"
fi

# shellcheck source=scripts/godot-bin.sh
source "${REPO_ROOT}/scripts/godot-bin.sh"
godot_resolve || exit 1

echo "[run-client] building client C# (Debug)"
dotnet build "${REPO_ROOT}/client/wivuullegiance.csproj" -c Debug

cd "${REPO_ROOT}"
exec "${GODOT}" --path client/ "$@"
