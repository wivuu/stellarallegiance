#!/usr/bin/env bash
#
# populate-db.sh — publish the module to the local SpacetimeDB server.
#
# Publishing compiles the module and runs its `Init` reducer, which SEEDS the
# database: 1 Match (Lobby, tick 0), 2 Bases, and the asteroid field. So
# "populating" the DB is just publishing the module. After it succeeds, the
# seeded Match row is printed as confirmation.
#
# Usage:
#   scripts/populate-db.sh            # publish (seed only if DB is empty/new)
#   scripts/populate-db.sh --reset    # wipe existing data first (--delete-data)
#
# Requires: the server running and the CLI logged in. Use publish-local.sh, which
# starts the server (persistent) and calls this only when needed.
#
# NOTE: this does NOT regenerate client bindings. After a *schema* change run:
#   docker run --rm -v "$(pwd)":/workspace -w /workspace clockworklabs/spacetime \
#     generate --lang csharp --out-dir client/module_bindings \
#     --module-path module/spacetimedb --yes
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DB="${STDB_DB:-stellar-allegiance}"
PORT="${STDB_PORT:-3001}"
SERVER="http://localhost:${PORT}"
IMAGE="${STDB_IMAGE:-clockworklabs/spacetime}"
CONFIG="${REPO_ROOT}/.stdb-config"

RESET_FLAG=""
if [[ "${1:-}" == "--reset" ]]; then
  RESET_FLAG="--delete-data"
  echo "[populate] --reset: existing data will be DESTROYED"
fi

echo "[populate] publishing module -> '${DB}' @ ${SERVER}"
# Mount the whole repo (not just module/) so the module's <ProjectReference> to
# ../../shared/Shared.csproj resolves inside the container; -w is the module dir
# so spacetime.json's "module-path": "./spacetimedb" still points at the module.
docker run --rm \
  -v "${CONFIG}":/home/spacetime/.config/spacetime \
  -v "${REPO_ROOT}":/workspace -w /workspace/module --network host \
  "${IMAGE}" publish "${DB}" --server "${SERVER}" ${RESET_FLAG} --yes

echo "[populate] seeded Match row:"
docker run --rm \
  -v "${CONFIG}":/home/spacetime/.config/spacetime --network host \
  "${IMAGE}" sql "${DB}" "SELECT id,tick,phase FROM Match" --server "${SERVER}"

echo "[populate] done."
