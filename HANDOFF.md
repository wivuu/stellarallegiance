# Agent Handoff — wivuullegiance

## Current state

T0–T3, **T4** (mechanically verified; subjective flight-feel sign-off still needs a human at
the keyboard), **T5, T6, T7, and T8 are complete** (the subjective sign-offs — T4 flight feel,
T7 class distinctness, T8 combat feel — all still want a human at the keyboard). The repo has:
- A SpacetimeDB C# module with the **full game schema** (7 tables + 2 enums + scheduled SimTick) published to a local server, seeded with 1 Match, 2 Bases, 30 Asteroids. `Ship` has `AngVelX/Y/Z`; `ShipInput` is now a **server-private per-tick input buffer** (keyed by `InputId`, indexed by `ShipId`).
- The 20 Hz `SimTick` **integrates every ship** via the shared `FlightModel` (ships only — projectiles/hits are T8), applying the input stamped for the current tick, and stamps `Ship.LastInputTick = Match.Tick`
- Player-action reducers: `SpawnShip`, `Respawn`, `ApplyInput`, `SetName`
- A Godot 4.6.3 C# client that connects, subscribes, renders the static world, **spawns and flies either ship class (HUD spawn menu) with tick-aligned prediction + rollback reconciliation (zero divergence)**, renders **other players' ships with 100 ms snapshot interpolation**, a rigid chase camera, and a HUD with a Scout/Fighter spawn menu
- A **shared, deterministic, fixed-`dt` flight model** with **deterministic `sin/cos`** (`shared/FlightModel.cs`) copied byte-identically into `module/` and `client/`, with a passing determinism+golden test
- `dotnet build` (client) and the module wasm publish both succeed (one harmless generated-code warning on the client)

**Controls (Allegiance-style):** `W/S` throttle fwd/back · `A/D` strafe left/right · `E/C` strafe up/down · `Q/Z` roll · arrow keys yaw/pitch · **`Space`/left-mouse fire** · `1` spawn Scout / `2` spawn Fighter (or click the HUD menu) · `P` (debug) inject divergence.

**Next task: T9 — base damage & win condition** (see `.PLAN/08-BUILD-ORDER.md`, `03`, `04`, `05`).
Make projectiles damage the **enemy base** (in `SimTick` Pass B/C — currently they fly through
bases); when a `Base.Health <= 0` set `Match.Phase = Ended` + `Match.Winner = otherTeam`. Add a
HUD match-end banner driven by `Match.Phase == Ended` / `Winner` (the `Match` subscription +
`WorldRenderer.ServerTick` already track the row; add `Phase`/`Winner` exposure). Confirm a full
match can be played start to finish.

---

## Environment

| Thing | Detail |
|-------|--------|
| SpacetimeDB server | Docker on **host port 3001** (port 3000 is taken) |
| Database name | `stellar-allegiance` |
| Godot | `~/.local/bin/godot` → Godot 4.6.3 mono |
| .NET SDK | 10.0 (host) |

> **Identity persistence (important — read before publishing).** Each `docker run` of
> the CLI gets a *fresh* identity unless you mount a persistent config dir. If you publish
> with a different identity than the one that created the DB, you get `403 ... reset database`
> / "not authorized". The repo keeps a persistent CLI config in **`.stdb-config/`** (gitignored,
> holds a token). **Always mount it** (`-v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime`)
> on every publish/sql/generate-needing-auth call. Then iterative republish "just works"
> ("Updated database"). **After a server restart**, the server rotates its signing key, so the
> saved token goes stale (`401 Invalid token`) — refresh it once with the `login` command below.

**Start the server + seed the DB (preferred):**
```bash
./scripts/start-db.sh          # starts (persistent), waits, populates if needed
./scripts/start-db.sh --reset  # also force a fresh publish (--delete-data)
```
This starts the server with a **persistent Docker named volume `stdb-data`**, so the DB and the
server's signing keys now **survive container restarts** (data persists; the CLI token stays
valid across restarts). It's idempotent — re-run any time. `scripts/populate-db.sh` just
publishes (which runs `Init` → seeds Match/Bases/Asteroids); `start-db.sh` calls it as needed.

> **Why a named volume + `--user root`** (learned the hard way): SpacetimeDB's commitlog calls
> `fsync`, which macOS Docker Desktop **bind mounts don't support** (gRPC-FUSE/virtiofs) — a
> host-path data dir panics the server with *"Failed to fsync segment"*. A named volume lives in
> the Linux VM's ext4 and works; it's root-owned, so the server runs as `--user root` (the image's
> default `spacetime` user can't write to it). **Local auth:** the local server signs tokens with
> its own keys, so a maincloud/`.stdb-config` token gives `401 Invalid token: InvalidSignature`.
> Publishing to a *not-yet-existing* DB works anonymously; if a stale token is present, `start-db.sh`
> mints a fresh server-issued token from `POST /v1/identity` and stores it via `login --token`.
> (The old `login --server-issued-login` flag is gone in CLI 2.3; `login --auth-host <local>` does
> NOT work — it runs the spacetimedb.com web flow and clears your token.)

**Manual start (fallback, ephemeral — data lost on restart):**
```bash
docker run -d --rm -p 3001:3000 --name stdb clockworklabs/spacetime start
```

**Publish module** (run from repo root after any change to `module/`):
```bash
docker run --rm \
  -v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime \
  -v "$(pwd)/module":/workspace -w /workspace --network host \
  clockworklabs/spacetime publish stellar-allegiance \
    --server http://localhost:3001 --yes
```
> Breaking schema change on an existing DB? Add `--delete-data` (a **boolean flag** — NOT
> `--delete-data always`). Only the owning identity can reset; with `.stdb-config` mounted that's you.

**Regenerate bindings** (run from repo root after schema changes; no server/auth needed):
```bash
docker run --rm -v "$(pwd)":/workspace -w /workspace \
  clockworklabs/spacetime generate --lang csharp \
    --out-dir client/module_bindings \
    --module-path module/spacetimedb \
    --yes
```

**Build client:**
```bash
cd client && dotnet build
```

---

## T1 (DONE): schema + seed data

`module/spacetimedb/Lib.cs` now holds the full schema, seed, and lifecycle reducers:
- **7 tables** (`Player`, `Ship`, `ShipInput`, `Base`, `Asteroid`, `Projectile`, `Match`),
  all `Public = true`, plus the scheduled `SimTickTimer` table.
- **2 enums**: `ShipClass { Scout, Fighter }`, `MatchPhase { Lobby, Active, Ended }`
  (no explicit values — SpacetimeDB forbids them; order defines them).
- **Reducers**: `Init` (seeds 1 Match singleton, 2 Bases at ±500 on X, 30 Asteroids via
  deterministic `ctx.Rng`, and schedules SimTick @ 20 Hz), `ClientConnected`
  (create/reactivate Player + team-balance assignment + Lobby→Active when both teams ready),
  `ClientDisconnected` (Online=false, destroy ship). `SimTick` is scheduled and **stubbed** —
  it only increments `Match.Tick` (full integration is T4).

Verified: `SELECT * FROM Match` = 1 row (Phase=Lobby; `Tick` is a moving number because the
scheduler is live), 2 Bases at distinct positions, 30 Asteroids, `dotnet build` clean.

### Re-verify any time (sql check)
```bash
docker run --rm -v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime --network host \
  clockworklabs/spacetime sql stellar-allegiance \
    "SELECT id,tick,phase,winner FROM Match" \
    --server http://localhost:3001
```

## T2 (DONE): client subscriptions + render

- `ConnectionManager.cs` now exposes `Conn`/`LocalIdentity`, fires a `Connected` event in
  `OnConnect` (so renderers can attach row callbacks **before** the snapshot), then calls
  `conn.SubscriptionBuilder()…SubscribeToAllTables()`.
- `WorldRenderer.cs` (new, `Node3D`) attaches `Base`/`Asteroid` `OnInsert`/`OnDelete` and
  instances `MeshInstance3D` spheres per row (bases colored by team, asteroids scaled by
  `Radius`). It also positions the overview `Camera3D` via `LookAt` (a proper chase
  `CameraRig` comes in T4).
- `Main.tscn` is now `Node3D` with `ConnectionManager`, `WorldRenderer`, `WorldEnvironment`,
  `DirectionalLight3D`, and `Camera3D`.

**Verified:** two headless clients run simultaneously, each logs `bases: 2, asteroids: 30,
ships: 0` with identical base positions `(-500,0,0)`/`(500,0,0)`; a windowed screenshot shows
the blue/red bases + asteroid field rendering correctly.

**Gotchas learned:** the SDK table-callback signature is `(EventContext ctx, Row row)`. Don't
hand-author the `Camera3D` `Transform3D` in the `.tscn` (easy to mis-aim — it rendered an
empty frame); orient it in code with `LookAt`. Removed stale `Person.g.cs.uid` leftovers from
the template under `module_bindings/{Tables,Types}/`.

## T3 (DONE): shared flight model

- **`shared/FlightModel.cs`** is the canonical, pure, fixed-`dt` (`Dt = 1/20`) integrator.
  It uses self-contained `Vec3`/`Quat` structs (no Godot / System.Numerics types) in
  namespace `StellarAllegiance.Shared`, so the copies are engine-independent and truly
  identical. `StatsFor(byte)` (0=Scout, 1=Fighter) keeps it free of any game enum.
- **`shared/sync.sh`** copies the canonical file VERBATIM into `module/spacetimedb/FlightModel.cs`
  and `client/scripts/FlightModel.cs`, then `diff`s to prove all three are byte-identical.
  **Edit `shared/FlightModel.cs` then run `bash shared/sync.sh` — never edit a copy.**
- **`tests/FlightModelTest/`** is a standalone net8.0 console test: it integrates a fixed
  200-tick input sequence, asserts two runs are **bit-identical** (determinism), matches a
  recorded golden state within **1e-5**, and that terminal speed respects `MaxSpeed`.
  Run with `cd tests/FlightModelTest && dotnet run -c Release` → `ALL TESTS PASSED`.

**Verified:** `diff shared/FlightModel.cs module/.../FlightModel.cs` and `…client/…` are both
empty; the test passes; the client `dotnet build` and the module wasm publish both compile the
file cleanly. Integration path has **no `delta`** — always `FlightModel.Dt`.

**Gotchas learned:** the module/client `ShipClass` enums live in *different* namespaces, so the
shared file must not reference either — it takes a `byte` class id. Transcendental funcs
(`sin/cos`) are a theoretical cross-runtime determinism risk (wasm vs mono); fine so far, noted
in `99` to revisit at T5.

## T4 (DONE, pending subjective sign-off): local flight + prediction

- **Server** (`module/spacetimedb/Lib.cs`): added `Ship.AngVelX/Y/Z`; reducers `SpawnShip` /
  `Respawn` (shared `SpawnShipInternal` — spawns at the team base, creates the `ShipInput` row,
  sets `Player.ShipId`), `ApplyInput` (overwrites the ship's `ShipInput`, no integration), and
  `SetName`. `SimTick` marshals each `Ship` row → `ShipState`, calls `FlightModel.Integrate`
  with its `ShipInput`, writes back transform + `AngVel`, and stamps `LastInputTick = Match.Tick`
  (the server integration index — see the T5 section; this replaced the original `ClientTick`).
- **Client**: `ShipMath.cs` (row↔FlightModel↔Godot marshaling), `PredictionController.cs`
  (local ship: fixed-dt prediction, ring buffer, prev→current **interpolated** rendering —
  uniform motion, ~1 tick visual latency, quaternions normalized before `Slerp`; rollback
  reconciliation at a **tight 1.0 u** tolerance with position-only easing; `ReconcileCount` +
  `LastReconcileError`), `RemoteShip.cs` (snap-only; T6 adds interpolation), `ShipController.cs`
  (prediction tick **slaved to the server tick** via a slewed local clock — see T5; spawn on
  key `1`; `--autofly` dev flag), `CameraRig.cs` (**rigidly locks to the ship transform** so it
  moves at exactly the ship's rate; overview fallback), `Hud.cs` (spawn prompt + speed +
  reconcile counter). `WorldRenderer` instances the right node per ship, tracks `ServerTick`
  (from `Match`), and exposes `LocalShip`.
- **Controls**: WASD thrust/strafe, Space/Shift up-down, arrow keys aim, Q/E roll. Ships fly
  along local **+Z** (matches the flight model); the cone mesh and chase cam are built around that.

**Verified:** `--autofly` headless run spawns a Scout and flies it — SQL shows the `Ship` row
moving away from the base, proving the full `ApplyInput → SimTick integrate → Ship update` loop.
A windowed capture confirms the cone renders, oriented along travel, with the chase cam behind
it and HUD speed (~35 u/s terminal). After the rendering was simplified (see below), reconcile
is ~0.23 Hz over ~116 s of continuous turning (was ~1.5 Hz), all client errors gone.

**Chase-cam smoothness (hard-won, see `99`):** the local ship renders **interpolated** (not
extrapolated) prediction, the camera is a **rigid** follow (no smoothing lag/beat), and all
quaternions are normalized before use. The jerk turned out to be a per-frame
`Quaternion is not normalized` exception from an over-engineered rotation-correction smoother
that aborted the ship's transform update each frame — **deleted, do not re-add.** (The
server-tick stamping and a server-slaved prediction clock were *also* reverted at T4 because the
first attempt paired them with a buggy lead-bounding stall; T5 re-introduced them **correctly**
— stamping + a *slewed* clock with no stall. See the T5 section + `99`.)

**Not done:** the subjective "is it fun for two minutes" gate needs a human flying with the
keyboard. Constants are the `.PLAN/03` placeholders (Scout: thrust 45 / maxspeed 70 / drag 1.2;
angular 3.5 / drag 2.5) — tune to taste in `shared/FlightModel.cs` then `bash shared/sync.sh`.

**Gotchas learned:** headless Godot runs **uncapped**, so `--quit-after <frames>` elapses in a
fraction of a real second — to observe live state, run with a huge frame cap in the background
and sample, then stop it (don't expect SQL to catch a short headless run). Ships spawn *inside*
the 45-unit base sphere (see-through from within due to back-face culling) and fly out — fine,
noted in `99`. The chase-cam framing of a small cone seen dead-rear reads as a faceted disc;
that's correct.

## T5 (DONE): reconciliation correctness — **true zero divergence**

Reached in three stacked fixes (each verified to remove a distinct divergence source). The
order of discovery is in `.PLAN/99`; the final design is:

1. **Tick alignment.** Server stamps `Ship.LastInputTick = Match.Tick` (its integration index).
   The client predicts in that tick space: `ShipController` anchors `_predTick` to
   `WorldRenderer.ServerTick` on spawn and advances on a **slewed local clock** (fixed-dt
   accumulator whose *pacing* is nudged, gain `SlewGain`/`±MaxSlew`, to hold `TargetLead = 1`
   over authority — no discrete skip/stall). So `predicted[N]` and `auth[N]` index the same
   integration count. This killed the **~8.8 u step-count drift** (the server actually ticks
   **~18.7 Hz**, not 20, so a free-running 20 Hz client drifted).
2. **Deterministic trig** (`MathDet.Sin/Cos` in `shared/FlightModel.cs`). IEEE `+,-,*,/,sqrt`
   are bit-identical across the wasm server and mono client, but `Math.Sin/Cos` (libm) are not —
   that mismatch drifted the *rotation* integration and was the **turn-only jerk**. A polynomial
   in plain float ops makes both sides bit-identical (`rotErr → 0`).
3. **Server per-tick input buffer.** `ShipInput` is now one row per `(ship, tick)` and `SimTick`
   applies the input **stamped for the current tick** (not "latest"), so the server replays the
   client's exact input sequence. This removed the last residual — the **latest-input steering
   transient** (which a wider tolerance only capped, not eliminated).

**Result (measured, `--autofly` @60fps):** with tight tolerance (**1.0 u pos / 0.05 rad rot**),
~5 min of continuous weaving produced **zero** drift reconciles — only a one-time ~0.5 u
spawn-anchor alignment and the deliberate injection. `rotErr = 0.0000` throughout.

**Gate.** Instrumentation: `ReconcileCount` / `LastReconcileError` in the HUD. Injection: **P**
key (or autofly self-test) → `InjectDivergence` offsets the predicted state + buffer so the next
update exceeds tolerance. Verified a 25 u injection recovers in **one** correction, no
rubber-banding. Reconciliation + the position/rotation **correction smoothing** now only act on
real perturbation.

**Future (`99`):** `TargetLead = 1` and the input-arrival fallback assume low RTT (localhost);
revisit lead/fallback under real WAN latency at T10.

## T6 (DONE): two clients, remote interpolation

`RemoteShip.cs` no longer snaps. It buffers authoritative transforms (with client arrival
timestamps) and renders at **`now − 100 ms`** (`InterpDelayMs`), finding the two samples that
bracket the render time and **lerping position / slerping rotation** between them. If render
time is past the newest sample (updates stopped) it **holds the latest** — no extrapolation
(`.PLAN/07`). Quaternions normalized before slerp. First sample renders as-is until a pair
exists. This is the "smoothing" that makes a remote ship glide instead of stepping at ~18.7 Hz.

**Verified:** two headless `--autofly` clients each spawn a ship and render the other's as an
interpolating remote (auto-team-balanced to 0/1), zero errors over the run. Visual smoothness
confirmed by the user in a live 2-client session.

**Controls remapped this milestone (Allegiance-style), in `ShipController.ReadInput`:**
`W/S` throttle · `A/D` strafe L/R · `E/C` strafe up/down · `Q/Z` roll · arrows yaw/pitch.
(Roll moved off `E` because `E` is now strafe-up.)

## T7 (DONE, pending subjective sign-off): both ship classes + spawn menu

Client-only milestone — `SpawnShip(ShipClass)` and `FlightModel.Fighter`/`Scout` stats already
existed (server unchanged, no republish needed):
- **`Hud.cs`** now shows a **spawn menu** (Scout/Fighter `Button`s in a `VBoxContainer`) whenever
  the player has no ship; each button calls `ShipController.RequestSpawn(class)`. While flying it
  shows the speed/reconcile readout. The `1`/`2` keys are kept as keyboard shortcuts.
- **`ShipController`** spawn flow refactored to a nullable `_spawnRequest` `ShipClass?`: the menu,
  the `1`/`2` keys, and `--autofly` all set it; the reducer fires once connected with the existing
  retry, and it clears once the ship exists. New dev flag **`--fighter`** makes `--autofly` spawn
  a Fighter (for headless verification).
- **`WorldRenderer.BuildShipMesh(team, class)`** gives the Fighter a chunky `BoxMesh`
  (`3.6×1.6×5.5`) vs the Scout's sleek cone, so the two classes read as distinct silhouettes.

**Verified headless:** `--autofly` spawns a Scout (`Class=scout`), `--autofly --fighter` spawns a
Fighter (`Class=fighter`); both integrate and fly. Sampled cruise speeds: **Scout ~34 u/s vs
Fighter ~26 u/s**, matching the Scout's higher thrust/maxspeed (45/70 vs 30/50). The subjective
"do they *feel* distinct" check, like T4's fun gate, needs a human at the keyboard.

> **Headless gotcha (cost ~20 min):** pass `--autofly`/`--fighter` **before** any `--` separator
> — `OS.GetCmdlineArgs()` only sees engine args; args after `--` go to `GetCmdlineUserArgs()`.
> Launch: `Godot --headless --path client --autofly --fighter` (godot bin is
> `/Applications/Godot_mono.app/Contents/MacOS/Godot`; the old `~/.local/bin/godot` is gone).

## T8 (DONE, pending subjective sign-off): weapons, damage, death, collisions

**Schema:** added `Ship.LastFireTick` (uint) and `Projectile.Damage` (float). Republished
`--delete-data` + regenerated bindings.

**Server — `SimTick` restructured into three passes** (`module/spacetimedb/Lib.cs`):
- **Pass A** — integrate every ship (unchanged path), then **fire**: if `Firing` and
  `tick - LastFireTick >= FireInterval(class)`, insert a `Projectile` at the nose
  (`pos + forward*3`) with velocity `forward*250 + shipVel`, `Damage = WeaponDamage(class)`,
  `ExpiresAtTick = tick + 50`; stamp `LastFireTick`.
- **Pass B** — advance each projectile by `dt`, cull at `ExpiresAtTick`, resolve hits: blocked by
  asteroids (cull, no damage), damages **enemy** ships (friendly fire skipped by `Team`), damage
  accumulated into a dict. (Projectile→base is **T9**.)
- **Pass C** — collisions (enemy ship-vs-ship pairwise; ship-vs-asteroid; ship-vs-**enemy**-base
  — own base passes through), then apply accumulated damage and `KillShip` at `Health<=0`
  (`KillShip` deletes the ship row + its input buffer + clears `Player.ShipId`).
- Combat tuning (`.PLAN/03`): Scout dmg 4 / interval 4 ticks (5 shots/s), Fighter dmg 10 /
  interval 8. All combat constants are **server-only** — clients just render `Projectile` rows.
- **Spawn now faces the sector center** (yaw so local +Z points base→origin): a nicer spawn AND
  it makes the head-on combat test deterministic.

**Client:** `ProjectileView.cs` (new) renders each shot by **velocity-extrapolation** from the
last authoritative sample (smooth + accurate for straight-line shots; re-anchors each 20 Hz
update). `WorldRenderer` instances/updates/frees projectile nodes (bright unshaded sphere). HUD
shows `HP cur/max`. `Firing` wired to **Space / left-mouse** in `ReadInput`. New `--combat-test`
dev flag (implies `--autofly`): flies straight + fires (no weave, no divergence self-test).

**Verified headless:**
- `--autofly` (weave + fire): projectiles spawn at the fire rate, advance, and cull in a bounded
  rolling window (IDs increment, old ones disappear).
- Two `--combat-test` clients (teams 0/1, spawn at ∓500 facing center): closed head-on and
  **both ships were destroyed in-sim**; **`Player.ShipId` cleared to null on both** (so the HUD
  spawn menu reappears); `Match` stayed `Active` (ship death ≠ match end — that's T9).
- The lone firing ship in the first test stayed at full health → own-team/self projectiles do no
  damage (the `Team` friendly-fire guard).

> The T8 server changes don't touch the integration/`LastInputTick` path (Pass A is the same
> integrate + a projectile insert + fire-tick stamp), so T5's zero-divergence prediction parity
> still holds; spawn-facing-center is just initial state, identical on both sides.

**Subjective:** "is combat fun / readable" needs a human flying (fire feel, tracer visibility,
collision bounce). Tune the combat constants in `Lib.cs` if needed (then republish).

---

## Key facts to avoid repeating past mistakes

- The SDK builder uses **`WithDatabaseName()`** not `WithModuleName()`.
- `OnConnectError` callback signature is `Action<Exception>`, not `Action<ErrorContext>`.
- **SpacetimeDB enums forbid explicit values.** `enum ShipClass : byte { Scout = 0 }` fails
  with `BSATN0002`. Declaration order defines the value — write `{ Scout, Fighter }`.
- **`--delete-data` is a boolean flag**, not `--delete-data always` (that errors with
  "unexpected argument 'always'"). The `always`/`migrate` style values belong on `--yes`.
- Mount **`.stdb-config/`** on every authenticated CLI call so the identity stays stable
  (see the Environment section). A bare `docker run` gets a new identity → `403`/`401`.
- Don't mount a *named* docker volume at the CLI config path — it's root-owned and the
  container user can't use it (the publish hangs silently). Use the host-path bind mount.
- `spacetime init` puts the C# project in `module/spacetimedb/` (not `module/` directly). The `module/spacetime.json` ties it together. **Do not move these files.**
- The module's `CLAUDE.md` (generated by `spacetime init`) has accurate 2.x syntax — treat it as the ground truth for attribute names and DB operation patterns.
- The `.PLAN/` files are the design spec; they were written before the SDK was pinned and some syntax details differ. Prefer `module/CLAUDE.md` for C# specifics, `.PLAN/` for game design intent.

---

## File map

```
.stdb-config/             ← persistent CLI identity/token (gitignored); mount on every auth'd call
scripts/
  start-db.sh             ← start server (persistent named volume) + populate-if-needed; idempotent
  populate-db.sh          ← publish the module (runs Init -> seeds Match/Bases/Asteroids); --reset wipes
shared/
  FlightModel.cs          ← CANONICAL deterministic flight model; edit here only
  sync.sh                 ← copies FlightModel.cs verbatim into module/ + client/, diffs to verify
module/
  spacetime.json          ← points to spacetimedb/ subdirectory
  spacetimedb/
    Lib.cs                ← schema (Ship has AngVel) + reducers; SimTick integrates ships
    FlightModel.cs        ← GENERATED copy of shared/FlightModel.cs (via sync.sh); do not edit
    StdbModule.csproj     ← do not touch
    global.json           ← pins net8.0, do not touch
  CLAUDE.md               ← 2.x API reference, read this

client/
  scripts/ConnectionManager.cs   ← connects, exposes Conn/Identity + Connected event, subscribes
  scripts/WorldRenderer.cs       ← instances base/asteroid/ship nodes; exposes LocalShip (PredictionController)
  scripts/ShipController.cs      ← server-tick-slaved 20 Hz input loop → ApplyInput + prediction; controls; --autofly flag
  scripts/PredictionController.cs← local ship: fixed-dt predict in server-tick space, rollback reconcile, pos+rot correction smoothing
  scripts/RemoteShip.cs          ← other players' ships: 100 ms snapshot interpolation (lerp pos / slerp rot)
  scripts/ProjectileView.cs      ← projectile render: velocity-extrapolated from last authoritative sample
  scripts/CameraRig.cs           ← chase camera (Camera3D), overview fallback
  scripts/Hud.cs                 ← Scout/Fighter spawn menu (shipless) + speed/reconcile readout (flying)
  scripts/ShipMath.cs            ← Ship row ↔ FlightModel ↔ Godot marshaling
  scripts/FlightModel.cs         ← GENERATED copy of shared/FlightModel.cs (via sync.sh); do not edit
  module_bindings/               ← GENERATED (tables + reducers), do not edit
  wivuullegiance.csproj
  scenes/Main.tscn               ← Node3D root: ConnectionManager, WorldRenderer, ShipController, env, light, CameraRig, Hud

tests/
  FlightModelTest/        ← standalone net8.0 console test for FlightModel (dotnet run -c Release)

.PLAN/                    ← design docs; read for intent, not for copy-paste syntax
ACCEPTANCE01.md           ← T0 manual test checklist (already passing)
```
