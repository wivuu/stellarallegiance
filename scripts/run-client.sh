#!/bin/bash
# Build and launch the Godot client. By DEFAULT it opens the server browser pointed at the public
# lobby (PUBLIC_LOBBY, default https://wivuu-public-lobby-production.up.railway.app) so you can pick
# a server; pass --local to skip
# the browser and connect straight to localhost. Builds the client C# fresh so godot-mono can't
# launch a stale assembly against a rebuilt server (silent protocol skew). Other args pass through
# to Godot.
#   scripts/run-client.sh                          # public lobby server browser
#   scripts/run-client.sh --local                  # connect directly to localhost:8090
#   scripts/run-client.sh --host some.host:8090    # connect to a specific server
#   scripts/run-client.sh --write-movie out.avi    # record Movie Maker video to out.avi
#   PUBLIC_LOBBY=host:port scripts/run-client.sh   # browse a different lobby
#
# Movie Maker perf: recording cost is dominated by per-frame game sim + MJPEG encoding,
# both of which scale with the frame count (render itself is ~free). So when --write-movie
# is active we default the capture to 30 fps, which ~halves total record time vs Godot's
# 60 fps default. Override with MOVIE_FPS (e.g. MOVIE_FPS=60 for smoother output, or a
# lower value for faster grabs). MOVIE_RESOLUTION (e.g. 1280x720) additionally shrinks the
# captured frame to cut encoding time further — off by default since it's lossy. An explicit
# --fixed-fps / --resolution passed on the command line always wins over these defaults.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Pull our own --local switch out of the args; everything else passes through to Godot.
# --write-movie <path> turns on Godot's Movie Maker mode, recording to <path> (path
# defaults to recording.avi in the working dir if given without a value).
LOCAL=0
MOVIE_PATH=""
new_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --local) LOCAL=1 ;;
    --write-movie)
      # optional value: consume the next arg as the path unless it looks like a flag
      if [[ $# -gt 1 && "$2" != -* ]]; then MOVIE_PATH="$2"; shift; else MOVIE_PATH="recording.avi"; fi
      ;;
    --write-movie=*) MOVIE_PATH="${1#*=}" ;;
    *) new_args+=("$1") ;;
  esac
  shift
done
set -- ${new_args[@]+"${new_args[@]}"}

if [[ "${LOCAL}" == "1" ]]; then
  # Direct connect, no lobby. A later user-supplied --host still wins (the client takes the last).
  echo "[run-client] --local: connecting directly to localhost:${SIM_PORT:-8090}"
  set -- --host "localhost:${SIM_PORT:-8090}" "$@"
else
  : "${PUBLIC_LOBBY:=https://wivuu-public-lobby-production.up.railway.app}"
  export PUBLIC_LOBBY
  echo "[run-client] public lobby ${PUBLIC_LOBBY} server browser (use --local for direct localhost)"
fi

# shellcheck source=scripts/godot-bin.sh
source "${REPO_ROOT}/scripts/godot-bin.sh"
godot_resolve || exit 1

echo "[run-client] building client C# (Debug)"
dotnet build "${REPO_ROOT}/client/wivuullegiance.csproj" -c Debug

cd "${REPO_ROOT}"
if [[ -n "${MOVIE_PATH}" ]]; then
  # Godot resolves relative --write-movie paths against res:// (client/), so anchor
  # a non-absolute path to the caller's original working dir to avoid surprises.
  case "${MOVIE_PATH}" in
    /*) : ;;
    *) MOVIE_PATH="${OLDPWD:-$PWD}/${MOVIE_PATH}" ;;
  esac
  echo "[run-client] Movie Maker mode: recording to ${MOVIE_PATH}"

  # Perf defaults, only applied when the user hasn't set them explicitly (last wins in Godot,
  # but we skip injecting to keep the command line honest / avoid dupes).
  has_arg() { local f="$1"; shift; for a in "$@"; do [[ "$a" == "$f" || "$a" == "$f="* ]] && return 0; done; return 1; }

  : "${MOVIE_FPS:=30}"
  if [[ -n "${MOVIE_FPS}" ]] && ! has_arg --fixed-fps "$@"; then
    echo "[run-client] capturing at ${MOVIE_FPS} fps (set MOVIE_FPS to override, or pass --fixed-fps)"
    set -- --fixed-fps "${MOVIE_FPS}" "$@"
  fi
  if [[ -n "${MOVIE_RESOLUTION:-}" ]] && ! has_arg --resolution "$@"; then
    echo "[run-client] capturing at ${MOVIE_RESOLUTION} (set MOVIE_RESOLUTION= to disable)"
    set -- --resolution "${MOVIE_RESOLUTION}" "$@"
  fi

  set -- --write-movie "${MOVIE_PATH}" "$@"
fi
exec "${GODOT}" --path client/ "$@"
