---
name: hulls-weapons
description: Author and tune playable HULLS (ships), WEAPONS (guns), and LAUNCHERS/expendables (missiles, mines, decoys, probes) in server/Content/core. Use when adding or rebalancing a hull's stats/shield/afterburner/payload, mounting or swapping a weapon on a hull, tuning a cannon/missile/torpedo, budgeting payload-capacity vs default cargo, or debugging a CoreValidator/ContentValidator boot refusal about loadouts, mass, or win conditions. For HP_ node geometry/placement use the `hardpoints` skill; for wiring a brand-new authored FIELD end-to-end (model→wire→client) use the `tech-tree-content` skill.
---

# Configuring hulls, ships & weapons

All ship/weapon balance is **authored YAML, streamed at runtime** — no compile-time content, no
client fallback (see the `tech-tree-content` skill for the full pipeline & iron rules). This skill
is the hands-on reference for the content files that define what a ship *is* and *carries*.

## The files (all under `server/Content/core/`)

| File | Defines | Wire id |
|------|---------|---------|
| `hulls.yaml` | playable ships + escape pod: flight stats, payload, hardpoint bindings, default cargo, equipment slots (`allowed-parts`/`preferred-parts`), energy/ammo pools | `class-id` |
| `weapons.yaml` | guns (cannons): damage via projectile, cadence, spread, mass, `shield-damage-multiplier`, `ammo-per-shot`/`energy-per-shot` | `weapon-id` |
| `launchers.yaml` | missile racks + chaff/mine/probe dispensers: magazine (`amount`), cadence, mounted `mass`, referenced expendable | `weapon-id` |
| `expendables.yaml` | the payloads a launcher fires: missiles/mines/decoys/probes — ballistics, `mass`, `cargo-id`, `can-damage-base` — plus `fuels:` (fuel pods) and `ammo-packs:` (both pure cargo, no launcher; auto-load into the tank/magazine when it runs dry) | `cargo-id` (dispensed kinds) |
| `equipment.yaml` | the shield/afterburner/cloak PARTS a hull's `allowed-parts` may pick from — one per-ship slot each, no payload cost | `EquipmentDef.EquipmentId` (catalog position) |
| `stations.yaml` | bases/garrisons (`base-type-id`) — see `hardpoints` skill for their docking nodes | `base-type-id` |

The manifest `core.manifest.yaml` lists which files load; bump its `version:` when adding a file.

## The shared weapon-id namespace

`weapons.yaml` guns and `launchers.yaml` racks share **one** `weapon-id` space. A hull hardpoint's
`weapon-id` may resolve to either. Current stock ids (verify against the files — do not trust this
table blindly after edits):

| id(s) | thing | file | mounted mass |
|----|-------|------|--------------|
| 0/1/2 | PW Gat Gun 1/2/3 — the all-round gun | weapons | 1 |
| 9/10/11 | PW Mini-Gun 1/2/3 — rapid-fire | weapons | 1 |
| 12/13/14 | PW AutoCan 1/2/3 — heavy assault gun (AP: `shield-damage-multiplier` 0.5) | weapons | 1 |
| 15/16/17 | ER Nanite 1/2/3 — the **healing** gun | weapons | 2 |
| 3/18/19 | MRM Seeker 1/2/3 | launchers | 4 |
| 4/20/21 | MRM Quickfire 1/2/3 | launchers | 2 |
| 5/22/23 | SRM Anti-Base 1/2/3 (**`can-damage-base`**) | launchers | 4 |
| 24/25/26 | SRM Dumbfire 1/2/3 (quick-lock, low-turn) | launchers | 4 |
| 6/27/28 · 7/29/30 · 8/31/32 | counter / prox-mine / ews-probe dispenser tiers 1/2/3 (NOT hull-mounted — dispensed from cargo) | launchers | 0 |

Tier-2/3 ids (18–32) are the appended higher tiers of each line, chained via
`obsoleted-by-techs`/`successor-part-id`; dispenser tiers 2/3 carry no `cargo-id` (spawn resolution
walks the tier chain).

**Mounted mass is the launcher's own `mass`, not the missile's.** An anti-base rack costs 4 to
mount even though the srm-anti-base-1 expendable is mass 6 — the rack supplies its magazine
(`amount`) for free.

## Mounting a weapon on a hull

A hull hardpoint entry **binds** a weapon-id to a mesh `HP_Weapon_<index>` node:

```yaml
hardpoints:
  - { kind: weapon, index: 0, weapon-id: 2 }   # binds mesh HP_Weapon_0
  - { kind: weapon, index: 1, weapon-id: 2 }   # binds mesh HP_Weapon_1
  - { kind: weapon, index: 2, weapon-id: 5 }   # binds mesh HP_Weapon_2
```

- **Geometry comes from the mesh** — the muzzle position/direction is the GLB `HP_` node, NOT
  authored here. `off-*`/`dir-*` are the deliberate *override* knob only. **For anything about HP_
  node inventory, placement, or geometry overrides, use the `hardpoints` skill.**
- **`index` ordering matters**: keep guns at the low indices and racks after them, because the
  index is the per-barrel spread-seed the server and client both key off — reordering desyncs the
  spread pattern. Add new mounts at the end.
- An unbound mesh `HP_Weapon_*` node that NO yaml entry binds or types becomes **NonMountable** —
  NOT a loadout slot: hidden in the hangar, rejected by the server. To expose it as an empty
  ASSIGNABLE mount, author an entry with `mount:` (and no `weapon-id`).
- **Mount types** (loadout gate): every weapon mount has a type — **gun** (Bolt only), **missile**
  (racks only), **any**, or **non-mountable** (accepts nothing, hidden) — enforced identically by
  the hangar filter and the server's `ResolveLoadout` (`HardpointDef.MountAccepts`). Default derives
  from the bound weapon (gun → gun mount, rack → missile mount); an UNAUTHORED empty mesh mount →
  **non-mountable**. Author `mount: gun|missile|any` on the entry to type it — the only way to
  expose an EMPTY mount (mesh HP_ nodes carry no gun/missile distinction):
  `- { kind: weapon, index: 1, mount: missile }` (no `weapon-id` = empty, assignable in the hangar).
  A `mount:` contradicting the bound weapon, or a `successor-part-id` that would change a
  weapon's category at tier migration, refuses boot.

## Equipment slots (shield / afterburner / cloak)

Unlike guns/racks, these three are NOT hardpoint mounts — every hull gets at most one of each,
picked in the hangar from `equipment.yaml`'s catalog (shields, then afterburners, then cloaks; a
part's `EquipmentId` is its position in that order — append within a section, never reorder).

- **`allowed-parts`** (hulls.yaml, keys `shield`/`afterburner`/`cloak`) is what gives a hull a slot
  at all — an omitted key means the hull has NO such slot (e.g. the Lt Interceptor has no `shield`
  key, so it has no shield, ever). A listed part's whole successor chain
  (`obsoleted-by-techs`/`successor-part-id`) is IMPLICITLY allowed too, so listing just `sm-shield-1`
  is enough to also permit `sm-shield-2`/`-3` once researched:
  ```yaml
  allowed-parts:
    shield: [sm-shield-1]
    afterburner: [booster-1, lt-booster-1, crs-booster]
  ```
- **`preferred-parts`** (a flat list, IGC `preferredPartsTypes` order) picks each slot's STATIC
  default: the first entry the slot allows whose `required-techs` the HULL ITSELF already requires
  (so the default is always buildable the moment the hull is — a research-locked preferred part is
  skipped, never handed out free). A researched tier then migrates that default live at spawn — no
  YAML change needed when a new tier lands.
- **No payload cost, no flight-mass cost.** `mass` in `equipment.yaml` is a hangar DISPLAY number
  only — equipment never counts against `payload-capacity` and never changes a ship's `mass`.
- **Energy + ammo pools** are hull fields, not equipment: `max-energy`/`energy-recharge-rate`
  (drawn by energy guns and a cloak) and `max-ammo` (drawn by ammo guns, refilled by ammo packs)
  live in `hulls.yaml` beside `allowed-parts`. A cloak slot needs `max-energy > 0` — boot refuses
  otherwise.
- **Per-shot costs on `weapons.yaml`**: `ammo-per-shot` (u16) and/or `energy-per-shot` (float) per
  gun. Keep Allegiance's drain PER SECOND at our cadence: `cost = IGC cost × fire-interval-ticks ÷
  (20 × IGC dtimeBurst)`, rounded UP to a whole round for ammo. A shot the ship's pools can't cover
  doesn't fire and doesn't stamp cooldown — CoreValidator/ContentValidator refuse a default loadout
  whose cheapest gun can't afford one shot from a full pool on its hull.
- **REMOVED / tombstoned hull keys** — `shield-capacity`/`-recharge`/`-delay` and
  `ab-accel`/`-on-rate`/`-off-rate`/`-fuel-drain` moved onto the equipment parts (`max-strength`/
  `regen-rate`/`recharge-delay` on a shield; `max-thrust`/`on-rate`/`off-rate`/`fuel-consumption` on
  an afterburner). They survive on `Hull.cs` only as nullable properties CoreValidator REFUSES when
  set (a boot error naming the key), so an old bundle that still authors them fails loudly instead of
  silently flying with no shield/boost. `max-fuel` (the tank) and `ab-fuel-recharge` stay hull
  fields.

## Payload budgeting (boot gate — `CoreValidator`)

Every armed hull must satisfy, or the server **refuses to boot**
(`CoreValidator.cs` `ValidateHulls` payload budget, throw ~L135: "authored default loadout payload … exceeds payload-capacity"):

```
sum(mounted weapon/launcher mass)  +  sum(default-cargo count × expendable mass)   ≤   payload-capacity
```

Stock expendable masses: prox-mine 1, counter 1, ews-probe 2, fuel-pod 1, mrm-seeker 4,
mrm-quickfire 3, srm-dumbfire 4, srm-anti-base 6 (only the `default-cargo`-dispensed items count here —
magazine ammo rides free inside its launcher). Fuel pods additionally require the hull to model fuel
(`max-fuel > 0`) — authoring them on a fuel-less hull refuses boot. When you add or up-mass a weapon, **either raise `payload-capacity` to keep
the existing default cargo, or trim `default-cargo`**. Keep the inline `# math …` comment in sync —
it's the reviewer's check.

`cargo-capacity` (int, 0..255, default 0) is a SEPARATE budget: hold SLOTS for loose salvage the hull
can't equip (a gun with no free mount, foreign-rack rounds, a pack past the payload budget, a fuel pod
with no tank). One slot per part or per consumable stack; hold contents cost no payload, re-drop on
death and are lost on dock. 0 = no hold — the hull ricochets what it can't use. Stock: scout/
lt-interceptor 2, enh/adv fighter 3, bomber 4, devastator 5. Not validated against the loadout
(nothing in it is equipped). GLOSSARY "Cargo hold (cargo-capacity)".

## Boot-time invariants that bite

- **Win condition** (`shared/ContentValidator.cs` ~L330-348): at least one hull's *default* loadout must
  mount a `can-damage-base` weapon, else "bases can never be destroyed, matches can never end" and
  boot fails. Today only the **SRM Anti-Base line (weapon-ids 5/22/23)** is `can-damage-base`, and
  only the **bomber** mounts it (id 5) — do not strip it without giving another hull a base-cracker.
- **Shield / afterburner stats live in `equipment.yaml` now** (see *Equipment slots* above) — a
  shield part needs a positive `regen-rate` (else it never comes back) and `recharge-delay >= 0`
  (0 = Allegiance's continuous regen); an afterburner part needs `max-thrust > 0` (refuses the
  never-ported Retro Booster) and `fuel-consumption > 0`.
- **Afterburner fuel pairing**: a hull with an `afterburner` key in `allowed-parts` MUST author
  `max-fuel > 0` (and vice-versa); `ab-fuel-recharge` must stay BELOW the lowest `fuel-consumption`
  of any allowed booster (never net-refills in flight). `ab-fuel-recharge: 0` = valid "dock-only"
  refuel (the stock value everywhere).
- **Cloak**: a `cloak` key in `allowed-parts` requires `max-energy > 0`; the part itself needs
  `0 < max-cloaking < 1` and positive `on-rate`/`off-rate`.
- **Every hardpoint** needs a mesh node OR authored geometry; a zero-length direction, a duplicate
  `(kind,index)`, or a dangling `weapon-id` all fail boot.
- **`radar-signature` must be positive** on ships and bases.

## Hull flight-stat derivation

YAML → runtime `ShipClassDef` (see `hulls.yaml` header comment): `mass`→mass, `speed`→max-speed,
`thrust`→accel, `max-turn-rates`→rate-*-deg, `armor-hit-points`→max-hull,
`strafe/reverse-thrust-multiplier`→side/back-mult. `class-id`, `drift-*-deg`, `ab-fuel-recharge`,
`max-energy`/`energy-recharge-rate`/`max-ammo`, `allowed-parts`/`preferred-parts`, vision-*, and
`hardpoints` are explicit runtime extensions.

## Launch/dock base restriction (`launch-station-classes`)

`launch-station-classes: [shipyard]` (list of station `class:` keywords from stations.yaml) restricts
which base CLASSES the hull may **launch from and dock at**. Omitted = anywhere (every hull but the
Devastator). A restricted hull bounces off other friendly bases like an enemy one, and at an allowed
base enters only through the **largest docking door** (side doors stay small-ship-only). Projected to
`ShipClassDef.LaunchClassMask` (u16 bitmask over `StationClassId`); shared rules live in
`shared/Collision/DockRules.cs`, server gate in `Simulation.TryResolveLaunchSite` (spawn, pre-charge
reject) + `ResolveOwnBaseDock` (dock). An unknown keyword fails YAML enum parse at boot; authoring it
on a hull with no `class-id` is a `CoreValidator` error. Distinct from `required-techs` (team-wide
unlock) — this gates WHERE, not WHETHER.

## Verify

```sh
dotnet run --project tests/ContentTest      # projection + merged hardpoints + payload + win-condition
dotnet run --project tests/FactionsTest     # raw YAML field parsing
dotnet run --project tests/EquipmentTest    # equipment slots, defaults, tier migration, loadout rows
dotnet run --project tests/AmmoEnergyTest   # ammo/energy pools, per-shot gating, PIG rearm
```

`tests/ContentTest/Program.cs` asserts per-hull merged layouts and payload capacities — **update
those assertions when you change a hull's weapons or capacity** (they pin exact weapon-ids, mount
counts, and `PayloadCapacity` floats). For a live in-client check, use the `verify` skill
(server + autofly capture). If you added a new authored *field* (not just values), follow the
`tech-tree-content` end-to-end checklist and bump the wire protocol — the single
`Wire.ProtocolVersion` constant (`shared/Net/Wire.cs`), which both `Protocol.Version` and
`GameNetClient.ProtocolVersion` alias.
