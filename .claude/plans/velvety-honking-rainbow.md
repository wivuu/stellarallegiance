# Timed reload from cargo (ammo + fuel)

> **Shipped 2026-07-24. One deviation from the plan below, for the better:** the authoring field is
> the EXISTING core Allegiance `Expendable.LoadTime` (`load-time:`, seconds) — it was already in the
> model and schema, unused, and it covers all five kinds including the fuel pod (a `FuelPod` is an
> `Expendable`). So no new field on `Launcher`/`FuelPod`, no schema change, and seconds→ticks converts
> once at projection (`FactionsContentProjection.LoadTicks`). Consequence: a gun *cannot* author a
> load time from YAML (`Weapon` has no such key — its unused `reload-time` is IGC `dtimeReady`), so the
> `ContentValidator` bolt-weapon refusal is a projection-invariant guard rather than an authoring gate;
> `tests/ReloadTest` asserts every gun stays at 0. Stock content also authors every load time ABOVE its
> launcher cadence, so the reload is the governing gate on every cargo-fed slot (see "Authoring").

## Context

Everything a ship draws out of its hold is **instant** today:

- **Fuel pods** — `server/Sim/Simulation.cs:814-825` (Pass A, pre-`Integrate`): the tick the tank hits
  0 while boost is held, a pod is spent and `State.Fuel` is *set* to the pod yield in the same tick.
  The afterburner never even flickers ("keeps the afterburner lit with no gap").
- **Missile rack / chaff / mine / probe** — each press is gated only by the weapon's authored
  `fire-interval-ticks` (`Simulation.cs:2945`, `Simulation.Chaff.cs:50`, `Simulation.Mines.cs:60`,
  `Simulation.Probes.cs:63`), i.e. a pure rate limit. `Simulation.cs:1349` even says so:
  *"full magazine at spawn (no rearm yet)"*.

`.PLAN/README.md:23` asks for the missing beat: **pulling a round or a fuel charge out of the hold
should take a configurable amount of time**, so an empty tank or a spent launcher costs the pilot a
real, visible window instead of nothing. Outcome: a per-item authored **load time**, streamed to
clients like every other balance number, enforced server-side, and surfaced on the HUD as a
RELOADING state so the pilot can feel it.

Guns are **infinite-ammo** (`TryFire` has no ammo check) and feed from no hold — they are out of
scope and must stay on their existing per-mount cadence.

## Model

One rule, five consumers. Each cargo-fed launcher/dispenser has a **round in the tube**; using it
starts a load of the next charge out of the hold, and the slot cannot be used again until the load
finishes:

```
usable at tick T  ⇔  T - LastUseTick >= max(FireIntervalTicks, ReloadTicks)
```

- The charge leaves the hold at **use** time (unchanged accounting — the streamed ammo byte keeps
  meaning "charges you can still spend"); `ReloadTicks` is the window before the next one is usable.
- `ReloadTicks` omitted / 0 ⇒ **byte-identical to today's behavior** (append-only, omit-when-default).
- Ships spawn/launch **loaded** — `LastUseTick == 0` is already "ready" in every gate.
- Tick domain, not seconds, matching `fire-interval-ticks` ("to avoid rounding drift",
  `launchers.yaml:4`) and keeping the gate deterministic for PIG replay.

**Fuel pods** are the one variant: the pod is committed (count decremented) when the tank empties
mid-boost, but the tank stays at **0 for the whole load** — boost dies, then the tank fills at
completion. That is the point of the feature for fuel.

Shared rule lives in `shared/FireCadence.cs` (new `LoadIntervalTicks(fireIntervalTicks,
reloadTicks)` next to `MountFires`) so server, client HUD, and tests read one function — same
"shared rule both peers use" pattern as `HardpointDef.MountAccepts` / `DockFaceParser`.

## Authoring (per item, content YAML → streamed defs)

`world.yaml` is explicitly **not** an option: `shared/Defs.cs:723-727` declares those blocks
never-streamed, and the client keeps no compile-time tuning fallback
(`CONTRIBUTING.md:21-22`, GLOSSARY "Client No Baked Tuning Fallback"). The value must ride `MsgDefs`.

**As shipped** — `load-time` (seconds) on every expendable in `server/Content/core/expendables.yaml`,
every tier (a tier that omits it deploys instantly, since the FIRED tier is the researched successor).
Each value deliberately exceeds its launcher's `fire-interval-ticks` so the reload is what gates:
MRM Seeker `2.0` (cadence 30t), MRM Quickfire `0.75` (10t — the line stays a hose), SRM Dumbfire
`1.75` (30t), SRM Anti-Base `3.5` (60/50t), Counter/chaff `2.5` (40t), Prox Mine `5.5` (100t),
EWS Probe `5.5` (100t), Fuel Pod `2.0` (no cadence at all — pure new behavior: 2 s of dead
afterburner). All are one-line balance edits.

## Change list

**1. Library model + boot validation** (`factions/`)
- `Model/Parts/Launcher.cs` — `public uint ReloadTicks { get; set; }` beside `FireIntervalTicks`
  (omit-when-default so sample data is unaffected).
- `Model/Expendables/FuelPod.cs` — `public int ReloadTicks { get; set; }` beside `FuelPerCharge`.
- `Validation/CoreValidator.cs` — refuse a **gun** (`weapons.yaml`) authoring `reload-ticks`
  (infinite-ammo, no hold); refuse negative values.

**2. Defs + wire** (`shared/`, `server/Net/`, protocol bump)
- `shared/Defs.cs` — `WeaponDef.ReloadTicks` (append at the tail after the bolt-mesh block, comment
  the byte-stability rule) and `CargoItemDef.ReloadTicks` (after `FuelPerCharge`).
- `server/Content/FactionsContentProjection.cs` — carry both through `ProjectWeapon` /
  `ProjectCargoItem:494-504` (`FuelPerCharge` at `:503` is the exact precedent).
- `shared/ContentValidator.cs` — mirror the negative/gun refusals (boot gate 2).
- `server/Net/Protocol.cs` — `WriteWeaponDefs` + `WriteCargoDefs:1464-1477` append the u32s;
  **`Protocol.ShipRecordSize` is untouched** (see "No ship-record growth" below).
- `shared/Net/Wire.cs` — `ProtocolVersion 35 → 36` + a dated changelog paragraph above it.
- `schemas/` — regenerate, never hand-edit:
  `dotnet run --project server/SimServer.csproj -- --gen-schemas "$(pwd)/schemas"`.

**3. Server gates** (`server/Sim/`)
- `shared/FireCadence.cs` — add `LoadIntervalTicks`.
- Rewrite the four identical gates through it (they all currently open-code
  `lastTick != 0 && tick - lastTick < w.FireIntervalTicks`):
  `Simulation.cs:2945` (missile), `Simulation.Chaff.cs:50`, `Simulation.Mines.cs:60`,
  `Simulation.Probes.cs:63`. Each becomes
  `if (!FireCadence.MountFires(tick, ship.LastXTick, FireCadence.LoadIntervalTicks(w.FireIntervalTicks, w.ReloadTicks))) return;`
  Tier migration is free — `MigrateWeaponTier` already resolves the fired tier's `WeaponDef`.
- `Simulation.cs` `ShipSim` — one new field, `uint FuelLoadEndTick` (0 = idle; guard tick 0 like the
  other `Last*Tick` sentinels). Reset it in the respawn/rebind reset next to
  `MountLastFire = null` (`Simulation.cs:1641-1642`).
- `Simulation.cs:809-825` Pass A becomes: if a load is pending and due → fill the tank and clear;
  else if the tank is empty mid-boost with pods left → spend one pod and stamp
  `FuelLoadEndTick = tick + reloadTicks`. Written so `reloadTicks == 0` completes in the *same* tick
  (preserving today's exact behavior). Pod reload ticks come from the streamed
  `CargoItemDef.ReloadTicks`, cached at spawn next to `FuelPodFuelPerCharge`
  (`SeedDispenserAmmo`, `Simulation.cs:1371-1376`) so Pass A stays a dictionary-free hot loop.
- `FlightModel.Integrate` stays untouched (PIG determinism), as with the original auto-load.

**4. Client** (`client/scripts/`)
- `DefRegistry.cs` / `GameNetClient.cs` `ReadWeaponDef` + `ReadCargoItemDef:1797-1807` — read the new
  fields position-for-position.
- `PredictionController.cs:316-333` `ConsumeFuelPod` — mirror the new two-phase rule exactly (it is
  the single mirror for both live `Step` and reconcile replay). Needs a predicted
  `_predFuelLoadEnd` alongside `_predFuelPods`, buffered in the input-ring `Entry` (`:513`) and
  adopted wherever authority is adopted (`:459, :614, :634, :649, :706`). Pull reload ticks from
  `_defs.FuelCargoItem()` next to the existing `_fuelPodYield` re-pull (`:502`) — 0 until defs
  arrive keeps the mirror disabled, no baked fallback. Expose `FuelReloadFrac` for the HUD.
- `PredictionController.cs:434` `SetAfterburner` — today it keeps the plume lit while `pods > 0`; it
  must now drop the plume during a load and re-light on completion (the tank really is dead).
- `GameNetClient.cs:2215-2220` — where `LocalMissileAmmo/Chaff/Mine/Probe` are assigned from the
  snapshot, stamp a per-kind `LocalXLoadTick = tick` **on a decrease**. That edge *is* the server's
  `LastXTick` (the local ship is always in the nearest AOI tier, so its record ships every tick), so
  the HUD gets an exact reload clock with **zero new wire bytes**.

**5. HUD** (read `DESIGN.md` first; colors from `DesignTokens`, never literals)
- `WeaponsPanel.cs` — `DrawDispenserRow:223-268` and `DrawSecondaryRow:271-322` show `RELOADING`
  plus a progress bar using the existing local `DrawBar:419-424`, exactly like the primary gun's
  `CYCLE` bar at `:155-158`. Fraction = `(ServerTick - LocalXLoadTick) / LoadIntervalTicks` (from
  `_world.ServerTick` — same clock the stamp came from, so no tick-space mixing). Label `RELOADING`
  only when `ReloadTicks > FireIntervalTicks`, else keep `CYCLE`.
- `SystemRing.cs:124-132` — while a pod is loading, the `POD +N` tag reads its progress and the FUEL
  arc (`:99-102`) draws in the dim/warn state, so "empty tank, pod on the way" is unmistakable.
- `Hud.cs:436-467` — the empty-click SFX must not fire while a slot is *reloading* with charges
  left (that is not an empty press).

**6. Tests + docs**
- `tests/FuelPodTest/Program.cs` — new cases: tank stays 0 and boost stays dead for the whole load;
  fills on the completion tick; the pod decrements exactly once; `reload-ticks: 0` reproduces the
  current instant refill byte-for-byte.
- New `tests/ReloadTest` (or extend `MissileTest` / `MineTest`): second launch blocked until the load
  elapses; `max(interval, reload)` precedence both ways; ammo unchanged by a blocked press;
  a tier-2 rack uses its own reload value.
- Re-baseline `tests/ContentTest` (def-stream determinism hash changes with the new fields) and any
  MissileTest/MineTest timing expectations that assumed instant re-fire.
- `GLOSSARY.md` — new **Reload (load-from-hold)** entry + update **Fuel Pod** and **Expendables**;
  `.PLAN/README.md:23` gets checked off.

## Deliberate calls (flagged, easy to reverse)

- **No ship-record growth.** Streaming a per-kind reload timer would cost ~5 B × every ship in every
  snapshot; deriving it from the already-streamed ammo edge + an authored def costs nothing and
  follows the established "derive, don't stream" precedent (clients already derive *which* gun mounts
  fired from a single `LastFireTick`). Cost: the dispenser/missile bar can be off by at most one
  snapshot. If that ever shows, the fallback is a u8-per-kind record tail (57 → 62) + protocol bump.
- **`max(interval, reload)`, not `interval + reload`** — one gate, monotone, no double-counting for
  authors, and `reload-ticks: 0` is exactly today's behavior.
- **Charge leaves the hold at use, not at load completion** — keeps a single ammo-accounting seam and
  makes the streamed byte mean "rounds you can still fire".

## Verification

1. `dotnet build wivuullegiance.slnx` (server + shared + tests).
2. Content gates: `dotnet run --project server/SimServer.csproj -- --selftest` — CoreValidator and
   ContentValidator must accept the new keys and refuse a gun that authors `reload-ticks`.
3. Suites: `FuelPodTest`, `ReloadTest`, `MissileTest`, `MineTest`, `ContentTest`, `LoadoutTest`,
   `FlightModelTest` (determinism). Baseline per memory: all green except the four pre-existing
   `CollisionTest` failures — anything else is a regression.
4. Regenerate schemas (command above) and confirm `git diff schemas/` only adds the new keys.
5. **Protocol bump smoke** (dotnet suites do not cover the Godot client): run server + client
   (`scripts/run-client.sh`, flags before `--`, and per memory use `-GodotArgs` under pwsh) and
   `--autofly` to confirm defs load and no wire desync.
6. Feel/HUD check in a live client: burn the tank dry on the booster hull (`hulls.yaml:102-104`,
   `ab-fuel-recharge: 0`) — boost must cut out for the full load window, `POD` shows progress, the
   plume relights on completion. Fire the rack / chaff / mine / probe twice and watch `RELOADING`
   plus the blocked second press. Optionally capture evidence with the `verify` skill.
