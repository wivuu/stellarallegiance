# GLB & Hardpoint Format — Ships and Bases

How visual meshes (`.glb`) and the **hardpoint** mount points that drive their FX,
weapons, lights and docking markers are authored, transported, and consumed in this
project.

There are two things that the words "hardpoint" and "GLB" touch, and they meet at one
naming convention:

1. **The hardpoint data contract** — the authoritative list of mount points lives in
   `shared/Defs.cs`, is sent **server → client over the wire** (`Protocol.MsgDefs`), and
   is rendered by the client. This is live today.
2. **The GLB mesh convention** — a ship/base mesh is a `.glb` that carries its hardpoints
   *in the mesh itself* as empty nodes named `HP_<Kind>_<Index>`. The client currently
   renders **procedural placeholder** silhouettes and reads hardpoints from the def data;
   the GLB path is the planned drop-in replacement, and the node-naming contract is chosen
   so it slots in with **no change** to the FX/weapon code.

The single rule that ties them together:

> **A hardpoint is a local-space node named `HP_<Kind>_<Index>` whose local +Z axis is the
> hardpoint's forward.** Both the def-seeded markers and any future GLB node obey this, so a
> `.glb` can override the procedural markers transparently.

---

## 1. Coordinate & axis conventions

Everything is **local to the hull/base origin**, in the engine's `+Z = forward` convention.

| Axis | Direction |
|------|-----------|
| `+Z` | forward (nose / muzzle aim / "out the front") |
| `-Z` | aft (engine nozzles point their *forward* here, i.e. `Dir = (0,0,-1)`) |
| `+Y` | up |
| `+X` | right (ship's starboard) |

- **Offset** (`OffX/OffY/OffZ`) is the mount's position relative to the hull origin.
- **Forward / direction** (`DirX/DirY/DirZ`) is a (normally unit) vector giving the
  hardpoint's facing. A weapon muzzle faces `+Z`; an engine nozzle faces `-Z` (its plume
  streams aft).
- Units are world units (the same units the sim integrates in). Authored meshes are built
  at their true game size — there is no separate scale factor for ships (asteroids *are*
  scaled by `row.Radius`; ships/bases are not).

The client builds a marker's basis with `BasisFacingZ(forward)` (orthonormal, local +Z
along `forward`; see `ShipModelLoader.BasisFacingZ` / `BaseModelLoader.BasisFacingZ`). A
GLB node should be oriented the same way: **point the node's local +Z down the forward
vector.**

---

## 2. Hardpoint kinds (`HardpointKind`)

Defined in `shared/Defs.cs`. The enum is serialized as a **byte by declaration order**, so
it is **append-only** — never reorder or remove members, only add to the end.

| Byte | Kind | Meaning | Consumed today by |
|------|------|---------|-------------------|
| 0 | `Weapon` | gun muzzle; `WeaponId` names which `WeaponDef` fires from here | muzzle spawn (client prediction + server) |
| 1 | `MainEngine` | primary thruster nozzle | engine glow + team-trail anchor |
| 2 | `Booster` | afterburner / secondary nozzle | engine glow + team-trail anchor |
| 3 | `Thruster` | maneuvering (RCS) thruster | engine glow (cosmetic) |
| 4 | `Turret` | turret base | marker only (firing logic is a later phase) |
| 5 | `Light` | blinking nav light | blinking team-tinted beacon (`BaseBeacon`) |
| 6 | `DockingEntrance` | where a ship docks in | marker only (docking logic later) |
| 7 | `DockingExit` | where a ship spawns back out | marker only (spawn-exit logic later) |
| 8 | `Cockpit` | eye point for the first-person camera | first-person view (`CameraRig` parks the eye here; **client-only** — the sim never reads it) |

`Index` disambiguates multiples of one kind on the same hull (e.g. a Fighter's two
`Booster`s are `Booster_0` and `Booster_1`). `WeaponId` is meaningful only for `Weapon`
hardpoints (0 otherwise) and references a `WeaponDef.WeaponId`.

### How each kind is used by the client

- **Weapon** — `DefRegistry.TryGetWeapon` finds the first `Weapon` hardpoint and its
  `WeaponDef`; `PredictionController` spawns the bolt at `origin + Rot·Offset`, aimed along
  `Rot·Dir` (`PredictionController.cs:180`). The offset/forward fully determine the muzzle,
  so moving the hardpoint moves the muzzle with no code change.
- **MainEngine / Booster / Thruster** — `ShipModelLoader.AttachEngineGlow` collects every
  engine-class nozzle offset and feeds them to the `EngineGlow`; the average nozzle Z
  anchors the `TeamTrail`. (A pod gets the trail but no glow — it's a powered-down
  lifeboat.)
- **Light** — `BaseModelLoader` parks a self-phasing blinking `BaseBeacon` (emissive mote +
  `OmniLight3D`) at each `Light`, team-tinted. When the loaded `base.glb` carries its own
  `HP_Light_*` nodes the beacons follow those authored hull positions (via
  `GlbLoader.FindHardpoints`); they fall back to the def-seeded `Light` offsets only for the
  procedural sphere placeholder.
- **Turret / DockingEntrance / DockingExit** — carried as data and shown as positioned
  `HP_` markers now; their gameplay logic is out of scope for the current phase.
- **Cockpit** — `CameraRig` resolves the local ship's `HP_Cockpit_0` marker to a ship-local eye
  offset for the first-person camera (falling back to `(0, 0.5, 1)` when a hull carries none).
  Client-only: no server plumbing, no wire change (`Kind` is a generic `u8`).

---

## 3. The data contract: `HardpointDef`

`shared/Defs.cs`:

```csharp
public sealed class HardpointDef
{
    public HardpointKind Kind;
    public byte  Index;            // disambiguates multiples of one kind
    public float OffX, OffY, OffZ; // local offset from hull origin
    public float DirX, DirY, DirZ; // local forward (+Z muzzle / −Z nozzle)
    public uint  WeaponId;         // Weapon hardpoints only; 0 otherwise
}
```

Hardpoints hang off the content defs that own them:

- `ShipClassDef.Hardpoints` (one def per ship class; `ClassId` is a raw byte — new hulls are
  data-only additions; the pod uses reserved `ClassId = 255`).
- `BaseDef.Hardpoints` (one def per base type; also carries `Radius` and `MaxHealth`).

These are **authored in `GameContent`** (`Defs.cs`), the compile-in defaults. The client
keeps **no compile-time tuning fallback** — until the `MsgDefs` frame arrives it guards
(holds authority) rather than rendering baked numbers.

### Seeded content (current defaults)

| Hull / base | Hardpoints |
|-------------|-----------|
| **Scout** (class 0) | `Weapon_0` @ (0,0,3) +Z; `MainEngine_0` @ (0,0,−2.25) −Z |
| **Fighter** (class 1) | `Weapon_0` @ (0,0,3) +Z; `Booster_0` @ (−1.1,0,−2.75) −Z; `Booster_1` @ (1.1,0,−2.75) −Z |
| **Bomber** (class 2) | `Weapon_0` @ (0,0,3) +Z; `MainEngine_0` @ (−1.4,0,−3.4) −Z; `MainEngine_1` @ (1.4,0,−3.4) −Z |
| **Pod** (class 255) | `MainEngine_0` @ (0,0,−2.25) −Z (unarmed) |
| **Garrison base** (type 0) | `DockingEntrance_0` & `DockingExit_0` @ (0,0,R) +Z; `Light_0` @ (0,R,0); `Light_1` @ (0,−R,0) (R = base radius, 90) |

`NoseOffset = 3` is the muzzle distance ahead of center; `BaseRadius = 90` matches the
client's render radius.

### Wire format (`Protocol.MsgDefs`)

Sent once, right after `Welcome`, full-float (not a hot per-tick frame). Hardpoints are
encoded by `Protocol.WriteHardpoints` (`server/Net/Protocol.cs:197`):

```
count : u8
repeat count times:
  Kind     : u8   (HardpointKind)
  Index    : u8
  OffX,OffY,OffZ : f32 f32 f32
  DirX,DirY,DirZ : f32 f32 f32
  WeaponId : u32
```

The same block is embedded per ship class (after its flight stats + `MaxHull` + `FactionId`)
and per base type (after `Radius` + `MaxHealth`). The client deserializes into the same
`HardpointDef` and stashes them in `DefRegistry`.

**Operator runtime edits** flow through here: an `Upsert*` that moves a nozzle or muzzle is
reflected on the **next spawn** with no client rebuild, because the marker positions are
derived from the def, not hard-coded.

---

## 4. The GLB mesh convention

A ship/base `.glb` is the planned replacement for the procedural placeholder mesh. The
contract is intentionally identical to the marker contract so the swap is transparent.

### Node naming

Each hardpoint is an **empty / `Marker3D`-equivalent node** in the GLB scene tree, named
exactly:

```
HP_<Kind>_<Index>
```

where `<Kind>` is the `HardpointKind` name (`Weapon`, `MainEngine`, `Booster`, `Thruster`,
`Turret`, `Light`, `DockingEntrance`, `DockingExit`, `Cockpit`) and `<Index>` is the byte index. This
is the same string the loaders synthesize today:
`Name = $"HP_{hp.Kind}_{hp.Index}"` (`ShipModelLoader.cs:161`, `BaseModelLoader.cs:78`).

Examples: `HP_Weapon_0`, `HP_Booster_0`, `HP_Booster_1`, `HP_MainEngine_0`,
`HP_Light_1`, `HP_DockingExit_0`.

### Node transform

- **Position** = the hardpoint local offset.
- **Orientation** = local **+Z points along the hardpoint forward**. (Engine nozzles face
  `-Z`/aft; weapon muzzles face `+Z`/forward.)
- Nodes are **children of the mesh node** (the +Z-forward hull node), so the whole mesh can
  be re-parented/rotated as a unit and the hardpoints ride along in the same local frame.

### Mesh authoring

- Build the hull/base at **true game scale**, centered on the hull origin, nose along `+Z`.
- Use a **glTF metallic-roughness** material (the standard PBR set: baseColor / normal /
  ORM). It imports onto a Godot `StandardMaterial3D` with colour/roughness/metalness/AO
  wired automatically. The team tint is currently applied as a `MaterialOverride`; an
  authored GLB should expect its material to be tinted or overridden per team.
- **Normal maps are OpenGL-style** (green = +Y), Godot's default — no flip.

### Planned loader behaviour (not yet wired)

Today `ShipModelLoader.Build` / `BaseModelLoader.Build` always build the procedural
placeholder (`BuildPlaceholderMesh` / the sphere) and add an `HP_` `Marker3D` per def
hardpoint. The documented future path (see the header comments in both files):

1. If a `<class>.glb` / base `.glb` exists, load it **in place of** the placeholder mesh.
2. Read the GLB's `HP_<Kind>_<Index>` nodes; they **override** the procedurally-placed
   markers (the GLB author has placed them in-mesh).
3. `AttachEngineGlow`, the weapon/muzzle spawn, the beacons, etc. keep working unchanged —
   they only ever look up nodes/offsets by the `HP_` contract.

The def data remains the **authority** for gameplay (weapon offset used by prediction lives
in `DefRegistry`); the GLB nodes are the *visual* placement. Author them consistently.

> **One committed file per asset:** the `.glb` is the *only* file checked in per asset (it
> embeds its own PNG textures). Godot still imports it the normal way — `GlbLoader` loads the
> resulting `res://...glb` `PackedScene` — but the import artifacts it leaves on disk
> (`.glb.import`, the extracted `_N.png`, and their `.png.import`) are **gitignored**
> (`client/.gitignore`: `assets/**/*.import`, `assets/**/*.png`), not committed.
>
> Because the sidecars aren't in the repo, anything that loads `res://` assets **must run an
> import first** or it silently falls back to the procedural placeholder
> (`ResourceLoader.Exists` is false until then). The editor imports on open; for headless /
> CI / export builds run `godot --headless --import --path client` before exporting.

---

## 5. `ship-gen` — the procedural ship/GLB generator

`tools/ship-gen/` turns a compact **YAML part list** into a single Godot-ready `.glb` with
baked PBR materials and the `HP_<Kind>_<Index>` nodes already placed. It is the sibling of
`tools/asteroid-gen/` (pure Python: numpy + Pillow + pygltflib, no system deps), and the
same Docker-as-canonical-producer rule applies (same input ⇒ same bytes within the pinned
build image).

> The Python sources (`parts.py`, `glb.py`, `bake.py`) build the GLB from the YAML below;
> outputs land in `tools/ship-gen/build/` (gitignored, like `asteroid-gen`). The committed
> canonical hulls are `scout.glb` / `fighter.glb` / `bomber.glb` / `pod.glb`; `ship-1-0N`
> are generated examples. `build/manifest.json` records each output's bytes / sha256 /
> part & hardpoint counts.

### YAML schema

```yaml
name: ship-1-00          # output basename (-> ship-1-00.glb)
seed: 1755663524         # RNG seed (deterministic shape, if procedurally varied)
parts:                   # the mesh, assembled from primitive parts
  - type: cylinder
    material: hull       # hull | cockpit | engine  (-> a baked PBR material)
    radius: 2.04
    length: 6.05
    taper: 0.36          # scalar end-taper (cylinder)
    segments: 16
  - type: ellipsoid
    material: cockpit
    size:    [0.9, 0.48, 1.09]
    pos:     [0.0, 0.54, 1.36]
  - type: cylinder
    material: engine
    radius: 0.42
    length: 1.64
    segments: 12
    pos:     [1.3, 0.0, -2.53]
    mirror:  x           # also emit the X-mirrored copy (port/starboard symmetry)
  - type: wedge
    material: hull
    size:    [0.15, 2.13, 2.31]
    rot:     [0, 0, -90] # Euler degrees
    pos:     [1.83, 0.0, -0.6]
    mirror:  x
hardpoints:              # -> HP_<Kind>_<Index> nodes baked into the GLB
  - kind: Weapon         # a HardpointKind name
    index: 0
    offset:  [0.0, 0.0, 3.52]
    forward: [0, 0, 1]   # +Z muzzle
  - kind: Booster
    index: 0
    offset:  [-1.3, 0.0, -3.35]
    forward: [0, 0, -1]  # -Z nozzle
  - kind: Booster
    index: 1
    offset:  [1.3, 0.0, -3.35]
    forward: [0, 0, -1]
```

### Part fields

| field | applies to | meaning |
|-------|-----------|---------|
| `type` | all | primitive: `cylinder`, `ellipsoid`, `wedge`, `taper`, … |
| `material` | all | `hull` / `cockpit` / `engine` — selects a baked PBR material |
| `pos` | all | `[x,y,z]` local position (default origin) |
| `rot` | all | `[x,y,z]` Euler **degrees** (default none) |
| `mirror` | all | `x` (or `y`/`z`) — also emit the axis-mirrored copy |
| `radius`, `length`, `segments` | `cylinder` | tube dimensions + radial tessellation |
| `taper` | `cylinder` | scalar end-radius factor; `taper` part takes a `[ex,ey]` pair |
| `size` | `ellipsoid`/`wedge`/`taper` | `[x,y,z]` extents |

The `hardpoints:` block maps **one-to-one** onto `HardpointDef` (`kind`→`Kind`,
`index`→`Index`, `offset`→`Off*`, `forward`→`Dir*`). The generator writes them as
`HP_<Kind>_<Index>` nodes (forward → local +Z), so the GLB satisfies the §4 convention by
construction.

### Keeping YAML and `Defs.cs` in sync

The GLB's hardpoints are **visual placement**; `GameContent` in `shared/Defs.cs` is the
**gameplay authority** (it's what's sent over the wire and what prediction/FX read). When
authoring a hull, keep the YAML hardpoints and the corresponding `ShipClassDef`/`BaseDef`
hardpoints **aligned** (same kinds, indices, offsets, forwards). Mismatches show up as FX or
muzzles that don't sit on the mesh feature they belong to.

---

## 6. Quick reference — adding a new hull or base

1. **Author the mesh** as `name.glb` via `ship-gen` YAML (parts + `hardpoints:`), or by hand
   in a DCC tool placing empty nodes named `HP_<Kind>_<Index>` (local +Z = forward).
2. **Add the gameplay def** in `shared/Defs.cs` `GameContent`:
   a `ShipClassDef` (new byte `ClassId`) or `BaseDef`, with a matching `Hardpoints` list.
   Flight stats come from a `FlightModel` block; weapons reference a `WeaponDef.WeaponId`.
3. **Ship the GLB to the client**: copy the single `.glb` into `client/assets/...` and import it
   (open the editor or `godot --headless --import --path client`). Commit only the `.glb` — the
   regenerated `.import`/`.png` artifacts are gitignored.
4. The defs flow server → client automatically on connect (`MsgDefs`); the client guards
   until they arrive, then renders markers/FX from them (and, once the GLB loader path is
   wired, overrides the placeholder with your mesh).

## Source map

| File | Role |
|------|------|
| `shared/Defs.cs` | `HardpointKind`, `HardpointDef`, `ShipClassDef`, `BaseDef`, seeded `GameContent` |
| `server/Net/Protocol.cs` | `WriteHardpoints` / `BuildDefs` — the `MsgDefs` wire encoding |
| `client/scripts/DefRegistry.cs` | client-side def store; `GetHardpoints`, `TryGetWeapon` |
| `client/scripts/ShipModelLoader.cs` | ship placeholder mesh + `HP_` markers + engine glow/trail |
| `client/scripts/BaseModelLoader.cs` | base placeholder sphere + `HP_` markers + nav beacons |
| `client/scripts/PredictionController.cs` | muzzle spawn from the `Weapon` hardpoint offset/forward |
| `tools/ship-gen/` | YAML-parts → `.glb` (+ baked PBR + `HP_` nodes) generator |
| `tools/asteroid-gen/README.md` | sibling generator; reference for the GLB/Godot import pipeline |
