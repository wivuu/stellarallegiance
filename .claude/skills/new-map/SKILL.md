---
name: new-map
description: Author new playable MAPS — standalone sector-layout YAML files (geometry, garrisons, gate links, per-sector environment) that ship next to the binary and are picked by name. Use when creating or editing a map, adding sectors/garrisons/links, designing a map's 2D silhouette, or touching server/Content/maps/*.yaml, schemas/map.schema.json, or MapLoader.cs.
---

# Map authoring

A **map** is a standalone YAML file describing sector geometry — it is NOT part of the
faction/tech-tree content pipeline (see the `tech-tree-content` skill for that). One map = one
`.yaml` file in `server/Content/maps/`. The stock reference map is
[`server/Content/maps/brimstone-gambit.yaml`](../../../server/Content/maps/brimstone-gambit.yaml) —
its header is the authoritative field-by-field reference. Read it before authoring.

## Pipeline (file → world)

```
server/Content/maps/*.yaml     authored map (kebab-case keys; schema below)
  └─ MapLoader.LoadAvailable   server/Content/MapLoader.cs — parses stock + SIM_MAPS_DIR, keyed by name
       └─ MapLoader.Resolve    picks the selected map by name (case-insensitive), fail-fast
            └─ MapLoader.ApplyTo(map, WorldConfig)   projects sectors/links/scale onto the live world
                 └─ MapCatalog.Build → streamed to the lobby map picker
```

- Stock maps ship at `<binary>/content/maps/` (the csproj copies `server/Content/maps/`).
- Operators drop extra maps into the folder named by `SIM_MAPS_DIR` / `--maps-dir`; a map there
  with the same `name` **overrides** a stock one.
- Select with `SIM_MAP` / `--map "<name>"` (default `"Brimstone Gambit"`).

## Schema & validation

`schemas/map.schema.json` is the JSON Schema, bound in `.vscode/settings.json` to
`server/Content/maps/*.yaml`, so editors validate live. **`additionalProperties: false`
everywhere** — an unknown/misspelled key is an error. The schema is generated from `MapDef`
(`MapLoader.cs`); regenerate after changing the C# model:

```sh
dotnet run --project server -- --gen-schemas   # writes schemas/*.json (default outdir: schemas/)
```

Quick key-check a map without a full build (uses the yaml lib already in the repo):

```sh
node -e '
const YAML=require("./.opencode/node_modules/yaml"),fs=require("fs");
const s=JSON.parse(fs.readFileSync("schemas/map.schema.json","utf8"));
const top=new Set(Object.keys(s.properties)), sp=s.properties.sectors.items.properties;
const sec=new Set(Object.keys(sp)), env=new Set(Object.keys(sp.environment.properties));
const d=YAML.parse(fs.readFileSync(process.argv[1],"utf8")), ids=new Set(d.sectors.map(x=>x.id)), e=[];
for(const k of Object.keys(d)) if(!top.has(k)) e.push("top:"+k);
for(const x of d.sectors){ for(const k of Object.keys(x)) if(!sec.has(k)) e.push("sec"+x.id+":"+k);
  for(const k of Object.keys(x.environment||{})) if(!env.has(k)) e.push("env"+x.id+":"+k); }
for(const [a,b] of (d.links||[])) if(!ids.has(a)||!ids.has(b)) e.push("link "+a+"-"+b);
console.log(e.length?e:"OK");
' server/Content/maps/<file>.yaml
```

Full validation (and the real fail-fast messages) happens at server boot — a malformed or
nameless map **throws** rather than loading silently.

## Hard constraints (enforced by MapLoader — violating these throws at boot)

1. **`name` is required.** It is also the case-insensitive selection key. Two maps in one dir
   with the same name → the later (deterministic order) wins.
2. **At least one garrison.** A `garrison: { team: N }` on a sector = team N's home base. The
   SET of garrison team-ids across the map = the teams the map supports. A 2-team map needs
   exactly two sectors with garrisons (teams `0` and `1`). Team ids are 0-based, `0..255`.
3. **Every `links` entry is a pair `[a, b]`** and both ids must exist in `sectors`. Links are
   bidirectional gate (aleph) edges. Omit `links` entirely → the sim rings the sectors by id.
4. **`asteroids` is `field` | `belt` | `none`** (omit → `field`).
5. **`sector.id`s** are referenced by `garrison`-bearing sectors and by `links`; keep them a
   contiguous `0..N-1` set unless you have a reason not to.

## Designing the 2D silhouette (`map-pos`)

`map-pos: [x, y]` (each ~`-1..1`) is where the sector's node draws on the minimap / lobby
preview — this is the map's *shape*. Omit `map-pos` on ALL sectors → the client auto-lays them
on a ring. To author a deliberate silhouette, give every sector a `map-pos` and place the two
homes at opposing extremes. The three companion maps in this folder are worked examples:

| Map | Shape | map-pos idea |
|-----|-------|--------------|
| `kestrel-cross.yaml`  | cruciform (+) | hub `[0,0]`, arms `[±1,0]`/`[0,±1]`; homes on opposite arms |
| `vesper-crown.yaml`   | hexagon ring  | six points on a hex (`[±0.866,±0.5]`,`[0,±1]`); homes top/bottom |
| `serpents-reach.yaml` | serpentine S  | zig-zag `[-1,-0.7]→[-0.4,0.4]→[0,0]→[0.4,-0.4]→[1,0.7]`; a pure chain |

**Make the `links` topology match the drawn shape** — a cross should link only through its hub,
a chain should link only end-to-end, a ring should close the loop. The picture and the pathing
should tell the same story.

## Per-sector feel (optional)

Everything below is optional; an omitted key falls back to ONE shared default (see the
brimstone header for exact fallbacks). `environment:` carries the look:

- **`sun`**: `azimuth`/`elevation` (deg), `color` `[r,g,b]`, `energy`, `ambient` (flat fill —
  raise for open/lit sectors, drop for murky ones), `size` (disc width, default 900),
  `god-rays`.
- **`nebula`**: `color-a`/`color-b` gradient, `intensity`, optional `seed`.
- **`dust`**: `amount` (0..1 VISUAL density) and `opacity` (0..1 how hard it cuts radar/vision,
  default 1) are **decoupled** — a big fluffy cloud can be radar-thin, a light haze radar-opaque.
  Omit the whole `dust` block → no dust and no dust shadow-shafts.
- `radius` (absolute world units; omit → map `sector-radius` × scale), `asteroid-density`
  (per-sector multiplier).

## Recipe for a new map

1. Copy `brimstone-gambit.yaml` (keep its header) → `server/Content/maps/<kebab-name>.yaml`.
2. Set a unique `name`. Decide team count = number of garrisons; place homes at shape extremes.
3. Lay out `sectors` with `id`, `name`, `map-pos` forming your silhouette; give homes a larger
   `radius` and a garrison.
4. Write `links` so the gate topology matches the drawn shape.
5. Give each sector an `environment` block for mood (homes bright/lit, chokepoints murky/dusty).
   Mirror the two sides (cool vs. warm sun) so teams read as opposed but balanced.
6. Validate (node check above; then a real boot). Fix any `additionalProperties`/typo errors.
7. Smoke it: `dotnet run --project server -- --server --map "<name>"` and confirm the boot log
   lists it; the client lobby map picker shows it and draws the silhouette.

## Gotchas

- Kebab-case keys only (`sector-radius`, `map-pos`, `color-a`, `god-rays`). camelCase silently
  fails schema validation and boots as a null/default.
- Colors are `[r, g, b]` floats ~`0..1`, not 0–255.
- Changing `MapDef`/`SectorEnvDef` fields means: update the XML doc comment, then rerun
  `--gen-schemas`, then update `brimstone-gambit.yaml`'s header reference.
- Maps are geometry only — no ship/weapon/price/tech values live here (that's
  `tech-tree-content`). A map may override world `sector-scale` / `asteroid-density` /
  `sector-radius`, nothing else gameplay-balance.
