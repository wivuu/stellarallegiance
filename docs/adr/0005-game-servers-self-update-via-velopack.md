---
status: accepted
date: 2026-09-20
---
# Game servers self-update through Velopack; the public lobby is the one watcher of the release feed

A release that bumps `Wire.ProtocolVersion` makes every not-yet-updated game server invisible to updated
clients — the server browser subscribes to `/servers/events?protocol=N`, so a stale server simply drops out
of the list, with no error to explain why. Game clients already update themselves through the Game Launcher
(Velopack, [0004](0004-game-launcher-fronts-velopack.md)); game servers still needed an operator to notice a
release and pull a new image or rebuild by hand, and nothing told the players keeping an old server alive
that it was stale.

A game server is not a desktop app a player restarts at will. It is a long-running process that owns a live
match and, in the shipping container image, is PID 1 — there is no point where "wait for me to exit, then
relaunch" is safe to assume, because nothing else in the container survives that exit. Velopack's own
apply-then-relaunch contract, built for a foreground desktop app nobody minds killing, is wrong here twice
over: applying while players are connected would drop a live match, and exiting to let something else
relaunch the process only works if something else actually does.

We decided a game server updates **itself**, through Velopack, on the same package/delta/checksum plumbing
as the desktop client — gated by the public lobby telling it (and every player) that a release exists, and
applied only once nobody would notice.

## Consequences we chose deliberately

- **Never forced; no update work while anyone is connected.** A doorbell may ring at any time and raises a
  notice, but the download and the apply only start after the server has been empty for an idle window
  (above the client's own 20 s auto-reconnect), and the apply itself happens behind a closed front door (the
  drain: `IUpdateGate.TryBeginDrain`) that refuses new joins with `RejectMessage.CodeUpdating` (3, "server
  updating") for the few seconds the swap takes, rather than the generic disconnect a stale client would
  otherwise show.
- **The advert is a doorbell; Velopack is the truth.** The public lobby — not each game server — is the one
  process that watches the release feed. Every listed server already holds a control WebSocket to it
  (`/servers/ws`) and every player's server browser already watches it over SSE (`/servers/events`); a
  Release Advert rides both, right after connecting and again whenever the version rises. It names a version
  and nothing else — the receiving server still asks the Velopack feed for the real package, its checksum,
  and whether it is actually newer, so a wrong, delayed, or even hostile lobby can cost at most a few no-op
  checks, never a bad install. Servers are told `max(baked, confirmed)` (the lobby's own deployed version
  counts immediately, which matters because a lobby redeploy is usually *why* every server just reconnected);
  clients are told `confirmed` only (polled from the real feed), because a premature "UPDATE READY" would
  send a player through the launcher and straight back having installed nothing. A server the lobby cannot
  reach — unlisted, or talking to a lobby that predates adverts — falls back to its own slow safety-net poll
  (first check 60 s after boot, then every 6 h, skipped whenever a lobby advert already arrived) instead of
  going deaf.
- **A server that is never empty never updates — by design.** The idle-window gate means a busy community
  server can sit on an old build indefinitely; once its release is old enough that `Wire.ProtocolVersion`
  no longer matches, it quietly drops out of updated clients' server list — the same silent disappearance
  this feature exists to eventually fix — rather than update out from under the players keeping it alive.
- **Packaging is Linux only; the AppImage is the install.** Channels `server-linux-x64` /
  `server-linux-arm64`, package id `StellarAllegianceServer`. The release image
  `ghcr.io/wivuu/stellarallegiance-sim` is no longer built from source but *assembled* from the same AppImage
  attached to the release, run inside the container by a small supervisor entrypoint
  (`server/docker-entrypoint.sh`) that is PID 1 in the server's place: it extracts the AppImage itself (no
  FUSE), runs the real binary as a direct child, and forwards TERM/INT/HUP/QUIT to it. The swap is
  synchronous — the server starts Velopack's updater (`UpdateExe.Apply(waitPid: 0, restart: false)`) and
  *awaits its exit itself* instead of asking Velopack to wait-and-relaunch — then exits with code `85`, the
  same code the desktop client already uses toward the Game Launcher, so the entrypoint's loop re-extracts
  and relaunches inside the same container.
- **Outside a supervisor the server relaunches itself — not through Velopack.** A hand-run AppImage
  (`SIM_UPDATE_RESTART=relaunch`, the default with no container and no systemd) swaps the same synchronous
  way, then leaves a few lines of `/bin/sh` behind that wait for it to exit and `exec` the new AppImage:
  same arguments, environment and working directory (`server/Update/Relauncher.cs`). Velopack's own
  `UpdateNix start --waitPid` was what the first release rehearsal ran, and it failed quietly twice. It runs
  from *inside* the old version's mounted AppImage and moves into its own folder, so the new server inherited
  a working directory in the old mount — a relative `--content` path loaded the stock content out of the old
  package. And it only outlives the old server because it inherits the AppImage runtime's descriptors, which
  it hands on to the next AppImage: every update left one more dead version mounted, FUSE helper and ~180 MB
  of deleted AppImage included. Withholding those descriptors is no fix — UpdateNix then loses its own files
  the moment the old server exits, and nothing comes back. The helper inherits none of them (they are marked
  close-on-exec first) and needs nothing from the package.
- **Pinned image tags still self-update.** `docker-compose.server.yml` recommends an exact tag for a
  reproducible deploy, but auto-update replaces the running AppImage inside the container, not the tag it
  was pulled from — only `SIM_AUTO_UPDATE=warn` or `off` actually stops it. A freshly pulled container has no
  prior package to diff against, so its first update is a full ~200 MB download; only later, in-place updates
  within that container's lifetime are deltas, unless `/var/tmp/velopack` is mounted as a volume across
  recreations. A platform that recreates the container from the image on every deploy (rather than keeping
  one running) sees the same doorbell, and pays the same idle-window wait and full download, again at every
  cold boot.
- **Two failed apply attempts suspend that release.** `UpdateAttemptStore` remembers "I tried to move from A
  to B, N times" across restarts (beside the lobby credential, so it survives the run directory being
  re-extracted); if the server is still on the old build after two tries — whatever the updater claimed —
  it stops offering that release rather than looping a drain-download-apply cycle forever.
- **Players are told through the wire, not left to notice a stale build.** A standing Server Notice message
  (`ServerNoticeMessage`, protocol 42 → 43) drives a Game Lobby banner ("SERVER UPDATE PENDING") and one ★
  system chat line, so pilots already in flight see it too. It survives the mid-session re-Welcomes a match
  start or a fog team change sends, and clears only on a fresh connection — never silently, once withdrawn.

## Alternatives considered

- **Per-server polling of GitHub.** Every listed server asking GitHub's API on its own repeats the
  unauthenticated 60/hour/IP quota across however many servers share an egress IP (Railway, a home NAT) — N
  pollers instead of one — and is slower than the lobby link every listed server already holds open.
- **Watchtower, or a plain image re-pull.** Neither can gate on "no players are connected" — that tooling has
  no idea a match is running — and recreating the container from a new image loses whatever the running
  container held that isn't on a volume. That's a replacement, not a Velopack apply.
- **Exit and rely on the Docker restart policy, with Velopack's own wait-for-exit apply.** The updater is a
  child of this process; it dies with PID 1 the moment the server exits, before it can finish the swap. Even
  with our synchronous apply instead of Velopack's, exiting and trusting a restart policy still loops forever
  on a platform that recreates the container from the (unchanged) image rather than restarting the same one,
  and leaves a plain `docker run --rm` server simply dead.
- **Run the AppImage runtime itself in the process tree (`APPIMAGE_EXTRACT_AND_RUN=1`).** It forwards no
  signals to the payload it mounts, so `docker stop` would lose the graceful shutdown (spooled match reports,
  the lobby listing drop) entirely, and it re-extracts the ~215 MB image on every single container start, not
  only after an update.
- **Pack the images CI already builds from source.** A source build stamps no version — the SDK's unstamped
  default `1.0.0` would outrank every real `0.0.x` release — so it can't tell a dev or branch build from a
  shipped one. Packing it would happily "update" (really: downgrade) a debug build to whatever the latest
  real release is.
