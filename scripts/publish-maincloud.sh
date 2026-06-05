#!/usr/bin/env bash
#
# publish-maincloud.sh — build the module (in Docker) and publish it to Maincloud.
#
# Why two tools: the *native* CLI holds your Maincloud login token (run
# `spacetime login` once), but building the C# module to WASM needs the
# wasi-experimental .NET workload, which the native CLI can't auto-install
# without privileged rights. The clockworklabs/spacetime Docker image already
# has that toolchain. So we build the wasm in Docker, then publish the prebuilt
# binary with the native CLI (`-b`), which only needs the token, not the builder.
#
# Usage:
#   scripts/publish-maincloud.sh            # build + publish (preserves data)
#   scripts/publish-maincloud.sh --reset    # build + publish, wiping ALL data first
#
# Prereqs: `spacetime login` against maincloud (default server), Docker running.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DB="${STDB_DB:-stellar-allegiance}"
IMAGE="${STDB_IMAGE:-clockworklabs/spacetime}"
WASM="module/spacetimedb/bin/Release/net8.0/wasi-wasm/AppBundle/StdbModule.opt.wasm"

DELETE=""
[[ "${1:-}" == "--reset" ]] && DELETE="--delete-data=always"

echo "[maincloud] building module wasm in Docker ..."
# Mount the whole repo (not just module/) so the module's <ProjectReference> to
# ../../shared/Shared.csproj resolves; -w is the module dir so `build` finds the
# spacetimedb/ subdir and writes the wasm to module/spacetimedb/bin/... as before.
docker run --rm -v "${REPO_ROOT}":/workspace -w /workspace/module "${IMAGE}" build

if [[ ! -f "${REPO_ROOT}/${WASM}" ]]; then
  echo "[maincloud] ERROR: built wasm not found at ${WASM}" >&2
  exit 1
fi

echo "[maincloud] publishing prebuilt wasm to Maincloud as '${DB}' ${DELETE:+(RESET)}"
spacetime publish --server maincloud -y ${DELETE} -b "${REPO_ROOT}/${WASM}" "${DB}"

echo "[maincloud] seeded Match row:"
spacetime sql --server maincloud "${DB}" "SELECT id,tick,phase FROM Match"
echo "[maincloud] done. Dashboard: https://spacetimedb.com/${DB}"
