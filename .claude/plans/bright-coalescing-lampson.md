# Per-ship weapon loadouts (MsgSpawn mounts + MsgShipLoadout)

## Context

Bug that prompted this: picking "leave empty" on the scout's cannon in the hangar still launches with the cannon. `LoadoutState` (client/scripts/ui/LoadoutState.cs) is explicitly cosmetic-only — weapon overrides never go over the wire; the sim fires from the class-authored `ClassMuzzles` table. This plan wires the hangar's weapon-slot assignments end-to-end: client sends overrides on MsgSpawn, the server validates + stores per-ship mounts and fires from them, and every client learns each ship's effective weapon mounts so remote bolt rendering is correct.

User-confirmed scope:
- **Full weapon-mount fidelity** across clients (gun type + count per ship). Cargo/expendables stay private — never broadcast the hold.
- **Per-mount cooldowns** — each gun mount fires on its own `FireIntervalTicks` (replaces the single per-ship gate; the "Stage 2 mixed loadouts" comment at Simulation.cs:1983 lands now).
- **No ProtocolVersion bump** (stays 38 — nothing released between PRs; client+server ship together, already the norm).
- Bots (PIGs) keep authored default loadouts.

## Verified design facts

- **No cap on weapon hardpoints per hull** (user requirement: hardpoint count is the mesh/faction author's call — today's max is 3, but nothing may hardcode a limit). This rules out a fixed-width "which mounts fired" mask in the ship record; instead, which mounts fired at `LastFireTick` is **derived deterministically client-side** (see wire section) — eligibility depends only on observed fire ticks + known per-mount intervals, so no wire field and no width limit are needed.
- **Positional-barrel trap (highest-risk item)**: the spread seed `FlightModel.SpreadDirection(fwd, spread, ShipId, tick, barrel)` uses the hardpoint-array index. Server `TryFire` (Simulation.cs:2004) iterates the FULL muzzle array (including NoWeapon slots); client `WorldRenderer.SpawnBoltFor` (WorldRenderer.cs:2396) iterates the **filtered** `DefRegistry.WeaponMounts` list. They agree today only because empty GLB-merged mounts trail the authored ones. Per-ship emptying creates holes at any position → every barrel-indexed consumer (server TryFire, PredictionController, SpawnBoltFor) must migrate to a full positional slot list **in the same change**.
- Message id **28** is free (MsgRockGone = 27 is the highest server→client id).
- Ship snapshot records are fixed-stride (`Protocol.ShipRecordSize`, strided by ClientHub AOI code at :159, :1829-1846, :1978-2154) — variable-length per-ship data in the record is off the table, which independently supports deriving the fired-mount set instead of streaming it.
- No per-join state-dump seam exists; the repo pattern is **on-change + coarse keepalive broadcast** (`CoarseEveryTicks = 10` ticks ≈ 0.5 s — MsgProbes/MsgBases/MsgResearchState pattern). New joiners self-heal within 0.5 s.
- Respawn re-feeds `SpawnCombatShip` from `_clientInfo` (Simulation.cs:386, drained :1280) — mount overrides must ride in that tuple or loadouts silently revert on death.
- Team tech ownership server-side: `World.TeamStates[team].OwnedTechs`; check idiom at Simulation.Constructors.cs:911-912 (`RequiredTechIdx` → `Content.Techs[t].Id`).
- Server has **no weapon-tech gate at spawn today** (only class unlock via `TryReserveSpawn`); the client arsenal gates it (ShipLoadout.cs:647) but the server must enforce it in the new resolver.
- Dock→relaunch already flows through a fresh MsgSpawn (DockShip despawns + refunds), so spawn-time loadout covers rearm; no separate mid-flight path needed.

## Wire changes

**MsgSpawn = 4 (client→server)** — append after the cargo block:
`[u8 nMounts][nMounts × (u8 hpIndex, u32 weaponId)]`
- `weaponId == HardpointDef.NoWeapon (u32.Max)` = deliberately empty; slots not listed = authored default. Client sends only overridden slots. Old-length frames (no tail) parse as zero overrides.

**MsgShipLoadout = 28 (server→client, new)** — full-table, reconcile-by-omission (MsgProbes pattern):
`[u8 count] count × (u64 shipId, u8 nSlots, nSlots × u32 weaponId)`
- Per-barrel **effective** weapon ids in hardpoint declaration order (NoWeapon = empty slot). Skip pods. Sent **reliable**, on change (`LoadoutsChangedThisStep`) + coarse keepalive. ~24 B/ship. Doubles as the owner's authoritative echo (arrives alongside MsgYouAre, before the ship can fire).
- Accepted trade-off: leaks enemy gun types for fogged ships — same class of leak as the broadcast MsgMinerTargets, and requirement 1 wants exactly this data shared.

**Ship snapshot record** — **unchanged** (stays 56 bytes). Which mounts fired at `LastFireTick` is reconstructed deterministically: fire eligibility is `mountLast[i] == 0 || tick - mountLast[i] >= interval[i]`, which depends only on the fire tick and per-mount state — not on held-input state (mount eligibility at an observed fire tick is input-independent: the event itself proves firing was held that tick). A client that sees every LastFireTick change (near-tier cadence) and knows the ship's effective mounts keeps a per-ship shadow `mountLast[]` in perfect lockstep with the server. Far-tier clients that skip events drift and self-correct — same lossiness class as today. Reconnect/late-join starts the shadow at zero (first observed volley renders all-eligible mounts) — minor visual, self-corrects.

**Shared cadence helper (drift-proofing)** — put the per-mount eligibility + stamping rule in ONE shared function (e.g. `FlightModel.MountsFiredAt(tick, mountLast[], intervals[])` or a small `shared/FireCadence.cs`), consumed by server `TryFire`, client `SpawnBoltFor` shadows, and `PredictionController` — the same pattern as `FlightModel.SpreadDirection`, so the three mirrors cannot drift.

**shared/Net/Wire.cs** — version stays 38; append to the history comment: MsgSpawn mount tail, MsgShipLoadout=28, per-mount cadence (fired mounts derived, record unchanged), "deploy client+server together".

## Step 1 — Shared

- `shared/Net/Wire.cs:10-66`: history-comment entry (above).
- New shared cadence helper (above) with the per-mount eligibility rule.

## Step 2 — Server protocol

`server/Net/Protocol.cs`
- Update the `MsgSpawn` layout comment (:66). Ship record untouched.
- New `MsgShipLoadout = 28` + `BuildShipLoadouts(Simulation)` — effective per-barrel ids from `ShipSim.MountWeaponIds` falling back to `ClassMuzzles` authored ids.

`server/Net/ClientHub.cs`
- MsgSpawn decode (:582-626): parse optional mount tail (bounds-checked like the cargo block); pass into `EnqueueJoin`.
- `AfterStep` (~:1325, next to the probes/bases cadence blocks): `loadoutFrame = (_sim.LoadoutsChangedThisStep || coarse) ? Protocol.BuildShipLoadouts(_sim) : null;` → `SendReliable` to every client.

## Step 3 — Server sim (`server/Sim/Simulation.cs`)

- **ShipSim** (:172): add `uint[]? MountWeaponIds` (null = authored — bots/pods/miners stay null), `uint[]? MountLastFire` (lazy-alloc in TryFire).
- Effective-mount seam: `WeaponIdAt(ship, barrel)` → override array else `ClassMuzzles[Class][barrel].WeaponId`. Geometry (Off/Dir) always from `ClassMuzzles` — overrides never move a hardpoint.
- Thread `(byte hpIndex, uint weaponId)[] mounts` through `_joinQueue` (:360), `_clientInfo` (:386), `EnqueueJoin` (:560), the drain (:859), and `ProcessRespawns` (:1280) so respawns keep the loadout.
- **`ResolveLoadout(team, cls, mounts, cargo)`** — restructures `ResolveCargo` (:1152-1182) into one joint validator:
  1. Build authored per-barrel id array + `hp.Index → barrel` map (don't assume Index == position).
  2. Per override: hpIndex must map to a Weapon-kind barrel; `NoWeapon` = empty; else id must resolve in `WeaponDefs` with `Kind ∈ {Bolt, Missile}` (dispensers rejected); every `WeaponDef.RequiredTechIdx` owned via `World.TeamStates[team].OwnedTechs` (Constructors.cs:911 idiom).
  3. Cargo ids must be dispensers (existing rule); **effective** mount mass + cargo mass ≤ `PayloadCapacity` (replaces the authored-mass sum at :1161-1165).
  4. Any failure → whole-request reject: authored mounts (null) AND authored `DefaultCargo`, with new source-generated log events in `server/Logging/Log.Sim.cs` (`SpawnMountInvalid`, `SpawnMountTechLocked`, `SpawnLoadoutPayloadExceeds`) next to `SpawnCargoNotDispenser`. Only reachable by hacked/buggy clients — the UI already gates capacity + tech.
- **`SpawnCombatShip`** (:1085): call `ResolveLoadout` once; set `MountWeaponIds`; seed missile magazine (:1103) and `SeedDispenserAmmo` from resolved results; set new `LoadoutsChangedThisStep = true` (cleared at top of Step, beside `DeathsThisStep.Clear()` :625). Ship removal also sets the flag (table shrinks → clients prune by omission; `ApplyShipGone` prunes immediately anyway).
- **`TryFire`** (:1975-2017) — per-mount cadence:
  - Drop the single primary/LastFireTick gate. Loop all barrels (full array, keeping empties for seed alignment): resolve `WeaponDefs[WeaponIdAt(ship, barrel)]`, skip non-Bolt/missing; gate + stamp via the shared cadence helper (`MountLastFire[barrel] == 0 || tick - MountLastFire[barrel] >= w.FireIntervalTicks`); on fire: `FireBolt(..., barrel)`.
  - If any mount fired: `LastFireTick = tick` (unchanged wire trigger — clients derive WHICH mounts from the shared rule).
- **Missiles**: new ship-aware `MissileMountFor(ShipSim)` (first effective Missile mount, geometry from ClassMuzzles); migrate `TryFireMissile` (:2279), `UpdateLock` (:2196), spawn seeding (:1103), and the siege check in Simulation.Orders.cs:232. Keep class-based `MissileMountFor(byte)` for pig paths (Pig.cs:422, :952, :1176) — bots are authored. Missile cadence stays single `LastMissileTick`.
- `PlaceAtBase` (:1246): also clear `MountLastFire` (it already resets `LastFireTick = 0`).
- `PrimaryWeapon(byte cls)` (:143): pig threat heuristic only now — leave class-based, update comment. `PigShotSpeed` ctor cache + `SigBias` authored projection stay approximations (noted, deferred).

## Step 4 — Client

- `client/scripts/ui/LoadoutState.cs`: add `WeaponOverridesFor(classId, hps)` → `(hpIndex, weaponId)[]` (only overridden slots, null → NoWeapon) and `ExpectedEffectiveIds(classId, hps)` → per-barrel `uint[]` for optimistic prediction. Rewrite the "cosmetic-only" header.
- `client/scripts/GameNetClient.cs`:
  - `RequestSpawn` (:333): new mounts param, append the tail.
  - New `case 28: ApplyShipLoadout` in the dispatch switch: decode into `Dictionary<ulong, uint[]> _shipMounts`, replace-whole; raise `ShipLoadoutsChanged`; expose `TryGetShipMounts(shipId, out uint[])`; prune in `ApplyShipGone` (:2026).
- `client/scripts/ShipController.cs` (:445-450): pass `LoadoutState.Shared.WeaponOverridesFor(...)` into RequestSpawn. Autofly sends none (authored defaults — keeps the smoke deterministic).
- `client/scripts/DefRegistry.cs` — never mutate `_mountsCache`:
  - New positional `WeaponSlots(byte classId)` → ALL Weapon-kind hardpoints in declaration order, `WeaponDef?` null for unresolvable ids (own cache).
  - New overlay `SlotsWithWeapons(classId, uint[] effectiveIds)` → positional slots with the given ids (no class-cache mutation).
  - Filtered `WeaponMounts` survives only for non-barrel HUD listing.
- `client/scripts/WorldRenderer.cs` `SpawnBoltFor` (:2378): mounts via `_net.TryGetShipMounts` → `SlotsWithWeapons`, else `WeaponSlots(class)`; barrel loop over the FULL positional list; per remote ship keep a shadow `mountLast[]` (keyed by shipId, sized to the slot list — reset on loadout change, pruned with the mount cache on ShipGone): on a LastFireTick change, spawn a bolt only for barrels the shared cadence helper says fired, then stamp those shadows. Skip null/non-Bolt slots.
- `client/scripts/PredictionController.cs` (:362-417): replace scalar `_lastFireTick` with `uint[] _mountLastFire` over the positional slot list; per-mount gate + stamp via the shared cadence helper — exact mirror of the new TryFire. Effective mounts injected via `SetLoadout(uint[])` from the `ShipLoadoutsChanged` handler (local ship id), initialized optimistically from `ExpectedEffectiveIds` at spawn (echo arrives with MsgYouAre, before first fire — correction window ≈ 0 ticks). Reconciliation (:462/:539): apply the same cadence rule at `row.LastFireTick` (when ≠ 0) to stamp the mounts that fired. Expose `LastFireTickFor(weaponId)` for the HUD cooldown bar.
- HUD consumers → local **effective** mounts through one shared resolution helper (small static, e.g. on DefRegistry, taking the optional net-cache `uint[]`): `WeaponsPanel.cs` :74 (rows) + :311 (cooldown via `LastFireTickFor`), `TargetMarkers.cs` :479/:468/:490, `SystemRing.cs` :71, `Hud.cs` :319. All degrade to class defaults when the cache is empty; pre-spawn hangar views keep reading LoadoutState as today.

## Step 5 — Verification

Build order: shared → server → client (`dotnet build`; Godot client references shared directly).

**New `tests/LoadoutTest`** (console PASS/FAIL, boots the real sim like `tests/MissileTest/Program.cs` — ContentLoader → World → Simulation → StartMatch, pigs/miners/shields/fog off; drive `EnqueueJoin` with mounts):
1. Scout, cannon emptied → no bolt, `LastFireTick` stays 0 (the motivating bug).
2. Swap with tech seeded (`content.Start.BaseTechs.Add`, MissileTest idiom) → swapped damage/cadence observed; without tech → authored fallback + log.
3. Whole-request reject: bad hpIndex / dispenser id / mounts+cargo over capacity → authored mounts AND cargo.
4. Per-mount cadence: two guns with different intervals fire independently; the shared cadence helper, replayed over the observed LastFireTick sequence, reconstructs exactly which mounts fired each event (the client-derivation invariant); spread seeds stable with slot 0 emptied.
5. Missile rack emptied → `MissileAmmo` 0, `TryFireMissile` no-op, siege order (Orders.cs:232) respects it.
6. Bots keep authored loadouts; death → respawn keeps player overrides (via `_clientInfo`).
7. Determinism: same script twice → bit-identical.

**Existing suites**: full `tests/` run; only the 6 known content-drift failures (ShieldTest/ContentTest/FactionsTest) are acceptable. MissileTest is most exposed.

**Client smoke** (dotnet suites don't cover the Godot client): `--autofly` server + two headless clients exercises MsgSpawn encode/decode and MsgShipLoadout. Then a `verify`-skill pass: empty the scout cannon in the hangar → launch → no bolts locally AND on a second client; swap a gun → remote bolt visuals use the swapped def.

Note: this repo auto-commits/pushes mid-session — sequence work so shared+server+client stay coherent within each push.

## Risks

- **Positional-slot migration** must land atomically across server TryFire / PredictionController / SpawnBoltFor, or spread seeds desync the moment a leading slot is emptied.
- Derived fired-mounts under lossy far tiers / late join: a missed LastFireTick event drifts the remote shadow (a wrong-mount bolt or missed volley until the next event) — accepted, same lossiness class as today's single-tick behavior, visual-only.
- SigBias / PigLead stay class-default approximations — deferred balance items.
- If the server rejects a loadout, the hangar UI still shows the override (echo corrects sim/rendering, not the screen) — acceptable; optionally add a system-chat notice from the reject path later.
