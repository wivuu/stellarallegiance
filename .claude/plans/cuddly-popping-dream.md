# Plan: Multi-agent code-review sweep (unused / DRY / messy / refactor)

## Context

The game-app C# (client, server, shared, factions, public-lobby — ~64k lines across
~200 files) has grown feature-by-feature (weapons, mining, tech tree, warp, fog, orders,
constructors) with an **auto-commit hook** that merges work continuously and **no analyzer
enforcement**: only two csproj `NoWarn`s exist, no `TreatWarningsAsErrors`, no
`.editorconfig` severities. So the compiler never surfaces unused private members
(IDE0051/CS0169), dead branches, or duplication — they accumulate silently. The client is
code-driven (only **3 `.tscn`** files, **3** files calling `Connect(`), which makes a
dead-code sweep tractable but still requires accounting for Godot lifecycle/signal/export
wiring and reflection.

**Goal:** dispatch review agents across cohesive segments of the app, hunt for (1) unused/
dead code, (2) repeated code / DRY violations, (3) messy code, (4) obvious refactors —
**adversarially verify** each mechanical claim to kill false positives, and produce a single
**ranked Markdown report**. **Report only — zero code changes.**

## Approach — one `Workflow` (ultracode)

Pipeline per segment (review → verify as soon as that segment finishes), then a barrier for
cross-segment duplication, then synthesis. Findings are structured objects; verdicts filter
them; a synthesis agent writes the ranked report; the main loop writes the file (workflow
scripts can't touch the filesystem).

```
pipeline(SEGMENTS,
  seg     => agent(review prompt,  schema=FINDINGS)   // Phase 1 — Review
  (rev,s) => verify rev.findings                       // Phase 3 — Verify (per-segment fan-out)
)
--- barrier: collect all Phase-1 DRY candidates ---
crossDry = parallel([client↔shared, server↔shared, repo-wide constants])  // Phase 2
verify crossDry
--- deterministic: drop refuted, dedup by (file,line,category), rank ---
synthesis agent → ranked Markdown → main loop writes file
```

### Segment map (14 review agents)

Each agent gets its **explicit file list** (from `git ls-files`) as its boundary, but may
grep the whole repo to check references. Approx sizes guide balance.

| # | Segment | Representative files | ~lines |
|---|---------|----------------------|-------|
| C1 | Client — world render & VFX | `WorldRenderer.cs` (3341), `SectorEnvironment.cs`, `EngineGlow.cs`, `ShadowVolume.cs`, `AlephView.cs`, `BaseModelLoader.cs`, `ShipModelLoader` | ~6.5k |
| C2 | Client — HUD / targeting / overview | `TargetMarkers.cs` (1805), `SectorOverview.cs` (1442), `Hud.cs`, `WeaponsPanel.cs` | ~4k |
| C3 | Client — networking & prediction | `GameNetClient.cs` (2200), `PredictionController.cs`, `ConnectionManager.cs`, `ConnectLinkModal.cs` | ~4.5k |
| C4 | Client — ship control & input | `ShipController.cs`, input-binding scripts, client autopilot | ~2k |
| C5 | Client — lobby & server browser | `Lobby.cs` (1402), `ServerLobbyOverlay.cs` | ~2.5k |
| C6a | Client UI — game-screen tabs | `ui/ResearchTab.cs`, `ui/BuildTab.cs`, `ui/ShipLoadout*.cs`, `ui/CommandSidebar.cs` | ~4.5k |
| C6b | Client UI — component library | rest of `client/scripts/ui/*` (tokens, theme, `ChamferButton`, `KeybindRow`, `SettingsDialog`, `UiShowcase`, …) | ~4.5k |
| S1a | Server — sim core + AI + vision | `Sim/Simulation.cs` (3623), `Simulation.Vision.cs`, `Simulation.Pig.cs` | ~6.2k |
| S1b | Server — sim subsystems | `Simulation.Constructors/Mining/Orders/Research/Mines.cs` | ~3.3k |
| S2 | Server — networking & protocol | `Net/ClientHub.cs` (2206), `Net/Protocol.cs` (1663), `WebRtcListener`, `LobbyRegistrar` | ~4.5k |
| S3 | Server — world / content / assets | `Sim/World.cs`, `Content/WorldLoader/MapLoader/FactionsContentProjection/HardpointGeometryMerge`, `Assets/*`, `Logging/*` | ~3.5k |
| SH1 | Shared — sim / wire / collision | `Defs.cs`, `FlightModel.cs`, `ContentValidator.cs`, `AutoSteer.cs`, `WireQuant`, `Net/Wire`, `Collision/*` | ~4.9k |
| F1 | Factions library | `Model/*`, `Validation/CoreValidator.cs`, `Resolution/*`, `Serialization/*`, `Schema/*`, `Cli/Program.cs` | ~3k |
| PL1 | Public-lobby service | `PublicLobby.cs`, `ServerRegistry.cs`, `Signaling.cs`, `ReachabilityProbe.cs`, `Contracts.cs` | ~1.1k |

### The four categories each reviewer reports

1. **Unused / dead code** — private members with no references; public members with no
   caller anywhere in the solution; unreachable branches; commented-out blocks; dead files.
   Marked `needsVerify=true` (highest false-positive risk).
2. **Repeated code / DRY** — copy-pasted blocks, parallel switch/if ladders, duplicated
   magic constants, helpers re-implemented when a shared one exists. Records the canonical
   location if one exists (`needsVerify=true` when it claims "canonical already exists").
3. **Messy code** — over-long methods, deep nesting, god-classes, unclear names, mixed
   responsibilities, stale comments. Subjective → high-severity items spot-verified only.
4. **Obvious refactor** — extract method/class, name the magic number, reuse a shared util,
   collapse duplicated branches.

Each finding: `{segment, category, file, line, severity(high/med/low),
confidence(high/med/low), title, evidence, suggestion, needsVerify, crossRefs}`.

### Repo-specific guardrails (baked into every reviewer prompt — kills noise)

- **Godot is not dead code:** lifecycle (`_Ready/_Process/_PhysicsProcess/_Input/_ExitTree/
  _EnterTree`), `[Export]` fields, `[Signal]` delegates, `Connect(` targets, `_on_*`
  handlers, and CLI-flag entrypoints are all live even with no direct C# caller.
- **Wire dispatch is not dead code:** message handlers keyed by protocol msg-id
  (`MsgSpawn`, `MsgOrder`, …) are reachable via the dispatch table, not direct calls.
- **Reflection-bound is not dead code:** `factions/` model classes and YAML-deserialized
  types are constructed by the loader, not `new`'d directly.
- **`Simulation.*` are partial classes** of one type — a member "used only in another file"
  is used.
- **Determinism / protocol are load-bearing:** `AutoSteer` is PIG-determinism-critical and
  wire quantization is version-pinned — never suggest a refactor that would change sim
  output or the wire format. Flag such duplication as "note only, do not merge."
- **Respect authored conventions:** prefer C# local functions over `Func<>` (repo pref); no
  hardcoded balance numbers (they belong in `server/Content/*.yaml`); UI must use
  `DesignTokens`/`UiFonts` not literals. **Do NOT propose blanket reformatting** — CSharpier
  is pinned and HEAD carries ~163 format-dirty files; formatting suggestions are out of scope.

### Phase 2 — cross-segment DRY (barrier)

After all per-segment reviews complete, 3 cross-cutting agents look for duplication that no
single segment can see, seeded with the aggregated Phase-1 DRY candidates:
`client↔shared/server` (quantization, geometry, constants re-implemented client-side),
`server↔shared` (collision/sim math), and a repo-wide magic-constant/duplicated-literal
sweep. Their dead/DRY claims are verified too.

### Phase 3 — adversarial verification

Every `needsVerify` finding gets one independent agent prompted to **refute** it:
- **dead-code:** grep the entire repo — all `.cs`, all 3 `.tscn`, every csproj — for the
  symbol; check Godot wiring / reflection / wire-dispatch / cross-assembly use. Verdict:
  `confirmed-dead | used | uncertain` (default to `used` when unsure).
- **DRY-canonical:** confirm the duplication is real and the named canonical helper is
  actually substitutable, not just superficially similar.
- **high-severity messy:** confirm the metric (e.g., method really is ~300 lines).

Per-segment verify fan-out is **capped at ~25**; if a segment exceeds it, the overflow is
`log()`-ed (no silent truncation). Low/med messy + refactor findings pass through as
"unverified suggestion" (subjective by nature).

### Phase 4 — synthesis & report

Deterministic: drop `used`/`refuted`, dedup by `(file,line,category)`, rank by
severity→confidence. A synthesis agent writes the ranked Markdown:
- **Top 10 highest-value actions** (verified, high-severity first)
- Sections: **Dead code (verified)** · **DRY / duplication** · **Messy code** · **Refactor
  suggestions**, each finding with `file:line`, evidence, suggestion, verdict
- **Per-segment summary table** (counts by category)
- **Appendix:** candidates that failed verification (transparency — what was ruled out)

**Report location:** written to the session scratchpad (`…/scratchpad/code-review-findings.md`)
and surfaced via `SendUserFile`, **not committed** — this keeps "report only" true and avoids
the auto-commit/push hook publishing a scratch dump. I'll offer to drop it into the repo if
you want it tracked.

## Files touched

**None** (report-only). The workflow reads across `client/`, `server/`, `shared/`,
`factions/`, `public-lobby/`; the only write is the Markdown report to scratchpad.

## Verification

- Spot-check 3–5 "confirmed-dead" findings by hand (grep the symbol repo-wide) — expect
  zero that are actually Godot/reflection/wire-wired (that's what Phase 3 exists to prevent).
- Confirm every category has findings and the per-segment table sums correctly.
- Confirm no finding proposes a wire-format, determinism, or blanket-format change.
- Since it's report-only, "correctness" = the report is accurate and actionable; you review
  it and decide what (if anything) to act on in a follow-up.
