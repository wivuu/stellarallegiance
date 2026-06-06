# Phase 1 — Configurability & maintainability refactor

## Context

Today every ship/weapon/base tuning value is a hard-coded constant compiled into
**both** the server WASM module and the Godot client. Ship classes are a 2-value
enum (`Scout`, `Fighter`); a weapon's identity is derived at fire time from a
`switch`-like ternary (`WeaponDamage(class)`, `FireInterval(class)`); muzzle and
engine-nozzle positions are magic floats duplicated across server and client.
Adding a third ship, a new gun, or a different base means editing code in three
files and redistributing binaries.

`shared/FlightModel.cs` exists precisely because server (WASM) and client (Mono)
must read **bit-identical** tuning so prediction matches authority — that
constraint governs the whole design.

**Goal:** move ship/weapon/base content into runtime, server-deployable data so
new ships, weapons, bases — and eventually whole **factions** — are config, not
code, and an operator can retune a server *without recompiling or redistributing
the client*. Then break the 1,734-line `Lib.cs` into focused modules, and add
client loaders that resolve ship/base **hardpoints** (weapons, engines, turrets,
docking, lights) from that data instead of hard-coded offsets.

### Architecture decision (per user steer)

Definitions live in **public SpacetimeDB tables** (the authoritative, runtime
source), **seeded in `Init` from compiled-in defaults**, **overridable at runtime
via admin reducers** (so an operator changes rows without rebuilding the WASM),
and **subscribed by the client**. Determinism holds because the server *writes*
the f32 tuning into the table and the client *reads the identical bits* — both
feed the same `FlightModel` math. Compile-in defaults double as a safe fallback
until subscription data arrives. This is the seam that later carries `FactionDef`.

What stays a constant: sim-infrastructure values that are not per-class content
(`SimTickHz`, `DtMicros`, `InputKeep`, `MaxCatchupSteps`, collision scales,
boundary DPS, sector/world geometry). A `GlobalConfig` table is a noted future
extension, not part of this phase.

---

## Data model (new module tables, all `Public = true`)

Defined as `[SpacetimeDB.Type]`/`[SpacetimeDB.Table]` in the module. Hardpoints
are embedded as `List<HardpointDef>` so a class/base's full definition is one row
(authoring-friendly, faction-ready).

- **`ShipClassDef`** — PK `byte ClassId`. Fields: `Name`, flight stats
  (ThrustAccel, MaxSpeed, LinearDrag, Mass, AngularAccel, AngularDrag,
  BoostThrustMult, BoostSpeedMult), `MaxHull`, `List<HardpointDef> Hardpoints`,
  `uint FactionId` (reserved, default 0).
- **`WeaponDef`** — PK `uint WeaponId`. Fields: `Name`, `Damage`,
  `FireIntervalTicks`, `ProjectileSpeed`, `ProjectileLifeTicks`,
  `ProjectileRadius`, `SpreadRad`.
- **`BaseDef`** — PK `byte BaseTypeId`. Fields: `Name`, `Radius`, `MaxHealth`,
  `List<HardpointDef> Hardpoints`.
- **`HardpointDef`** (`[SpacetimeDB.Type]` value struct, not a table): `Kind`
  (enum), `Index` (byte, for multiples), local offset `OffX/OffY/OffZ`, local
  forward `DirX/DirY/DirZ`, `uint WeaponId` (weapon hardpoints only).
- **`HardpointKind`** enum (declaration order fixes values, per STDB rules):
  `Weapon, MainEngine, Booster, Thruster, Turret, Light, DockingEntrance, DockingExit`.

Migration of current magic values:
- `NoseOffset = 3f` → a `Weapon` hardpoint offset on each `ShipClassDef`.
- `ProjectileSpeed/LifeTicks/Radius`, `WeaponDamage`, `FireInterval`,
  `Scout/FighterSpread` → `WeaponDef` rows (one per class's gun initially).
- `MaxHull` + the `FlightModel.Scout/Fighter` stat blocks → `ShipClassDef` seed.
- `BaseMaxHealth`, `BaseRadius` → `BaseDef`.
- Hard-coded nozzle/trail positions in `WorldRenderer` → `MainEngine`/`Booster`/
  `Thruster` hardpoints on `ShipClassDef`.

---

## Milestones (sequenced)

### M1 — Definition tables, seed, and admin reducers (`module/spacetimedb/Defs.cs`, new)
- Declare the tables/types/enum above (table structs are top-level; helper
  methods join `public static partial class Module`, the existing class at
  `Lib.cs:274`).
- `SeedDefaults(ctx)` called from `Init` (`Lib.cs` ~485). Seed Scout/Fighter from
  `FlightModel.Scout/Fighter` (`shared/FlightModel.cs:165-193`) so those authored
  numbers stay single-sourced; seed the two weapons and the base type from the
  values currently at `Lib.cs:286-326`.
- Admin upsert reducers (`UpsertShipClassDef`, `UpsertWeaponDef`, `UpsertBaseDef`)
  gated on `ctx.Sender` == server owner identity. These give the no-recompile
  runtime-override path (callable via `spacetime call` or a JSON seed script).
- Server read helpers: `ShipDef(ctx, classId)`, `WeaponFor(ctx, weaponId)`,
  `BaseDefFor(ctx, typeId)`.

### M2 — Server consumes defs; split `Lib.cs`
Replace constant lookups with table reads, then relocate code into partial-class
files (no namespace change — `Module` is already `partial`):
- **`Ships.cs`** — move `Ship` struct (`Lib.cs:76`), `SpawnShip`/`Respawn`
  reducers (`Lib.cs:800-810`), `SpawnShipInternal` (`Lib.cs:1459`), `KillShip`.
  `MaxHull`/`MassFor`/spawn now read `ShipClassDef`.
- **`Weapons.cs`** — move `Projectile` struct (`Lib.cs:211`); extract the fire
  pass from `SimTick` (`Lib.cs:1081-1118`) into `TryFire(ctx, ship, ...)` reading
  the ship's `Weapon` hardpoint + `WeaponDef` (muzzle offset, speed, damage,
  spread, life). `SimTick` calls the helper — keep the hot loop's structure.
- **`Bases.cs`** — move `Base` struct (`Lib.cs:138`), base seeding
  (`Lib.cs:512`), projectile/ship-vs-base damage and regen (`Lib.cs:1238-1267`,
  `1599`), win check. Radius/health from `BaseDef`.
- `Lib.cs` retains enums, remaining tables (Player, Match, Sector, Aleph,
  Asteroid, ChatMessage, ShipInput, SimTickTimer), `Init`, lifecycle, chat, and
  the `SimTick` orchestrator that calls the extracted helpers.
- Regenerate client bindings (`spacetime generate`) for the new public tables.

### M3 — Client def registry + prediction reads data (`client/scripts/DefRegistry.cs`, new)
- Subscribe to `ShipClassDef`/`WeaponDef`/`BaseDef`; build dictionaries; expose
  `GetStats(classId)→ShipStats`, `GetHardpoints(classId)`, `GetWeapon(id)`,
  `GetBaseDef(id)`. Wire into `ConnectionManager`'s subscription set.
- `PredictionController.cs:154-161` — replace local `NoseOffset`/`ProjectileSpeed`/
  `WeaponSpreadRad`/`StatsFor` with registry lookups (muzzle hardpoint + weapon
  def). **Fallback** to `FlightModel` compile-time defaults until the def loads
  (defs arrive in the initial subscription, before any ship can spawn). Guard so
  prediction never runs on a missing def.
- `FlightModelTest` must still pass unchanged (math untouched; it validates the
  fallback/default numbers).

### M4 — Ship mesh + hardpoint loader (`client/scripts/ShipModelLoader.cs`, new)
- `Build(classId) → MeshInstance3D` that (a) renders the **existing procedural
  placeholder** (cone for Scout, box for Fighter — current `BuildShipMesh`,
  `WorldRenderer.cs:680-707`) and (b) instantiates a `Marker3D` child per
  `HardpointDef`, named `HP_<Kind>_<Index>`, at the def's local offset/forward.
- `AttachEngineGlow` (`WorldRenderer.cs:713-757`): populate `EngineGlow.Nozzles`
  from `MainEngine`/`Booster`/`Thruster` hardpoints; anchor the team trail from a
  hardpoint — deleting the hard-coded `(0,0,-2.25)`/`(±1.1,0,-2.75)` floats.
- Document the future GLB convention: when a ship `.glb` exists, the loader loads
  it and reads same-named `HP_*` nodes to override placeholder markers. Turret
  hardpoints are carried as data + markers now; turret *firing logic* is out of
  scope (later phase).

### M5 — Base mesh + hardpoint loader (`client/scripts/BaseModelLoader.cs`, new)
- Mirror M4 for bases: procedural sphere placeholder (`WorldRenderer.cs:272-288`)
  + `Marker3D` hardpoints for `DockingEntrance`, `Light`, `Exit` from `BaseDef`.
- Add a simple blinking light node at each `Light` hardpoint (the roadmap's
  "lighting (blinking)"). Docking/exit are exposed as positioned markers only —
  docking/spawn-exit *logic* belongs to later phases; this phase just carries the
  data and visualizes it.

---

## Key files
- New (server): `module/spacetimedb/Defs.cs`, `Ships.cs`, `Weapons.cs`, `Bases.cs`
- Edit (server): `module/spacetimedb/Lib.cs` (shrinks to core/sim + Init),
  `PigAI.cs` (it reuses `WeaponDamage`/`FireInterval`/spread — repoint to
  `WeaponDef` reads)
- New (client): `client/scripts/DefRegistry.cs`, `ShipModelLoader.cs`,
  `BaseModelLoader.cs`
- Edit (client): `ConnectionManager.cs` (subscriptions), `PredictionController.cs`,
  `WorldRenderer.cs`, `EngineGlow.cs` (nozzles from data)
- Unchanged authored source of truth: `shared/FlightModel.cs` (math + default
  stat blocks used as seed/fallback)

## Risks / watch-items
- **Determinism:** never change `FlightModel` math; only the *source* of the
  numbers moves. Server writes, client reads identical f32 → no drift.
- **Client gating:** prediction/render must fall back to compiled defaults if a
  def row hasn't arrived; never index a missing def.
- **`PigAI` coupling:** drones currently share the same constants — repoint them
  to `WeaponDef`/`ShipClassDef` reads in the same pass or they break.
- **Behavior parity:** seeded values must equal today's constants so M1–M3 are a
  pure refactor (no gameplay change); M4–M5 likewise visually identical.

## Verification (end-to-end)
1. Build module in Docker + publish local: `scripts/publish-local.sh` (mount repo
   root so the `shared/` ProjectReference resolves — known gotcha).
2. Confirm seed: `spacetime sql <db> "SELECT * FROM ShipClassDef"` (and
   `WeaponDef`, `BaseDef`) show the expected rows.
3. Determinism: `cd tests/FlightModelTest && dotnet run -c Release` → ALL PASSED.
4. Headless sim: run a client with `--server --anonymous` and hold the connection
   (sim only ticks while a client is connected — known gotcha); verify ships
   spawn, fire from the muzzle hardpoint, engine glow at nozzles, base renders.
5. **Runtime-config proof:** `spacetime call <db> UpsertWeaponDef ...` to bump
   Scout damage, observe the client reflect it **without** a rebuild — this is the
   core Phase-1 success criterion.
6. `graphify update .` to refresh the knowledge graph after the refactor.
