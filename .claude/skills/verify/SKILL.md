---
name: verify
description: Runtime-verify a change by driving the real server + Godot client headlessly and capturing screenshots/movies as evidence. Use after any nontrivial gameplay/sim/client change.
---

# Verify a change end-to-end (server + client)

## Launch

```sh
# 1. Server (rebuilds Release, kills stale :8090; --autostart skips ready-up):
scripts/run-server.sh --local --autostart          # run in background, wait for :8090 LISTEN

# 2. Self-driving client with evidence capture (quits by itself after the shot):
MOVIE_RESOLUTION=1280x720 scripts/run-client.sh --local --autofly --combat-test \
  --write-movie <scratch>/smoke.avi -- --ui-shot=<scratch>/live.png --ui-shot-delay=14
```

- Game flags (`--autofly`, `--combat-test`, `--fighter`, `--bomber`, `--host`) go BEFORE `--`;
  UI-harness flags (`--ui-shot=<path>`, `--ui-shot-delay=<sec>`) go AFTER `--`.
- `--autofly` auto-joins and flies a Scout; `--combat-test` also fires continuously.
- `--ui-shot` saves a PNG after the delay then quits cleanly — this also finalizes
  `--write-movie`. Extract frames: `ffmpeg -ss <t> -i smoke.avi -frames:v 1 f.png`.
- Expect in the client log: `defs received`, `local ship N spawned`, `UI_SHOT_SAVED:`.
  Grep for `SCRIPT ERROR|Exception`. `Reconciles: 0` on the HUD = client prediction and
  server sim agree (strong signal for sim/geometry changes).

## Gotchas

- Default camera is FIRST-PERSON (own ship invisible). To see the own ship, temporarily set
  `[view] first_person=false` in
  `~/Library/Application Support/Godot/app_userdata/stellarallegiance/settings.cfg`
  — BACK IT UP AND RESTORE IT (it's the user's real preference file).
- Content probes without touching the repo: copy `server/Content/core` to scratch, edit the
  copy, then `dotnet run --project server -c Release --no-build -- --port 8099 --autostart
  --content <scratch>/core/core.manifest.yaml` (SIM_PUBLIC_NAME="" keeps it off the lobby;
  point the client at it with `SIM_PORT=8099 scripts/run-client.sh --local ...`).
- A held connection is required or the sim loop won't tick (autofly provides one).
- Kill servers when done: `kill $(lsof -tnP -iTCP:8090 -sTCP:LISTEN)`.
