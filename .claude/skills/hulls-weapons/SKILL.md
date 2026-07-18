---
name: hulls-weapons
description: Author and tune playable HULLS (ships), WEAPONS (guns), and LAUNCHERS/expendables (missiles, mines, decoys, probes) in server/Content/core. Use when adding or rebalancing a hull's stats/shield/afterburner/payload, mounting or swapping a weapon on a hull, tuning a cannon/missile/torpedo, budgeting payload-capacity vs default cargo, or debugging a CoreValidator/ContentValidator boot refusal about loadouts, mass, or win conditions. For HP_ node geometry/placement use the `hardpoints` skill; for wiring a brand-new authored FIELD end-to-end (model→wire→client) use the `tech-tree-content` skill.
---

# Configuring hulls, ships & weapons

All ship/weapon balance is **authored YAML, streamed at runtime** — no compile-time content, no
client fallback (see the `tech-tree-content` skill for the full pipeline & iron rules). This skill
is the hands-on reference for the four content files that define what a ship *is* and *carries*.

## The files (all under `server/Content/core/`)

| File | Defines | Wire id |
|------|---------|---------|
| `hulls.yaml` | playable ships + escape pod: flight stats, shield, afterburner, payload, hardpoint bindings, default cargo | `class-id` |
| `weapons.yaml` | guns (cannons): damage via projectile, cadence, spread, mass, `shield-damage-multiplier` | `weapon-id` |
| `launchers.yaml` | missile racks + chaff/mine/probe dispensers: magazine (`amount`), cadence, mounted `mass`, referenced expendable | `weapon-id` |
| `expendables.yaml` | the payloads a launcher fires: missiles/mines/decoys/probes — ballistics, `mass`, `cargo-id`, `can-damage-base` | `cargo-id` (dispensed kinds) |
| `stations.yaml` | bases/garrisons (`base-type-id`) — see `hardpoints` skill for their docking nodes | `base-type-id` |

The manifest `core.manifest.yaml` lists which files load; bump its `version:` when adding a file.

## The shared weapon-id namespace

`weapons.yaml` guns and `launchers.yaml` racks share **one** `weapon-id` space. A hull hardpoint's
`weapon-id` may resolve to either. Current stock ids (verify against the files — do not trust this
table blindly after edits):

| id | thing | file | mounted mass |
|----|-------|------|--------------|
| 0 | scout-cannon | weapons | 2 |
| 1 | fighter-cannon | weapons | 5 |
| 2 | bomber-cannon (AP: `shield-damage-multiplier` 0.5) | weapons | 11 |
| 3 | seeker-rack-1 (mrm-seeker-1) | launchers | 4 |
| 4 | quickfire-rack-1 (mrm-quickfire-1) | launchers | 2 |
| 5 | anti-base-rack-1 (srm-anti-base-1 — **`can-damage-base`**) | launchers | 4 |
| 6/7/8 | counter / prox-mine / ews-probe dispenser tier 1 (NOT hull-mounted — dispensed from cargo) | launchers | 0 |
| 18–26 | rack tiers 2/3 + dumbfire-rack-1/2/3 (`obsoleted-by-techs`/`successor-part-id` chains) | launchers | 4 or 2 |
| 27–32 | dispenser tiers 2/3 (no cargo-id — spawn resolution walks the tier chain) | launchers | 0 |

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

## Payload budgeting (boot gate — `CoreValidator`)

Every armed hull must satisfy, or the server **refuses to boot**
(`CoreValidator.cs` ~L44-82, "authored default loadout payload … exceeds payload-capacity"):

```
sum(mounted weapon/launcher mass)  +  sum(default-cargo count × expendable mass)   ≤   payload-capacity
```

Stock expendable masses: prox-mine 1, counter 1, ews-probe 2, mrm-seeker 4, mrm-quickfire 3,
srm-dumbfire 4, srm-anti-base 6 (only the `default-cargo`-dispensed items count here — magazine ammo rides free
inside its launcher). When you add or up-mass a weapon, **either raise `payload-capacity` to keep
the existing default cargo, or trim `default-cargo`**. Keep the inline `# math …` comment in sync —
it's the reviewer's check.

## Boot-time invariants that bite

- **Win condition** (`shared/ContentValidator.cs` ~L199): at least one hull's *default* loadout must
  mount a `can-damage-base` weapon, else "bases can never be destroyed, matches can never end" and
  boot fails. Today only the **SRM Anti-Base line (weapon-ids 5/22/23)** is `can-damage-base`, and
  only the **bomber** mounts it (id 5) — do not strip it without giving another hull a base-cracker.
- **Shield**: authoring `shield-capacity` requires a positive `shield-recharge` (else it never comes
  back). `shield-delay` is the quiet-time before regen resumes.
- **Afterburner**: `ab-accel > 0` requires `max-fuel` (and vice-versa); `ab-fuel-recharge` must be
  `< ab-fuel-drain` (never net-depletes). `ab-fuel-recharge: 0` = valid "dock-only" refuel.
- **Every hardpoint** needs a mesh node OR authored geometry; a zero-length direction, a duplicate
  `(kind,index)`, or a dangling `weapon-id` all fail boot.
- **`radar-signature` must be positive** on ships and bases.

## Hull flight-stat derivation

YAML → runtime `ShipClassDef` (see `hulls.yaml` header comment): `mass`→mass, `speed`→max-speed,
`thrust`→accel, `max-turn-rates`→rate-*-deg, `armor-hit-points`→max-hull,
`strafe/reverse-thrust-multiplier`→side/back-mult. `class-id`, `drift-*-deg`, `ab-*`, vision-*, and
`hardpoints` are explicit runtime extensions.

## Verify

```sh
dotnet run --project tests/ContentTest      # projection + merged hardpoints + payload + win-condition
dotnet run --project tests/FactionsTest     # raw YAML field parsing
```

`tests/ContentTest/Program.cs` asserts per-hull merged layouts and payload capacities — **update
those assertions when you change a hull's weapons or capacity** (they pin exact weapon-ids, mount
counts, and `PayloadCapacity` floats). For a live in-client check, use the `verify` skill
(server + autofly capture). If you added a new authored *field* (not just values), follow the
`tech-tree-content` end-to-end checklist and bump both protocol-version constants.
