# Rock meshes/textures by resource type

## Context

Every asteroid has two orthogonal, currently-unrelated properties:

- **`RockClass`** (gameplay): `Carbonaceous / Silicon / Uranium / Helium3 / Regolith / Ice`
  (`shared/Defs.cs:270`), assigned in `World.AssignOre` by per-rock `OreMix(seed,id)` hashes.
- **`Variant`** (visual mesh): a random index into `AsteroidShapes.Variants` drawn from the
  shared `DetRng` in `World.NextShape` (`server/Sim/World.cs:998`), streamed as a byte; the
  client loads `res://assets/asteroids/<name>.glb` by that name.

The two are **completely decoupled** — a Uranium rock can render as any of the 31 shapes, and
the asteroid-gen tool's visual "kinds" (`carbonaceous/stony/metallic`) are never read by the
game. The goal: make a rock's **mesh + texture reflect its resource type**, so each class reads
at a glance (He3 pale-cyan crystal, Ice white-blue, Regolith grey dust, etc.).

**Decisions (confirmed):** texture/material-only differentiation (reuse the 3 existing
silhouette generators, no new shape code); He3 gets a distinctive **bright albedo** (no new
emissive bake channel).

## Approach

Give each of the 6 `RockClass` values its own PBR material "kind" in asteroid-gen, author a pool
of a few mesh variants per class, and have the **server** pick each rock's `Variant` from its
class's pool (deterministically, off the same `OreMix` hash that assigns the class). The client
stays dumb (loads by streamed name); the server's collision hull already keys off `Variant`, so
hull and visual stay matched.

### 1. asteroid-gen: add per-class material kinds

`tools/asteroid-gen/shapefield.py` — extend the three lookups (reuse existing silhouette
generators via `_SHAPE`, so **no new shape code**):

- `KINDS` (line 28): add `helium3, uranium, regolith, icy` (keep `carbonaceous`, `stony`).
- `_SHAPE` (line 31): map new kinds to existing generators —
  `helium3→"faceted"`, `uranium→"faceted"`, `regolith→"lobed"`, `icy→"faceted_gouged"`.
- `_MATERIAL` (lines 34-38): add rows `(lit_tone, shadow_tone, rough_base, metal)`. Suggested
  starting values (tune against `/verify`):
  - `carbonaceous` → **Carbonaceous** (existing dark charcoal)
  - `stony` → **Silicon** (existing tan/grey silicate)
  - `uranium` → green-steel, e.g. `((0.30,0.40,0.28),(0.15,0.22,0.14),0.45,0.75)`
  - `helium3` → bright pale-cyan, e.g. `((0.55,0.72,0.78),(0.28,0.40,0.45),0.40,0.10)`
  - `regolith` → mid-grey dust, e.g. `((0.30,0.29,0.27),(0.15,0.145,0.135),0.95,0.0)`
  - `icy` → white-blue, e.g. `((0.70,0.76,0.85),(0.40,0.45,0.55),0.30,0.0)`

Color flows `_MATERIAL` → `_colour_params` (shapefield.py:162) → `bake_surface`
(`bake.py:77`) with no other change needed; the GLB material name already embeds the kind
(`glb.py:100`).

### 2. asteroid-gen: rebuild the catalog by class

Rewrite `tools/asteroid-gen/asteroids.json` as ~5 entries per class (≈30 total, matching
today's count), each with its class `kind` and tuned `radius`/`facets`/`gouges`/`lobes`/
`tint`/`value` for silhouette variety within the class. Keep descriptive names but group them by
class in file order (that order becomes the wire order in step 4).

Then regenerate and stage the assets:

```
cd tools/asteroid-gen && ./build.sh          # Docker = canonical reproducible producer
cp build/asteroid-*.glb ../../client/assets/asteroids/   # replace old set; delete stale names
# regenerate Godot import artifacts (res:// needs an import; see [[godot-glb-needs-import]])
godot --headless --import      # from client/, so tests + client can GD.Load the new GLBs
```

`client/assets/asteroids/*.glb` is the single source of truth — the **server** csproj globs the
same files (`server/SimServer.csproj:37-40`) for collision hulls, so no separate server asset
step. Commit **only** the `.glb` files (`.import`/`_N.png` stay gitignored per existing pattern).

### 3. shared: class → variant-pool mapping

`shared/AsteroidShapes.cs`:

- Rewrite `Variants[]` to the new catalog names (wire order = catalog order). The stale "keep in
  sync with the module's `Lib.cs`" comment is obsolete — no module exists; both peers build from
  this file, and there is no persisted variant state, so a wholesale rewrite is safe.
- Add a `RockClass → byte[]` pool table (indices into `Variants`) and:
  ```csharp
  public static byte VariantForClass(RockClass cls, ulong hash);  // pool[hash % pool.Length]
  ```
  with a safe fallback pool if a class is somehow empty. (`RockClass` lives in the same
  `StellarAllegiance.Shared` namespace, so it's directly referenceable here.)

### 4. server: pick Variant from class

`server/Sim/World.cs`:

- Add `private void AssignVariants()` that rewrites every rock's mesh from its class:
  ```csharp
  for (int i = 0; i < Asteroids.Count; i++) {
      var r = Asteroids[i];
      r = r with { Variant = AsteroidShapes.VariantForClass(RockClassOf(r.Id), OreMix(Seed, r.Id)) };
      Asteroids[i] = r;
  }
  ```
  This uses only the per-rock `OreMix` hash (**never** the shared `DetRng`), so positions/
  rotations stay byte-identical for a pinned seed.
- **Reorder the ctor** (currently `LoadRockBodies()` @363 runs *before* `AssignOre` @375): run
  `AssignOre(secCfg)` then `AssignVariants()` **before** `LoadRockBodies()`, so the collision
  hull (`LoadRockBodies` keys off `r.Variant`, World.cs:747) is built from the class-derived
  mesh and matches the visual.
- Keep the existing `DetRng` variant draw in `NextShape` (line 998) — removing it would shift the
  RNG stream and move every rock. Add a one-line comment that its result is now overwritten by
  `AssignVariants`; the draw is retained solely for stream/position stability.

No wire-format change: `Variant` is still one byte; rock-static size is unchanged (so FogTest's
size literals and the Protocol layout are untouched).

### 5. tests: update the CANARY

`tests/MiningTest/Program.cs`:

- Test 2 "THE CANARY" (lines 143-155) asserts `Variant` is identical across wildly different
  mining knobs. That no longer holds — `Variant` now legitimately tracks class, and class flips
  with He3 count. **Drop `ra.Variant != rb.Variant` from the cross-knob identity loop**, keeping
  the `Id/SectorId/Pos/Radius/Rot` checks (those are the real "ore assignment never touched the
  shared RNG" guarantee) and update the message.
- Test 1 "same seed ⇒ identical class + capacity" (~line 122): add `Variant` to the same-seed
  identity check — it is now a deterministic function of class, so a second build with the same
  seed must produce the same mesh. Add a new invariant assert that each rock's `Variant` is a
  member of its class's pool (`AsteroidShapes.VariantForClass` range).

Client-side needs no change: `TargetMarkers.RockClassName` already labels all 6 classes, and
`WorldRenderer.AsteroidMesh` already loads whatever name is streamed.

## Files to modify

- `tools/asteroid-gen/shapefield.py` — `KINDS`, `_SHAPE`, `_MATERIAL` (add 4 class kinds)
- `tools/asteroid-gen/asteroids.json` — rewrite catalog grouped by class
- `client/assets/asteroids/*.glb` — regenerated set (commit `.glb` only)
- `shared/AsteroidShapes.cs` — new `Variants[]` + class→pool table + `VariantForClass`
- `server/Sim/World.cs` — `AssignVariants()` + ctor reorder + `NextShape` comment
- `tests/MiningTest/Program.cs` — relax CANARY, tighten same-seed check

## Verification

1. **Build + unit tests** (native sim, no Godot needed):
   `dotnet test` (or the per-suite runners). Focus: **MiningTest** (canary + same-seed +
   pool-membership), **ContentTest**, **FogTest** (rock-static size unchanged). Green MiningTest
   proves the shared RNG stream / layout stayed byte-stable.
2. **Server collision hulls**: run the server once and confirm the
   `Log.RockHullsLoaded` line reports `N/N` (every rock hull-collided) — proves the new GLB names
   resolve for the reordered `LoadRockBodies`.
3. **Visual (`/verify` skill)**: headless server + Godot client with a pinned `--seed`; fly to a
   sector, screenshot, and confirm each class renders its material — He3 pale-cyan, Ice
   white-blue, Regolith grey, Uranium green-steel, Silicon tan, Carbonaceous charcoal. Cross-check
   against the F3 map rock labels (which name the class) and the He3 ore readout. Fog-of-war may
   gate discovery — approach rocks or run with fog off for the shot.
