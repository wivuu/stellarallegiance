# Configurable special-rock class weights (per-map + world default)

## Context

The Supremacy tech branch (Enh/Adv Fighter, gat-2/3, autocan-2/3, dev-upgrade-supremacy) gates on
building a **Supremacy Center**, which requires a constructor to build on a **Carbonaceous** rock
(`server/Content/core/stations.yaml:97` → `build-on-rock-class: carbonaceous`). But which special
class a rock becomes is chosen by a hardcoded uniform draw:

```
server/Sim/World.cs:636-637
RockClass cls = isSpecial[i] ? (RockClass)(byte)(hash[i] % 3) : RockClass.Regolith;
//                             0=Carbonaceous, 1=Silicon, 2=Uranium — uniform
```

With stock `special-per-sector: 1` and `home-special-chance: 0.0`, a 3-sector map like Brimstone
Gambit seeds exactly one special rock (in the single neutral sector), Carbonaceous only ~1/3 of the
time. Silicon/Uranium gate no station, so 2/3 of specials are "wasted" and the whole Supremacy branch
is unreachable ~2/3 of matches. There is no way for a map author to influence the class.

**Goal:** let map authors (and the world default) configure the special-rock class distribution per
sector, so a map can say e.g. *"Defaultio's special is always Carbonaceous"* or *"50% Carbonaceous /
25% Silicon / 25% Uranium"*. Reachability is **not** enforced — per the user, "if you can't build a
station because the rock doesn't spawn on that map, that's how the game goes sometimes." Silicon and
Uranium stay as cosmetic variety in un-authored sectors (the default distribution is unchanged).

## Approach

Add a **`special-weights`** weighted distribution over the three special classes (Carbonaceous /
Silicon / Uranium), authorable at two levels:

- **World default** — `world.yaml` `seeding:` block. Omitted → uniform (equal weights) = today's
  exact behavior.
- **Per-sector override** — a map's `sectors[]` entry. Wins over the world default for that sector.

`special-count` (already exists) still controls **how many** special rocks a sector gets;
`special-weights` controls **which class** each becomes. The draw stays deterministic and reuses each
rock's existing per-rock `hash` — it never touches the shared world-gen RNG stream (so layout stays
byte-identical for a seed; the MiningTest canary still passes).

Example (`brimstone-gambit.yaml`, Defaultio):
```yaml
  - id: 2
    name: Defaultio
    special-weights:
      carbonaceous: 1.0     # this sector's special is always Carbonaceous
```
Mixed example:
```yaml
    special-count: 2
    special-weights:
      carbonaceous: 0.5
      silicon: 0.25
      uranium: 0.25
```

### The weighted pick (deterministic, byte-stable default)

New type in `shared/Defs.cs`, `SpecialWeights { float Carbonaceous=1, Silicon=1, Uranium=1 }` with:

```csharp
public bool AnyPositive => Carbonaceous > 0 || Silicon > 0 || Uranium > 0;
// Equal positive weights reproduce the historical hash%3 draw EXACTLY — keeps existing pinned
// seeds' rock classes/variants byte-stable when nothing is authored.
private bool IsLegacyUniform => Carbonaceous == Silicon && Silicon == Uranium && Carbonaceous > 0;

public RockClass Pick(ulong hash)
{
    if (IsLegacyUniform) return (RockClass)(byte)(hash % 3);
    // Cumulative-weight draw over a fixed integer quantization — pure integer math from the
    // per-rock hash, so it is fully deterministic. A zero-weight class gets a zero-width bucket
    // and is never chosen.
    const long Scale = 1_000_000;
    long wc = (long)(Carbonaceous * Scale), ws = (long)(Silicon * Scale), wu = (long)(Uranium * Scale);
    long sum = wc + ws + wu;                 // > 0 guaranteed by load-time validation
    long r = (long)(hash % (ulong)sum);
    return r < wc ? RockClass.Carbonaceous : r < wc + ws ? RockClass.Silicon : RockClass.Uranium;
}
```

## Files to change

**1. `shared/Defs.cs`**
- Add the `SpecialWeights` class above (with `Pick`, `AnyPositive`).
- `WorldSeedingTuning` (~line 720): add `public SpecialWeights SpecialWeights = new();` (default
  uniform), next to `SpecialPerSector` / `HomeSpecialChance`.
- `WorldSectorConfig` (~line 440): add `public SpecialWeights? SpecialWeights;` next to
  `SpecialCount` (null → world default).

**2. `server/Sim/World.cs` (`AssignOre`, ~line 636)**
- Resolve `var weights = sc?.SpecialWeights ?? _seed.SpecialWeights;` once per sector.
- Replace the hardcoded pick with `RockClass cls = isSpecial[i] ? weights.Pick(hash[i]) : RockClass.Regolith;`.

**3. `server/Content/WorldLoader.cs`**
- `WorldSeedingDef` (~line 389): add a `SpecialWeightsDef? SpecialWeights { get; set; }` DTO
  (`double? Carbonaceous/Silicon/Uranium`).
- Projection (~line 626, next to `SpecialPerSector`): if present, build a `SpecialWeights` (missing
  key → 0), **validate** all ≥ 0 and `AnyPositive` (else throw `InvalidDataException`, fail-fast like
  the rest of the loader), assign onto `t.SpecialWeights`.

**4. `server/Content/MapLoader.cs`**
- `MapSectorDef` (~line 66, next to `SpecialCount`): add `SpecialWeightsDef? SpecialWeights` (reuse
  the same DTO shape — either share it from WorldLoader or a small local DTO).
- `ApplyTo` (~line 241): project `s.SpecialWeights` onto `WorldSectorConfig.SpecialWeights` with the
  same non-negative / at-least-one-positive validation (throw on bad input).

**5. Schemas — regenerate, do not hand-edit** (`schemas/map.schema.json`, `schemas/world.schema.json`
have `additionalProperties:false`, so the new keys must be added). Run:
```
dotnet run --project server -- --gen-schemas schemas
```
(Program.cs:39 emits from `MapDef` / `WorldDef`.) Verify the diff only adds `special-weights`.

**6. Content authoring**
- `server/Content/core/world.yaml` (`seeding:` block ~line 148): add a **commented** `special-weights`
  example documenting the default (uniform) and the per-sector override, matching the file's
  existing comment style. No behavior change (omitted = uniform).
- `server/Content/maps/brimstone-gambit.yaml`: author Defaultio (id 2) with
  `special-weights: { carbonaceous: 1.0 }` so the default map always offers the Supremacy branch.
  Also extend the sector-field doc comment at the top of the file to mention `special-weights`.
  (The other 3 stock maps — kestrel-cross, serpents-reach, vesper-crown — are left at the uniform
  default per "that's how the game goes"; note in the plan they can opt in the same way.)

**7. `tests/ConstructorTest/Program.cs`** — add weighted-seeding coverage (a real regression test for
the feature). Build a small `WorldConfig` with one non-home sector, `SpecialCount = 1`, and:
- `SpecialWeights { Carbonaceous=1, Silicon=0, Uranium=0 }` → across several seeds, assert the single
  special is **always** Carbonaceous.
- `Silicon=1` only → always Silicon.
- `Carbonaceous=1, Silicon=1, Uranium=0` → across many seeds, assert both C and S appear and U never
  does.
Leave the existing 40-seed default-path scan as-is (it still validates the uniform path). Reuse
`FindRock` / the world-build helpers already in the file.

**8. `server/Program.cs` (optional, non-fatal)** — after `maps` load (~line 224), log an
**informational warning** (not a boot failure) for any shipped map that cannot guarantee a buildable
station's required class (a non-Regolith `BuildRockClass` with no sector whose resolved
`special-weights` gives it positive weight *and* `special-count > 0`). Purely diagnostic — boot still
proceeds. Skip if it adds meaningful complexity; the user explicitly does not want reachability
enforced.

## Non-goals / notes
- **No wire/proto bump.** Seeding + weights are server-only; the resolved `RockClass` byte already
  streams per-asteroid (`Protocol.cs:442`). Nothing new crosses the wire.
- **Silicon/Uranium kept as cosmetic default** — the uniform default path is unchanged (`Pick` returns
  `hash % 3` for equal weights), so un-authored sectors behave exactly as today.
- **`special-count` + `special-weights` compose**: count = how many, weights = which class. Both may
  be set on one sector.

## Verification
1. `dotnet run --project server -- --gen-schemas schemas` → confirm only `special-weights` added to
   the two schemas.
2. Build server + shared: `dotnet build server`.
3. Run the seeding tests: `dotnet run --project tests/ConstructorTest` and
   `dotnet run --project tests/MiningTest` (the determinism canary must stay green).
4. Boot with the default map and a few pinned seeds
   (`dotnet run --project server -- --seed 1 --anonymous` … , holding a `--server --anonymous`
   connection so the sim ticks per the headless-sim-testing note) and confirm via logs / a client
   that Defaultio's special rock is Carbonaceous every time; confirm an un-authored map still rolls
   mixed C/S/U.
5. Full suite spot-check: `ContentTest`, `FogTest` (pre-existing FogTest sector-leak failure is
   unrelated per the test-suite baseline memory).
