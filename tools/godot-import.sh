#!/usr/bin/env bash
#
# godot-import.sh — regenerate Godot's GLB import artifacts.
#
# Each asset's `.glb` is the ONLY committed file (it embeds its own textures). Godot's
# import sidecars (`*.glb.import`, extracted `*_N.png`, `*.png.import`) are gitignored
# and regenerated here, so a fresh clone (or a newly-added `.glb`) needs an import before
# `res://...glb` resolves — otherwise meshes silently fall back to procedural placeholders.
#
#   tools/godot-import.sh           # import only if something is missing (fresh clone / new asset)
#   tools/godot-import.sh --force   # always reimport (after editing an existing asset)
#
# The Godot (.NET/"mono") executable is resolved by scripts/godot-bin.sh — $GODOT, then PATH,
# then standard install locations. Set the `godot.executablePath` VS Code setting to override.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
client="$repo/client"
force="${1:-}"

# shellcheck source=scripts/godot-bin.sh
source "$repo/scripts/godot-bin.sh"

needs_import() {
  [ "$force" = "--force" ] && return 0
  # Import needed if any .glb lacks its sibling .glb.import (fresh clone or a new asset).
  while IFS= read -r glb; do
    [ -f "$glb.import" ] || return 0
  done < <(find "$client/assets" -name '*.glb')
  return 1
}

if ! needs_import; then
  echo "[godot-import] assets already imported — nothing to do (pass --force to reimport)."
  exit 0
fi

if ! godot_resolve; then
  # Exit 0 so an auto-run on folder-open doesn't surface as a task failure on machines
  # without Godot (godot_resolve already printed how to set the path).
  exit 0
fi

echo "[godot-import] importing assets with: $GODOT"
"$GODOT" --headless --import --path "$client"
echo "[godot-import] done."
