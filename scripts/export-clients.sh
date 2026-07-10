#!/usr/bin/env bash
#
# export-clients.sh — export the Godot client for macOS + Windows + Linux and
# package them for tester distribution.
#
# macOS gotcha: Godot's built-in macOS signing always enables the *hardened
# runtime* (codesign flags 0x10002). That's only meaningful once you notarize;
# on an un-notarized ad-hoc build it makes the kernel SIGKILL the app at launch
# ("application can't be opened", no crash log). So we export to a .app, strip
# Godot's signature, and re-sign plain ad-hoc (flags 0x2) — which Apple Silicon
# still requires to run at all — then zip with `ditto` to preserve the signature
# and framework symlinks (plain `zip` corrupts them).
#
# Testers still need to clear quarantine once on the downloaded zip:
#   xattr -dr com.apple.quarantine stellarallegiance.app
#
# Usage: scripts/export-clients.sh   (run from anywhere; needs godot-mono on PATH)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLIENT="${REPO_ROOT}/client"
OUT="${REPO_ROOT}/build"

# shellcheck source=scripts/godot-bin.sh
source "${REPO_ROOT}/scripts/godot-bin.sh"
godot_resolve || exit 1

mkdir -p "${OUT}/mac" "${OUT}/win" "${OUT}/linux"

# Make sure GLB import sidecars exist before exporting — un-imported assets export "successfully"
# but silently fall back to procedural placeholders at runtime. No-op when already imported.
"${REPO_ROOT}/tools/godot-import.sh"
test -f "${CLIENT}/assets/bases/base.glb.import" || {
  echo "[export] ERROR: GLB import sidecars missing — export would ship placeholder meshes" >&2
  exit 1
}

# Godot's export rebuilds the C# project with `dotnet` under the hood. MSBuild
# node reuse can leave wedged worker processes (e.g. from a different SDK or the
# IDE's C# Dev Kit) that the next build connects to and hangs on. Disabling node
# reuse and clearing any stale servers up front keeps the export from hanging.
# UseSharedCompilation=false tells MSBuild not to spin up / connect to a Roslyn
# compiler server (VBCSCompiler), which is the most common cause of dotnet
# publish hanging inside a Godot headless export.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
pkill -9 -f "VBCSCompiler" >/dev/null 2>&1 || true
pkill -9 -f "MSBuild.dll" >/dev/null 2>&1 || true
dotnet build-server shutdown >/dev/null 2>&1 || true

# The macOS .app export + ad-hoc re-signing only works on macOS (codesign/ditto are
# macOS-only). On Windows/Linux we skip it and still produce the Windows + Linux builds.
if [ "$(uname -s)" = "Darwin" ]; then
  echo "[export] macOS .app ..."
  APP="${OUT}/mac/stellarallegiance.app"
  rm -rf "${APP}"
  "${GODOT}" --headless --path "${CLIENT}" --export-release "macOS" "${APP}"

  echo "[export] re-signing macOS app as plain ad-hoc (no hardened runtime) ..."
  codesign --remove-signature "${APP}" 2>/dev/null || true
  codesign --force --deep --sign - "${APP}"
  codesign --verify --deep --strict "${APP}" && echo "[export]   signature valid"

  echo "[export] zipping macOS app with ditto ..."
  rm -f "${OUT}/mac/stellarallegiance-macos.zip"
  ditto -c -k --keepParent "${APP}" "${OUT}/mac/stellarallegiance-macos.zip"
else
  echo "[export] skipping macOS build (only exportable from macOS — needs codesign/ditto)"
fi

echo "[export] Windows .exe ..."
"${GODOT}" --headless --path "${CLIENT}" --export-release "Windows Desktop" "${OUT}/win/stellarallegiance.exe"

echo "[export] zipping Windows folder (testers need the whole folder) ..."
rm -f "${OUT}/stellarallegiance-windows.zip"
( cd "${OUT}/win" && zip -rq "${OUT}/stellarallegiance-windows.zip" . )

echo "[export] Linux .x86_64 ..."
"${GODOT}" --headless --path "${CLIENT}" --export-release "Linux" "${OUT}/linux/stellarallegiance.x86_64"
chmod +x "${OUT}/linux/stellarallegiance.x86_64"

echo "[export] zipping Linux folder (testers need the whole folder) ..."
rm -f "${OUT}/stellarallegiance-linux.zip"
( cd "${OUT}/linux" && zip -rq "${OUT}/stellarallegiance-linux.zip" . )

echo ""
echo "[export] done:"
echo "  macOS:   ${OUT}/mac/stellarallegiance-macos.zip"
echo "  Windows: ${OUT}/stellarallegiance-windows.zip"
echo "  Linux:   ${OUT}/stellarallegiance-linux.zip"
