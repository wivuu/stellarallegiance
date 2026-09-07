---
name: verify
description: Runtime-verify a change by driving the real server + Godot client headlessly and capturing screenshots/movies as evidence. Use after any nontrivial gameplay/sim/client change when requested - defer to manual verification otherwise.
---

# Verify a change end-to-end (server + client)

## Launch

```bash
# 1. Server (background, doesn't attach a dashboard; brings up the whole local stack):
aspire start
aspire wait server                                 # block until the sim server resource is ready

# 2. Self-driving client with evidence capture (quits by itself after the shot):
aspire resource client launch --mode autofly --write-movie <scratch>/smoke.avi \
  --godot-args "--combat-test -- --ui-shot=<scratch>/live.png --ui-shot-delay=14"

# 3. Tear down when done:
aspire stop
```

For perf-sensitive measurements, run the raw Release server instead of Aspire's Debug build:
`dotnet run --project server -c Release -- --port 8090 --autostart` (or `scripts/run-server.ps1 -Local --autostart`).
`scripts/run-client.ps1` still exists for the hosted lobby; from zsh call it as
`pwsh -Command "& ./scripts/run-client.ps1 -Local -GodotArgs @('--autofly','--','--ui-shot=…')"` —
the comma-list / bare `--` forms silently drop the flags (tell: `0 sectors`, no spawn, window never quits).

- `--godot-args` is **one quoted string**, split on spaces by the launcher; it may itself contain
  the `--` separator before UI-harness flags. Game flags (`--autofly` is covered by `--mode
  autofly`; `--combat-test`, `--fighter`, `--bomber`, `--host`) go before the `--` inside that
  string; UI-harness flags (`--ui-shot=<path>`, `--ui-shot-delay=<sec>`) go after it.
- `--mode autofly` auto-joins and flies a Scout; `--combat-test` (in `--godot-args`) also fires
  continuously.
- `--mode autofly` connects DIRECTLY to localhost:8090, which a **Verified** local server refuses
  (`join token required`). It works while the server is unapproved/unlisted (the default until you run
  `aspire resource lobby approve-device-code`). Against an approved server use the lobby path instead:
  `aspire resource client launch --mode lobby --seed-dev-login --godot-args "--join-listing=<sim-public-name> --autofly -- --ui-shot=..."`
  (`--seed-dev-login` writes a dev sign-in for the local lobby into `user://auth.json` — it replaces
  any existing session file). Or un-pair by deleting `apphost/.local/server/lobby-auth.json`.
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
  point a client at it with `aspire resource client launch --mode direct --godot-args
  "--host=localhost:8099"`).
- A held connection is required or the sim loop won't tick (autofly provides one).
- Kill stray servers when done: `kill $(lsof -tnP -iTCP:8090 -sTCP:LISTEN)`; `aspire stop` should
  already have torn down the ones it started.
- `timeout` likely will not work on MacOS.
- `--ui-open=scoreboard-live|scoreboard-post` (after `--` inside `--godot-args`) raises the match
  scoreboard before a `--ui-shot`.