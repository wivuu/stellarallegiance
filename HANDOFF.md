# Agent Handoff — wivuullegiance

## Current state

T0–T3 and **T4 are complete** (T4 mechanically verified; subjective flight-feel sign-off
still needs a human at the keyboard). The repo has:
- A SpacetimeDB C# module with the **full game schema** (7 tables + 2 enums + scheduled SimTick) published to a local server, seeded with 1 Match, 2 Bases, 30 Asteroids. `Ship` now also has `AngVelX/Y/Z`.
- The 20 Hz `SimTick` **integrates every ship** via the shared `FlightModel` (ships only — projectiles/hits are T8)
- Player-action reducers: `SpawnShip`, `Respawn`, `ApplyInput`, `SetName`
- A Godot 4.6.3 C# client that connects, subscribes, renders the static world, **spawns and flies a Scout with client-side prediction + rollback reconciliation**, a chase camera, and a minimal HUD
- A **shared, deterministic, fixed-`dt` flight model** (`shared/FlightModel.cs`) copied byte-identically into `module/` and `client/`, with a passing determinism+golden test
- `dotnet build` (client) and the module wasm publish both succeed (one harmless generated-code warning on the client)

**Next task: T5 — reconciliation correctness** (see `.PLAN/08-BUILD-ORDER.md`, `07`). Instrument
and confirm reconciliation rarely fires under good conditions and recovers cleanly when
divergence is injected. `PredictionController.ReconcileCount` already exists as the counter to
build on; also resolve the transcendental-determinism question (`.PLAN/99`) if drift appears.

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

**Start the server** (do this first; data does NOT persist across container restarts):
```bash
docker run -d --rm -p 3001:3000 --name stdb clockworklabs/spacetime start
```

**Refresh the CLI token** (run once after every fresh server start):
```bash
docker run --rm -v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime --network host \
  clockworklabs/spacetime login --server-issued-login http://localhost:3001
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
  `SetName`. `SimTick` now marshals each `Ship` row → `ShipState`, calls `FlightModel.Integrate`
  with its `ShipInput`, and writes back transform + `AngVel` + `LastInputTick = ShipInput.ClientTick`.
- **Client**: `ShipMath.cs` (row↔FlightModel↔Godot marshaling), `PredictionController.cs`
  (local ship: fixed-dt prediction, ring buffer, rollback reconciliation @ 0.25u / 0.05rad,
  velocity-extrapolated rendering, `ReconcileCount`), `RemoteShip.cs` (snap-only for now; T6
  adds interpolation), `ShipController.cs` (20 Hz input loop → `ApplyInput` + `Step`; spawn on
  key `1`; `--autofly` dev flag), `CameraRig.cs` (chase cam, overview fallback), `Hud.cs`
  (spawn prompt + speed). `WorldRenderer` instances the right node per ship and exposes `LocalShip`.
- **Controls**: WASD thrust/strafe, Space/Shift up-down, arrow keys aim, Q/E roll. Ships fly
  along local **+Z** (matches the flight model); the cone mesh and chase cam are built around that.

**Verified:** `--autofly` headless run spawns a Scout and flies it — SQL shows the `Ship` row
moving away from the base with `LastInputTick` advancing (e.g. 131→134→136), proving the full
`ApplyInput → SimTick integrate → Ship update → LastInputTick echo` loop. A windowed capture
confirms the cone renders, oriented along travel, with the chase cam behind it and HUD speed
(~35 u/s terminal). Reconciliations are rare (2–5 over a few seconds on localhost), not
rubber-banding.

**Not done:** the subjective "is it fun for two minutes" gate needs a human flying with the
keyboard. Constants are the `.PLAN/03` placeholders (Scout: thrust 45 / maxspeed 70 / drag 1.2;
angular 3.5 / drag 2.5) — tune to taste in `shared/FlightModel.cs` then `bash shared/sync.sh`.

**Gotchas learned:** headless Godot runs **uncapped**, so `--quit-after <frames>` elapses in a
fraction of a real second — to observe live state, run with a huge frame cap in the background
and sample, then stop it (don't expect SQL to catch a short headless run). Ships spawn *inside*
the 45-unit base sphere (see-through from within due to back-face culling) and fly out — fine,
noted in `99`. The chase-cam framing of a small cone seen dead-rear reads as a faceted disc;
that's correct.

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
  scripts/ShipController.cs      ← 20 Hz input loop → ApplyInput + prediction; spawn key; --autofly flag
  scripts/PredictionController.cs← local ship: fixed-dt predict, ring buffer, rollback reconcile
  scripts/RemoteShip.cs          ← other players' ships (snap-only; interpolation is T6)
  scripts/CameraRig.cs           ← chase camera (Camera3D), overview fallback
  scripts/Hud.cs                 ← spawn prompt + speed readout
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
