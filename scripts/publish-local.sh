#!/usr/bin/env bash
#
# publish-local.sh — start the local SpacetimeDB server and populate it as needed.
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
#   scripts/publish-local.sh             # start + populate-if-needed
#   scripts/publish-local.sh --reset     # force a fresh publish; if the local DB was
#                                   # seeded under a different identity (so the
#                                   # server refuses "reset database"), wipe the
#                                   # data volume and reseed as the current owner
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
# $1: extra flags for populate-db.sh ("--reset" for a wipe; empty for a plain
# publish, i.e. hot-swap an existing DB or seed a fresh one).
publish_with_auth_retry() {
  local flags="${1:-}"
  local out
  if out="$("${REPO_ROOT}/scripts/populate-db.sh" ${flags} 2>&1)"; then
    echo "${out}"; return 0
  fi
  echo "${out}"
  if grep -qiE '401|unauthorized|invalid token|invalidsignature|403' <<<"${out}"; then
    login_local || return 1
    "${REPO_ROOT}/scripts/populate-db.sh" ${flags}
  else
    return 1
  fi
}

# Start the server container if it isn't already running.
start_server() {
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
}

# Block until the server answers (give up after 30s).
wait_for_server() {
  echo -n "[start] waiting for server "
  for i in $(seq 1 30); do
    if docker run --rm --network host "${IMAGE}" server ping "${SERVER}" >/dev/null 2>&1; then
      echo "— online"; return 0
    fi
    echo -n "."; sleep 1
  done
  echo; echo "[start] ERROR: server did not come up on ${SERVER}" >&2; return 1
}

# Destroy the container and its data volume, then recreate an empty one. This is
# the only way to reset a DB owned by a *different* identity: the volume holds
# both the DB data and the server's signing keys, so wiping it drops the old
# owner (and invalidates the stored token). The next publish makes the current
# identity the owner.
recreate_volume() {
  echo "[start] recreating data volume '${VOLUME}' (wipes DB + server keys)"
  docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true
  docker volume rm "${VOLUME}" >/dev/null 2>&1 || true
  docker volume create "${VOLUME}" >/dev/null
}

# 1. Start the server and wait until it answers.
start_server
wait_for_server || exit 1

# 2. Populate as needed. A populated DB has the Match singleton (id 0). If the
#    probe fails (DB missing, or token not valid for this server) or comes back
#    empty, (re)publish to seed it.
if [[ -n "${RESET}" ]]; then
  # Try an in-place reset first: it preserves the server's keys/identity and
  # works whenever the current token owns the DB. If it fails — most often
  # because the volume was first seeded under a *different* identity, so the
  # server refuses "reset database" — wipe the whole data volume and republish
  # fresh, which makes the current identity the owner.
  if ! publish_with_auth_retry --reset; then
    echo "[start] in-place reset failed (DB likely owned by another identity) — wiping volume"
    recreate_volume
    start_server
    wait_for_server || exit 1
    login_local || exit 1          # old token won't verify against regenerated keys
    publish_with_auth_retry || exit 1
  fi
elif probe="$(stdb sql "${DB}" "SELECT id FROM Match" --server "${SERVER}" 2>/dev/null)" \
     && grep -qE '^[[:space:]]*0[[:space:]]*$' <<<"${probe}"; then
  echo "[start] '${DB}' already populated — nothing to do"
else
  publish_with_auth_retry || exit 1
fi

echo "[start] ready: ${SERVER} (db '${DB}', container '${CONTAINER}')"
