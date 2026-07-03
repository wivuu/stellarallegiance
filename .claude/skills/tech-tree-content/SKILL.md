---
name: tech-tree-content
description: The tech-tree/content configuration pipeline — how ALL gameplay/balance values (hull stats, weapon stats, payload/cargo, prices, tech gating) are authored in YAML and streamed to clients. Use when adding/tuning any gameplay stat, hull, weapon, cargo/expendable, tech, or development; when touching server/Content/factions/*.yaml, shared/Defs.cs, Protocol.BuildDefs, GameNetClient.ApplyDefs, or DefRegistry; or when tempted to hardcode a balance number anywhere.
---

# Tech-tree content pipeline

Every gameplay value in this game is **authored YAML, streamed at runtime**. There is no
compile-time content and no client fallback. If you are about to hardcode a stat, derive one
client-side, or invent a placeholder constant — stop and author it through this pipeline instead.

## The pipeline (source → client)

```
server/Content/factions/*.yaml            authored bundle (manifest-driven, kebab-case keys)
  └─ CoreSerializer.Load                  factions/src/Allegiance.Factions/Serialization/CoreSerializer.cs
       └─ CoreValidator.Validate          factions/.../Validation/CoreValidator.cs  (boot gate #1 — throws)
            └─ FactionsContentProjection  server/Content/FactionsContentProjection.cs (Core → runtime defs)
                 └─ ContentValidator      shared/ContentValidator.cs (boot gate #2, on projected defs)
                      └─ ContentSet       server/Content/ContentSet.cs (Ships/Weapons/CargoItems/Bases/World)
                           └─ Protocol.BuildDefs (MsgDefs)   server/Net/Protocol.cs — sent once after Welcome
                                └─ GameNetClient.ApplyDefs   client/scripts/GameNetClient.cs (mirrors writer)
                                     └─ DefRegistry          client/scripts/DefRegistry.cs (client def store)
```

- **Library** (`factions/src/Allegiance.Factions/`): pure content data model (Buildable/Hull/
  Part/Weapon/Expendable/Development/Tech…). Never references game code. Game-runtime-only
  fields (wire ids, hardpoints, tick ballistics, payload) are optional "runtime extension"
  fields on the models — see `Model/RuntimeData.cs` header and the extension banners in
  `Hull.cs` / `Parts/Weapon.cs` / `Expendables/Expendable.cs`.
- **Live bundle**: `server/Content/factions/` (manifest `core.manifest.yaml`). Shallow today —
  everything gated on the `base` capability. The **rich sample** with a real tech tree
  (tech.yaml, developments.yaml, per-faction gating) is `factions/sample-data/`.
- **Runtime entry with a wire id = streamed def**: `class-id` (hull), `weapon-id` (weapon),
  `base-type-id` (station), `cargo-id` (expendable). Catalog entries without one are
  tech-tree/catalog-only and never reach the wire.
- **Tech gating today**: `Simulation.ResolveTeamUnlocks` (server/Sim/Simulation.cs) resolves
  buildable hull classes per team from the catalog (`BuildableResolver`). The stat-modifier
  system (`AttributeResolver`, developments' `attributes:`) exists in the library but is NOT
  wired into the sim yet.

## Iron rules

1. **No compile-time gameplay values.** The client guards until defs arrive (empty
   lists/false getters from `DefRegistry`) — it never falls back to baked constants or stub
   catalogs. Server-side, the sim resolves stats only from `ContentSet`.
2. **Wire layout changes bump BOTH versions together**: `Protocol.Version`
   (server/Net/Protocol.cs) and `GameNetClient.ProtocolVersion` (client/scripts/GameNetClient.cs).
   Writer (`BuildDefs`) and reader (`ApplyDefs`) must mirror field-for-field, in order.
3. **Append-only enums** (`HardpointKind`, `WeaponKind` + their `Runtime*` library mirrors) —
   the wire encodes them as bytes.
4. **Runtime extension fields are omit-when-default** so the library's sample-data and tests
   are unaffected by game-specific additions.
5. **Validate at boot, not at runtime.** Authoring mistakes must refuse server boot with a
   named YAML key (e.g. an armed hull whose default weapons exceed `payload-capacity`), never
   surface as a mid-match KeyNotFound or a soft-locked UI.
6. **Determinism**: two loads of the same bundle must project byte-identical `MsgDefs`
   (tests/ContentTest guards this). Keep projection iteration in Core list order; no
   dictionary-order dependence.

## Checklist: adding a new authored field end-to-end

1. **Model** — add the property to the library model (kebab-case YAML key is automatic via
   `HyphenatedNamingConvention`). Game-runtime-only? Put it in the runtime-extension section.
2. **Validator** — if bad authoring can break gameplay, add a `CoreValidator` rule (library
   invariants) and/or a `shared/ContentValidator` rule (projected-def invariants).
3. **Shared def** — add the field to the matching record in `shared/Defs.cs`.
4. **Projection** — map it in `server/Content/FactionsContentProjection.cs` (and extend
   `ContentSet` if it's a new def kind).
5. **Wire** — write it in `Protocol.BuildDefs`, read it in `GameNetClient.ApplyDefs`
   (same position!), bump both protocol version constants.
6. **Client store** — extend `DefRegistry.Load` + accessors if it's a new def kind. Sorted
   accessors (`All*` by id) keep UI order stable.
7. **YAML** — author values in `server/Content/factions/*.yaml`; add new files to the
   manifest `catalog:`; bump the manifest `version:`.
8. **Tests** — spot-check in `tests/ContentTest` (projection + wire determinism) and
   `tests/FactionsTest` (raw YAML fields); validator cases in
   `factions/tests/Allegiance.Factions.Tests/ValidationTests.cs`.

## Verify

```sh
dotnet test factions/tests/Allegiance.Factions.Tests
dotnet run --project tests/FactionsTest/FactionsTest.csproj -c Release
dotnet run --project tests/ContentTest/ContentTest.csproj -c Release
dotnet build server/SimServer.csproj -c Release && dotnet build client/stellarallegiance.csproj
```

Deploy note: a protocol bump means deployed servers (Railway) and clients must redeploy
together — old clients get the protocol-mismatch toast by design.

## Worked example

The payload/cargo system (2026-07): `payload-capacity` on hulls, `mass` on weapons (reusing
`Part.Mass`), expendables with `cargo-id`/`mass`/`glyph` → `CargoItemDef` streamed to the
hangar's cargo hold. Before this, the client derived payload from mass/damage heuristics —
that placeholder shipped fighter/bomber defaults overburdened, which is exactly why rule #1
exists.
