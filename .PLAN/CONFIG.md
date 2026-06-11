# Phase 1 — Allegiance flight feel + configurability & maintainability refactor

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

**Two goals, in order:**

1. **Replicate Allegiance's flight feel.** `.PLAN/ship_movement/` is a
   self-contained extraction of the original engine's movement model (per-tick
   integration loop, rotation math, per-hull stat schema, and real numbers for
   74 hulls). The current `FlightModel.Integrate` is a generic
   accel/linear-drag model that does **not** feel like Allegiance — it lacks
   throttle-commands-speed semantics, exponential drag equilibrium, per-axis
   rate+torque-limited turning, speed-dependent agility, and weak strafe/
   reverse. Rework the shared model to the Allegiance loop **first**, because
   it changes the per-ship stat schema everything else carries.
2. **Move ship/weapon/base content into runtime, server-deployable data** so
   new ships, weapons, bases — and eventually whole **factions** — are config,
   not code, and an operator can retune a server *without recompiling or
   redistributing the client*. Then break the 1,734-line `Lib.cs` into focused
   modules, and add client loaders that resolve ship/base **hardpoints**
   (weapons, engines, turrets, docking, lights) from that data instead of
   hard-coded offsets.

Sequencing rationale: the Allegiance stat schema (the "nine knobs", below) *is*
the `ShipClassDef` row layout. Landing M0 first means the def tables, seeds,
admin reducers, client registry, and golden tests are built once against the
final schema instead of being migrated mid-phase.

### Architecture decision (per user steer)

Definitions live in **public SpacetimeDB tables** (the authoritative, runtime
source), **seeded in `Init` from compiled-in defaults**, **overridable at runtime
via admin reducers** (so an operator changes rows without rebuilding the WASM),
and **subscribed by the client**. Determinism holds because the server *writes*
the f32 tuning into the table and the client *reads the identical bits* — both
feed the same `FlightModel` math. Compile-in defaults double as a safe fallback
until subscription data arrives. This is the seam that later carries `FactionDef`
and Allegiance's per-team Global Attributes (tech upgrades multiply the raw
stats; see `03_constants_and_enums.md` — reserved, not in this phase).

What stays a constant: sim-infrastructure values that are not content
(`SimTickHz`, `DtMicros`, `InputKeep`, `MaxCatchupSteps`, collision scales,
boundary DPS). **Sector/world geometry moves to config in this phase** — a
small `WorldConfig` table (sector scale, asteroid density) feeds the
deterministic map seeding. A fuller `GlobalConfig` (Allegiance's
`Constants.csv` float-constants analog — exit-warp speed, ripcord time, etc.)
remains a noted future extension; `WorldConfig` is its first slice.

---

## The flight model we are replicating

Reference: `.PLAN/ship_movement/README.md` (feel summary),
`01_flight_model.md` (the 8-step loop), `02_rotation_math.md`,
`04_data_schema.md` (stat schema + derivations), `05_reference_implementation.py`
(runnable port), `06_extracted_hull_stats.md` + `hull_stats.csv` (real numbers).

The five signatures of the feel, and what each means for our code:

1. **Newtonian flight + exponential drag; `maxSpeed` is an equilibrium, not a
   cap.** Per tick: `f = exp(-thrust·dt/(mass·maxSpeed))`,
   `drag = v·(1-f)/(dt/mass)`. Full thrust asymptotes to `maxSpeed`; speed
   imposed from outside (collisions, afterburner) bleeds off smoothly. This
   *replaces* `LinearDrag` and the current soft speed cap entirely.
2. **Turning is rate- AND acceleration-limited, per axis.** Stick commands a
   target angular velocity (`MaxTurnRate[yaw|pitch|roll]`); the persistent
   actual rate slews toward it by at most `TorqueMult·TurnTorque[axis]·dt/mass`
   per tick. Releasing the stick keeps you rotating briefly. This *replaces*
   the single scalar `AngularAccel`/`AngularDrag` pair.
3. **Speed-dependent agility.** `TorqueMult = 0.5 + 0.5·(2f/(f+1))` with
   `f = |v|/maxSpeed` — 50% angular accel at rest, 100% at max speed. Max
   *rate* is constant.
4. **Strafe and reverse are deliberately weak.** Local thrust components are
   divided by `SideMultiplier`/`BackMultiplier` (<1) before clipping against
   engine capacity; forward (local −z) is unpenalized.
5. **Throttle commands a desired speed, not a force.** Engine thrust is
   computed from `(desiredVelocity − velocity)/thrustToVelocity + drag`, then
   clipped. A **coast** mode (vector lock) makes thrust exactly cancel drag.
   Afterburner adds `power·abThrust` along the forward axis (power ramps up/
   down at `onRate`/`offRate`), raising the equilibrium speed to
   `maxSpeed·(1 + abThrust/thrust)`.

**Mass cancels out of flight feel** (algebra in `04_data_schema.md`): thrust is
stored as a force `mass·accel`, so effective linear accel is `accel` and turning
is likewise mass-free. Mass matters only for collisions/momentum — which is
exactly the behavior our current `accelMul = baselineMass/actualMass` hack
approximates; under the real model a heavier *instance* (same hull force,
larger mass) accelerates slower with no special-casing.

### The authoring schema (the nine knobs + afterburner)

Per `04_data_schema.md`, a hull's entire flight feel is:

```yaml
maxSpeed:        # terminal velocity (u/s)
acceleration:    # forward accel (u/s²) — thrust = mass·acceleration internally
rateYaw:         # deg/s max yaw rate          (stored in def as authored;
ratePitch:       # deg/s                        deg→rad conversion is derived)
rateRoll:        # deg/s
driftYaw:        # deg overshoot angle — smaller = snappier (biggest feel lever)
driftPitch:      # deg (also reused for roll torque, faithful to the original)
sideMultiplier:  # 0..1 strafe thrust fraction
backMultiplier:  # 0..1 reverse thrust fraction
mass:            # collisions/momentum only
# afterburner (Allegiance: a mounted part; we fold it into the hull row)
abAccel:         # extra forward accel at full power (abThrust = mass·abAccel)
abOnRate:        # power ramp-up per second
abOffRate:       # power ramp-down per second
```

Derived once at load (NOT stored, NOT authored): `thrust = mass·accel`,
`turnTorque[axis] = mass·(rate²/(2·drift))·π/180` (rates→rad/s), and the
per-tick drag factor `f = exp(-accel·dt/maxSpeed)` (constant for fixed dt).

### Seed values (from the extracted real hulls)

From `06_extracted_hull_stats.md` (`artwork/tester.igc`):

| Class | maxSpeed | accel | yaw/pitch/roll °/s | drift° | side | back | mass |
|---|--:|--:|--:|--:|--:|--:|--:|
| Scout (orig "Scout") | 160 | 30 | 50/50/50 | 5 | 0.5 | 0.25 | 40 |
| Fighter (orig "Fighter") | 100 | 25 | 60/60/60 | 5 | 0.5 | 0.5 | 36 |
| Pod (orig "Lifepod") | 60 | 15 | 40/40/40 | 8 | 1.0 | 1.0 | 10 |

Note the faithful quirk: the Fighter out-*turns* the Scout (60 vs 50 °/s); the
Scout's edge is straight-line speed and snap. Afterburner seeds preserve
today's boost feel as a starting point: `abAccel` chosen so
`abThrust/thrust ≈ 0.6` (Scout) / `0.4` (Fighter) → boosted equilibrium ≈
1.6×/1.4× maxSpeed; `onRate/offRate ≈ 2.0/1.0` (sub-second ramp), tune in M0.
Pod: `abAccel = 0` (no afterburner).

**Units / world scale (DECIDED): adopt Allegiance-native numbers unchanged.**
They're internally consistent (drift formulas, torque ratios, AB ratios all
assume them), and everything world-scale that hangs off them becomes config in
this phase anyway: projectile speed/life via `WeaponDef` (M1), and **sector
size + asteroid density via `WorldConfig`** (below). Allegiance speeds are
~2× our current (160 vs 70 u/s), so the default sector scale should land
around 2-2.5× today's radii (Core 1100 → ~2500) — pick the default during the
M0 feel pass so cross-sector travel time feels right at the new speeds.
Camera and collision scales get a one-time sanity check in the same pass.

---

## Data model (new module tables, all `Public = true`)

Defined as `[SpacetimeDB.Type]`/`[SpacetimeDB.Table]` in the module. Hardpoints
are embedded as `List<HardpointDef>` so a class/base's full definition is one row
(authoring-friendly, faction-ready).

- **`ShipClassDef`** — PK `byte ClassId`. Fields: `Name`, the **authoring
  schema above** (`Mass`, `MaxSpeed`, `Accel`, `RateYawDeg`, `RatePitchDeg`,
  `RateRollDeg`, `DriftYawDeg`, `DriftPitchDeg`, `SideMult`, `BackMult`,
  `AbAccel`, `AbOnRate`, `AbOffRate`), `MaxHull`,
  `List<HardpointDef> Hardpoints`, `uint FactionId` (reserved, default 0).
  Only authored knobs are stored; both sides derive thrust/torques/drag-factor
  from the identical f32 inputs via the same shared code, so derived values are
  bit-identical by construction.
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
- **`WorldConfig`** — singleton row (PK `byte Id = 0`). Fields:
  `float SectorScale` (multiplier on the authored per-sector radii; default
  ~2.25 per the units decision) and `float AsteroidDensity` (asteroids per
  unit of normalized sector volume; default chosen so today's 30/14 counts
  are reproduced at scale 1.0). Consumed by map seeding, not per-tick sim:
  - **Sector radius** = authored radius × `SectorScale` (`CoreRadius`/
    `VergeRadius`, `Lib.cs:362-363`, written into the `Sector` rows the client
    already subscribes to — boundary warnings/minimap pick it up for free).
  - **Aleph spread** falls out automatically: aleph and asteroid placement
    derive from sector radius (`RandomOuterPos` picks positions in ~[0.6, 0.9]
    of it, `Lib.cs:485-495`; the Verge belt radius scales with its sector), so
    a bigger sector spreads the alephs farther apart by construction.
  - **Asteroid count** = `round(AsteroidDensity × baseCount × SectorScale³)`
    per field (base counts `Lib.cs:300,364`) — bigger sectors get more rocks
    at the same density; density is the independent knob on top. (Cube law is
    the starting point; if big sectors feel cluttered or empty, dropping to
    square-law is a one-line tune — decide during M2 verification.)

Migration of current magic values:
- `NoseOffset = 3f` → a `Weapon` hardpoint offset on each `ShipClassDef`.
- `ProjectileSpeed/LifeTicks/Radius`, `WeaponDamage`, `FireInterval`,
  `Scout/FighterSpread` → `WeaponDef` rows (one per class's gun initially).
- `MaxHull` + the (M0-reworked) per-class stat blocks → `ShipClassDef` seed.
- `BaseMaxHealth`, `BaseRadius` → `BaseDef`.
- Hard-coded nozzle/trail positions in `WorldRenderer` → `MainEngine`/`Booster`/
  `Thruster` hardpoints on `ShipClassDef`.

---

## Milestones (sequenced)

### M0 — Allegiance flight model (`shared/FlightModel.cs` rework)

The one milestone that intentionally **changes gameplay**; everything after it
is pure refactor against the new baseline.

- **`ShipStats` → authoring schema.** Replace
  `ThrustAccel/MaxSpeed/LinearDrag/AngularAccel/AngularDrag/BoostThrustMult/BoostSpeedMult`
  with the nine knobs + afterburner fields. Add a `DerivedStats` (or lazily
  computed block) holding `thrust`, `turnTorqueRad[3]`, `maxTurnRateRad[3]`,
  `dragFactor` — computed once per stats instance, never per tick.
- **`MathDet.Exp`.** The drag factor needs `exp`. Add a deterministic float
  `Exp` beside `MathDet.Sin/Cos` (same recipe: range-reduce via integer ops,
  Horner polynomial, float +,−,* only — no libm). It runs only at stats-load
  time, so accuracy ~1e-6 is ample; what matters is both runtimes computing
  identical bits from identical f32 inputs.
- **`ShipInputState` semantics.** `Yaw/Pitch/Roll` stay analog −1..1 but are
  now *commanded rate fractions* (and get the unit-**sphere** clamp:
  `l = yaw²+pitch²+roll²; if l>1 scale by 1/√l`). `Thrust` becomes `Throttle`
  ∈ [0,1] = fraction of maxSpeed commanded (Allegiance's −1..1 throttle maps
  affinely; rest = 0 = no thrust). `StrafeX/StrafeY` (+ reverse via throttle
  semantics or an explicit back axis) feed the manual-strafe branch, scaled by
  full thrust. Add `bool Coast` (vector lock: thrust exactly cancels drag).
  `Boost` stays, now driving the afterburner **power ramp** (a new
  `float AbPower` field on `ShipState`, persisted/synced like `AngVel`).
- **`Integrate` → the 8-step loop** (port of `01_flight_model.md` /
  `05_reference_implementation.py`, quaternion form):
  1. sphere-clamp stick; 2. per-axis slew `AngVel` toward
  `stick·maxTurnRate` clamped by `TorqueMult·turnTorque·dt/mass`
  (TorqueMult from current speed); 3. apply attitude — **yaw, then pitch
  (negated), then roll, in that order** as three sequential local-axis
  quaternion rotations (matches the engine's matrix order; do NOT combine into
  one rotation vector), then normalize; 4. drag from the exponential form;
  5. afterburner power ramp + thrust folded into drag; 6. engine thrust
  (manual-strafe / coast / throttle-commands-speed branches); 7. side/back
  clipping against `thrust` capacity; 8. `vel += (engine − drag)·dt/mass`,
  integrate position. The current soft speed cap block is **deleted** — the
  drag equilibrium is the cap (this is feature 1 of the feel; closed form for
  reconciliation: `V(t)=V0·e^(−accel·t/maxSpeed)`).
- **Mass.** Keep `ShipState.Mass` (instance mass). `thrust = st.Mass·st.Accel`
  uses *class* mass; integration divides by *instance* mass — heavier instance
  flies heavier, baseline instance matches the hull numbers exactly,
  collisions/momentum keep using instance mass. Delete the `accelMul` hack.
- **Callers.** Update server `SimTick` + `PigAI` (drone steering actually
  *simplifies*: throttle-commands-speed is what its chase logic wants) and
  client `PredictionController` / input gathering to the new input semantics.
  Client throttle UX: map current thrust key to full throttle (hold = 1,
  release = 0) for now; Allegiance-style incremental throttle setting is a
  later UX item, not flight-model work.
- **Seeds + tuning pass.** Seed Scout/Fighter/Pod from the table above
  (post units-decision). Fly it: confirm terminal-speed equilibrium, turn
  spin-up/overshoot (~5° drift), locked-up-at-rest vs crisp-at-speed, weak
  strafe/reverse, AB overspeed + bleed-off.
- **Tests.** `FlightModelTest` golden run is **regenerated** (the old golden
  encodes the old model — note: 2 of its cases already fail on clean master).
  Add targeted unit tests: drag equilibrium (`|v| → maxSpeed` under full
  throttle), drift angle (integrate a full-rate stop, measure overshoot ≈
  authored drift°), TorqueMult endpoints (0.5/1.0), side/back clip ratios,
  determinism (server/client bit-identity harness unchanged).

### M1 — Definition tables, seed, and admin reducers (`module/spacetimedb/Defs.cs`, new)
- Declare the tables/types/enum above (table structs are top-level; helper
  methods join `public static partial class Module`, the existing class at
  `Lib.cs:274`).
- `SeedDefaults(ctx)` called from `Init` (`Lib.cs` ~485). Seed Scout/Fighter
  (and Pod) from the M0 `FlightModel` stat blocks so those authored numbers
  stay single-sourced; seed the two weapons and the base type from the values
  currently at `Lib.cs:286-326`.
- Admin upsert reducers (`UpsertShipClassDef`, `UpsertWeaponDef`,
  `UpsertBaseDef`, `UpsertWorldConfig`) gated on `ctx.Sender` == server owner
  identity. These give the no-recompile runtime-override path (callable via
  `spacetime call` or a JSON seed script). With the Allegiance schema this is
  also the **content pipeline**: any row of `hull_stats.csv` (379 records) can
  be poured straight into `ShipClassDef` — Interceptor, Bomber, Gunship etc.
  become data-only additions. `UpsertWorldConfig` additionally triggers a
  deterministic map rebuild (the existing regenerate-from-seed path,
  `Lib.cs:498-512,583`) so the new scale/density take effect immediately —
  same seed + same config ⇒ byte-identical map.
- Server read helpers: `ShipDef(ctx, classId)`, `WeaponFor(ctx, weaponId)`,
  `BaseDefFor(ctx, typeId)`. Reads construct/caches the shared `ShipStats`
  (and its derived block) from the row.

### M2 — Server consumes defs; split `Lib.cs`
Replace constant lookups with table reads, then relocate code into partial-class
files (no namespace change — `Module` is already `partial`):
- **`Ships.cs`** — move `Ship` struct (`Lib.cs:76`), `SpawnShip`/`Respawn`
  reducers (`Lib.cs:800-810`), `SpawnShipInternal` (`Lib.cs:1459`), `KillShip`.
  `MaxHull`/mass/spawn now read `ShipClassDef`.
- **`Weapons.cs`** — move `Projectile` struct (`Lib.cs:211`); extract the fire
  pass from `SimTick` (`Lib.cs:1081-1118`) into `TryFire(ctx, ship, ...)` reading
  the ship's `Weapon` hardpoint + `WeaponDef` (muzzle offset, speed, damage,
  spread, life). `SimTick` calls the helper — keep the hot loop's structure.
- **`Bases.cs`** — move `Base` struct (`Lib.cs:138`), base seeding
  (`Lib.cs:512`), projectile/ship-vs-base damage and regen (`Lib.cs:1238-1267`,
  `1599`), win check. Radius/health from `BaseDef`.
- **Map seeding consumes `WorldConfig`** — `Sector` row radii, asteroid counts,
  and (transitively) aleph/belt placement read scale + density per the data
  model above (`SeedAsteroidField`/`SeedAsteroidBelt`/`RandomOuterPos`,
  `Lib.cs:439-495`; sector inserts `Lib.cs:564-565`); the hard-coded
  `CoreRadius`/`VergeRadius`/`AsteroidCount`/`VergeAsteroidCount` become the
  authored *base* values the config multiplies. Base positions (±500) stay
  authored for now — they belong to `BaseDef`/map authoring, not world scale.
- `Lib.cs` retains enums, remaining tables (Player, Match, Sector, Aleph,
  Asteroid, ChatMessage, ShipInput, SimTickTimer), `Init`, lifecycle, chat, and
  the `SimTick` orchestrator that calls the extracted helpers.
- Regenerate client bindings (`spacetime generate`) for the new public tables.

### M3 — Client def registry + prediction reads data (`client/scripts/DefRegistry.cs`, new)
- Subscribe to `ShipClassDef`/`WeaponDef`/`BaseDef`; build dictionaries; expose
  `GetStats(classId)→ShipStats` (constructing the same shared struct + derived
  block from the row's f32s — bit-identical to the server's), `GetHardpoints`,
  `GetWeapon(id)`, `GetBaseDef(id)`. Wire into `ConnectionManager`'s
  subscription set.
- `PredictionController.cs:154-161` — replace local `NoseOffset`/`ProjectileSpeed`/
  `WeaponSpreadRad`/`StatsFor` with registry lookups (muzzle hardpoint + weapon
  def). **Fallback** to `FlightModel` compile-time defaults until the def loads
  (defs arrive in the initial subscription, before any ship can spawn). Guard so
  prediction never runs on a missing def.
- `FlightModelTest` must still pass unchanged from M0 (math untouched in
  M1-M3; it validates the fallback/default numbers).

### M4 — Ship mesh + hardpoint loader (`client/scripts/ShipModelLoader.cs`, new)
- `Build(classId) → MeshInstance3D` that (a) renders the **existing procedural
  placeholder** (cone for Scout, box for Fighter — current `BuildShipMesh`,
  `WorldRenderer.cs:680-707`) and (b) instantiates a `Marker3D` child per
  `HardpointDef`, named `HP_<Kind>_<Index>`, at the def's local offset/forward.
- `AttachEngineGlow` (`WorldRenderer.cs:713-757`): populate `EngineGlow.Nozzles`
  from `MainEngine`/`Booster`/`Thruster` hardpoints; anchor the team trail from a
  hardpoint — deleting the hard-coded `(0,0,-2.25)`/`(±1.1,0,-2.75)` floats.
  Engine-glow/trail intensity can now key off the *real* engine vector
  (throttle/coast/AB power) rather than raw input — small, optional polish. When the user cuts throttle engine glow should disappear, regardless of whether the ship is actually drifting or held still by drag.
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
- Reference (read-only): `.PLAN/ship_movement/*` — the flight-model spec,
  rotation math, schema/derivations, runnable Python port, and real hull stats.
- Edit (shared): `shared/FlightModel.cs` — M0 rewrite of `ShipStats`,
  `ShipInputState`, `ShipState` (+`AbPower`), `Integrate`, `MathDet` (+`Exp`).
  Weapon-spread helpers (`SpreadDirection` etc.) are untouched.
- New (server): `module/spacetimedb/Defs.cs`, `Ships.cs`, `Weapons.cs`, `Bases.cs`
- Edit (server): `module/spacetimedb/Lib.cs` (M0 input/SimTick touch-ups, then
  shrinks to core/sim + Init), `PigAI.cs` (M0: steering to new input semantics;
  M2: repoint `WeaponDamage`/`FireInterval`/spread to `WeaponDef` reads)
- New (client): `client/scripts/DefRegistry.cs`, `ShipModelLoader.cs`,
  `BaseModelLoader.cs`
- Edit (client): input gathering + `PredictionController.cs` (M0 semantics, M3
  registry), `ConnectionManager.cs` (subscriptions), `WorldRenderer.cs`,
  `EngineGlow.cs` (nozzles from data)
- Edit (tests): `tests/FlightModelTest` — regenerate golden, add the M0
  feel-invariant unit tests.

## Risks / watch-items
- **M0 is a deliberate gameplay change; M1-M5 are not.** Lock the new feel in
  M0 (golden + feel checklist), then hold behavior parity through the refactor:
  seeded values must equal the M0 constants, M4-M5 visually identical.
- **Determinism of the new math:** the Allegiance loop adds `exp` and
  branchier control flow. `MathDet.Exp` must follow the same
  no-libm/no-FMA/Horner rules as `Sin/Cos`; all branches key off f32 compares
  on identically-sourced values, so they fork identically on both runtimes.
  Rotation must be applied as sequential yaw→pitch(−)→roll local rotations —
  combining axes into one rotation vector changes the result and the feel.
- **Per-tick cost:** derive thrust/torques/dragFactor once per stats load, not
  in `Integrate` (server runs this per-ship at 20 Hz in WASM).
- **Units/world scale (decided: Allegiance-native):** don't mix scales — all
  ship rows, weapon speeds, and the default `SectorScale` move together in one
  calibration pass (M0). Projectile speed must stay proportionate to ship
  speeds (Allegiance guns are ~4-6× ship maxSpeed) or dogfights break.
- **`WorldConfig` regen at runtime:** rebuilding the map mid-match relocates/
  re-creates asteroids and alephs under live ships — ships may end up inside a
  rock or outside the (shrunken) boundary. Acceptable for an operator action
  for now, but the reducer should at minimum re-clamp ships into the new
  sector radius; full graceful handling (or restricting regen to between
  matches) is a noted follow-up.
- **Throttle/strafe input plumbing:** the `ShipInput` row semantics change in
  M0 (throttle 0..1, coast bit, sphere-clamped stick). Old clients vs new
  module disagree — fine locally, but flag if anything persistent depends on
  the old fields.
- **Client gating:** prediction/render must fall back to compiled defaults if a
  def row hasn't arrived; never index a missing def.
- **`PigAI` coupling:** drones share the flight model and weapon constants —
  update steering in M0 and repoint constants to defs in M2 or they break.
- **Reconciliation feel:** drag equilibrium replaces the soft cap; the closed
  form `V(t)=V0·e^(−kt)`, `k=accel/maxSpeed` (see `03_constants_and_enums.md`)
  is available if prediction-error smoothing needs it.

## Verification (end-to-end)
1. M0 unit tests: `cd tests/FlightModelTest && dotnet run -c Release` → ALL
   PASSED against the **regenerated** golden + the new feel-invariant tests
   (drag equilibrium, drift overshoot, TorqueMult endpoints, side/back ratios,
   cross-runtime bit-identity).
2. M0 feel pass (manual, against `.PLAN/ship_movement/README.md` TL;DR): speed
   asymptotes — no hard snap at maxSpeed; ship keeps rotating briefly after
   stick release and overshoots ~drift°; turning sluggish at rest, crisp at
   speed; strafe/reverse visibly weaker than forward; AB overspeed then smooth
   bleed-off; coast holds velocity exactly. Optionally cross-check a scripted
   input tape against `05_reference_implementation.py` outputs (tolerance, not
   bit-exact — it's float64 Python with matrix rotation).
3. Build module in Docker + publish local: `scripts/publish-local.sh` (mount repo
   root so the `shared/` ProjectReference resolves — known gotcha).
4. Confirm seed: `spacetime sql <db> "SELECT * FROM ShipClassDef"` (and
   `WeaponDef`, `BaseDef`, `WorldConfig`) show the Allegiance-schema rows and
   the default scale/density.
5. Headless sim: run a client with `--server --anonymous` and hold the connection
   (sim only ticks while a client is connected — known gotcha); verify ships
   spawn, fire from the muzzle hardpoint, engine glow at nozzles, base renders.
6. **Runtime-config proof:** `spacetime call <db> UpsertShipClassDef ...` to,
   e.g., drop the Scout's `DriftYawDeg` 5→2 (snappier) or bump a weapon's
   damage, observe the client reflect it **without** a rebuild — this is the
   core Phase-1 success criterion. Bonus proof: insert a third class straight
   from a `hull_stats.csv` row (e.g. Interceptor: 80/15/60/60/60/5/1/1/20) and
   spawn it.
7. **World-config proof:** `spacetime call <db> UpsertWorldConfig` with
   `SectorScale` 2.25→3.0 and a higher `AsteroidDensity` → map regenerates
   deterministically; client shows a wider boundary, alephs farther out, and a
   denser field — no rebuild. Re-apply the original config + same seed ⇒ the
   original map, byte-identical.
8. `graphify update .` to refresh the knowledge graph after the refactor.
