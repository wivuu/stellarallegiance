# Execute: Mines — deploy animation, in-view HUD glyph, radar discoverability

## Context

Implement the already-approved design in
[.claude/plans/golden-hugging-stream.md](golden-hugging-stream.md). That file is the
source of truth for every change — this plan only adds execution order, delegation, and
verification. All of its file/line anchors were re-verified against the current tree and
are accurate, with **one correction**: `MinefieldsChangedThisStep` is declared in
`server/Sim/Simulation.cs:292` (set at Simulation.cs:561/893, Simulation.Mines.cs:105/123,
consumed at ClientHub.cs:826) — not in Simulation.Vision.cs. The apply step in Part 3b
still just sets that existing flag; only its home differs from the design doc's phrasing.

User directive: **delegate easy tasks to Opus**; keep the threading-sensitive vision work
on the main (Fable) loop.

## Execution

### Wave 1 — two Opus subagents in parallel (disjoint files)

**Agent A (Opus): client work — Parts 1 + 2 of the design doc**
- `client/scripts/MinefieldViews.cs` — deploy expand animation (FieldView gains
  `FinalOrigins`/`Bases`/`DeployElapsed`; seed on `!armed` in `Upsert`; ease-out-cubic
  drive in `_Process`, `DeployDuration ≈ 0.35f`, `StartFactor ≈ 0.03`), plus the
  zero-alloc `VisibleMinefields()` feed mirroring the probe feed.
- `client/scripts/WorldRenderer.cs` — pass-through `VisibleMinefields()` next to
  `VisibleProbes()` (~line 784).
- `client/scripts/TargetMarkers.cs` — `Kind.Mine`, spiked-circle hazard glyph in
  `DrawClassGlyph`, draw loop beside the probe loop (~484-491) with
  `hideOffScreen: true, friendly: true`.

**Agent B (Opus): content projection — Part 3a**
- `shared/Defs.cs` — `WeaponDef.MineSignature` (server-only; `BuildDefs` must skip it,
  per the server-only comment at Defs.cs:202-206).
- `server/Content/FactionsContentProjection.cs` — mine branch (~235-254):
  `MineSignature = mn.Signature <= 0 ? 1f : (float)mn.Signature` (probe rule, ~290).
- `shared/ContentValidator.cs` — mirror the probe positive-signature check (~116-117).

### Wave 2 — main loop (Fable), after Agent B lands (needs `MineSignature`)

**Parts 3b + 3c — vision pipeline + hub gate** (threading-sensitive, kept on Fable):
- `server/Sim/Simulation.Vision.cs` — `MineTargetSnap`, `_inMineTargets` buffer + reset,
  armed-only capture from `_minefields` (flat `MineSignature`, NOT `SignatureModel.Compute`),
  `VisibleEnemyMines` on `TeamResult`/`TeamVision`, `ClassifyTarget` classify loop,
  apply block with new `MinefieldExists(id)` helper setting `MinefieldsChangedThisStep`
  (the Simulation.cs:292 flag) on set change, clear in `ResetVision`.
  Copy the probe path at every seam; snapshot discipline identical to `_inProbeTargets`.
- `server/Net/ClientHub.cs` — seed `mineVisByTeam` (~866-880) from
  `_sim.VisionFor(t).VisibleEnemyMines` before the existing LOS loop (union, don't replace);
  `BuildMinefieldsFor` unchanged.

**FogTest case** — add a focused mine case to `tests/FogTest` mirroring its probe/ship
cases: armed field in radar range → id in `VisibleEnemyMines` + streams via
`BuildMinefieldsFor`; un-armed field → not visible; rock between → occluded.

### Wave 3 — verification (main loop)

1. `dotnet build` server + shared + factions; run `tests/ContentTest` + `tests/FogTest`
   (all suites were green as of 2026-06-12 — any failure is a real regression). Watch the
   NuGet-lock hang gotcha (kill stopped `aspire-managed` procs).
2. Client smoke per the design doc's Verification §3: run with `--autofly` while holding a
   `--server --anonymous` connection (sim won't tick otherwise), deploy a mine, confirm
   expand animation, on-screen-only glyph (no edge arrow), own-always / enemy-on-reveal.
3. Fog-off parity: fog disabled → both teams still see all in-sector fields, byte-stable
   streaming (no wire change, no proto bump anywhere in this work).

## Constraints carried from the design doc

- **No proto bump / no Wire.cs change.** Detection widens who receives existing
  `MsgMinefields` records; client needs zero changes for Part 3.
- Armed-only capture keeps the arming window stealthy and guarantees the deploy
  animation/sound never fires enemy-side.
- `MineSignature` is server-only in `WeaponDef` — never emitted by `BuildDefs`.
