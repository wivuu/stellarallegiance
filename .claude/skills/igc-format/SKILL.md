---
name: igc-format
description: "Read, decode, and reason about Allegiance binary static-core files (.igc) — the packed game database of factions, ships (hulls), parts/weapons, stations, developments (tech), drones, and expendables. Use whenever a task involves an .igc file, extracting a faction/ship/weapon/tech roster, resolving what a faction can build, or understanding how Allegiance loads static game data. Cores live in artwork-full/*.igc."
---

# Allegiance .igc static-core format

An `.igc` static core is Allegiance's **compiled game database**: every faction, ship, part,
station, tech/development, drone, and expendable, packed as **raw memcpy'd MSVC C-structs**.
There is no SQL/XML in the repo — only the compiled binary + the C++ loader. Cores are in
`artwork-full/*.igc` (canonical default: `static_core.igc`, `#define IGC_STATIC_CORE_FILENAME`).
The struct definitions are the ground truth in `src/Igc/igc.h`; the loader is
`src/Igc/missionigc.cpp`. See also `FACTION-AND-TECH-TREE-FORMAT.md` in the repo root.

## Fastest path: use the bundled parser

`igc_parser.py` (next to this file) is a validated, dependency-free decoder.

```bash
python3 igc_parser.py <core.igc>                     # version, object counts, civ list
python3 igc_parser.py <core.igc> --dump hulls        # hulls|parts|stations|devs|drones|civs|missiles|mines|chaff|probes
python3 igc_parser.py <core.igc> --faction "Iron Coalition"   # resolve that faction's buildable roster
python3 igc_parser.py <core.igc> --iron-slice                 # raw + anchor-translated combat-stat report
```

**Combat stats** (added 2026-07-16). `parse_hull` now also decodes `mass/signature/speed/maxTurnRates[3]/
turnTorques[3]/thrust/sideMult/backMult/scannerRange/maxFuel/ecm/length/maxEnergy/rechargeRate/ripcordSpeed/
ripcordCost/maxAmmo/hitPoints/defenseType/capacity*` (derived offsets rel. BUY=364, validated against the
already-anchored `hullID@+82`/`habm@+130`/540-B sizeof). `parse_part` appends the `DataWeaponTypeIGC` tail
for `equipmentType==1` guns (`dtimeReady@+32/dtimeBurst/energyPerShot/dispersion/cAmmoPerShot/projectileTypeID`).
New `parse_projectile` (ObjectType 22, a `DataObjectIGC`-derived struct, **not** a buyable, no name — join
weapons via `projectileTypeID` → projectile `projID@72`): `power/blastPower/blastRadius/speed/lifespan/width/
radius/damageType`. `parse_station` adds `signature/maxArmor/maxShield/armorRegen/shieldRegen/scannerRange/radius`.
The `--iron-slice` mode resolves the Iron Coalition roster and prints the Phase-2 hull/gun/station/dev/ordnance
picks with RAW IGC values plus anchor-translated core-bundle YAML fields (Enh Fighter↔`fighter`, PW Gat Gun 1↔
`fighter-cannon`, Garrison armor 20000↔2000, price ×0.06). Two gotchas surfaced: sustained fire cadence is
`dtimeBurst` (weaponIGC.cpp:310), not the uniform-0.25 `dtimeReady`; and ER Nanite projectiles carry **negative
power** (the heal, later modeled as an explicit `is-healing` flag).

**Ordnance** (added 2026-07-18). `parse_missile/mine/chaff/probe` fully decode `DataExpendableTypeIGC`-family
records (ObjectType 23/24/25/26): shared launcher-buyable fields (price/model/name/req/eff, embedded LauncherDef
at record offset 64) plus type-specific tails at offset 464 — missile `acceleration/turnRate/initialSpeed/
lockTime/readyTime/maxLock/chaffResistance/dispersion/lockAngle/power/blastPower/blastRadius/width/damageType`,
mine `radius/power/endurance/damageType`, chaff `chaffStrength`, probe `scannerRange/dtimeBurst/dispersion/
accuracy/ammo/projectileTypeID/dtRipcord`. `parse_part` now also decodes magazine records (partType, size<100,
`DataLauncherTypeIGC`): `amount/partID/successorPartID/launchCount/expendableTypeID`, joined to an expendable via
`expendableTypeID`. `resolve_faction` gates missiles/mines/chaff/probes the same as parts (`req ⊆ localUltimate`).
The `--iron-slice` ORDNANCE section prints raw + judgment-translated stats (documented in the `_O` header
comment) for the Iron roster's missiles/mines/chaff/probes plus their magazine amount/launchCount/successor chain.

Import it as a module for custom work: `read_records`, `parse_all`, `parse_buyable`,
`parse_hull/part/station/dev/drone/civ`, `parse_missile/mine/chaff/probe`, `mask_bits`, `resolve_faction`.

## The format (why the parser is written the way it is)

**File** = `[int32 version][int32 totalDataSize][records…]` (version is a unix build time).
**Each record** = `[int16 ObjectType][int32 size][size bytes of payload]`. The payload is
exactly one `Data…IGC` struct, copied verbatim. Walk records by advancing `6 + size` bytes.

**ObjectType** ids (igc.h:120-187), static-core subset: 22 projectileType · 23 missileType ·
24 mineType · 25 probeType · 26 chaffType · **27 civilization** · 28 treasureSet ·
**29 hullType** · **30 partType** · **31 stationType** · **32 development** · **33 droneType** ·
34 constants.

**Scalar sizes** (igc.h typedefs): `ObjectID` and every `*ID`/`SoundID` = **int16**;
`Money` = int32; `HitPoints`/all stats = float32; `Mount`/`BuyableGroupID`/`StationClassID`/
`PilotType`/`DefenseTypeID` = 1 byte; `PartMask`/`EquipmentType`/`AbilityBitMask` = int16.

**Tech masks** — `TechTreeBitMask` = `TLargeBitMask<400>` = **50 raw bytes**. Bit `i` lives in
byte `i>>3`, mask `0x80 >> (i&7)` (**MSB-first** within each byte). `GlobalAttributeSet` = **25
float32** (100 bytes), each a multiplier defaulting to 1.0.

**`DataBuyableIGC`** is the base of hull/part/station/development/drone and has **fixed offsets**
(so name + price + tech masks decode without knowing the subtype):

| field | off | type |
|---|---|---|
| price | 0 | int32 |
| timeToBuild | 4 | uint32 |
| modelName[13+1] | 8 | char |
| iconName[13] | 22 | char |
| name[25] | 35 | char |
| description[201] | 60 | char |
| groupID | 261 | char |
| ttbmRequired[50] | 262 | tech mask |
| ttbmEffects[50] | 312 | tech mask |
| **sizeof** | **364** | (align 4) |

**Derived structs start at offset 364** — MSVC does *not* pack derived members into the base's
tail padding. Key derived offsets (relative to 364), all validated:
- **Hull** (+82 hullID, +84 successor, +86 maxWeapons, +87 maxFixed, +130 habm, +146 pmEquipment[8]).
  A variable **`HardpointData[]` tail follows the 540-byte struct**, 30 bytes each, `PartMask` at
  +26, `bFixed` at +28. Hardpoint count == maxWeapons; a weapon fits a hardpoint when
  `weapon.partMask & hardpoint.partMask`. (`pmEquipment[ET_Weapon]` is *not* how guns attach.)
- **Part** (+0 mass, +8 partID, +10 successor, +12 equipmentType, +14 partMask). Magazines &
  dispensers are instead tiny `DataLauncherTypeIGC` (~24 B, **no** buyable base) — detect by `size<100`.
- **Station** (+24 income, +32 ttbmLocal[50], +82 stationID, +84 successor, +88 sabm, +90 aabm,
  +92 classID, +94 constructionDrone).
- **Development** (+0 gas[25], +100 devID). `techOnly` is derived, not stored: true iff all 25 gas
  == 1.0 (developmentigc.cpp:38); a techOnly dev is obsolete once its effects ⊆ owned techs.
- **Drone** (+12 pilot, +14 hullTypeID, +16 droneID).
- **Civilization** (standalone, not a buyable): incomeMoney f32 @0, bonusMoney f32 @4, name[25] @8,
  iconName @33, hudName @46, ttbmBaseTechs @59, ttbmNoDevTechs @109, gasBaseAttributes[25] @160,
  lifepod i16 @260, civID @262, initialStation @264.
- **Expendable** name is at offset **99** (DataObjectIGC 52 B + loadTime/lifespan/signature 12 B +
  LauncherDef's DataBuyableIGC, name at +35).

## The tech tree is a bitset, not a graph

There is no stored tree. Every buyable declares `ttbmRequired` and `ttbmEffects` (400-bit sets).
"Available" ⟺ `required ⊆ owned`; completing an item OR-s its effects into owned. A **faction**
(`DataCivilizationIGC`) only stores starting tech-bits + a starting station + lifepod + stat
multipliers; it connects to the shared catalog *indirectly* through those bits. Each of the 5
canonical factions (Bios, Belters, Gigacorp, Iron Coalition, Rixian) owns a unique seed bit
(20/97/18…; Iron = **21**) and a duplicated station/ship block (Iron = station ids `1xx`).

**Resolving a faction's roster** replicates `CsideIGC::CreateBuckets` (`src/Igc/sideigc.cpp`),
done by `resolve_faction()`:
1. seed = civ base-techs **+ the four path bits {1,2,3,4}** (Shipyard/Expansion/Tactical/Supremacy
   Allowed — set from mission params at `missionigc.cpp:3874`, all paths enabled in a normal game;
   they are *not* granted by any object, so you must add them).
2. OR in every part & station effect (assume anything capturable), then fixed-point over
   developments (`req ⊆ ultimate → ult |= eff`).
3. An item is available when `req ⊆ ultimate`. Faction identity holds because only the civ grants
   its seed bit, which gates that faction's development chain.

Note: `ttbmNoDevTechs` applies **only when developments are disabled** — don't fold it into the
normal (developments-on) starting set.

## Gotchas

- Files are ISO-8859/CRLF; decode strings as latin-1 and stop at the first NUL.
- Some cores extend `StationClassID` past 7 (custom mines) and use extra `aabm` bits (0x20/0x40)
  for custom minerals — label unknowns generically.
- Many `.igc` files are *map* cores; `static_core.igc` is the clean 5-faction static catalog.
- `zone_core` is an encrypted variant (munge callback, `IGC_ENCRYPT_CORE_FILENAME`).
