#!/usr/bin/env bash
# Render a labeled contact sheet of every GLB in a source folder.
#
#   ./gallery.sh [SRC_DIR] [OUT_PNG] [SIZE] [LIMIT]
#
# Defaults to the repo's pick-assets folder.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

SRC="${1:-$REPO/pick-assets}"
OUT="${2:-$REPO/tools/glb-gallery/glb-gallery.png}"
SIZE="${3:-320}"
LIMIT="${4:-0}"

SRC="$(cd "$SRC" && pwd)"
THUMBS="$HERE/thumbs"

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
[ -x "$GODOT" ] || GODOT="/Applications/Godot.app/Contents/MacOS/Godot"

echo "== rendering thumbnails with Godot =="
mkdir -p "$THUMBS"
"$GODOT" --path "$HERE" --resolution 400x400 --rendering-driver metal \
  -- --src "$SRC" --out "$THUMBS" --size "$SIZE" --limit "$LIMIT"

echo "== composing grid =="
python3 "$HERE/compose.py" --thumbs "$THUMBS" --out "$OUT" --cols 10 --cell "$SIZE"

echo "== done: $OUT =="
