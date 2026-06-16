#!/usr/bin/env bash
# godot-bin.sh — shared Godot-executable resolver, *sourced* by the other scripts.
#
# After `source scripts/godot-bin.sh; godot_resolve` the variable $GODOT holds a runnable
# Godot 4 .NET ("mono") binary. Resolution order:
#   1. a preset $GODOT (the VS Code tasks set this from the `godot.executablePath` setting;
#      CI / shells can export it directly). Empty string = treated as unset.
#   2. `godot-mono` / `godot4` / `godot` on PATH.
#   3. standard install locations (macOS .app bundles, Windows, scoop).
# Returns non-zero (and prints guidance) if nothing usable is found, so callers can bail.
#
# NOTE: $GODOT must be the Godot *executable*, not the macOS `.app` folder that the
# godot-tools extension's `godotTools.editorPath.godot4` setting points at.

godot_resolve() {
  # 1. explicit override (skip if empty, e.g. the task passed an unset config value)
  if [ -n "${GODOT:-}" ]; then
    if command -v "$GODOT" >/dev/null 2>&1 || [ -x "$GODOT" ]; then return 0; fi
    echo "[godot] GODOT='$GODOT' is not an executable — falling back to auto-detect." >&2
  fi

  # 2. on PATH
  local c
  for c in godot-mono godot4 godot; do
    if command -v "$c" >/dev/null 2>&1; then GODOT="$c"; return 0; fi
  done

  # 3. standard install locations (macOS / Windows / scoop). Globs that don't match expand
  #    to themselves, so the -x test filters them out.
  local p
  for p in \
    "/Applications/Godot_mono.app/Contents/MacOS/Godot" \
    "/Applications/Godot.app/Contents/MacOS/Godot" \
    "$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot" \
    "/c/Program Files/Godot/"*mono*/Godot*.exe \
    "/c/Program Files/Godot_mono/Godot"*.exe \
    "$HOME/scoop/apps/godot-mono/current/"Godot*.exe \
    "$HOME/scoop/apps/godot/current/"Godot*.exe ; do
    if [ -x "$p" ]; then GODOT="$p"; return 0; fi
  done

  echo "[godot] No Godot .NET (mono) executable found." >&2
  echo "[godot] Set the 'godot.executablePath' VS Code setting (User scope), or export GODOT=/path/to/Godot." >&2
  return 1
}
