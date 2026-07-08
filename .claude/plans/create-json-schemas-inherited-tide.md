# Plan: JSON schemas for YAML content via `GetJsonSchemaAsNode`

## Context

All authoritative game content is authored as kebab-case YAML and loaded through the
`Allegiance.Factions` library (`CoreSerializer`, YamlDotNet + `HyphenatedNamingConvention`). The
server's live bundle (`server/Content/core/*.yaml`) deserializes into the factions POCOs
(`Core` fragments, `Faction`, `Manifest`), and the standalone `world.yaml`/`maps/*.yaml` files
deserialize — via the *same* `CoreSerializer.Deserialize<T>` — into the server-only `WorldDef` and
`MapDef` POCOs. Today there are no schemas for `server/Content/`, so authoring these YAML files gets
no editor validation or IntelliSense.

A schema generator already exists (`factions/.../Cli/JsonSchemaGenerator.cs`) but it is **hand-rolled
reflection**, not the requested `System.Text.Json` `JsonSchemaExporter.GetJsonSchemaAsNode`. The goal
is to (1) replace it with a `GetJsonSchemaAsNode`-based generator, (2) cover every content root type
including `WorldDef`/`MapDef`, and (3) wire the generated schemas into the **repo-root** VS Code
settings so `server/Content/**` YAML validates as you edit it.

Decisions (confirmed with user): **replace** the reflection generator; **also cover World & Maps**;
emit schemas to a **repo-root `schemas/` dir**.

## Root types to cover (5)

| Root POCO | Source | YAML it validates |
|---|---|---|
| `Core` | `factions/.../Model/Core.cs` | `server/Content/core/{hulls,weapons,projectiles,launchers,expendables,stations}.yaml` (each a `Core` fragment) + factions `sample-data/*` fragments |
| `Faction` | `factions/.../Model/Faction.cs` | `server/Content/core/factions/*.yaml` |
| `Manifest` | `factions/.../Serialization/Manifest.cs` | `server/Content/core/core.manifest.yaml` |
| `WorldDef` | `server/Content/WorldLoader.cs` | `server/Content/core/world.yaml` |
| `MapDef` | `server/Content/MapLoader.cs` | `server/Content/maps/*.yaml` |

## Approach

### 1. Shared exporter helper in the factions library (new file)

`factions/src/Allegiance.Factions/Schema/YamlJsonSchema.cs` — a public static helper that produces a
draft-2020-12 schema for any POCO root using `JsonSchemaExporter.GetJsonSchemaAsNode` (in-box in
net10.0; no new package). Config must reproduce **exactly** what `CoreSerializer` emits, so key
names match the YAML byte-for-byte:

- **Naming**: a custom `JsonNamingPolicy` that delegates to YamlDotNet's own
  `HyphenatedNamingConvention.Instance.Apply(name)` — used for both `PropertyNamingPolicy` and
  `DictionaryKeyPolicy`. Delegating to the *same* convention `CoreSerializer` uses guarantees
  parity (avoids subtle `KebabCaseLower`-vs-`Hyphenated` divergence). Mirror the convention set in
  `factions/.../Serialization/CoreSerializer.cs:14-27`.
- **Enums as kebab strings**: register `new JsonStringEnumConverter(namingPolicy)` on the options so
  enum members serialize as kebab-case strings (matches `.WithEnumNamingConvention(...)`).
- **`JsonSchemaExporterOptions.TransformSchemaNode`**: for nodes whose
  `context.TypeInfo.Kind == JsonTypeInfoKind.Object`, add `"additionalProperties": false` (preserves
  the existing generator's "unknown key" warnings); on the root node add the
  `"$schema": "https://json-schema.org/draft/2020-12/schema"` marker.
- Public API: `static JsonNode GenerateNode(Type root)` and `static string Generate(Type root)`
  (pretty-printed). Deterministic output.

Rationale for placing it in the library (not the CLI): the **server** project references the factions
library but not the CLI, and only the server can see `WorldDef`/`MapDef`. A library-level helper lets
both the factions CLI and the server call one code path.

### 2. Replace the reflection generator

- **Delete** `factions/src/Allegiance.Factions.Cli/JsonSchemaGenerator.cs`.
- **Edit** `factions/src/Allegiance.Factions.Cli/Program.cs` `Schema(...)` (lines 163-211): call
  `YamlJsonSchema.Generate(typeof(Core|Faction|Manifest))` instead of `JsonSchemaGenerator.Generate`.
  Keep its existing three-file output to `factions/.vscode/` so the standalone factions workspace
  (validating `sample-data/*`) keeps working. Output filenames unchanged
  (`allegiance-{core,faction,manifest}.schema.json`).

### 3. New server generation command (the repo-wide generator)

**Edit** `server/Program.cs` — add a `--gen-schemas [<outdir>]` early-return handler next to
`--pregen-assets`/`--selftest` (around line 33). It calls `YamlJsonSchema.Generate` for all five root
types and writes to `outdir` (default `schemas/` at repo root), producing:
`allegiance-core.schema.json`, `allegiance-faction.schema.json`, `allegiance-manifest.schema.json`,
`world.schema.json`, `map.schema.json`. `WorldDef`/`MapDef` are `public` types in `SimServer.Content`
— reference them via `typeof(WorldDef)`/`typeof(MapDef)`.

Run once to generate the committed `schemas/*.json` (5 files).

### 4. Wire root VS Code settings

**Edit** `/.vscode/settings.json` — add a `yaml.schemas` block (paths relative to repo root):

```jsonc
"yaml.schemas": {
  "./schemas/allegiance-core.schema.json": [
    "server/Content/core/hulls.yaml", "server/Content/core/weapons.yaml",
    "server/Content/core/projectiles.yaml", "server/Content/core/launchers.yaml",
    "server/Content/core/expendables.yaml", "server/Content/core/stations.yaml"
  ],
  "./schemas/allegiance-faction.schema.json": ["server/Content/core/factions/*.yaml"],
  "./schemas/allegiance-manifest.schema.json": ["server/Content/core/core.manifest.yaml"],
  "./schemas/world.schema.json": ["server/Content/core/world.yaml"],
  "./schemas/map.schema.json": ["server/Content/maps/*.yaml"]
}
```

Add a `"// note"` line documenting the regen command (mirroring the note in
`factions/.vscode/settings.json`). Leave `factions/.vscode/settings.json` as-is (its schemas are still
produced by the factions CLI in step 2).

## Files touched

- **New**: `factions/src/Allegiance.Factions/Schema/YamlJsonSchema.cs`
- **New (generated, committed)**: `schemas/{allegiance-core,allegiance-faction,allegiance-manifest,world,map}.schema.json`
- **Delete**: `factions/src/Allegiance.Factions.Cli/JsonSchemaGenerator.cs`
- **Edit**: `factions/src/Allegiance.Factions.Cli/Program.cs` (Schema command → shared helper)
- **Edit**: `server/Program.cs` (`--gen-schemas` handler)
- **Edit**: `/.vscode/settings.json` (`yaml.schemas` block)

## Known fidelity notes

- `AttributeModifiers : Dictionary<GameAttribute, double>` — `JsonSchemaExporter` emits
  `type: object` + `additionalProperties: {number}` but does **not** constrain keys to the enum set
  (the old hand-rolled generator added `propertyNames`). Editor still validates values; key-set
  constraint is a minor, acceptable loss. Can be re-added in the transform later if wanted.
- `GetJsonSchemaAsNode` fully inlines non-recursive types (our models have no type recursion), so each
  schema file is self-contained with no cross-file `$ref` — exactly what the VS Code YAML extension
  needs.

## Verification

1. **Build**: `dotnet build factions/src/Allegiance.Factions.Cli` and `dotnet build server` — confirm
   the deleted generator and new helper compile.
2. **Generate**: `dotnet run --project server -- --gen-schemas` → 5 files in `schemas/`. Also
   `dotnet run --project factions/src/Allegiance.Factions.Cli -- schema --output factions/.vscode/allegiance-core.schema.json`
   → factions workspace schemas regenerate.
3. **Parity check**: diff the new `allegiance-core.schema.json` property names against the current
   `factions/.vscode/allegiance-core.schema.json` — confirm kebab-case keys match (e.g. `max-speed`,
   `base-techs`, `hud-name`); investigate any divergence (the custom naming policy should make them
   identical).
4. **Real-file validation**: open `server/Content/core/hulls.yaml`, `world.yaml`, and a
   `maps/*.yaml` in VS Code with the YAML extension and confirm no false errors on valid content, and
   that a deliberately misspelled key (e.g. `max-speeed:`) is flagged.
5. **Round-trip sanity**: `dotnet run --project factions/src/Allegiance.Factions.Cli -- validate server/Content/core/core.manifest.yaml`
   still passes (schema changes are editor-only and must not affect loading).
