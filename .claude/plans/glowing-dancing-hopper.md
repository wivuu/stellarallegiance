# Expendables: consistency + debt paydown

## Context

The timed-reload / fuel-pod work (`8ef5a44`) finished the expendables *mechanics*. What it left
behind is drift: the same rule implemented two or three times, four sibling expendables given four
different treatments, and authored data that means one thing on one code path and another on the
next. Every gap below exists **because** a rule was duplicated instead of shared — so each fix is
paired with collapsing the duplication that let it drift, rather than patching the one instance and
leaving the next copy to rot.

Five deliverables, each stated as the inconsistency it removes. (Maps to review items 1, 3, 5, 6, 7.)

Scope note, agreed with the user: **the PIG change ships as groundwork only.** PIGs never set
`DropChaff/DropMine/DropProbe` (only `ClientHub.cs:828-830` does) and never set `Boost`, so a seeded
drone hold is never spent — no observable in-game change today. What it buys is that `default-cargo`
finally means the same thing for a drone as for a player. The AI reaction was explicitly deferred.

---

## A. One tier-migration rule, three implementations → one shared rule

**Inconsistency:** the hangar cargo rows say "PROX MINE" while the in-flight `WeaponsPanel` says
"PROX MINE 3" for the same hold. **Debt:** the tier walk exists three times, and the copies are
kept in sync only by comments calling each other "twin" and "mirror":

- `Simulation.MigrateWeaponTier` (`server/Sim/Simulation.cs:1572`) — authoritative, at spawn
- `DefRegistry.MigrateWeaponTier` (`client/scripts/DefRegistry.cs:279`) — display
- `ShipLoadout.MigrateTier` (`:819`) and `WeaponsPanel.MigratedDispenser` (`:216`) — thin wrappers
- `shared/ContentValidator.cs:71` validates the succession rule against a third reading of it

Both real copies operate on the *same* `shared/Defs.cs` `WeaponDef` and differ only in how they look
up a def and test tech ownership.

- Add `shared/WeaponTier.cs` — a tiny shared rule class alongside `shared/FireCadence.cs`, the
  existing precedent for "one rule both peers must agree on". Signature roughly
  `Migrate(uint weaponId, Func<uint, WeaponDef?> getWeapon, Func<ushort, bool> ownsTech)`, carrying
  the guard count, the `SucceededByWeaponId`/`ObsoletedByTechIdx` predicates, and the mass guard.
  Delegates as *parameters* (fed by method-group conversion), not `Func<>` locals.
- Server and client `MigrateWeaponTier` both become one-line calls into it; the wrappers stay as-is.
  Point the `ContentValidator` comment at the shared rule so there is one place to read it.
- **Then** the actual fix falls out: add `DefRegistry.DispenserForCargo(uint cargoId)` next to
  `GetCargoItem` (`:307`) — the Chaff/Mine/Probe-kind `WeaponDef` whose `CargoId` matches, else null
  (the fuel pod fires nothing). This is the lookup `WeaponsPanel.DispenserFor` (`:194-205`) already
  open-codes; point its inner scan at the helper. In `ShipLoadout.Hangar.cs` `RefreshCargoSection`
  (`:288-325`), resolve the row name through `MigrateTier(disp.WeaponId, Team)` instead of printing
  `item.Name`, falling back to `item.Name` when there is no dispenser.

Known limitation, unchanged and matching the rest of the screen: `RefreshCargoSection` runs on def
arrival (`ShipLoadout.cs:463`) and on `SelectShip` (`:607`), not per frame, so research completing
while docked relabels on the next hull pick. `RefreshLoadoutViews` (the arsenal) is call-driven the
same way. Not expanding that here.

## B. One "what a hull spawns holding" seam, two spawn paths

**Inconsistency:** `SpawnCombatShip` seeds the hold from `default-cargo`; `SpawnPigShip`
(`server/Sim/Simulation.Pig.cs:405-425`) open-codes a partial version that sets `MissileAmmo` and
nothing else, so drones silently ignore authored cargo.

- Extract the `fallbackCargo` expression inside `ResolveLoadout` (`Simulation.cs:1445-1447`) into a
  private `DefaultCargoFor(byte cls)` and use it from both callers.
- In `SpawnPigShip`, add `SeedDispenserAmmo(s, DefaultCargoFor(slot.Class));`. `SeedDispenserAmmo`
  (`:1383`) already tolerates a missing `TeamState` and runs the tier walk, so drones follow team
  research for free. PIG classes are Scout / Enh Fighter / Bomber (`Simulation.Pig.cs:282-285`) and
  all three author `default-cargo`.
- Do **not** touch `MountWeaponIds` — PIGs deliberately fly `ClassMuzzles`; assigning migrated mounts
  would change gun tiers and emit `MsgShipLoadout` rows. The comment must say plainly that nothing
  consumes the seeded hold yet.

## C. One dispenser gate, three copies — and the eject geometry that stayed hardcoded

**Debt:** `TryDropChaff` / `TryDeployMine` / `TryDeployProbe` (`Simulation.Chaff.cs:43`,
`Mines.cs:53`, `Probes.cs:56`) each open-code the identical prologue: ammo/weapon-id check →
`WeaponDefs.TryGetValue` → `FireCadence.MountFires(tick, lastTick, LoadIntervalTicks(...))` →
decrement + stamp. **Inconsistency:** `mechanics:` in `world.yaml` already owns `launch-speed`,
`dock-radius-frac` and the pod-eject pair, but these siblings never followed — the Stage-0 deferred
item.

- Collapse the shared prologue into one private helper on `Simulation` (e.g.
  `TryConsumeDispenser(ShipSim ship, ref byte ammo, uint weaponId, ref uint lastTick, uint tick,
  out WeaponDef w)`), leaving each file only its own spawn geometry. One place then owns the
  "server-side cadence gate is the only drop-input debounce" invariant that all three comments
  currently restate.
- Lift the eject constants into `mechanics:` following the existing four-file knob pattern (trace
  `PodEjectSpeed` for the template):

  | Site | Constant | Knob |
  |---|---|---|
  | `Chaff.cs:69-71` | aft offset `4f` | `chaff-eject-offset: 4` |
  | `Chaff.cs:69-71` | velocity inherit `0.5f` | `chaff-eject-vel-inherit: 0.5` |
  | `Chaff.cs:69-71` | aft kick `10f` | `chaff-eject-kick: 10` |
  | `Mines.cs` deploy | clearance past `MineCloudRadius` | `mine-eject-clearance: 4` |
  | `Probes.cs` deploy | clearance past `ShipRadius + hitRadius` | `probe-eject-clearance: 2` |

  1. `shared/Defs.cs` `WorldMechanicsTuning` (`:833`) — five `float` fields whose initializers *are*
     the stock values. 2. `server/Content/WorldLoader.cs` `WorldMechanicsDef` (`:300`) — five
     `double?` props with `<summary>` docs. 3. the apply block (`:642`) — five `t.X = F(me.X, t.X);`
     lines. 4. `world.yaml` `mechanics:` — author the stock values next to `pod-eject-speed`.
  Read them from `_mech` (already a `Simulation` field, `:34`). Server-only, no protocol change: the
  resulting positions are streamed, so the client never mirrors these. Leave the `hitR ... : 4f`
  fallback in `TryDeployProbe` alone — an unauthored-field guard, not tuning.

## D. Four sibling expendables, four different audio treatments

**Inconsistency:** chaff reuses `Impact` pitched up (its own comment: "no bespoke asset"), mines
reuse `MissileLaunch` pitched down, probe deploy is silent, fuel-pod load is silent. `audio-index.md`
names the original Allegiance wave for every one of them. **Debt:** the sector gate is applied to
node visibility but not to the sound in the same functions, and `Hud`'s empty-ammo blip is
copy-pasted four times.

Copy from `pick-assets/sound-effects/` into `client/assets/audio/` (the pattern already used by
`mining_loop`, `contact_*`, `missile_*`, `probe_ping`):

| Source | New asset | Allegiance logical sound |
|---|---|---|
| `dropobject.ogg` | `deploy_object.ogg` | `deployChaffSound`, `deployProbeSound` |
| `dropmine.ogg` | `deploy_mine.ogg` | `deployMineSound` |
| `mount.ogg` | `reload_start.ogg` | `mountSound`, `startReloadSound` |

- `SfxManager.cs`: add `DeployObject`, `DeployMine`, `ReloadStart` to `SfxId` (`:28`) and the `Files`
  map (`:55`). Not loops.
- Retire the two stand-ins: `ChaffFx.cs:73` → `DeployObject`; `MinefieldViews.cs:112` → `DeployMine`.
- **Probe deploy** — `ProbeRenderer.NetUpsert` (`:38`) fires on *first sight*, not on deploy, so it
  needs the same freshness test the minefield cue already uses ("first seen while still arming was
  just laid", `MinefieldViews.cs:108-112`): the row carries `TicksLeft` and the def carries
  `ProjectileLifeTicks`, so `TicksLeft + ~20 ticks >= ProjectileLifeTicks` means freshly dropped.
  Probes discovered mid-life (sector entry, reconnect, an enemy buoy entering radar) stay silent.
- **Sector gate (real fix, same drift):** these stream positions are sector-*local*, and neither
  `ChaffFx` nor `MinefieldViews` gates its deploy *sound* on the view sector — a teammate dropping in
  another sector can sound adjacent. Gate all three. `ChaffFx.cs:159` and `MinefieldViews.cs:244`
  hold byte-identical `SectorVisible`/`SectorOf` pairs; fold them into one shared helper both call
  (`ProbeRenderer` is not a Node and reads `_sectors.ViewSector` instead).
- **Fuel-pod load** — the HUD draws the load arc (`SystemRing.cs:128`) with no cue. Add a rising-edge
  `PlayUi(ReloadStart)` on `PredictionController.FuelLoading` in `Hud.cs` `_Process`. Rather than
  pasting a fifth near-identical block, first collapse the existing four (`Hud.cs:427-467`, plus the
  `_firing2Held/_chaffHeld/_mineHeld/_probeHeld` fields at `:46-49`): the three dispenser blocks
  differ only by action name, ammo accessor and held flag, so drive them from one small table + a
  local function, then add the fuel edge beside them sharing the `_emptyClickCd` debounce (which also
  keeps a prediction rollback from stuttering it).

`.import` sidecars are gitignored (`client/.gitignore:14`) — commit only the three `.ogg` files and
run a headless import before testing, or the loader warns and silently plays nothing.

## E. Three hulls model a fuel tank, one carries pods

**Inconsistency:** `enh-fighter` (`hulls.yaml:176`) and `adv-fighter` (`:226`) author tanks (max-fuel
15 / 20, drain 3.0/s, regen 0.5/s ⇒ ~30 s to refill) but no pod; only the Lt Interceptor does.

- Add `- { item: fuel-pod-1, count: 1 }` to each hull's `default-cargo`. **One** pod, not two: the
  Interceptor's 2-pod reserve stays its identity as the dedicated booster hull, while a fighter gets
  one extra ~5 s dash instead of waiting out the regen. Payload fits easily (capacity 20, ~5 used,
  pod mass 1), so `ContentValidator`'s default-loadout check (`shared/ContentValidator.cs:337`) passes.
- **Breaks a test by design:** `tests/ContentTest/Program.cs:331-336` asserts the fighter's default
  cargo is exactly `[(3,2)]`. Update to the authored order `[(3,2),(5,1)]` and refresh its comment.

---

## Deliberately not included

Review item 2 (PIG chaff-on-lock reaction) and item 4 (reload time in the hangar cargo rows) — both
deferred by the user. No new gameplay behavior in this batch.

## Verification

- **Console suites** (`dotnet run` apps in `wivuullegiance.slnx`): ContentTest with the updated
  fighter-cargo assertion, plus FuelPodTest, ReloadTest, MissileTest, LoadoutTest, MineTest,
  CommanderTest. Baseline per repo history is all green *except* the four pre-existing CollisionTest
  failures — not a regression.
- **A and C are refactors of live rules, so they must be behavior-neutral.** Prove it, don't assume:
  the tier tests in LoadoutTest/ContentTest must pass unchanged after the shared `WeaponTier` swap,
  and ReloadTest/MineTest must pass unchanged after the dispenser-prologue collapse. Knob values
  equal to the current constants must be byte-identical; sweep one (`chaff-eject-kick`) to confirm it
  is actually wired, then restore.
- **B has no runtime effect**, so prove it in a test or it is unobservable: add a short `Check` in
  `tests/CommanderTest` (which already finds PIGs via `sim.Ships.Where(s => s.IsPig ...)`) asserting
  a spawned drone's `ChaffAmmo > 0`.
- **A's fix**: run the hangar self-drive `--hangar-demo=<dir>` (`ShipLoadout.Hangar.cs:379`) and
  confirm the cargo rows in the `01-open` / `06-overcap` snapshots name the live tier — seed a
  research state owning a tier-2/3 dispenser so the migration is actually exercised.
- **D**: headless-import first, then fly manually — drop a probe (G), chaff (C), mine (B), and boost
  a Lt Interceptor to a dry tank for the pod load. Watch the Godot log for `[SfxManager] missing
  audio asset` (means the import did not run). Warp out while a teammate deploys to confirm the new
  sector gate stays silent.
- `run-client.ps1` gotcha: pass game flags via `-GodotArgs`, never as the first bare `--flag` (it
  binds to `-WriteMovie` and starts a time-warped movie-mode client).
- Format only the files touched — `csharpier` is pinned at 1.2.6 and HEAD carries widespread
  pre-existing format drift, so never run a blanket `format .`.
