# Fuel Pods: cargo-carried reserve afterburner fuel + interceptor fuel tuning

## Context

Boost (afterburner) is the only fuel consumer, and the Lt Interceptor — the dedicated booster
hull — carries just 20 fuel at 4/s drain (~5 s of boost) with `ab-fuel-recharge: 0` (dock-only
refill). That's too little to do its job, and there is no way to carry spare fuel. This change
(a) triples the interceptor tank to 60 (~15 s), and (b) adds a **fuel pod** — a new
configuration-driven cargo expendable that **auto-loads when the tank runs dry while boosting**,
refilling the tank. Everything is YAML-authored (expendables.yaml / hulls.yaml), streamed to the
client via defs, and validated at boot like every other expendable.

**User decisions:** interceptor `max-fuel` 20 → **60**; pod = **full-tank refill** (authored as a
generous `fuel-per-charge` clamped to the hull's tank); interceptor default loadout gains
**2 pods** (in `default-cargo`, so it stays YAML-configurable).

**Verified facts the design leans on:**
- `shared/Net/Wire.cs:94` — `ProtocolVersion = 34` → bump to **35** (memory's "proto 38" is a
  milestone label, not this constant).
- Pass A loop `server/Sim/Simulation.cs:748-752`: `input = InputFor(...)`, `stats = StatsFor(...)`,
  then `FlightModel.Integrate`. The fuel gate inside Integrate reads **pre-tick** fuel
  (`shared/FlightModel.cs:517-518`) and re-gates instantly at fuel > 0 — so a refill inserted
  between lines 751/752 on the first empty tick keeps the afterburner lit with **no gap** and no
  AbPower decay. `FlightModel.Integrate` is PIG-determinism-critical and stays **untouched**.
- Cargo carries **no credit cost** today (`TryReserveSpawn` Simulation.cs:1558-1570 charges hull
  only; `PaidCost` is hull-only) — pods are free like mines/chaff/probes, no refund interaction.
- Interceptor payload: capacity 12, guns 2×2 + chaff 2×1 = 6 used → 2 mass-1 pods fit (8/12).
- `CoreSerializer` is YamlDotNet + HyphenatedNamingConvention — a `Fuels` list on `Core` binds a
  `fuels:` section for free; `ClientHub` MsgSpawn cargo parse is generic `(u32,u8)` — no change.

## Design summary

- New expendable type **FuelPod** (`fuels:` section in expendables.yaml): pure cargo — no
  weapon/dispenser def, nothing fired, single tier, no tech gate. `cargo-id: 5`.
- `CargoItemDef` gains `FuelPerCharge` (f32; 0 = not fuel), streamed in MsgDefs.
- `ShipSim` gains `FuelPodAmmo` (byte) + `FuelPodFuelPerCharge` (float, resolved at seed).
- **Auto-consume, server-side** (Pass A, before Integrate): fuel-modeled ship + boost held +
  `Fuel <= 0` + pods > 0 → pod--, `Fuel = min(MaxFuel, yield)`.
- **Client prediction mirror** in PredictionController (same rule, streamed yield) so the plume /
  predicted boost doesn't hitch for a round trip.
- Ship snapshot gains a 5th ammo byte → `ShipRecordSize` 56 → **57**, proto 34 → **35**.
- HUD: pod reserve count beside the FUEL arc in SystemRing. Hangar: stepper row appears via the
  existing cargo catalog, **hidden** (not greyed — repo rule) on hulls with `MaxFuel <= 0`.

## Steps

### 1. Factions authoring model
- **New** `factions/src/Allegiance.Factions/Model/Expendables/FuelPod.cs` — `record FuelPod :
  Expendable` with `double FuelPerCharge` (XML doc: restored per consumed charge, clamped to the
  hull's max-fuel). Inherits CargoId/Glyph/ChargesPerPack/Mass/Description.
- `Model/Core.cs`: `List<FuelPod> Fuels = []` next to Probes (:46); append `.Concat(Fuels)`
  **last** in `AllExpendables()` (:64-68, keeps existing catalog order); `Fuels.AddRange` in
  `Merge` (:95-98).
- `Serialization/CoreSerializer.cs:99-105`: add `Fuels = core.Fuels` to the expendables.yaml
  `WriteFragment` Core.
- `Validation/CoreValidator.cs`: new fuels block (near hull fuel rules :200-218):
  `fuel-per-charge > 0` required; `cargo-id` **required**; `mass < 0` error. Cargo-id uniqueness
  is free via the `AllExpendables()` loop (:220-224). In the hull DefaultCargo loop (:87-98):
  resolved item `is FuelPod && hull.MaxFuel <= 0` → error. Launcher dispatch (:196) already
  rejects FuelPod-pointing launchers.

### 2. Runtime def + projection + shared validator
- `shared/Defs.cs` `CargoItemDef` (:296-304): `public float FuelPerCharge; // 0 = not a fuel item`.
- `server/Content/FactionsContentProjection.cs` `ProjectCargoItem` (:465-474):
  `FuelPerCharge = e is Factions.FuelPod f ? (float)f.FuelPerCharge : 0f`. Catalog/DefaultCargo
  projection is automatic via `AllExpendables()`; update the order comment at :104.
- `shared/ContentValidator.cs`: `FuelPerCharge < 0` → error (cargo check near :40-45); in
  `ValidatePayload` DefaultCargo loop (:257-264): fuel item on a `MaxFuel <= 0` hull → error
  (boot gate that makes the ResolveLoadout authored-fallback safe).

### 3. Wire protocol 34 → 35
- `shared/Net/Wire.cs:94` → 35, plus dated changelog comment in the file's style (ship record
  appends u8 fuelPodAmmo after probeAmmo, 56→57; MsgDefs cargo item appends f32 FuelPerCharge
  after Description).
- `server/Net/Protocol.cs`: `ShipRecordSize = 57` (:36); `WriteShip` writes `s.FuelPodAmmo` after
  ProbeAmmo (:223) + layout comment (:138-146); `BuildDefs` cargo block appends
  `w.Write(c.FuelPerCharge)` (:1412-1422).
- `client/scripts/NetTypes.cs`: `Ship.FuelPodAmmo` byte after ProbeAmmo (:70).
- `client/scripts/GameNetClient.cs`: snapshot reader adds `fuelPodAmmo` after probeAmmo
  (:2035-2039, assign :2060-2094) + `LocalFuelPodAmmo` next to `LocalProbeAmmo` (:117); defs
  cargo reader appends `FuelPerCharge = r.ReadSingle()` (:1720-1733).

### 4. Server sim: seed, validate, auto-consume (`server/Sim/Simulation.cs`)
- `ShipSim`: `byte FuelPodAmmo; float FuelPodFuelPerCharge;` next to probe block (:309-313).
- Content-load: `Dictionary<uint,float> _fuelPerCharge` filled where cargo lookups build
  (:588-592) from `CargoItemDef.FuelPerCharge > 0`.
- `SeedDispenserAmmo` (:1234-1266): at loop top, if `_fuelPerCharge` has the cargoId →
  `FuelPodAmmo = min(255, count × ChargesPerPack)`, store yield, `continue` (no MigrateWeaponTier
  — single tier, no weapon).
- `ResolveLoadout` cargo loop (:1374-1382): accept ids in `_fuelPerCharge` as a second path
  besides `_dispenserByCargo`; **reject** fuel cargo (count > 0) when the hull def has
  `MaxFuel <= 0` → whole-request revert to authored loadout (matching existing rejections; add a
  Log method beside `SpawnCargoNotDispenser`). Mass budget already counts via `_cargoMass`.
- **Auto-consume** in Pass A between `stats = StatsFor(...)` (:751) and Integrate (:752):
  ```csharp
  if (!s.IsPod && s.FuelPodAmmo > 0 && input.Boost
      && stats.MaxFuel > 0f && stats.AbThrust > 0f && s.State.Fuel <= 0f)
  {
      s.FuelPodAmmo--;
      s.State.Fuel = MathF.Min(stats.MaxFuel, s.FuelPodFuelPerCharge);
  }
  ```
  Comment why here: Integrate's gate reads pre-tick fuel, so refilling on the first empty tick is
  hitch-free; FlightModel untouched (PIG determinism).
- Edge cases (no code, covered by structure — assert in tests): escape pod (`MakePod` :2072-2102
  fresh ShipSim, pod stats MaxFuel 0, `!s.IsPod` belt-and-braces); PIGs never set Boost and seed
  no cargo; yield clamps to tank; dock = despawn/relaunch reseed as with all cargo.

### 5. Client prediction mirror (`client/scripts/PredictionController.cs`)
- State: `_predFuelPods` (byte), `_fuelPodYield` (float, re-pulled each Step from a new
  `DefRegistry.FuelCargoItem()` alongside `_stats` :399-403); `Entry.PredictedPods` (:51-56);
  public `FuelPods` for HUD.
- `Initialize` (:344-381): seed from `row.FuelPodAmmo`.
- `Step` (:389-471): mirror the server rule immediately before Integrate (:407); stamp
  `PredictedPods` into the buffered Entry.
- `OnAuthoritative` (:497-566) + `HardSnapTo` (:586-602): autopilot/no-buffer/snap paths adopt
  `row.FuelPodAmmo`; good-prediction path resyncs `_predFuelPods = max(0, row.FuelPodAmmo −
  burnedSinceAck)`; replay path starts from the authoritative count and re-applies the mirror
  before each replayed Integrate.
- Engine glow: extend `SetAfterburner` gate (:338-342) with `|| _predFuelPods > 0` so the plume
  doesn't flicker in the sub-tick window before the refill.

### 6. Client HUD + hangar
- `client/scripts/DefRegistry.cs` (near :262-267): `CargoItemDef? FuelCargoItem()` — lowest
  CargoId with `FuelPerCharge > 0`; comment: single fuel type by design (wire carries only a
  count); null until defs arrive.
- `client/scripts/SystemRing.cs`: when the hull models fuel and `local.FuelPods > 0`, draw a
  small reserve tag below the FUEL tag (:109-110), e.g. `POD +N`, warn-token color — reads the
  predicted count so it drops the instant a pod loads.
- `client/scripts/ui/ShipLoadout.Hangar.cs` `RefreshCargoSection` (:270-322): skip items with
  `FuelPerCharge > 0` when the selected hull's `MaxFuel <= 0` (hidden, not greyed). Re-run the
  filter on hull switch from `SelectShip` (`ShipLoadout.cs:505-528`). `StepCargo` + payload math
  need no change.
- `client/scripts/WeaponsPanel.cs`: **no change** — pods aren't a dispenser; reserve lives on
  SystemRing.

### 7. Content + docs
- `server/Content/core/expendables.yaml` — new `fuels:` section after `probes:` (~:453):
  ```yaml
  fuels:
    # Auxiliary afterburner fuel carried in the hold. Pure cargo (nothing is fired): when a
    # fuel-modeled hull's tank hits 0 while boost is held, one charge auto-loads and the tank
    # refills by fuel-per-charge (clamped to the hull's max-fuel — overshoot is wasted).
    - id: fuel-pod-1
      name: Fuel Pod
      description: Reserve afterburner fuel. Auto-loads when the tank runs dry.
      cargo-id: 5
      mass: 1               # payload cost per PACK (one hangar count = one pack)
      charges-per-pack: 1   # one refill per pack
      glyph: "◒"            # geometric like the other glyphs (◈◇◉) — HUD font renders no emoji
      fuel-per-charge: 999  # ≥ every tank ⇒ full refill (clamped to max-fuel); lower for partials
  ```
- `server/Content/core/hulls.yaml` lt-interceptor (:82-124): `max-fuel: 60` (~15 s at drain 4,
  update trailing comment); `default-cargo` gains `- { item: fuel-pod-1, count: 2 }` (now 8/12
  payload — update the :114 comment).
- Regen schema: `dotnet run --project server/SimServer.csproj -- --gen-schemas` (good XML
  `<summary>` on FuelPod feeds it). Update `.claude/skills/hulls-weapons/SKILL.md` (expendable
  sections list) and GLOSSARY.md (new "fuel pod" term).

### 8. Tests (`tests/`, console PASS/FAIL suites per CONTRIBUTING.md)
- **New `tests/FuelPodTest/`** (mirror MissileTest/LoadoutTest; add to `wivuullegiance.slnx`):
  spawn interceptor with pods → seeded count + full tank; hold Boost → at empty, next tick pod--,
  fuel = full tank, **AbPower never dips across the swap tick**; all pods spent → boost dies like
  legacy; 255 charge cap; death-eject → pod ship has 0 pods.
- `tests/LoadoutTest`: fuel cargo accepted on interceptor; fuel cargo on scout (MaxFuel 0) →
  whole-request authored fallback; pod mass counts toward payload-overflow reject.
- `tests/ContentTest`: cargo-id 5 exists with FuelPerCharge > 0; validator-negative: fuel
  default-cargo on MaxFuel-0 hull refused.
- Regression: FlightModelTest (must stay green — Integrate untouched), FactionsTest, CryptoTest.
  Note baseline: ShieldTest/ContentTest/FactionsTest carry known pre-existing content-drift
  failures on master — compare against that baseline, not zero.

### 9. Verification
1. Build shared + server (Release) + factions + client csproj.
2. Server boots clean on stock content (both validators pass with `fuels:`).
3. Suites green (vs. known baseline): FlightModelTest, CryptoTest, ContentTest, FactionsTest,
   LoadoutTest, FuelPodTest.
4. `--gen-schemas` diff shows only the fuels addition.
5. **Protocol-bump smoke** (dotnet suites don't cover the Godot client): run server + client
   `--autofly` — connects at v35, spawns, flies, no mismatch disconnect.
6. Live pass: fly interceptor, hold boost — 15 s drain to 0, pod auto-loads (plume never
   flickers), SystemRing pod count decrements 2→1→0, third dry-out kills boost; hangar shows the
   Fuel Pod stepper on interceptor, hides it on scout/bomber; payload clamp blocks over-budget pods.
7. `dotnet csharpier format .` before finishing (repo auto-commits+pushes — get changes final).

### Commit sequencing (each buildable)
(1) factions model+validators+yaml+schema → (2) defs/projection/shared validator → (3) wire bump
+ server sim + both snapshot ends (atomic with version bump) → (4) prediction mirror + HUD +
hangar → (5) tests → (6) tuning numbers already folded into (1).
