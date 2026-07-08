# Mines: deploy animation, in-view HUD glyph, radar discoverability

## Context

Minefields today pop into existence instantly (no deploy visual), have no HUD marker,
and are **not** radar-discoverable — an enemy sees a field only while its center sits
inside their direct line-of-sight vision volume (ship/base/probe sphere-cone), via the
plain geometric `IsPointVisibleToTeam` gate. They carry no signature and never enter the
radar/contact pipeline that ships/bases/probes use.

This change makes mines feel like deployed ordnance:
1. **Deploy animation** — a freshly-laid field's mesh cloud rapidly expands spherically
   from its center instead of appearing fully-formed.
2. **In-view HUD glyph** — a mine glyph is drawn for any minefield currently visible to
   you (your own fields always; enemy fields when revealed) *only while on-screen*; no
   edge-clamped arrow when it's off to the side/behind.
3. **Radar discoverability** — mines gain a **configurable per-expendable signature** and
   become radar contacts, so enemies detect armed fields at range without direct LOS,
   just like ships/bases/probes.

Decisions confirmed with the user: **add a radar signature** (not LOS-only); **glyph marks
own + visible-enemy** fields; the signature must be **configurable per deployed expendable**
(authored content, not a hardcoded constant).

Key precedent throughout: **recon probes** are the exact template — a static, radar-tier-only,
signature-scaled detectable with a HUD glyph. Copy the probe path at every seam.

---

## Part 1 — Deploy animation (client only)

**File:** `client/scripts/MinefieldViews.cs`

`BuildCloud` (line 85) currently sets every `MultiMesh` instance to its final seed-derived
offset immediately. `Upsert` (line 45) already computes `bool armed = serverTick >= row.ArmAtTick`
(line 61) and uses `!armed` to gate the deploy *sound* — an un-armed first sighting means the
field was **just laid**. Reuse that exact gate for the animation so only freshly-dropped fields
expand; fields streamed in mid-life (sector entry, reconnect, and — see Part 3 — radar detection)
appear fully-formed.

- Extend `FieldView` (line 35) with the data needed to animate the cloud open:
  `Vector3[] FinalOrigins`, `Basis[] Bases` (rotation×scale, already built in `BuildCloud`),
  and `float DeployElapsed` (`< 0` = not animating / done).
- Have `BuildCloud` hand back the per-instance origins + bases (or store them on the
  `FieldView` the caller builds) so `_Process` can re-drive the transforms.
- In `Upsert`, when `!armed`, seed `DeployElapsed = 0f` and set every instance's initial
  origin to `FinalOrigins[i] * StartFactor` (small, ~0.03, so mines emerge clustered at
  center). When `armed`, leave `DeployElapsed = -1` (fully-formed, no animation).
- In `_Process` (line 131), before the TTL sweep, advance any field with `DeployElapsed >= 0`:
  `DeployElapsed += delta`; `float k = EaseOutCubic(Min(DeployElapsed / DeployDuration, 1))`;
  for each instance set `mm.SetInstanceTransform(i, new Transform3D(Bases[i], FinalOrigins[i]*k))`.
  When `k >= 1`, write the finals once and set `DeployElapsed = -1`.
- Constants: `DeployDuration ≈ 0.35f`, ease-out cubic (`1-(1-t)^3`) for the "rapid then settle"
  pop. Keep the existing deploy sound as-is (it already fires on the same `!armed` gate).

No wire, server, or protocol change. The seed-regenerated layout is untouched — the animation
only interpolates the *origin* from center out to each already-computed final offset, so a
resync mid-animation still converges to the identical cloud.

---

## Part 2 — In-view HUD glyph (client only)

**Feed — `client/scripts/MinefieldViews.cs`:** add a `VisibleMinefields()` accessor mirroring
`WorldRenderer.VisibleProbes()` — iterate `_fields`, yield `(Vector3 Pos, byte Team)` per field
(`fv.Node.Position` is the field center, `fv.Team` the owner). Reuse a scratch `List` like the
probe feed so it allocates nothing per frame.

**Surface — `client/scripts/WorldRenderer.cs`:** add a pass-through `VisibleMinefields()` that
returns `_minefieldViews.VisibleMinefields()` (mirror the `VisibleProbes()` forwarding at
`WorldRenderer.cs:784`).

**Draw — `client/scripts/TargetMarkers.cs`:**
- Add `Mine` to the `Kind` enum (line ~73).
- Add a `DrawClassGlyph` case for `Kind.Mine` (line ~946) — a distinct hazard glyph, e.g. a
  small spiked circle / 6-point burst drawn off the reused `_poly6` scratch (visually separate
  from the pod circle, probe diamond, and aleph rings). Team-colored.
- Add a draw loop in `_Draw()` next to the probe loop (lines 484-491):
  ```
  foreach (var (pos, team) in _world.VisibleMinefields())
      DrawEntity(view, pos, Kind.Mine, TeamColor(team), focused: false, friendly: true,
                 hideOffScreen: true);
  ```
  `hideOffScreen: true` (already supported by `DrawEntity`, line 799) draws the glyph only
  when the field projects on-screen and suppresses the off-screen edge arrow — exactly
  "in-view, don't show on the sides." `friendly: true` gives the quiet dim glyph.

**Scope = own + visible enemy** falls out for free: the streamed/visible set (Part 3) is already
own-team + radar/LOS-revealed enemy, so `VisibleMinefields()` naturally contains exactly those.

---

## Part 3 — Radar discoverability (server; signature = configurable content)

Recommended path is **Option A: no wire change, no proto bump.** Radar detection widens *who
gets the field's existing `MsgMinefields` record*, exactly as radar-detecting a ship streams the
ship's normal snapshot. The client renders the real cloud via the existing `ApplyMinefields` →
`Upsert` path with **zero client changes**.

### 3a. Configurable signature (already authored — just project it)

`Expendable.Signature` (`factions/src/Allegiance.Factions/Model/Expendables/Expendable.cs:25`)
is a **shared YAML-authored property on every deployed expendable** (mine/chaff/probe/missile).
The probe pipeline already projects it. For mines, follow the `ProbeSignature` precedent exactly:

- **`shared/Defs.cs`** — add `public float MineSignature;` to `WeaponDef`, next to the mine
  block (lines 181-184) or the `ProbeSignature` field (207). **Server-only** — `BuildDefs`
  must skip it (see the server-only comment at Defs.cs:202-206; `MsgContacts`/streaming never
  needs it because detection is server-authoritative).
- **`server/Content/FactionsContentProjection.cs`** — in the mine branch (lines 235-254) add
  `MineSignature = mn.Signature <= 0 ? 1f : (float)mn.Signature,` (authored 0/omitted → 1.0,
  same rule as `ProbeSignature` at line 290).
- **`shared/ContentValidator.cs`** — optional: mirror the probe check (lines 116-117) to flag a
  non-positive projected `MineSignature` for mine-kind weapons.

### 3b. Vision pipeline — `server/Sim/Simulation.Vision.cs`

Copy the probe-target path at every seam (probes are radar-tier-only, no eyeball/cone/ghost —
identical to what a static mine needs):

- **Input struct:** add `MineTargetSnap { ulong Id; byte Team; ushort Sector; Vec3 Pos; float Sig; }`
  next to `ProbeTargetSnap` (line 249).
- **Buffer:** add `_inMineTargets` list (lines 184-189); clear it in the `CaptureVisionInput`
  reset block (~line 474).
- **Capture** (after the probe loop, ~line 579): iterate `_minefields`, and — to keep the arming
  window stealthy and avoid triggering an enemy-side deploy animation/sound — capture **armed
  fields only** (`tick >= f.ArmAtTick`). Signature = `WeaponDefs[f.WeaponId].MineSignature`
  (fallback `1f`). This is a flat per-def constant, **not** `SignatureModel.Compute` (a static
  field has none of the dynamic ship contributors) — same as `ProbeSignature`/`baseSig`/`_rockSig`.
- **Output sets:** add `HashSet<ulong> VisibleEnemyMines` to both `TeamResult` (line ~267) and
  `TeamVision` (line ~144), beside `VisibleEnemyProbes`.
- **Classify** (`ComputeVision`, after line 651): copy the probe loop — for each enemy mine
  `ClassifyTarget(team, mt.Sector, mt.Pos, mt.Sig, 0UL, out bool radar, out _)`; if `radar`,
  `tr.VisibleEnemyMines.Add(mt.Id)`. Rock-occlusion + dust attenuation of the field center come
  for free inside `ClassifyTarget`.
- **Apply** (`ApplyVisionResult`, after line 936): copy the probe apply block — filter through a
  new `MinefieldExists(id)` helper (mirror `ProbeExists`), and when the swapped set differs from
  the previous, set `MinefieldsChangedThisStep = true` so the hub actually resends before the
  coarse keepalive (the probe block does this via `ProbesChangedThisStep`; the probe code comments
  literally cite `MinefieldsChangedThisStep` as its model). Clear `tv.VisibleEnemyMines` in
  `ResetVision` (~line 352).

Threading: `_inMineTargets` is a value-copy snapshot captured on the sim thread; the 2 Hz worker
reads only that copy — never live `_minefields`. Same discipline as `_inProbeTargets`.

### 3c. Hub gate — `server/Net/ClientHub.cs`

**Augment, don't replace** the existing LOS gate. In the `mineVisByTeam` precompute (lines 866-880),
seed each team's visible set from its radar detections before the LOS loop:
```
var tv = _sim.VisionFor(t);
HashSet<ulong>? vis = (tv != null && tv.VisibleEnemyMines.Count > 0)
    ? new HashSet<ulong>(tv.VisibleEnemyMines) : null;   // radar-detected at range
for (int i = 0; i < fields.Count; i++)
    if (fields[i].Team != t && _sim.IsPointVisibleToTeam(t, fields[i].SectorId, fields[i].Center))
        (vis ??= new()).Add(fields[i].FieldId);           // plus direct LOS
if (vis != null) mineVisByTeam[t] = vis;
```
`BuildMinefieldsFor`'s `Visible` predicate (lines 1273-1295) is unchanged — it already unions
`enemyVisible`. The union means: radar gives at-range detection without LOS (the new feature);
LOS still gives immediate reveal through a window even out of radar range. `VisionFor(t)` is
public (Vision.cs:80); `VisibleEnemyMines` is swapped whole at the vision boundary and read on
the sim thread (quiescent), matching `VisibleEnemyProbes`.

### Interaction notes
- Armed-only capture keeps the ~arm-delay window stealthy: the enemy never receives the un-armed
  record, so the Part-1 deploy animation/sound fires **only** for the owning team (who see their
  own field immediately via the own-team gate). By the time radar reveals it, `armed` is true →
  no enemy-side animation. Consistent and intended.
- No proto bump. If playtesting later shows the precise mesh cloud reveals too much, a follow-up
  can add a "detected-but-fuzzy" HUD-only blip (Option B: a `MsgContacts` mine-id section or a
  tier flag on the record → proto bump). Out of scope for v1.

---

## Files touched (summary)

| File | Change |
|---|---|
| `client/scripts/MinefieldViews.cs` | deploy expand animation; `VisibleMinefields()` feed |
| `client/scripts/WorldRenderer.cs` | pass-through `VisibleMinefields()` |
| `client/scripts/TargetMarkers.cs` | `Kind.Mine` + glyph + in-view draw loop |
| `shared/Defs.cs` | `WeaponDef.MineSignature` (server-only) |
| `server/Content/FactionsContentProjection.cs` | project `mn.Signature` → `MineSignature` |
| `shared/ContentValidator.cs` | (optional) validate positive `MineSignature` |
| `server/Sim/Simulation.Vision.cs` | mine target capture/classify/apply (copy probe path) |
| `server/Net/ClientHub.cs` | union radar detections into the mine visibility gate |

No protocol / `Wire.cs` version change.

---

## Verification

1. **Build:** `dotnet build` the server + shared + factions (watch for the NuGet-lock IDE build
   gotcha noted in CLAUDE.md if restore hangs). Run the content projection so `MineSignature`
   resolves; `tests/ContentTest` and `factions` validation should stay green.
2. **Radar detection (headless):** exercise the fog worker the way `tests/FogTest` does — place an
   enemy viewer within `SphereRadius × MineSignature` of an **armed** field with clear LOS and
   assert the field id appears in `VisionFor(enemyTeam).VisibleEnemyMines` and thus streams via
   `BuildMinefieldsFor`; assert an **un-armed** field does not; assert a rock between viewer and
   field occludes it. Add a focused case to FogTest mirroring its probe/ship cases.
3. **Deploy animation + glyph (client smoke):** run the client with `--autofly` (hold a
   `--server --anonymous` connection so the sim ticks — see the headless-sim-testing note),
   deploy a mine, and confirm: (a) the cloud expands spherically from center over ~0.35s on drop;
   (b) a mine glyph appears over the field only while it's on-screen and vanishes (no edge arrow)
   when it's off to the side/behind; (c) your own fields always glyph, an enemy field glyphs only
   once its armed center is in your radar/LOS.
4. **Fog-off parity:** with fog disabled, both teams still see all in-sector fields (the gate is
   bypassed) — confirm no regression and that streaming stays byte-stable (no wire change).
