#!/usr/bin/env bash
#
# start-db.sh — start the local SpacetimeDB server and populate it as needed.
#
# Unlike the throwaway `docker run --rm ... start` in HANDOFF.md, this starts the
# server with a PERSISTENT Docker named volume (stdb-data -> container /data), so
# the database AND the server's signing keys survive container restarts. That
# means the seed data sticks around and the CLI token stays valid across restarts
# (you only have to obtain a token once, the first time the volume is created).
#
# Why a named volume (not a host bind mount): SpacetimeDB's commitlog calls fsync,
# which macOS Docker Desktop's bind mounts (gRPC-FUSE/virtiofs) don't support — a
# bind mount panics the server with "Failed to fsync segment". A named volume
# lives in the Linux VM's ext4 and works. It's root-owned, so the server runs as
# --user root (the image's default 'spacetime' user can't write to it).
#
# It is idempotent: run it any time.
#   - server already running -> leave it          - DB already seeded -> nothing
#   - server down            -> start (persistent) - DB missing/empty  -> publish
#
# Auth: a fresh server signs tokens with keys it generates into /data. If the
# stored CLI token isn't valid for this server (first run, or .stdb-data was
# wiped), this mints a server-issued token from POST /v1/identity and stores it.
#
# Usage:
#   scripts/start-db.sh             # start + populate-if-needed
#   scripts/start-db.sh --reset     # also force a fresh publish (--delete-data)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DB="${STDB_DB:-stellar-allegiance}"
PORT="${STDB_PORT:-3001}"
SERVER="http://localhost:${PORT}"
IMAGE="${STDB_IMAGE:-clockworklabs/spacetime}"
CONTAINER="${STDB_CONTAINER:-stdb}"
CONFIG="${REPO_ROOT}/.stdb-config"
VOLUME="${STDB_VOLUME:-stdb-data}"

RESET=""
[[ "${1:-}" == "--reset" ]] && RESET="--reset"

mkdir -p "${CONFIG}"

stdb() {  # run the CLI in a throwaway container with config + host network
  docker run --rm \
    -v "${CONFIG}":/home/spacetime/.config/spacetime --network host "${IMAGE}" "$@"
}

# Mint a token from the local server and store it in .stdb-config. The local
# server doesn't implement the spacetimedb.com web login flow, so we ask it
# directly for an identity+token. One-time per fresh data dir; the identity that
# first publishes becomes the DB owner, and persists because /data persists.
login_local() {
  echo "[start] obtaining a server-issued token from ${SERVER} ..."
  local tok
  tok="$(curl -sf -X POST "${SERVER}/v1/identity" \
        | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
  if [[ -z "${tok}" ]]; then
    echo "[start] ERROR: could not mint a token from ${SERVER}/v1/identity" >&2
    return 1
  fi
  stdb login --token "${tok}" >/dev/null
  echo "[start] token stored in .stdb-config"
}

# Publish via populate-db.sh; if it fails on auth, mint a token and retry once.
publish_with_auth_retry() {
  local out
  if out="$("${REPO_ROOT}/scripts/populate-db.sh" ${RESET} 2>&1)"; then
    echo "${out}"; return 0
  fi
  echo "${out}"
  if grep -qiE '401|unauthorized|invalid token|invalidsignature|403' <<<"${out}"; then
    login_local || return 1
    "${REPO_ROOT}/scripts/populate-db.sh" ${RESET}
  else
    return 1
  fi
}

# 1. Start the server container if it isn't already running.
if docker ps --filter "name=^${CONTAINER}$" --format '{{.Names}}' | grep -q "^${CONTAINER}$"; then
  echo "[start] server '${CONTAINER}' already running"
else
  docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true   # clear a stopped same-name container
  docker volume create "${VOLUME}" >/dev/null
  echo "[start] starting '${CONTAINER}' on :${PORT} with persistent volume '${VOLUME}'"
  docker run -d --name "${CONTAINER}" --user root \
    -p "${PORT}:3000" \
    -v "${VOLUME}":/data \
    "${IMAGE}" start --data-dir /data --listen-addr 0.0.0.0:3000 >/dev/null
fi

# 2. Wait until it answers.
echo -n "[start] waiting for server "
for i in $(seq 1 30); do
  if docker run --rm --network host "${IMAGE}" server ping "${SERVER}" >/dev/null 2>&1; then
    echo "— online"; break
  fi
  echo -n "."; sleep 1
  if [[ "${i}" -eq 30 ]]; then
    echo; echo "[start] ERROR: server did not come up on ${SERVER}" >&2; exit 1
  fi
done

# 3. Populate as needed. A populated DB has the Match singleton (id 0). If the
#    probe fails (DB missing, or token not valid for this server) or comes back
#    empty, (re)publish to seed it. --reset always republishes.
if [[ -n "${RESET}" ]]; then
  publish_with_auth_retry || exit 1
elif probe="$(stdb sql "${DB}" "SELECT id FROM Match" --server "${SERVER}" 2>/dev/null)" \
     && grep -qE '^[[:space:]]*0[[:space:]]*$' <<<"${probe}"; then
  echo "[start] '${DB}' already populated — nothing to do"
else
  publish_with_auth_retry || exit 1
fi

echo "[start] ready: ${SERVER} (db '${DB}', container '${CONTAINER}')"
