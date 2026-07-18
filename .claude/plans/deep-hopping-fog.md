# Iron Coalition ordnance import — replace the last placeholder content

## Context

Proto 39–41 replaced all placeholder guns/hulls/bases/techs with a real Iron Coalition slice from
Allegiance's `PCore014.igc`, but **explicitly deferred launchers (weapon-ids 3–8)**. The six lines —
`missile-rack`/`seeker-missile`, `dart-rack`/`dart-missile`, `torpedo-rack`/`anti-base-torpedo`,
`decoy-dispenser`/`sensor-decoy`, `mine-dispenser`/`proximity-mine`, `probe-dispenser`/`recon-probe`
— are the **only stale AI-invented content left** in `server/Content/core/` (no tech gating,
invented stats). **The IGC Iron Coalition extraction is the source of truth for what should be
available.** This plan removes every placeholder expendable and re-authors the whole set from
PCore014.igc, mirroring the gun-import conventions (our-magnitude anchors × IGC ratios) and the
wire-v35 weapon-tier machinery. **No protocol bump** — all wire fields exist; this is content plus
four small code seams.

### User decisions (locked)
1. **Scope: all expendables** — missiles, mines, chaff, probes.
2. **Full tier chains** — tier 1 seeded; tiers 2/3 as research devs, `obsoleted-by-techs` +
   `successor-part-id` auto-migration like guns.
3. **Roster: role-match + Dumbfire** — MRM Seeker 1/2/3, MRM Quickfire 1/2/3 (dart analog),
   SRM Anti-Base 1/2/3, SRM Dumbfire 1/2/3 (new), Prox Mine 1/2/3, Counter chaff 1/2/3,
   EWS Probe 1/2/3.
4. **Anti-Base power IGC-faithful 300/375/375** (garrison falls in ~7 tier-1 hits vs 11 today) —
   chosen over pinning today's siege pacing.
5. **Dumbfire is a missile TYPE, not a fire mode** (user correction): in Allegiance gameplay a
   dumbfire has a QUICK target lock and steers only slightly. Author it as a normal guided
   missile — short lock-time, low turn-rate, heavy punch. Do NOT build an unguided path, do NOT
   relax the lock validators. (The IGC record's lockTime 0 = "instant lock", not "no lock".)
6. **Delegate implementation to subagents where appropriate** — mechanical build work (YAML
   authoring, test-expectation updates, parser extension) goes to Sonnet subagents; keep the
   small code seams and balance judgment on the main thread (per `delegate-simpler-to-sonnet`).

### Verified design facts
- Sim missile path needs **no changes for dumbfire** — it's a standard `WeaponKind.Missile` with
  quick lock / low turn. (`TryFireMissile` at `Simulation.cs:2505` even tolerates unlocked
  launches already; not needed.) CoreValidator's lock-time/lock-angle/max-lock > 0 rules
  (`CoreValidator.cs:120-124`) stay as-is and all authored values satisfy them.
- **`ProjectLauncher` never stamps `ObsoletedByTechIdx`/`SucceededByWeaponId`** (only
  `ProjectWeapon` does, `FactionsContentProjection.cs:297`), and `weaponIdByPartId` (L72) is built
  from `core.Weapons` only — launchers must be added (change E).
- **Cargo→dispenser seam**: `_dispenserByCargo` (Simulation.cs:439/587) indexes dispenser weapons
  by CargoId; `SeedDispenserAmmo` (L1234) sets `MineWeaponId/ChaffWeaponId/ProbeWeaponId` at
  spawn; `MigrateWeaponTier` (L1384) exists — the tier walk hooks in here (change C).
- PIGs fire missiles only when locked and no default loadout mounts a dumbfire rack → no PIG change.
- Iron GAS `missile-damage ×1.10` applies once at detonation — authored powers are pre-multiplier.
- Hull payload sums verified: **no numeric hull changes needed** (scout 7/12, bomber 17/20;
  quickfire rack mass drops 3→2). Comments/item-ids only.
- IGC decode corrections (re-decoded, offsets validated against name@99/price@64): Seeker turn
  0.96/1.134/1.309, chaff-res 1.0/1.25/1.5; Quickfire chaff-res 0.9/1.15/1.4; **Anti-Base is
  instant-lock (lockTime 0) in IGC** — we keep our lock-based siege identity (siege orders/PIG
  siege/MissileTest are lock-based), rack amounts 10/10/4, loadTime 4.0/4.0/3.5; Counter strength 1.5/2.25/3.375,
  IGC blast/width all 0 on missiles (we carry our per-role fuse/splash values). Dev prices: IGC
  2500→150 cr/30 s, 5000→300 cr/60 s (matches gun-dev price points).

---

## Design

### D1. Weapon-id / cargo-id allocation
Ids 3–8 keep their `WeaponKind` (sim/client/PIG dispatch untouched); 15 new launchers append.
Cargo-ids stay **2/3/4 only** — carried by tier-1 expendables; tier-2/3 expendables author **no
cargo-id** (so they're not indexed in `_dispenserByCargo`; the dispenser tier is resolved
server-side per D2).

| weapon-id | launcher id | kind | placement |
|---|---|---|---|
| 3 | `seeker-rack-1` | Missile | in place (missile-rack) |
| 4 | `quickfire-rack-1` | Missile | in place (dart-rack) |
| 5 | `anti-base-rack-1` | Missile, can-damage-base | in place (torpedo-rack); bomber hp index 4 unchanged |
| 6 | `counter-dispenser-1` | Chaff, cargo 3 | in place (decoy-dispenser) |
| 7 | `prox-mine-dispenser-1` | Mine, cargo 2 | in place (mine-dispenser) |
| 8 | `ews-probe-dispenser-1` | Probe, cargo 4 | in place (probe-dispenser) |
| 18/19 | `seeker-rack-2/3` | Missile | append |
| 20/21 | `quickfire-rack-2/3` | Missile | append |
| 22/23 | `anti-base-rack-2/3` | Missile, can-damage-base | append |
| 24/25/26 | `dumbfire-rack-1/2/3` | Missile (quick-lock, low-turn) | append — new line |
| 27/28 | `counter-dispenser-2/3` | Chaff, no cargo-id | append |
| 29/30 | `prox-mine-dispenser-2/3` | Mine, no cargo-id | append |
| 31/32 | `ews-probe-dispenser-2/3` | Probe, no cargo-id | append |

### D2. Cargo-tier obsolescence — tier the dispenser weapon, ONE cargo item per line
Full cargo-item migration would need CargoItemDef tech fields (wire bump + hangar UI). Instead:
hangar keeps one tier-neutral cargo row per line (pack size fixed per line — accepted limitation);
at spawn, `SeedDispenserAmmo` walks the successor chain via `MigrateWeaponTier` against owned
techs, so the *fired* mine/puff/probe silently upgrades (change C, ~4 lines; dispenser mass 0
satisfies the mass guard). Resolution is server-side — clients can't out-tier the server.
Default-cargo needs no migration (cargo-ids stable); autofly's hardcoded hold
(`ShipController.cs:448-450`, ids 2/3/4) stays valid. Optional polish (change D):
`WeaponsPanel.DispenserFor` (~L194) walks the same chain client-side so the HUD row names the
live tier.

Missile racks need none of this — they're hardpoint/loadout-mounted, so existing
`ResolveLoadout`/`MigrateWeaponTier` + `ShipLoadout` arsenal hide/migrate apply as soon as the
projection stamps the fields (change E).

### D3. Hull mount masks: NOT enforced
IGC gives fighters Dumbfire/Seeker/Quickfire, bomber Anti-Base, interceptor/devastator none. Our
schema deliberately has no per-mount compatibility mask (documented behavior, tested in
LoadoutTest — dart-rack-on-scout is a feature). Keep the open model; payload budgets make abuse
costly. Document in the launchers.yaml header.

### D4. Model names — all IGC models exist in `pick-assets/`
Copy GLBs + run `tools/godot-import.sh`:
- `mis06` (Seeker), `mis08` (Quickfire), `mis05` (Dumbfire), `mis11` (Anti-Base) → `client/assets/missiles/`
- `dn_ptminprx` (Prox Mine) → `client/assets/mines/` (fallback `acs41` if it imports badly — decide at visual check)
- `utl23` (EWS Probe) → `client/assets/probes/` (keep `model-size: 4.0`; fallback `acs64`)
- `acs40` (Counter) already present, IGC-exact.
Client loaders resolve by authored name with graceful placeholder fallback. If a copied GLB
references an external texture, copy it as the ship import did. No user-provided assets needed.

### D5. Stat tables (YAML-ready)
Anchors (keep-our-magnitudes × IGC ratios): missiles anchor `seeker-missile` ↔ MRM Seeker 1
(power ×0.75, lock ×0.75, accel ×⅔, turn ≈×83.3 deg per rad/s); mines ↔ Prox Mine 1 (power
×0.15, radius ×0.8, lifespan = endurance ×0.03); chaff ↔ Counter 1 (strength ×⅔); probes ↔ EWS 1
(sight ×9.6). Flagged judgment calls: Quickfire turn capped 120/160/200 deg/s (raw IGC 6/8/10
rad/s would out-turn everything); Quickfire fire-interval 10 ticks (IGC 0.25 s load = DPS hose);
Anti-Base keeps our lock identity (lock 3.0 / angle 0.35 / max-lock 1500 / chaff-res 2.5).

**Missiles** (`expendables.yaml missiles:`; all author direct-hit-multiplier + trail; no cargo-id):

| field | `mrm-seeker-1/2/3` | `mrm-quickfire-1/2/3` | `srm-dumbfire-1/2/3` | `srm-anti-base-1/2/3` |
|---|---|---|---|---|
| name | MRM Seeker 1/2/3 | MRM Quickfire 1/2/3 | SRM Dumbfire 1/2/3 | SRM Anti-Base 1/2/3 |
| mass | 4 | 3 | 4 | 6 |
| lifespan | 8 / 8 / 9 | 5 / 5 / 5 | 5 / 6 / 6 | 12 / 12 / 12 |
| initial-speed | 90 | 180 | 120 | 60 |
| acceleration | 40 | 52 / 57 / 63 | 67 | 40 |
| max-speed | 220 | 320 | 260 | 140 |
| turn-rate | 80 / 95 / 109 | 120 / 160 / 200 | 67 (low steer, IGC 0.80 rad/s) | 15 |
| lock-time | 2.0 / 1.1 / 0.75 | 0.25 / 0.15 / 0.1 | 0.5 / 0.4 / 0.3 (quick) | 3.0 |
| lock-angle | 0.5 / 0.5 / 0.75 | 0.25 / 0.25 / 0.37 | 0.5 | 0.35 |
| max-lock | 1200 | 1000 | 800 (short-range) | 1500 |
| chaff-resistance | 1.0 / 1.25 / 1.5 | 0.9 / 1.15 / 1.4 | 1.0 | 2.5 |
| power | 45 / 45 / 56 | 30 / 34 / 38 | 75 / 94 / 113 | **300 / 375 / 375** |
| width | 1 | 2 | 2 | 1 |
| direct-hit-multiplier | 1.5 | 1.25 | 1.5 | 1.0 |
| blast-power | 30 / 30 / 37 | 17 / 19 / 21 | 50 / 63 / 75 | 60 / 75 / 75 |
| blast-radius | 25 | 12 | 25 | 30 |
| can-damage-base | — | — | — | true |
| model-name | mis06 | mis08 | mis05 | mis11 |
| trail life/scale/color | 0.7/0.45/ffc890ff | 0.4/0.3/aaccffcc | 0.6/0.5/ffb060ff | 1.0/0.6/ff9060ff |

**Mines / chaff / probes** (tier 1 carries cargo-id + glyph + charges-per-pack, name unsuffixed so
the hangar row stays tier-neutral; tiers 2/3 omit all three):

| field | `prox-mine-1/2/3` | `counter-1/2/3` | `ews-probe-1/2/3` |
|---|---|---|---|
| name | Prox Mine / 2 / 3 | Counter / 2 / 3 | EWS Probe / 2 / 3 |
| cargo-id / glyph / packs (t1) | 2 / ◈ / 1 | 3 / ◇ / 8 | 4 / ◉ / 2 |
| mass | 1 | 1 | 2 |
| lifespan | 60 / 75 / 90 | 3 | 1200 |
| power (dps) | 60 / 75 / 90 | — | — |
| cloud-radius | 80 / 80 / 100 | — | — |
| cloud-count / arm-delay / signature | 64 / 1 / 1.0 | — | — |
| chaff-strength | — | 1.0 / 1.5 / 2.25 | — |
| decoy-radius | — | 60 | — |
| sight-radius | — | — | 4800 / 5760 / 6720 |
| hit-points / hit-radius / model-size | — | — | 25 / 12 / 4.0 |
| signature | — | — | 1.0 / 0.83 / 0.67 |
| model-name | dn_ptminprx | acs40 | utl23 |

**Launchers** (tier-1 `required-capabilities: [base]`; every non-top tier carries
`obsoleted-by-techs: [<line>-N+1]` + `successor-part-id: <next launcher id>`; tiers 2/3 add
`required-techs: [<line>-N]`; masses constant per line → migration mass guard holds):

| line | slot | mass | amount t1/t2/t3 | fire-interval-ticks |
|---|---|---|---|---|
| seeker-rack | magazine | 4 | 6/6/6 (IGC 10 ×0.6) | 30 |
| quickfire-rack | magazine | 2 | 6/6/6 | 10 |
| anti-base-rack | magazine | 4 | 6/6/4 (IGC 10/10/4) | 60/60/50 |
| dumbfire-rack | magazine | 4 | 6/6/6 | 30 |
| counter-dispenser | chaff-launcher | 0 | 1 | 40 |
| prox-mine-dispenser | dispenser | 0 | 1 | 100 |
| ews-probe-dispenser | dispenser | 0 | 1 | 100 |

### D6. Techs + developments (append-only — wire indices are list order)
`techs.yaml`: append 14 at tail (indices 16–29): `seeker-2, seeker-3, quickfire-2, quickfire-3,
dumbfire-2, dumbfire-3, anti-base-2, anti-base-3, mine-2, mine-3, chaff-2, chaff-3, probe-2,
probe-3`. Total 30.

`developments.yaml`: append 14 at tail (indices 13–26; StrategyTest's 0–12 untouched), all
`group: WEAPONS`, tech-only, each granting its same-named tech. Missiles/chaff/probes 150 cr/30 s;
mines 300 cr/60 s. Homing (re-homed per Mini-Gun/Nanite precedent where the IGC base is
unimported):

| idx | dev id | required | IGC home |
|---|---|---|---|
| 13/14 | dev-seeker-2 / -3 | `[supremacy-1]` / `[supremacy-adv, seeker-2]` | Tactical → re-homed Supremacy |
| 15/16 | dev-quickfire-2 / -3 | `[supremacy-1]` / `[supremacy-adv, quickfire-2]` | Supremacy (faithful) |
| 17/18 | dev-dumbfire-2 / -3 | `[supremacy-1]` / `[supremacy-adv, dumbfire-2]` | Supremacy (faithful) |
| 19/20 | dev-anti-base-2 / -3 | `[supremacy-1]` / `[supremacy-adv, anti-base-2]` | Supremacy (faithful) |
| 21/22 | dev-mine-2 / -3 | `[base cap]` / `[garrison-str, mine-2]` | Expansion → re-homed Garrison |
| 23/24 | dev-chaff-2 / -3 | `[base cap]` / `[garrison-str, chaff-2]` | free / Tactical-Adv → Garrison |
| 25/26 | dev-probe-2 / -3 | `[supremacy-1]` / `[supremacy-adv, probe-2]` | Supremacy (faithful) |

`iron-coalition.yaml`: no change.

### D7. Code changes (two seams + one polish)
- **C — dispenser tier walk** (`Simulation.cs` `SeedDispenserAmmo` L1234): after
  `_dispenserByCargo` lookup, `wid = MigrateWeaponTier(ts, w.WeaponId)`; if changed, re-fetch def.
- **D (polish) — HUD dispenser tier name** (`client/scripts/WeaponsPanel.cs` `DispenserFor`
  ~L194): walk the successor chain via `_world.TeamOwnsTech` (same loop as
  `ShipLoadout.MigrateTier`).
- **E — projection** (`server/Content/FactionsContentProjection.cs`): extend `weaponIdByPartId`
  (L72) with `core.Launchers.Where(l => l.WeaponId is not null)`; stamp `ObsoletedByTechIdx` +
  `SucceededByWeaponId` in all four `ProjectLauncher` kind branches (library `Launcher : Part`
  already models both fields; JSON schema already includes the keys).

### D8. Parser extension (`.claude/skills/igc-format/igc_parser.py`) — do FIRST (reproducibility)
Decode full expendable structs (offsets validated): shared header `loadTime@52 lifespan@56
signature@60`, buyable at 64 (`price@64 model@72 name@99`), `req@326 eff@376`, launcher block
`sig@428 mass@432 partMask@436 expendableSize@438 hitPoints@440 defenseType@444
expendableTypeID@446`; tails @464 — missile `accel/turnRate/initSpeed/lockTime@476/readyTime@480/
maxLock@484/chaffResistance@488/dispersion@492/lockAngle@496/power@500/blastPower@504/
blastRadius@508/width@512/damageType(u8)@516` (record 524); mine `radius/power/endurance/
damageType`; chaff `chaffStrength@464`; probe `scannerRange@464 dtimeBurst@468 dispersion@472
accuracy@476 ammo(i16)@480 projectileTypeID@482 dtRipcord@488`. Magazines = `DataLauncherTypeIGC`
(partType, size<100): `amount@0 partID@2 successorPartID@4 launchCount@6 expendableTypeID@8`.
Add expendables to `resolve_faction` subsetting + an ORDNANCE section to `iron_slice_report` with
the D5 anchor factors. Validate against known fields (names, price 0, model strings, rack amounts
10/10/4/2/1). Update the SKILL.md `--iron-slice` line. Data:
`/Users/erik/projects/Allegiance/artwork-full/PCore014.igc`.

---

## File-by-file changes

| file | change |
|---|---|
| `.claude/skills/igc-format/igc_parser.py` (+SKILL.md) | D8 parser extension (first) |
| `server/Content/core/expendables.yaml` | full replacement: 12 missiles + 3 mines + 3 chaff + 3 probes (D5) |
| `server/Content/core/launchers.yaml` | 6 in-place + 15 appended (D1/D5), tier wiring (D6), header docs (D3, anchors) |
| `server/Content/core/techs.yaml` | +14 appended (D6) |
| `server/Content/core/developments.yaml` | +14 appended (D6) |
| `server/Content/core/hulls.yaml` | default-cargo item ids (`prox-mine-1`/`counter-1`/`ews-probe-1`) + comments only |
| `server/Content/FactionsContentProjection.cs` | change E |
| `server/Sim/Simulation.cs` | change C |
| `client/scripts/WeaponsPanel.cs` | change D (polish) |
| `client/assets/{missiles,mines,probes}/` | copy mis05/06/08/11, dn_ptminprx, utl23 from `pick-assets/` + `tools/godot-import.sh` |
| `GLOSSARY.md` L199 + comment sweep | sensor-decoy → Counter; stale id mentions in Protocol.cs/Defs.cs/Simulation.Chaff.cs/ProbeView.cs/ShipController.cs comments; hulls-weapons SKILL.md examples |

## Test updates

- **ContentTest**: weapon count 18→33; seeker assert: only ModelName mis09→mis06 changes (stats
  anchor-preserved); can-damage-base assertion → all Missile-kind CanDamageBase ∈ {5,22,23};
  techs 16→30, devs 13→27; add: launcher ObsoletedByTechIdx/SucceededByWeaponId asserts (id 3 →
  succeeded by 18), dumbfire id 24 quick-lock/low-turn stats (LockTicks 10, turn ≈67 deg/s in
  rad); cargo items stay 3.
- **FactionsTest** (L143-216): rename ids; update changed stats (anti-base 200→300, models,
  quickfire numbers); add launcher ObsoletedByTechs/SuccessorPartId assert.
- **LoadoutTest**: weapon-id 4 mount still valid (now quickfire; magazine read from def); recheck
  rack-mass arithmetic (3→2); add rack tier-migration at spawn (own seeker-2 → mount 3 migrates
  to 18) and a dumbfire-rack (24) mount+launch (quick lock acquired, missile tracks with low
  turn, no base damage).
- **MissileTest**: seeker duels numerically unchanged; base-siege hits-to-kill expectations →
  power 300 (×1.1 GAS where Iron on); add dumbfire scenario (lock completes in ~10 ticks, low
  turn-rate limits pursuit vs an evading target, ship hit, no base damage).
- **MineTest**: tier 1 unchanged; add tier-resolution case (grant mine-2, respawn, assert
  `MineWeaponId == 29`).
- **ShieldTest**: unchanged (seeker power 45 preserved). **FogTest**: expect pass as-is.
- **StrategyTest**: dev count 13→27; extend index-anchoring assert `[13]…[26]`.
- **Do not chase** (pre-existing): ContentTest garrison-vision golden, FogTest sector-leak,
  AutopilotTest ×3, CollisionTest ×4, CommanderTest time-seed flake.

## Sequencing (repo auto-commits+pushes — every stop must build)

Delegation: steps 1, 3, and the test-update halves of 4 are mechanical → Sonnet subagents with
this plan's tables as the spec; the projection/sim seams (E, C) and any balance judgment stay on
the main thread. Review each subagent's diff before moving on.

1. **Parser** (D8, delegable) → checkpoint: `--iron-slice` prints the ordnance table matching
   this plan.
2. **Projection** (change E) → checkpoint: build green (fields dormant, content unchanged).
3. **Content YAML** (expendables → launchers → techs → devs → hulls comments, delegable) + asset
   copies in one commit → checkpoint: server boots (CoreValidator/ContentValidator are
   boot-fatal), ContentTest/FactionsTest updated + green.
4. **Sim/client seams** (C, D) + LoadoutTest/MineTest/MissileTest additions (test half
   delegable) → checkpoint: full dotnet suite green, pre-existing failures unchanged.
5. **Runtime smoke**: `--autofly` (cargo path + spawn), then the `verify` skill — hangar shows
   tiered racks with locked rows; dumbfire quick-locks and tracks lazily; research a tier-2 dev
   and confirm rack auto-migration + dispenser tier resolution; bomber torpedo run on a base; new
   missile/mine/probe meshes render (check fallbacks; decide dn_ptminprx vs acs41 visually).

## Known accepted limitations
- Hangar cargo rows are tier-neutral (one pack entry per line; pack size can't vary per tier).
- One rack per ship effective (`MissileMountFor` takes the first Missile-kind mount — pre-existing).
- IGC hull magazine masks not enforced (open mount model is a tested feature; payload budget
  constrains abuse).
- Deferred IGC content (unimported bases/lines): LRM Hunter, Tac Nuke, XRM lines, EMP/Nerve Gas,
  LRM Killer/Swarm, Aleph Res, Mine Pack, EMP Mine, Hvy Counter, Pulse/Teleport probes, QL lines.
