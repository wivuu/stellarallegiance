# Allegiance Faction Model (C#)

A clean, idiomatic C# re-modeling of Allegiance's faction & tech-tree data, serializable to
human-readable YAML. It is a homage-game data layer derived from the original C++ structures
(see `../FACTION-AND-TECH-TREE-FORMAT.md` for the source mapping).

## What it models

A **`Core`** is a complete static data set: a shared catalog of buyables plus the factions that
draw on it.

- **`Faction`** — economy, starting techs, baseline stat modifiers, lifepod hull, start station
  (was `Civilization`).
- **`Team`** — a team in a match that selects a faction and accumulates owned techs (was `Side`).
- **`Buildable`** base → **`Hull`** (ship), **`Part`** (`Weapon`/`Shield`/`Cloak`/`Afterburner`/
  `AmmoPack`/`Launcher`), **`Station`** (building), **`Development`** (research), **`Drone`**.
- **`Expendable`** → `Missile`/`Mine`/`Chaff`/`Probe`; plus `Projectile`.
- **`Capability`** — a closed enum of engine-checked gates (`base`, `shipyard-allowed`,
  `expansion-allowed`, `tactical-allowed`). These are the things *code* branches on to decide what a
  team may do, so they are strongly typed rather than free strings. Carried as `CapabilitySet`
  (`requiredCapabilities`/`grantedCapabilities`/`baseCapabilities`).
- **`TechSet`** — a set of open-ended, authorable research-tech ids (e.g. `heavy-hulls`,
  `cloak-tech`), replacing the original 400-bit mask. Data-driven unlocks resolved purely by the
  rule `requiredTechs ⊆ ownedTechs` — no special code logic. Capabilities and techs both feed the
  same forward-closure resolution; a buildable becomes available only when *both* its required
  capabilities and required techs are owned.
- **`AttributeModifiers`** — 25 `GameAttribute` multipliers that stack multiplicatively.

## Layout

```
src/Allegiance.Factions      class library (model + serialization + resolution + validation)
src/Allegiance.Factions.Cli  validate / roundtrip CLI
tests/Allegiance.Factions.Tests
sample-data/                 a two-faction bundle: a manifest + shared catalog files + factions/
```

## Usage

```csharp
var core = CoreSerializer.Load("sample-data/core.manifest.yaml");

var result = CoreValidator.Validate(core);            // referential integrity
var reachable = TechResolver.ResolveReachable(core, faction);  // -> TechState (techs + capabilities)
var buildables = BuildableResolver.GetBuildables(core, reachable);
var stats = AttributeResolver.Resolve(faction, completedDevelopments);

CoreSerializer.Save(core, "out/");                    // write split files + manifest
```

CLI:

```
dotnet run --project src/Allegiance.Factions.Cli -- validate  sample-data/core.manifest.yaml
dotnet run --project src/Allegiance.Factions.Cli -- roundtrip sample-data/core.manifest.yaml
dotnet run --project src/Allegiance.Factions.Cli -- schema --output .vscode/allegiance-core.schema.json
```

## JSON schema for editing

`schema` emits JSON Schemas (draft 2020-12) describing the data model, using the same kebab-case
keys/enum values as the YAML serializer. Because the VS Code YAML extension can't resolve
`#/$defs/...` sub-schema fragments, it writes three self-contained files next to the `--output`
path — `allegiance-core.schema.json`, `allegiance-faction.schema.json`, and
`allegiance-manifest.schema.json`. They are checked in under `.vscode/` and wired up in
`.vscode/settings.json` so the extension validates and autocompletes the `sample-data/` files
(catalog fragments → core, `factions/*.yaml` → faction, the manifest → manifest).

The schema is generated from the model, so don't edit it by hand — regenerate it with the command
above. The **Regenerate Faction JSON Schema** GitHub Actions workflow
(`.github/workflows/regenerate-faction-schema.yml`) does this automatically on changes to the model
or CLI and commits the result.

## Build & test

```
dotnet test
```

> Note: `global.json` pins the SDK to .NET 8. If a concurrent IDE build server causes MSBuild
> node errors, build with `-m:1 -nodeReuse:false` (set `MSBUILDDISABLENODEREUSE=1`).
