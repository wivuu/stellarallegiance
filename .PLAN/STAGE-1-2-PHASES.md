# Stage 1 Pivot + Stage 2 — Phased Execution Plan

> **Handoff doc.** Each phase below is an independent work unit with a **working deliverable**. It is
> written so a fresh agent with no prior context can execute one phase at a time: it names exact
> files, the precise changes, the reuse points, the deliverable, and how to verify *that phase*.
>
> Execute phases in order — Phase 2 depends on 0–1; Phases 3–6 depend on the Stage-1 pivot
> (Phases 0–2) being complete. Mark status inline as you go: `☐` not started · `◐` in progress ·
> `✅` done, and update the status line below. Run `graphify update .` after any code change.
>
> **Status:** ✅ P0 · ✅ P1 · ✅ P2 · ✅ P3 · ☐ P4 · ☐ P5 · ☐ P6

---

## Context & rationale

`.PLAN/README.md` (Stage 1, final bullet) calls for a **PIVOT**: the bespoke
`ContentLoader`/`ContentSet`/`ContentValidator` (shipped as a working v1 loading a single
`server/Content/stock.yaml`) should be replaced by the in-repo **`Allegiance.Factions`** library
(`factions/`) as the *canonical content model* — one data layer that also feeds Stage 2's unlock
gating and Stage 4's tech tree/factions, instead of a parallel def schema that would later need
reconciling. The library already **builds + tests green (16/16)** but is dormant substrate today.

After the pivot lands, **Stage 2 — thin strategy spine** follows: per-team shared state, team credits
+ flat paycheck, a per-team unlock-gating hook (riding the library's `TechSet`/`Capability`
forward-closure), and a buy menu (the in-match spawn menu is the buy seam).

### Decisions (already made — do not relitigate)

- **Runtime-data carriage = hybrid (derive where clean, extend where not).** Derive the flight stats
  that map losslessly from existing `Hull` fields; add a small set of explicit optional runtime
  fields only for what has no clean source. **No fragile drift↔torque derivation.**
- **Faction scope = single stock bundle.** Author one `stock` faction reproducing today's
  Scout/Fighter/Bomber/Pod/Garrison/world numbers **exactly**. Multi-faction asymmetry stays a
  Stage-4 concern on the same model.
- **Stage-2 authority = bootstrap-simple** (any-player-spends / auto). **No commander** (Stage 4).

### Key facts established during exploration

- **Library is server-only.** `YamlDotNet` must not leak into `shared/`/`client`/wasm. The projection
  produces the existing `shared/Defs.cs` records (`ShipClassDef`/`WeaponDef`/`BaseDef`/`WorldConfig`),
  which already stream over `Protocol.MsgDefs`. **The wire path and the client are unchanged for
  Stage 1** — no protocol bump until Phase 4.
- **No SDK-pin conflict.** Repo has no root `global.json`; `server/` and the library both target
  `net8.0` and both already use `YamlDotNet 16.2.1`. **Do not** copy `factions/global.json` into the
  repo root.
- **The Core model lacks runtime-only data** the defs need: the 13 flight f32s, hardpoint geometry,
  stable byte ids, tick-based ballistics, and a world config (see `docs/GLB-AND-HARDPOINT-FORMAT.md`).
- **Determinism contracts to preserve:**
  - `ShipStats.FromDef` (`shared/FlightModel.cs`) stays byte-identical on both server (wasm-free
    native) and client (mono) — it is shared source; **do not change its math**.
  - `Protocol.BuildDefs` wire encoding is unchanged for Stage 1.
  - `tests/ContentTest` asserts two loads → byte-identical defs.
  - `tests/FlightModelTest` golden uses **inline** stat fixtures (decoupled from content) and must
    stay green.

### Library public API (from `factions/INTEGRATING-INTO-STELLARALLEGIANCE.md`)

```csharp
using Allegiance.Factions.Model;          // Core, Faction, Hull, Station, Weapon, Capability, TechSet, ...
using Allegiance.Factions.Serialization;  // CoreSerializer
using Allegiance.Factions.Resolution;     // TechResolver, BuildableResolver, AttributeResolver, TechState
using Allegiance.Factions.Validation;     // CoreValidator, ValidationResult

Core core = CoreSerializer.Load("…/core.manifest.yaml");        // merges manifest + catalog + factions
ValidationResult vr = CoreValidator.Validate(core);            // .IsValid / .Errors / .Warnings
TechState reach = TechResolver.ResolveReachable(core, techs, caps);
IReadOnlyList<Buildable> b = BuildableResolver.GetBuildables(core, techs, caps);
```

`Faction` carries `BaseTechs`, `BaseCapabilities`, `BonusMoney` (starting credits), `IncomeMoney`
(passive rate), `LifepodHullId`, `InitialStationId`. `Buildable` (base of `Hull`/`Weapon`/`Station`/
`Development`/`Drone`) carries `Id`, `Name`, `Price`, `BuildTimeSeconds`, `RequiredTechs`,
`RequiredCapabilities`, `GrantedTechs`, `GrantedCapabilities`, `Group`, `IconName`, `ModelName`.

### Concrete Core → runtime def mapping

| Runtime def field | Source | Mode |
|---|---|---|
| `ShipClassDef.Mass` | `Hull.Mass` | derive |
| `ShipClassDef.MaxSpeed` | `Hull.Speed` | derive |
| `ShipClassDef.Accel` | `Hull.Thrust` (model docstring = linear accel) | derive |
| `RateYawDeg/PitchDeg/RollDeg` | `Hull.MaxTurnRates.{Yaw,Pitch,Roll}` | derive |
| `SideMult` / `BackMult` | `Hull.StrafeThrustMultiplier` / `ReverseThrustMultiplier` | derive |
| `MaxHull` | `Hull.ArmorHitPoints` | derive |
| `DriftYawDeg` / `DriftPitchDeg` | **new explicit fields on `Hull`** | extend |
| `AbAccel` / `AbOnRate` / `AbOffRate` | **new explicit fields on `Hull`** | extend |
| `ShipClassDef.ClassId` (byte) | **new explicit field on `Hull`** | extend |
| `ShipClassDef.Hardpoints` | **new explicit list on `Hull`** (shape mirrors `HardpointDef`) | extend |
| `WeaponDef.Damage` | `Projectile.Power` | derive |
| `WeaponDef.ProjectileSpeed` | `Projectile.Speed` | derive |
| `WeaponDef.ProjectileRadius` | `Projectile.Width` | derive |
| `WeaponDef.{FireIntervalTicks,ProjectileLifeTicks}` | **new explicit tick fields** (sim tick-domain; avoids seconds→tick rounding drift) | extend |
| `WeaponDef.SpreadRad` | `Weapon.Dispersion` | derive |
| `WeaponDef.WeaponId` (uint) + `WeaponDef.Kind` (`WeaponKind`) | **new explicit fields** | extend |
| `BaseDef.Radius` / `MaxHealth` | `Station.Radius` / `Station.MaxArmor` | derive |
| `BaseDef.BaseTypeId` (byte) + `Hardpoints` | **new explicit fields on `Station`** | extend |
| `WorldConfig.*` (`Id`, `SectorScale`, `AsteroidDensity`, `DebugFreezeBrain`, `DebugNoFire`) | **new `Core`-level world-config record** | extend |

All extend-fields are **optional / omit-when-default** so the library's existing `sample-data` and 16
tests stay green. The library serializer already omits null/default/empty (`CoreSerializer`
`OmitNull | OmitDefaults | OmitEmptyCollections`). Regenerate JSON schemas from the model via the
library CLI after any model change.

### Source map (where things live today)

| Concern | File |
|---|---|
| Runtime def records | `shared/Defs.cs` (`ShipClassDef`, `WeaponDef`, `BaseDef`, `WorldConfig`, `HardpointDef`, `HardpointKind`, `WeaponKind`, `GameContent` id constants) |
| Flight derivation | `shared/FlightModel.cs` (`ShipStats.FromDef` / `ShipStats.Create`) |
| Shared validator | `shared/ContentValidator.cs` |
| Bespoke v1 loader | `server/Content/ContentLoader.cs`, `server/Content/ContentSet.cs`, `server/Content/stock.yaml` |
| Boot wiring | `server/Program.cs` (lines ~61–101: `--content`/`CONTENT_PATH`, load, validate) |
| Wire encoding | `server/Net/Protocol.cs` (`Version=9`, `BuildDefs`, `WriteHardpoints`, message ids) |
| Client def intake | `client/scripts/GameNetClient.cs` (`ProtocolVersion`, `ApplyDefs`, `ApplyBases`) |
| Client def store | `client/scripts/DefRegistry.cs` |
| Sim consumption | `server/Sim/Simulation.cs` (`WeaponDefs`/`ShipDefs`/`_stats`/`ClassMuzzles`/`HullFor`/`PrimaryWeapon`/`StatsFor`/`EnqueueJoin`/`SpawnCombatShip`), `server/Sim/Simulation.Pig.cs` |
| Per-team state | `server/Sim/World.cs` (`BaseSite`, `BaseHealth`) |
| Spawn menu UI | `client/scripts/Hud.cs` (`SpawnButton`), `client/scripts/Lobby.cs` (`MakeButton`) |
| Factions library | `factions/src/Allegiance.Factions/` (Model/Serialization/Validation/Resolution), `factions/src/Allegiance.Factions.Cli/` (schema gen), `factions/sample-data/`, `factions/.vscode/*.schema.json` |

---

## Phase 0 — Reference wiring + smoke test

**Goal:** the library is referenced and proven loadable inside this repo; game still runs on the v1
bespoke loader (untouched).

- Add a `ProjectReference` from `server/Server.csproj` to
  `factions/src/Allegiance.Factions/Allegiance.Factions.csproj`. Server already pulls YamlDotNet
  16.2.1, so no new transitive risk. **Do not** modify `shared`/`client`.
- Add a smoke test (extend `tests/ContentTest` or add a new `tests/FactionsTest`): load
  `factions/sample-data/core.manifest.yaml` via `CoreSerializer.Load`, assert
  `CoreValidator.Validate(core).IsValid`, and assert `BuildableResolver.GetBuildables` for
  `iron-coalition` is non-zero and differs from `bios` (proves per-faction gating flows). Per
  `factions/INTEGRATING-INTO-STELLARALLEGIANCE.md` §7.

**Deliverable:** `dotnet build` + `dotnet test` green including the new smoke test; the running game
is unchanged (still v1 loader).

## Phase 1 — Extend Core model + author the stock bundle

**Goal:** a factions-format `stock` bundle that loads + validates and carries everything the runtime
needs — but not yet wired into the server.

- Extend the in-repo library model (`factions/src/Allegiance.Factions/Model/`) with the optional
  extend-fields from the mapping table: `Hull` (`ClassId`, `DriftYawDeg`, `DriftPitchDeg`, `AbAccel`,
  `AbOnRate`, `AbOffRate`, `Hardpoints`); `Weapon` (`WeaponId`, `Kind`, `FireIntervalTicks`,
  `ProjectileLifeTicks`); `Station` (`BaseTypeId`, `Hardpoints`); and a `Core`-level world-config
  record. Reuse a hardpoint shape mirroring `shared/Defs.cs` `HardpointDef`
  (kind/index/off/dir/weaponId). Keep every new field **omit-when-default**.
- Regenerate the JSON schemas via the library CLI:
  `dotnet run --project factions/src/Allegiance.Factions.Cli -- schema --output factions/.vscode/allegiance-core.schema.json`
  (the schema is generated from the model — do not hand-edit it).
- Confirm the library's 16 tests still pass (new fields optional → `sample-data` unaffected).
- Author the stock bundle under `server/content/factions/` (manifest + catalog fragments +
  `factions/stock.yaml`) reproducing today's `server/Content/stock.yaml` numbers **exactly**:
  Scout/Fighter/Bomber `ClassId` 0/1/2, Pod = lifepod hull → `ClassId` 255, the Garrison base, and
  the world cfg. Cross-check each derived field against the current `stock.yaml` values (e.g. Scout:
  mass 40, max-speed 160, accel 30, rate 50/50/50, drift 5/5, side 0.5, back 0.25, max-hull 60;
  scout cannon: damage 4, fire-interval 4 ticks, speed 200, life 16 ticks, radius 1, spread 0.006;
  Garrison: radius 90, max-health 2000; world: sector-scale 2.25, density 1.0).

**Deliverable:** a test loads the stock bundle via `CoreSerializer.Load` + `CoreValidator.Validate`
(green); library tests still 16/16. Server still boots on v1.

## Phase 2 — Projection Core → runtime defs + server cutover

**Goal:** the server boots from the factions bundle; in-game ships are identical; client untouched.

- New `server/Content/FactionsContentProjection.cs`: maps a loaded `Core` → the existing
  `ContentSet` (`ShipClassDef`/`WeaponDef`/`BaseDef`/`WorldConfig`) per the mapping table. Derived
  flight stats feed the unchanged `ShipStats.FromDef`; **add no new derived math**. Pod = the
  faction's `LifepodHullId` hull → `ClassId` 255. Base = the faction's `InitialStationId` station.
- Rework `server/Content/ContentLoader.cs` to: `CoreSerializer.Load(manifestPath)` →
  `CoreValidator.Validate` (refuse-to-start on errors — the client has no fallback) → project →
  run the existing **shared `ContentValidator`** on the projected defs as a second gate (keeps the
  dangling-hardpoint / non-positive-hull / dup-id guarantees). `--content`/`CONTENT_PATH` now point
  at a **manifest** path; default = `content/factions/core.manifest.yaml` next to the binary
  (resolved via `AppContext.BaseDirectory`, mirroring today's `stock.yaml` resolution in
  `server/Program.cs`).
- Update `server/Server.csproj` `<Content>` glob to ship `content/factions/**`
  (`CopyToOutputDirectory="PreserveNewest"`) instead of the single `stock.yaml`. Delete the bespoke
  `ContentDto`/`ContentSet` *builder* bits and the old `server/Content/stock.yaml` once parity is
  confirmed (README: v1 stays live only until the projection lands). **Keep the `ContentSet`
  record** — it is the projection's output type.

**Deliverable:** server boots from the factions bundle; `tests/ContentTest` byte-determinism passes;
headless sim ticks (hold a `--server --anonymous` connection so the loop runs — see memory
`headless-sim-testing`) and a manual client connect renders the same ships/weapons/base.
**Wire protocol unchanged; no client change.** Run `graphify update .`.

> End of the Stage-1 pivot. Phases 3–6 are Stage 2.

## Phase 3 — Per-team state container + credits paycheck (server-only)

**Goal:** per-team economy state exists and accrues on the server; no client change yet.

- Add a `TeamState` container in `server/Sim/World.cs` keyed by team byte (0/1), holding `Credits`
  (use a type the wire can carry later), `OwnedTechs` (`TechSet`) + `OwnedCapabilities`
  (`CapabilitySet`) seeded from the stock faction's `BaseTechs`/`BaseCapabilities`, and `Score`.
  Initialize alongside `Bases`/`BaseHealth`. (Pattern: a `Dictionary<byte, TeamState>` parallel to
  the existing per-team arrays.)
- Accrue a flat paycheck every N ticks (+ `Faction.BonusMoney` at start, `Faction.IncomeMoney` rate
  from `Core`). Drive it from the sim step (the same tick loop that updates base health).

**Deliverable:** credits accrue in the headless sim (observable via a temporary debug log); a unit
test asserts accrual + seeding. No wire/client change.

## Phase 4 — Wire per-team state to client + ship cost on defs

**Goal:** the client knows team credits and per-hull cost.

- Project `Buildable.Price` → a new `ShipClassDef.Cost` (`uint`); extend `Protocol.BuildDefs`
  (`server/Net/Protocol.cs`), `GameNetClient.ApplyDefs` and `DefRegistry` (`client/scripts/`) to
  carry it. **Bump `Protocol.Version` 9 → 10** in both `server/Net/Protocol.cs` and
  `client/scripts/GameNetClient.cs` (`ProtocolVersion`).
- Add a new `MsgTeamState` message (server→client, **low rate** — not the per-tick hot path; do not
  bloat `MsgSnapshot`/`MsgBases`) carrying per-team `Credits` + `Score` (+ an unlocked snapshot for
  Phase 5). Store it in `WorldRenderer`/`DefRegistry` so the HUD can read it.

**Deliverable:** client receives + can surface team credits and ship costs (a HUD label is enough);
proto v10 handshake works end-to-end (out-of-date client/server is rejected by the existing version
check).

## Phase 5 — Unlock gating hook + server spawn-cost enforcement

**Goal:** spawning is gated by money + unlocks, server-authoritative.

- On a spawn request (the `Simulation.EnqueueJoin` → `ProcessRespawns` → `SpawnCombatShip` path in
  `server/Sim/Simulation.cs`), resolve the team's buildables via
  `BuildableResolver.GetBuildables(core, team.OwnedTechs, team.OwnedCapabilities)`; reject the spawn
  if the requested hull is unavailable or `Credits < Cost`; deduct `Cost` on a successful spawn.
  Authority = **bootstrap-simple** (any-player-spends / auto), **no commander**.
- Keep the rejection observable to the client (reuse/extend the existing spawn-pending/retry path in
  `client/scripts/ShipController.cs` so a rejected request doesn't hang `_spawnPending`).

**Deliverable:** a too-expensive or locked spawn is rejected server-side and credits deduct on a
successful spawn; a headless test covers accept / reject-locked / reject-poor / deduct.

## Phase 6 — Buy menu UI (spawn menu = buy seam)

**Goal:** the in-match menu shows cost + balance and disables unavailable options.

- Replace the three hardcoded buttons in `client/scripts/Hud.cs` (`SpawnButton`) with a def-driven
  list built from `DefRegistry` ships: show `Name — N credits`, gray out (`Disabled`) when
  `Cost > teamCredits` or the hull is locked (from the Phase-4 team-state snapshot). Reuse the
  `Lobby.MakeButton()` pattern (`client/scripts/Lobby.cs`).

**Deliverable:** in-match buy menu reflects cost/balance/locks; end-to-end playable. Verify by
building the client and connecting to a local server (the `/run` skill).

---

## Verification (end-to-end)

- **Per phase:** `dotnet build` + `dotnet test` (root tests + `factions` library 16/16) green.
- **Stage-1 parity (Phase 2):** boot the server on the factions bundle; confirm `tests/ContentTest`
  byte-determinism passes; run the headless sim (hold a `--server --anonymous` connection so the loop
  ticks — memory `headless-sim-testing`); connect a client and confirm Scout/Fighter/Bomber/Pod/
  Garrison look + fly + fire identically to pre-pivot. `tests/FlightModelTest` golden must stay green.
- **Stage-2 (Phases 3–6):** headless test for credit accrual + spawn accept/reject/deduct; manual
  client run confirms costs/balance shown and unaffordable/locked options grayed out.
- After any code change, run `graphify update .` to keep the knowledge graph current.

## Risks / notes

- **Tick-domain ballistics** are carried as explicit fields (not seconds→tick derivation) to avoid
  rounding drift against the current golden numbers.
- **Stable byte ids** (`ClassId` 0/1/2/255, `BaseTypeId`, `WeaponId`) are authored explicitly — the
  client's `ShipClass` enum and `GameContent` id constants depend on them.
- Keep `YamlDotNet` **server-only**; the projection's output is the existing `shared` records, so
  `shared`/`client`/wasm never reference the library.
- `factions/.vscode/*.schema.json` are **generated** from the model — regenerate (don't hand-edit)
  whenever the model changes (Phase 1, and Phase 4 if `Price`/runtime fields move into the model).
- The library deliberately does **not** model map/asteroid placement, economy ticks, build timers,
  networking, or hardpoint geometry — StellarAllegiance owns those (see
  `factions/INTEGRATING-INTO-STELLARALLEGIANCE.md` §4, §8).
```
