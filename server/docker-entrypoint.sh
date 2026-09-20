#!/bin/sh
# docker-entrypoint.sh — PID 1 of the release image (server/Dockerfile.release). Its only job is to
# make the server's Velopack self-update (server/Update/*, docs/adr/0005) work inside a container:
#
#   * A Velopack Linux install IS one AppImage file; an update REPLACES that file in place while the
#     server is still running (and only once it is empty of players), then the server exits 85 so
#     something relaunches it. In a container that something has to live INSIDE it: the server would
#     otherwise be PID 1, and nothing it starts - Velopack's updater included - outlives PID 1.
#   * The AppImage cannot simply be the entrypoint either. Its runtime mounts itself through FUSE,
#     which a plain container does not have; its no-FUSE mode (APPIMAGE_EXTRACT_AND_RUN) re-extracts
#     ~215 MB on every start and runs the payload as a grandchild WITHOUT forwarding signals, so
#     `docker stop` would kill the runtime and orphan the server - no graceful shutdown (the lobby
#     listing would linger, a live match would go unreported).
#
# So this script extracts the AppImage itself (`--appimage-extract`, squashfs only, no FUSE) and
# runs the real binary as a direct child, forever, replacing it after each 85 with a fresh unpack.
# `set -u` (not `-e`): every failure path below is handled explicitly (a supervisor loop that dies
# on its own first error defeats the point of being a supervisor).
set -u
IMG="${STELLAR_APPIMAGE:-/opt/stellar/StellarAllegianceServer.AppImage}"; RUN="${STELLAR_RUN_DIR:-/opt/stellar/run}"
child='' stop=''

say() { echo "[entrypoint] $*"; }

# TERM/INT/HUP/QUIT all just mean "stop" here — there is nothing to distinguish them for (no config
# reload, no separate fast-vs-graceful path) — so all four fold into one flag-and-forward. INT is
# resent as TERM rather than passed through: `sh` starts `&` background jobs with SIGINT held
# ignored (POSIX), and a child that inherits that disposition ignores a forwarded INT outright — an
# interactive `docker run` (not `-d`) would then need SIGKILL to stop at all.
on_stop() { stop=1; [ -n "$child" ] && kill -TERM "$child" 2>/dev/null; }
trap on_stop TERM INT HUP QUIT

# Cheap identity for "has the AppImage changed since we last unpacked it" — size:mtime, not a
# checksum, because it only has to catch our OWN replace-the-file update (and a size or mtime
# change is exactly what that produces); a content hash would cost a squashfs-sized read on every
# single restart for no extra safety here. GNU coreutils `stat -c` (this image is Debian-based).
stamp() { stat -c '%s:%Y' "$IMG"; }

# Re-extracts the AppImage into $RUN only when its stamp changed (first boot, or after an update),
# so a plain container restart is a stat + exec, not a squashfs unpack. The swap is two renames so a
# crash mid-extract can never leave $RUN half-written: build in $RUN.new, park the old tree in
# $RUN.old only for the instant it takes to move $RUN.new into place, then drop it.
unpack() {
  want=$(stamp) || return 1
  [ "$(cat "$RUN/.stamp" 2>/dev/null)" = "$want" ] && return 0
  say "unpacking $IMG ..."
  rm -rf "$RUN.new" "$RUN.old" && mkdir -p "$RUN.new" || return 1
  ( cd "$RUN.new" && "$IMG" --appimage-extract >/dev/null ) || return 1 # always ./squashfs-root
  echo "$want" > "$RUN.new/squashfs-root/.stamp" || return 1
  [ ! -d "$RUN" ] || mv "$RUN" "$RUN.old" || return 1
  mv "$RUN.new/squashfs-root" "$RUN" && rm -rf "$RUN.new" "$RUN.old"
}

while :; do
  cd / || exit 70 # never the CWD a stale unpack left behind, in case $RUN itself just got replaced
  if ! unpack; then
    # No good unpack anywhere is unrecoverable (nothing to run). A good PREVIOUS unpack is not: keep
    # serving it, but hold auto-update at warn so a persistently broken feed/AppImage doesn't spin
    # this loop — the operator gets one clean error line instead of a silent downgrade-and-retry.
    [ -x "$RUN/usr/bin/SimServer" ] || { say "FATAL: cannot unpack $IMG"; exit 70; }
    say "ERROR: unpack failed - running the previous build, auto-update held at warn"; export SIM_AUTO_UPDATE=warn
  fi
  [ -z "$stop" ] || exit 0 # a stop arrived while an unpack was in flight

  before=$(stamp)
  cd "$RUN/usr/bin" || exit 70 # content root: appsettings.json is read from the process CWD
  # APPIMAGE must be exported: Velopack's Linux locator requires it point at the (still-current) file
  # on disk, alongside UpdateNix + sq.version next to the executable (both vpk-packed into usr/bin/).
  # SIM_UPDATE_RESTART=exit overrides any operator setting: Velopack's own relaunch cannot work here
  # (no FUSE, and it dies with PID 1) — the contract's "exit 85, something else relaunches me" half
  # is what this loop exists to provide.
  APPIMAGE="$IMG" SIM_UPDATE_RESTART=exit ./SimServer "$@" & child=$!
  [ -z "$stop" ] || kill -TERM "$child" 2>/dev/null # a stop that raced the fork above

  # `wait` returns early (status > 128) if a trapped signal arrives while it's blocked, before the
  # child has actually exited — loop until `kill -0` confirms the child is really gone, so `code`
  # ends up holding its real exit status and not an artifact of signal timing.
  while :; do wait "$child"; code=$?; kill -0 "$child" 2>/dev/null || break; done
  child=

  if [ -n "$stop" ]; then
    [ "$code" -eq 85 ] && code=0 # an update mid-shutdown is not a failure worth reporting as one
    exit "$code"
  fi
  [ "$code" -eq 85 ] || exit "$code" # anything but 85 is a REAL exit (crash or a deliberate one)

  # 85 with an unchanged AppImage means the server thinks it updated but didn't — relaunching as-is
  # would boot loop forever. Hold auto-update at warn (same reasoning as the failed-unpack branch
  # above) and relaunch exactly once rather than looping here ourselves.
  [ "$(stamp)" != "$before" ] || { say "ERROR: exit 85 but the AppImage is unchanged - auto-update held at warn"; export SIM_AUTO_UPDATE=warn; }
  say "relaunching after an update"
done
