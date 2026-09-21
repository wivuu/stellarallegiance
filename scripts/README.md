# scripts/

Running and deploying the game now go through the .NET Aspire AppHost (`apphost/`) via the
`aspire` CLI — see the root [README](../README.md) and [QUICKSTART](../QUICKSTART.md) (`aspire
run` / `aspire start`, `aspire resource client launch`, `aspire do deploy-lobby`/`deploy-server`).

What's left here are the helpers Aspire doesn't replace: running a server/client against the
**hosted** public lobby (the AppHost always wires the local one), exporting release client builds,
and resolving a Godot install for the tools below. Both are **PowerShell 7+ (`pwsh`)** — cross-platform
(Windows natively; macOS/Linux after `brew install powershell` or your package manager) — and
wired up as VS Code tasks in `.vscode/tasks.json`. Run them **from the repo root**, e.g.
`scripts/export-clients.ps1`.

## Run against the hosted public lobby

| Script | What it does |
|--------|--------------|
| `run-server.ps1` | Builds (Release) and runs the sim server, **publishing it to the hosted public lobby** (`PUBLIC_LOBBY`, default `https://stellarlobby.wivuu.com`) under your hostname (`SIM_PUBLIC_NAME` to rename). First publish prints a device code to approve at the hosted lobby; the credential is saved beside `sim-cache/`. `-Local` stays private (direct `ws://` only); `--autostart`, `--secret`, `$env:SIM_PORT` pass through. |
| `run-client.ps1` | Rebuilds the client C# and launches Godot on the **hosted lobby's server browser**. `-Local` connects straight to `localhost:8090`; `-Release` runs optimized C#; `-WriteMovie <path>` records; extra args pass to Godot (`--host host:port`, harness flags). From zsh/bash hand the array to PowerShell itself: `pwsh -Command "& ./scripts/run-client.ps1 -GodotArgs @('--autofly','--','--ui-shot=…')"`. |

For the full local stack (local lobby + Postgres + server + dashboard) use `aspire run` instead.

## Godot resolution

| Script | What it does |
|--------|--------------|
| `godot-bin.ps1` | **Dot-sourced**, not run. `. scripts/godot-bin.ps1` defines `Resolve-Godot`, which returns the path to a runnable Godot 4 .NET ("mono") binary as a string (or `$null`) — callers do `$Godot = Resolve-Godot`. Resolution order: preset `$env:GODOT` → the per-workstation `dotnet user-secrets` store (key `godot.executablePath`, id `stellarallegiance`; set via the "Godot: set executable path" VS Code task) → `godot-mono`/`godot4`/`godot` on PATH → standard install locations. Dot-sourced by `export-clients.ps1`, `tools/godot-import.ps1`, and `tools/glb-gallery/gallery.ps1`; the Aspire AppHost uses the same resolution order independently for the `client` resource. |

## Tests

| Script | What it does |
|--------|--------------|
| `run-tests.ps1` | Runs every suite under `tests/` (each is a console app: `dotnet run --project tests/<Suite> -c Release`, printing its own PASS/FAIL lines and exiting non-zero on failure), then prints a suite/result/seconds summary table and exits 1 if any failed. `-Filter <substring>[,<substring>]` runs a subset (`-Filter Collision`). `tests/PublicLobbyTest` is skipped unless `-IncludeDocker` is passed — its schema/grain sections need a reachable Docker for the Testcontainers Postgres. **Local only; there is no CI**, so nothing runs these but you. Some suites are known-red at the moment. |

## Turret feel rig

| Script | What it does |
|--------|--------------|
| `turret-test.ps1` | **One command → you are in a bomber's dorsal turret** with a live aim readout. A gunner needs a captain, so it starts a private sim server on its own port (`-Port`, default 8097, stock content copied to a temp dir with the bomber pre-unlocked), a small CAPTAIN client that launches the bomber and sits still (`--turret-test=captain`), and YOUR client, which claims station T1 and hands you the mouse (`--turret-test=gunner`). Everything is torn down when your window exits; logs stay in the printed temp dir. The readout (also logged once a second as `[turret-stats]`) shows hand px/s, wanted vs applied °/s, `limited %` (frames the slew bucket scaled — should be 0 outside a deliberate hard spin), dropped °, bucket fill, the station's slew, the live gain in °/px and fps. `-Auto` scripts the mouse instead (100 px/s track, 1000 px/s sweep, a 300 px / 50 ms flick — all must land 1:1 — a 6000 px/s spin that must be limited, and an injected Enter that must open chat) and exits non-zero on a failure. Feel-tuning without a rebuild: `-Gain` (rad per stick unit, stock 0.12), `-SlewDeg` (every station's sustained rate; 110 = the Devastator's mount, 0 = unlimited), `-Window` (seconds of traverse the bucket holds, stock 0.15), `-Sens`. `-Pigs` leaves the AI on for moving targets; `-NoBuild` skips the builds. The `--turret-test` flag and the `TURRET_*` env overrides are honoured by **debug builds only** (editor / run-from-source — `OS.IsDebugBuild()`); an exported client ignores them, since the slew limit is client-enforced and an override would be a free traverse cheat. |

## Export & packaging

| Script | What it does |
|--------|--------------|
| `package-clients.ps1` | **The release path.** Builds THIS OS's Velopack package — the Game Launcher (`launcher/`) plus the Godot client as an installer, a self-updating portable zip and the update feed — into `build/releases/<channel>`. The same script CI runs (`.github/workflows/release.yml`). `-Version <x.y.z>` (required), On a real export it first writes the collision sidecars (`tools/collision-sidecars`) and, before packing, runs the staged game's `--verify-assets` check (`verify-game-assets.ps1`) — a game that cannot collide with every model is never packed. `-FakeGame` (stub game instead of a Godot export: seconds, not minutes), `-LauncherDir <dir>` (`-FakeGame` only: pack a launcher published by an earlier run instead of publishing again — the launcher takes its version from Velopack's manifest, not from its binary), `-GameDir <export>` (reuse an export), `-HostArchOnly` (macOS: skip the universal build), `-ReleaseNotes <md>`. Needs `zstd` on macOS/Linux. See [`docs/RELEASING.md`](../docs/RELEASING.md). |
| `launcher-e2e.ps1` | Proves install → update → restart → play on this machine in about a minute: packs three versions around the stub game from ONE launcher publish (the fake game's payload is what differs, so every delta still patches a real binary), installs the first the way a player would (Windows: the real `Setup.exe --silent`), then drives the installed launcher headless through two real updates — one triggered by the game's UPDATE NOW exit code — asserting every step. Also what the **Package dry-run** workflow runs on all three OSes. `-KeepFiles` to inspect `build/launcher-e2e`. |
| `verify-game-assets.ps1` | **Dot-sourced**, not run. Defines `Test-GameAssets -GameExe <exported game> -WorkDir <dir>`, which runs the EXPORTED game with `--headless --verify-assets=<report>` and returns `{ Ok, ExitCode, Lines }`: can this build render **and collide with** every base, ship and asteroid model? The report file is the verdict (not the exit code). Called by `package-clients.ps1` (step 3b — a failing build is never packed) and `export-clients.ps1`. See GLOSSARY → *Asset Verification*. |
| `export-clients.ps1` | Exports the Godot client for macOS + Windows + Linux as **plain zips** for a quick hand-off to a tester — no launcher, so no auto-update; not the release path. (Handles the macOS hardened-runtime/ad-hoc-signing gotcha that otherwise SIGKILLs the app at launch.) |
| `package-server.ps1` | The native sim-server's release path. Builds a single self-updating `.AppImage` per Linux arch (`server-linux-x64` / `server-linux-arm64`) into `build/releases/server-linux-<arch>`. Runs on any Docker host — the actual `dotnet publish` happens inside the SDK image, so the produced binary always matches the Docker *daemon's* arch, not the host's. The same script CI runs (`.github/workflows/release.yml`). `-Version <x.y.z>` (required), `-Arch <x64\|arm64>`, `-PublishDir <tree>` (pack an already-published tree, skipping the Docker publish), `-ImageContext <dir>` (also stage `server/Dockerfile.release`'s build context). Needs `zstd` and `mksquashfs` alongside Docker. See [`docs/RELEASING.md`](../docs/RELEASING.md). |
| `server-update-e2e.ps1` | Proves a packaged server drains, applies and restarts itself for real, end to end: packs three versions, builds the release image, boots it **unlisted** with a player held by `tools/simbot` (asserting no update work starts while connected), then watches it go `Available` → `Deferred` → (bot leaves) → `Applying` → restarted onto v2 inside the *same* container, and a delta apply onto v3. Beside it a **hand-run** server (the bare AppImage over FUSE, `SIM_UPDATE_RESTART=relaunch`) follows the same two releases by relaunching itself, and must end up in its own working directory with a single AppImage mount. Also what the **server update dry-run** workflow — and every release, as its `server-e2e` gate — runs on x64 + arm64 runners. `-KeepFiles` to inspect `build/`, `-Versions` for a different sequence, `-Fast` to skip the restart/stop/warn-mode checks, `-SkipHandRun` where Docker cannot give a container `/dev/fuse`. |

See the root [README](../README.md) and [QUICKSTART](../QUICKSTART.md) for prerequisites and the
`aspire run` local loop.
