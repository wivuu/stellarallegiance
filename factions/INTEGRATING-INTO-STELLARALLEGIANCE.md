# Handoff: Integrating `Allegiance.Factions` into StellarAllegiance

Audience: an LLM/agent working **inside the StellarAllegiance repo**. This document tells you how to
take a dependency on the `Allegiance.Factions` library (this repo, `AllegianceModel/`) and use it as
the faction / tech-tree / ship-catalog data layer for the StellarAllegiance game.

You do **not** need to read the library's C++ ancestry to use it. Everything below is the public
surface you actually call.

---

## 1. What the library gives you

`Allegiance.Factions` is a standalone **.NET 8 class library** (no game-engine dependencies, only
`YamlDotNet`). It owns:

- **The data model** — a `Core` (the complete static dataset) holding catalogs of `Hull`, `Part`
  subtypes (`Weapon`/`Shield`/`Cloak`/`Afterburner`/`AmmoPack`/`Launcher`), `Station`, `Development`,
  `Drone`, `Expendable` subtypes (`Missile`/`Mine`/`Chaff`/`Probe`), `Projectile`, `Tech`, and
  `Faction`.
- **Serialization** — load/save the `Core` from human-readable, split YAML files via a manifest
  (`CoreSerializer`).
- **Validation** — referential integrity checking (`CoreValidator`).
- **Resolution** — the runtime tech-tree logic: given what a team owns, compute what it can reach
  and what it can build (`TechResolver`, `BuildableResolver`, `AttributeResolver`).

The library is **pure data + rules**. It has no notion of frames, rendering, networking, or ECS. It
answers questions like "what can blue team build right now?" and "what are red team's effective stat
multipliers after these developments?". StellarAllegiance owns everything else.

### Project locations in this repo

```
AllegianceModel/
  AllegianceModel.sln
  global.json                                   # pins SDK to .NET 8 (8.0.403, rollForward latestFeature)
  src/Allegiance.Factions/                      # <-- the library you depend on
    Allegiance.Factions.csproj                  #     net8.0, RootNamespace "Allegiance.Factions", refs YamlDotNet 16.2.1
  src/Allegiance.Factions.Cli/                  # validate/roundtrip/schema CLI (optional tooling, do not ship)
  tests/Allegiance.Factions.Tests/              # xUnit tests (reference for usage patterns)
  sample-data/                                  # a runnable 3-faction bundle (manifest + catalog + factions/)
  .vscode/*.schema.json                         # JSON schemas for authoring the YAML (optional)
```

---

## 2. How to take the dependency

Pick one of these. **Project reference is recommended** if StellarAllegiance and AllegianceModel
live in (or can live in) the same repo/solution; it keeps the data layer co-evolving with the game.

### Option A — ProjectReference (recommended if co-located)

From the StellarAllegiance game project's `.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\Allegiance\AllegianceModel\src\Allegiance.Factions\Allegiance.Factions.csproj" />
</ItemGroup>
```

(Adjust the relative path to wherever the two repos sit. Both target `net8.0`; if the game targets a
later TFM that's fine, net8.0 libs load into later runtimes.)

**SDK pin caveat:** this repo's `global.json` pins the SDK to 8.0.x. If StellarAllegiance pins a
different SDK via its own `global.json`, the nearest `global.json` to the build root wins. Make sure
the chosen SDK can still build `net8.0` (any SDK ≥ 8.0 can). Don't copy this repo's `global.json`
into StellarAllegiance unless you actually want to pin to 8.0.403.

### Option B — Package it as NuGet

If the repos are decoupled, `dotnet pack src/Allegiance.Factions/Allegiance.Factions.csproj -c Release`
and consume the `.nupkg` (add `<PackageReference>`). You'll need to add package metadata
(`<PackageId>`, `<Version>`) to the csproj first. Remember it pulls in `YamlDotNet 16.2.1`
transitively.

### Option C — Source include / submodule

Add AllegianceModel as a git submodule and ProjectReference into it (same as Option A). Avoid
copy-pasting the source files — you'll lose updates and the schema/CI tie-in.

After referencing, you should be able to `using Allegiance.Factions.Model;` etc. and build.

---

## 3. The public API you will actually call

All types are in these namespaces:

```csharp
using Allegiance.Factions.Model;          // Core, Faction, Hull, Station, Capability, TechSet, ...
using Allegiance.Factions.Serialization;  // CoreSerializer
using Allegiance.Factions.Resolution;     // TechResolver, BuildableResolver, AttributeResolver, TechState
using Allegiance.Factions.Validation;     // CoreValidator, ValidationResult
```

### Loading the data

```csharp
// Loads the manifest, every catalog fragment, and every faction file; merges into one Core.
Core core = CoreSerializer.Load("data/core.manifest.yaml");
```

`Load(manifestPath)` reads `version` + `catalog:` (list of fragment files) + `factions:` (list of
faction files) from the manifest, deserializes each, and merges. Ship the `sample-data/` layout (or
your own authored equivalent) with the game and point `Load` at the manifest. There is also:

- `CoreSerializer.Save(Core, baseDir)` → writes the split files + manifest back out (returns the
  manifest path). Useful for tooling/editors, not normally needed at game runtime.
- `CoreSerializer.Serialize(core)` / `Deserialize(yaml)` → single-document, in-memory round-trip.

### Validating (do this once at load / in tests, not per frame)

```csharp
ValidationResult vr = CoreValidator.Validate(core);
if (!vr.IsValid)
    throw new InvalidDataException(string.Join("\n", vr.Errors));   // vr.Warnings are non-fatal
```

Checks unique ids, that every cross-reference (tech ids, `LifepodHullId`, `InitialStationId`,
successor ids, drone hull/expendable refs, weapon projectile, per-slot allowed parts) resolves.

### Resolution — the tech tree at runtime

The model splits "what a team owns" into **two** typed buckets:

- **`Capability`** (`Allegiance.Factions.Model.Capability`) — a **closed enum** of engine-checked
  gates: `Base`, `ShipyardAllowed`, `ExpansionAllowed`, `TacticalAllowed`, `SupremacyAllowed`.
  These are the things *game code branches on*. Carried as `CapabilitySet` (a `HashSet<Capability>`).
- **`TechSet`** — an **open-ended** set of research-tech id strings (e.g. `"heavy-hulls"`,
  `"cloak-tech"`). Authorable, no special code logic — purely subset-rule driven.

A team's owned state is `TechState(TechSet Techs, CapabilitySet Capabilities)`.

```csharp
Faction faction = core.Factions.Single(f => f.Id == "iron-coalition");

// Forward-closure: from the faction's BaseTechs + BaseCapabilities, repeatedly apply the granted
// effects of every development/station whose requirements are met, until nothing new appears.
TechState reachable = TechResolver.ResolveReachable(core, faction);

// Everything the team may build given what it owns (required techs AND required capabilities all
// owned), excluding obsolete tech-only developments.
IReadOnlyList<Buildable> buildables = BuildableResolver.GetBuildables(core, reachable);

// Effective team-wide stat multipliers: faction baseline × each completed development.
AttributeModifiers stats = AttributeResolver.Resolve(faction, completedDevelopments);
```

`ResolveReachable` answers "what is the **theoretical reachable** set for this faction" (the whole
tech tree it could climb). For **live gameplay** where a team owns only a partial set at a given
moment, drive resolution from the team's current owned sets instead:

```csharp
TechState now = TechResolver.ResolveReachable(core, ownedTechs, ownedCapabilities);
var available = BuildableResolver.GetBuildables(core, ownedTechs, ownedCapabilities);
```

### Key signatures (copy-reference)

```csharp
// CoreSerializer
static Core   Load(string manifestPath);
static string Save(Core core, string baseDir);          // returns manifest path
static string Serialize(Core core);
static Core   Deserialize(string yaml);

// TechResolver
static TechState ResolveReachable(Core core, Faction faction);
static TechState ResolveReachable(Core core, TechSet ownedTechs, CapabilitySet ownedCapabilities);
record TechState(TechSet Techs, CapabilitySet Capabilities) { int Count; }

// BuildableResolver
static IReadOnlyList<Buildable> GetBuildables(Core core, TechState owned);
static IReadOnlyList<Buildable> GetBuildables(Core core, TechSet techs, CapabilitySet capabilities);
static bool IsObsolete(Buildable buildable, TechState owned);

// AttributeResolver
static AttributeModifiers Resolve(Faction faction, IEnumerable<Development> completedDevelopments);

// CoreValidator
static ValidationResult Validate(Core core);            // .IsValid, .Errors, .Warnings
```

---

## 4. Mapping the model onto StellarAllegiance's runtime

The library types are **immutable-ish data templates** (records). Do **not** mutate them per-team at
runtime. Instead, in StellarAllegiance:

1. **Load + validate once** at startup (or on data hot-reload). Cache the `Core`. Treat it as
   read-only shared state for the whole match.
2. **Per team**, keep the *mutable* owned state — a `TechSet` and a `CapabilitySet` (or your own
   wrappers). Seed them from the chosen `Faction.BaseTechs` / `Faction.BaseCapabilities`.
   - ⚠️ The resolver `Clone()`s its inputs before mutating, so calling `ResolveReachable` does **not**
     change your owned sets. When a team *actually completes* a development or builds a granting
     station in-game, you union that buildable's `GrantedTechs` / `GrantedCapabilities` into the
     team's owned sets yourself, then re-run `GetBuildables` to refresh the build menu.
3. **Build menu** = `BuildableResolver.GetBuildables(core, teamTechs, teamCaps)`, then filter/group
   by your UI (use `Buildable.Group`, `Price`, `BuildTimeSeconds`, `Name`, `IconName`).
4. **Stats** = `AttributeResolver.Resolve(faction, teamsCompletedDevelopments)` whenever the set of
   completed developments changes; apply the resulting `AttributeModifiers` (25 `GameAttribute`
   multipliers, default 1.0, stack multiplicatively) to your ship/station stat pipeline.

### ⚠️ Critical gotcha: `buildable-on` is NOT a tech gate

`Station.BuildableOn` (the `AsteroidAbility` list — `Buildable`, `MineHe3`, `MineGold`,
`SpecialExpansion`, `SpecialTactical`, `SpecialSupremacy`, …) is a **map/placement constraint**, not
part of tech resolution. `TechResolver`/`BuildableResolver` **ignore it entirely**. A station can be
"buildable" per the tech tree yet still have nowhere to place it because the team controls no rock of
the required type.

StellarAllegiance must enforce placement separately: when the player tries to place a station, check
that the target asteroid's ability set contains one of the station's `BuildableOn` values. The
special rock kinds are **mutually exclusive** — a `SpecialTactical` rock accepts *only* a tactical
station, `SpecialSupremacy` *only* a supremacy center, `SpecialExpansion` *only* an expansion
station. This is how the original game gated tech-tier stations by map control, and the model
preserves that distinction but leaves enforcement to the engine (the resolver only knows about techs
and capabilities).

If you want rock-control to genuinely gate the reachable tech tree (e.g. "bios can't reach supremacy
because it never holds a supremacy rock"), you must layer a per-team "available rock types" concept
into your own resolution loop on top of `TechResolver` — the library does not model the map.

---

## 5. Worked example (drop-in)

```csharp
using Allegiance.Factions.Model;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Resolution;
using Allegiance.Factions.Validation;

public sealed class FactionData
{
    public Core Core { get; }

    public FactionData(string manifestPath)
    {
        Core = CoreSerializer.Load(manifestPath);
        var vr = CoreValidator.Validate(Core);
        if (!vr.IsValid)
            throw new InvalidDataException("Faction data invalid:\n" + string.Join("\n", vr.Errors));
    }

    public Faction GetFaction(string id) => Core.Factions.Single(f => f.Id == id);
}

public sealed class TeamState
{
    private readonly Core _core;
    public Faction Faction { get; }
    public TechSet OwnedTechs { get; }
    public CapabilitySet OwnedCapabilities { get; }

    public TeamState(Core core, Faction faction)
    {
        _core = core;
        Faction = faction;
        OwnedTechs = faction.BaseTechs.Clone();
        OwnedCapabilities = faction.BaseCapabilities.Clone();
    }

    // What this team can build right now (tech tree only — placement is enforced elsewhere).
    public IReadOnlyList<Buildable> CurrentBuildables()
    {
        var state = TechResolver.ResolveReachable(_core, OwnedTechs, OwnedCapabilities);
        return BuildableResolver.GetBuildables(_core, state);
    }

    // Call when the team completes a development / builds a granting station.
    public void Acquire(Buildable completed)
    {
        OwnedTechs.UnionWith(completed.GrantedTechs);
        OwnedCapabilities.UnionWith(completed.GrantedCapabilities);
    }

    public AttributeModifiers EffectiveStats(IEnumerable<Development> completedDevs) =>
        AttributeResolver.Resolve(Faction, completedDevs);
}
```

---

## 6. Authoring / shipping the data

- The data lives as YAML: a manifest (`core.manifest.yaml`) + catalog fragments (`tech.yaml`,
  `hulls.yaml`, `parts.yaml`, `stations.yaml`, `developments.yaml`, `drones.yaml`,
  `expendables.yaml`) + `factions/*.yaml`. Ship this directory with the game build as content, and
  `CoreSerializer.Load` the manifest at runtime.
- All keys and enum values are **kebab-case** (`max-armor-ship`, `base-capabilities`,
  `shipyard-allowed`). Null/default/empty values are omitted on serialize.
- For authoring, the repo ships JSON Schemas under `.vscode/` (core / faction / manifest) wired into
  `.vscode/settings.json` for validation + autocomplete. Regenerate them with
  `dotnet run --project src/Allegiance.Factions.Cli -- schema --output .vscode/allegiance-core.schema.json`
  (a GitHub Action also does this on model/CLI changes). The schema is generated from the model — do
  not hand-edit it.

---

## 7. Build / test / verify

```bash
# From AllegianceModel/
dotnet build AllegianceModel.sln          # compiles clean on .NET 8
dotnet test                               # xUnit tests (model round-trip, resolution, validation)
dotnet run --project src/Allegiance.Factions.Cli -- validate sample-data/core.manifest.yaml
```

> If a concurrent IDE/MSBuild build server causes node-reuse errors, build single-node:
> `MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet build -m:1 -nodeReuse:false`.
> Note `dotnet test` only builds the library + tests — if you change the model and want the CLI's
> output to reflect it, rebuild the CLI project explicitly first.

**Suggested first integration milestone in StellarAllegiance:**
1. Add the ProjectReference and get a clean build.
2. Write a smoke test that loads `sample-data/core.manifest.yaml`, asserts `CoreValidator.Validate`
   passes, picks `iron-coalition`, and prints `GetBuildables(...).Count` — confirm it's non-zero and
   differs from `bios` (proving per-faction gating flows through).
3. Wire `TeamState` (above) into your match setup; render the build menu from `CurrentBuildables()`.
4. Implement placement enforcement (`Station.BuildableOn` vs the target asteroid) in the game — the
   library deliberately does not.

---

## 8. Things the library does NOT do (StellarAllegiance owns these)

- Map / asteroid placement & the rock-type gate (see §4 gotcha).
- Economy ticks, build timers, partial-build progress (it only gives you `Price` /
  `BuildTimeSeconds` / income fields as data).
- Networking, persistence of live match state, ECS/rendering, hardpoints/GLB geometry (see
  `GLB-AND-HARDPOINT-FORMAT.md`).
- Mutating per-team state — you keep the owned `TechSet`/`CapabilitySet` and union grants in as the
  match progresses.
```
