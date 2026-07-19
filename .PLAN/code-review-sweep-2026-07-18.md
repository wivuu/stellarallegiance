# Code-review sweep — Stellar Allegiance game app

_Automated multi-agent review: 20 segment reviewers → per-finding adversarial verification → cross-segment DRY sweep. Report only; no code was changed._

## Executive summary

The codebase is structurally sound but carries broad low-risk maintenance debt rather than correctness problems: of 127 kept findings, none are correctness bugs — the load is dominated by DRY duplication (62) and dead code (21 unused, 21 confirmed-dead), concentrated heavily in the Godot client's UI and rendering layer. Two recurring themes stand out: a handful of oversized "god-methods" (5 confirmed high-severity methods of 160–600 lines each across `_Draw`, `_Process`, `AfterStep`, and sim brain steps) that dominate the readability cost, and copy-paste primitives — splitmix64 hashing, `Vec3` math, VFX gradient textures, countdown formatting, marker drawing — reimplemented across 3–8 sites each. The determinism-sensitive server sim paths are largely clean; the risk in consolidation is confined to a few shared-hash sites that must stay byte-identical.

## Top 10 highest-value actions

1. **Break up `AfterStep()` into per-stage methods** — `server/Net/ClientHub.cs:1230` — a ~600-line god-method mixing a dozen broadcast/snapshot responsibilities is the single biggest readability liability in the hot server path.
2. **Extract shared `Hash.SplitMix64`** — `shared/Collision/Collide.cs:97` — the finalizer is copy-pasted across 6+ client/shared/server sites; consolidate client-visual sites first, treat deterministic-sim sites as byte-exact-only.
3. **Split `TargetMarkers._Draw` into draw-pass helpers** — `client/scripts/TargetMarkers.cs:602` — a ~325-line method folds a dozen unrelated passes together despite an existing per-feature helper style to match.
4. **Split `ShipController._Process` into TickSpawn/TickAutopilot/StepPrediction** — `client/scripts/ShipController.cs:350` — ~265 lines of spawn, input, autopilot and prediction; the field subsets are disjoint so the split is mechanical.
5. **Add `Dot`/`Normalize` to shared `Vec3`** — `shared/Collision/Collide.cs:16` — the same vector math is re-implemented per-file across shared collision and server, a foundational dedup that many other sites lean on.
6. **Delete the dead debug-cone apparatus** — `client/scripts/BaseModelLoader.cs:204` — `MakeHardpointCone`, its consts, and a commented-out caller block are fully superseded dead code confirmed with no live path.
7. **Extract shared gate-align steering helper** — `server/Sim/Simulation.Mining.cs:845` — `AlignGated`/`CrossSector` are duplicated verbatim between miner and constructor execution; a pure refactor with no wire/determinism impact.
8. **Consolidate the radial soft-dot VFX texture** — `client/scripts/ExplosionEffect.cs:256` — the 128×128 `GradientTexture2D` builder is reimplemented in ~8 VFX files, 4 byte-identical.
9. **Extract a single `UserPrefs.Save()` helper** — `client/scripts/UserPrefs.cs:64` — the save-and-log-error block is copy-pasted across 8 setters, a high-drift-risk pattern collapsed to one seam.
10. **Split `DockApproach` into per-phase helpers** — `server/Sim/Simulation.cs:1931` — a ~239-line deeply-nested 3-case docking state machine; isolating `DockAlign`/`DockCreep`/`DockTransit` cuts nesting and guard tangling.

## Scoreboard

| Metric | Count |
|---|---|
| Kept findings | 127 |
| Confirmed dead code | 21 |
| Duplication (DRY) | 62 |
| Messy | 22 |
| Refactor | 22 |
| Ruled out by verification | 22 |

## Dead / unused code (21)

_✅verified = an independent agent tried to prove it reachable and failed. Treat unmarked/⚠️ items as candidates to check before deleting._

- **[high/high ✅verified]** `client/scripts/BaseModelLoader.cs:204` — Dead debug-cone apparatus: MakeHardpointCone + its consts + commented caller block
  - _MakeHardpointCone (line 204) is referenced only inside the commented-out block at lines 133-144; it has no live caller. Its only inputs — ShowHardpointDebug (const false, line 50), DebugConeRadius (line 51), DebugConeHeight (line 52) — are consumed nowhere else. The whole debug-viz block (const flag + two size consts + method + 12-line commented block) is dead._
  - → Delete MakeHardpointCone, the DebugConeRadius/DebugConeHeight consts, the ShowHardpointDebug const, and the commented-out lines 133-144 (the DockFaceParser convention superseded per-marker cones). If the viz is worth keeping, move it behind a real runtime flag instead of dormant commented code.
  - refs: No live reference exists. Only occurrences: definition sites (BaseModelLoader.cs:50-52 consts, :204 method body :223/:224/:233) and commented-out caller block (BaseModelLoader.cs:133-144). All within a single file; no external, scene, reflection, or callback reference anywhere in the repo.

- **[med/high ✅verified]** `client/scripts/WorldRenderer.cs:220` — Public RadarVisibleIds accessor has no callers
  - _Grepped client/ server/ shared/ factions/ tests/ — the only occurrence of RadarVisibleIds is its own declaration (line 220). The backing field _radarVisible is used internally, but this IReadOnlyCollection accessor is never read anywhere._
  - → Delete the RadarVisibleIds property (the internal _radarVisible field stays). Confirm no tscn/reflection reference first.
  - refs: No callers exist. Only occurrence is the declaration at client/scripts/WorldRenderer.cs:220. The backing field _radarVisible is used internally (WorldRenderer.cs:248,289,301,303,989,1640) but never via this accessor.

- **[med/high ✅verified]** `client/scripts/WorldRenderer.cs:219` — No-arg GhostContacts() overload is dead
  - _Grep across the repo shows the only consumer (TargetMarkers.cs:1226) calls the sector overload GhostContacts(uint). The parameterless GhostContacts() => _ghosts (line 219) has zero callers._
  - → Remove the parameterless GhostContacts() overload; keep GhostContacts(uint sector).
  - also: `client/scripts/WorldRenderer.cs:241 (the used overload)` · refs: Only caller is TargetMarkers.cs:1226 which calls the sector overload GhostContacts(uint) via _world.GhostContacts(_world.ViewSector); no reference targets the parameterless overload.

- **[med/high ✅verified]** `client/scripts/BaseModelLoader.cs:188` — Public BaseModelLoader.Radius has no callers; its doc comment is stale
  - _The comment claims 'WorldRenderer uses it to anchor the floating health bar', but WorldRenderer instead inlines `_defs.GetBaseDef(DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius` (WorldRenderer.cs:2705, 2813, 3051). Grep across client/server/shared/tests finds zero calls to BaseModelLoader.Radius(._
  - → Remove the unused Radius(DefRegistry, byte) method (and its misleading comment), or have WorldRenderer's three inline sites call it if a single seam is wanted.
  - refs: No real callers. The claimed consumer (WorldRenderer) duplicates the logic inline at WorldRenderer.cs:2705, 2813, 3051 instead of calling Radius().

- **[med/high ✅verified]** `client/scripts/MeshRaycaster.cs:39` — MeshRaycaster.HasGeometry is never read by any caller
  - _Public property HasGeometry has no external references (grep of whole repo excluding MeshRaycaster.cs = none). WorldRenderer.cs:2089 gates raycaster creation on `glbHull != null` and never consults HasGeometry; the property's own comment admits 'the caller keeps the raycaster only for GLB hulls, so this is effectively always true'._
  - → Delete HasGeometry, or actually use it at the WorldRenderer.cs:2089/2722 sites (skip building/keeping a raycaster whose subtree yielded no meshes) instead of leaving it as an unread guard.
  - refs: Only reference is the definition itself: client/scripts/MeshRaycaster.cs:39. Nearest real consumer of the raycaster is WorldRenderer.cs:2716 (`if (b.Ray != null)`), which does not consult HasGeometry.

- **[med/high ✅verified]** `client/scripts/SectorOverview.cs:40` — Public static SelectedId is never read anywhere in the repo
  - _public static ulong SelectedId => _selection.Count == 0 ? 0 : _selection[^1]; — a full repo grep (client/server/shared/factions/tests/tools/public-lobby and .tscn) finds the symbol only at its own declaration. The sibling statics (SelectionCount, FlightCommandActive, ClearFlightSelection) are all consumed by ShipController; SelectedId is not. Its doc comment claims it is the F3 'selection' analog of TargetMarkers.FocusedId, but no consumer exists._
  - → Delete the SelectedId property (and its comment) as dead code, or wire the intended consumer if one was meant to exist.
  - refs: Only non-declaration reference is GLOSSARY.md:882, which is documentation prose describing the F3 selection concept, not a code use.

- **[med/high ✅verified]** `client/scripts/DefRegistry.cs:140` — Public WeaponMounts() (and its _mountsCache) is dead — no callers anywhere
  - _rg across client/ server/ shared/ factions/ tests/ tools/ and *.tscn finds zero call sites for WeaponMounts( — only its own definition (line 140) plus doc/comment mentions. Its backing field _mountsCache (line 36, cleared in Load line 66) exists solely for this method, so it is dead too. Every real barrel/HUD consumer calls WeaponSlots/SlotsForShip instead (PredictionController, WeaponsPanel, TargetMarkers, WorldRenderer)._
  - → Delete WeaponMounts() and the _mountsCache field (plus its Clear() in Load). The comment claims it is 'kept for non-positional consumers (HUD listings)' but no such consumer exists.
  - also: `client/scripts/DefRegistry.cs:36, client/scripts/DefRegistry.cs:66` · refs: No usage sites. Only self-references (DefRegistry.cs:36 field, :66 clear, :140-151 definition body) and comment/doc mentions (DefRegistry.cs:157, WeaponsPanel.cs:190, HardpointGeometryMerge.cs:34, GLOSSARY.md, docs/GLB-AND-HARDPOINT-FORMAT.md). The live equivalent used everywhere is WeaponSlots/SlotsForShip.

- **[med/high ✅verified]** `client/scripts/ui/DesignTokens.cs:70` — SetTeamAccentTint is never called — chrome faction-tint feature is dead
  - _grep across the whole repo (client/ server/ shared/ tests/ tools/, *.cs) finds no caller of SetTeamAccentTint outside its own definition/comment in DesignTokens.cs. Because it is the ONLY writer of the mutable TeamAccent, TeamAccent always stays equal to TeamAccentBase, so the documented 'a player's chrome leans toward their team' behaviour never happens._
  - → Either wire SetTeamAccentTint into the client where the local team becomes known (e.g. on Welcome/team-join), or delete the method and the mutable/base split (collapse TeamAccent + TeamAccentBase into one readonly token) so the dead tinting path stops implying a feature that doesn't run.
  - refs: Only non-definition reference is documentation: DESIGN.md:34 ("subtly faction-tinted via `DesignTokens.SetTeamAccentTint(team)`") and the self-comment at client/scripts/ui/DesignTokens.cs:29. No executable caller exists.

- **[med/high ✅verified]** `server/Net/ClientHub.cs:255` — Public TakeBytesSent() has no caller anywhere in the repo
  - _rg across the whole repo finds `TakeBytesSent` only at its definition (ClientHub.cs:255). The `_bytesSent` field is still incremented in SendLoop (line 1223) but the accumulated total is only ever read via TakeBytesSent, which nothing calls — so the entire bytes-sent accounting is dead. (Contrast GameState/PlayerCount, which LobbyRegistrar does consume.)_
  - → Delete TakeBytesSent() and the `_bytesSent` field plus its Interlocked.Add in SendLoop, or wire it into the bench/heartbeat line it was evidently meant for.
  - refs: No caller exists. Only sites: server/Net/ClientHub.cs:240 (field), :255 (definition), :1223 (increment in SendLoop).

- **[med/high ✅verified]** `server/Sim/World.cs:1006` — Private static Dot(Vec3,Vec3) in World is never called
  - _`private static float Dot(Vec3 a, Vec3 b)` at World.cs:1006 has exactly one occurrence in the file (the definition). World is a single non-partial sealed class, so no sibling file can reference it. Repo-wide the only Dot users are each file's own private copy (SelfTest, Collide, DockFace, ConvexHull, AutoSteer, Simulation) — none call World's._
  - → Delete the unused private Dot helper from World.cs.
  - also: `server/Assets/SelfTest.cs:241 (separate, used copy)` · refs: No real reference site found. Only occurrence is the definition at server/Sim/World.cs:1006.

- **[med/high ✅verified]** `shared/Defs.cs:966` — GameContent.FighterWeaponId / BomberWeaponId are dead constants
  - _Repo-wide ripgrep (all cs/yaml/tscn/md/tests) finds FighterWeaponId (Defs.cs:966) and BomberWeaponId (Defs.cs:967) referenced ONLY at their declaration. Their sibling ScoutWeaponId is used throughout the sim (Simulation.cs, tests) as the PIG's representative gun; the fighter/bomber ids are vestigial — the sim resolves per-hull guns via each Weapon hardpoint's WeaponId, not these named constants._
  - → Delete the two unused consts (and trim the comment to reference only ScoutWeaponId), or wire them into the code paths that actually pick a class's representative gun if that was the intent.
  - also: `shared/Defs.cs:967` · refs: No real use found. Sibling ScoutWeaponId (the used one) is at server/Sim/Simulation.cs:170 and :625, and tests/ContentTest/Program.cs:63,98,175. The `WeaponId = 1/2` occurrences in factions/tests/Allegiance.Factions.Tests/ValidationTests.cs are integer literals, not references to FighterWeaponId/BomberWeaponId.

- **[med/high ✅verified]** `factions/src/Allegiance.Factions/Model/Team.cs:9` — Team record is never used anywhere in the repo
  - _The Team record is not part of Core (no Teams list), is never deserialized, projected, tested, or referenced. Repo-wide grep for `Model.Team`, `new Team(`, `List<Team>`, `: Team` returns nothing outside Team.cs itself; the server/tests use their own TeamState.OwnedTechs/OwnedCapabilities, not this type._
  - → Delete Team.cs — the runtime team state lives in server/Sim (TeamState). If kept as an intended library API surface, add a test/consumer.
  - refs: No real reference found. Nearest non-matches: server/Net/ClientHub.cs uses byte Team (client id, unrelated); factions/src/Allegiance.Factions/Model/Core.cs has no Teams list; Resolution/{BuildableResolver,TechResolver,AttributeResolver}.cs never reference Team.

- **[med/high ✅verified]** `public-lobby/Contracts.cs:20` — RegisterRequest.IceCandidates is dead — explicitly legacy/unused, never read anywhere
  - _Repo-wide ripgrep for `IceCandidates` returns only the declaration (Contracts.cs:20) and its own 'legacy/unused' comment (Contracts.cs:10); no reader in client/, server/, public-lobby/, or tests/. It is an optional nullable JSON field, so dropping it does not break existing registrants._
  - → Remove the `string[]? IceCandidates` parameter from RegisterRequest (and the comment). If wire-compat with old registrants is a concern, it can be left but should be documented as intentionally-ignored rather than as an active contract field.
  - refs: No readers exist. Related-but-distinct live field is ServerEntry.IceServers (from _iceServers in ServerRegistry.cs), which is the actual WebRTC/STUN config; RegisterRequest.IceCandidates is unrelated legacy.

- **[low/high ✅verified]** `client/scripts/ui/ShipLoadout.Hangar.cs:770` — ShipCard.SetGate `lockNote` parameter is never supplied by any caller
  - _SetGate has signature `SetGate(WorldRenderer.SpawnGate gate, string? lockNote = null)`. All three callers (ShipLoadout.Hangar.cs:165, UiShowcase.cs:404, UiShowcase.cs:407) pass only the gate, so lockNote is always null and the Locked branch always renders the constant '⚿ TECH LOCKED'._
  - → Drop the unused `lockNote` parameter and the `string.IsNullOrEmpty(lockNote)` branch, or wire a real note through if one was intended.
  - also: `client/scripts/ui/UiShowcase.cs:404` · refs: SetGate is called at ShipLoadout.Hangar.cs:165 (gate=variable), UiShowcase.cs:404 (SpawnGate.Locked), UiShowcase.cs:407 (SpawnGate.TooPoor) — all single-argument, none pass lockNote.

- **[low/high ✅verified]** `client/scripts/ui/UiFonts.cs:32` — Michroma brand font is registered but never used
  - _Case-insensitive grep for 'michroma' across all repo files (excluding the asset/.import/tools generator) finds references only inside UiFonts.cs itself: the MichromaPath const (l.24), the Michroma property (l.32), and its load (l.55). The brand wordmark is baked into logo art, so nothing draws with this Font._
  - → Remove MichromaPath, the Michroma property, and its LoadOrFallback call (and the doc bullet) — or actually use it for the on-screen wordmark. Keeping an unused font load only warms an asset nothing reads.
  - refs: No real usage site found. Only self-references within client/scripts/ui/UiFonts.cs (lines 8, 24, 32, 53, 55). Related-but-unused: MichromaPath const (l.24) and the load call (l.55) are dead alongside the property.

- **[low/high ✅verified]** `server/Sim/Simulation.Pig.cs:17` — PigKindNone const is never referenced
  - _Repo-wide grep finds PigKindNone only at its definition (Pig.cs:17); PigPlan.Kind defaults to 0 and PigExecute's switch handles it via the `default` arm, never the named const._
  - → Either delete the const or actually use it (e.g. as the switch default label / to initialize PigPlan.Kind) so the documented sentinel isn't dead.
  - refs: None — only occurrence is the definition at server/Sim/Simulation.Pig.cs:17. Sibling constants PigKindChase/SteerShip/SteerPoint/AttackPoint/Patrol are used, but PigKindNone is not.

- **[low/high ✅verified]** `server/Sim/World.cs:254` — Legacy accessor World.BaseModel has no readers
  - _`public SimModel? BaseModel => Model0.Model;` at World.cs:254. A repo-wide search for `.BaseModel` (excluding BaseModelFor/BaseModelData/BaseModelRotation/BaseModelLoader) finds only a code comment — zero real reads in server/, tests/, client/, shared/. The sibling legacy accessors (BaseHull, BaseSubHulls, BaseExits, BaseEntryAxis, BaseDoorCenter, BaseDockFaces) ARE used by SelfTest/tests; BaseModel alone is dead._
  - → Remove the BaseModel property (the SimModel is reachable via BaseModelFor(0).Model if ever needed).
  - refs: No real readers. Only non-definition occurrence is a comment at server/Sim/World.cs:911 ("CollisionConfig.BaseModel" + "Rotation" wrapped across lines). Sibling accessors that ARE used: server/Assets/SelfTest.cs:61-115, server/Sim/Simulation.cs:1538/3399-3401, tests/MissileTest/Program.cs:1080, tests/CollisionTest/Program.cs.

- **[low/high ✅verified]** `factions/src/Allegiance.Factions/Model/TechSet.cs:21` — IsSatisfiedBy on TechSet and CapabilitySet is never called
  - _Both TechSet.IsSatisfiedBy (TechSet.cs:21) and CapabilitySet.IsSatisfiedBy (Capability.cs:39) are thin wrappers over IsSubsetOf; repo-wide grep shows zero callers. All availability checks (TechResolver.TryGrant, BuildableResolver.GetBuildables) call IsSubsetOf directly._
  - → Delete both IsSatisfiedBy methods (dead + duplicative of IsSubsetOf), or route the resolvers through them if the named intent is worth keeping.
  - also: `factions/src/Allegiance.Factions/Model/Capability.cs:39` · refs: No callers found. Only definitions: TechSet.cs:21, Capability.cs:39. Related used method IsSubsetOf is called directly by TechResolver/BuildableResolver.

- **[low/med ✅verified]** `client/scripts/DefRegistry.cs:88` — FactionName / FactionAttributes stored from the wire but never read by any client consumer
  - _rg for FactionName and FactionAttributes finds no reader outside DefRegistry (server has its own unrelated FactionStart.n). The values are decoded in ApplyDefs (GameNetClient.cs:1891-1892) and stored, but the public getters (FactionName line 88, FactionAttributes line 93) and the _factionAttributes field are never consumed — the code comments themselves say they are 'kept here so a client identity/stat panel CAN surface it' (a not-yet-built panel)._
  - → Keep the wire reads in ApplyDefs (removing them would break decode symmetry), but consider dropping the unread storage getters/field until the identity panel exists, or leave a TODO noting they are forward-looking API with no current consumer.
  - also: `client/scripts/GameNetClient.cs:1891` · refs: Only writers/decoders exist, no readers: GameNetClient.cs:1891-1894 (decode + Load), DefRegistry.cs:81-82,89,93 (store + getter). Server-side FactionStart.FactionName (server/Content/FactionStart.cs:27,47) and Protocol.cs:1525 are a separate server class + its serializer, not client consumers.

- **[low/med ✅verified]** `server/Content/FactionStart.cs:21` — FactionStart.LifepodHullId / InitialStationId are written but never read
  - _Both properties are populated by ProjectFactionStart (FactionsContentProjection.cs:212-213) but a repo-wide search finds no read of `FactionStart.LifepodHullId` or `.InitialStationId` anywhere (the only hits are the Factions.Core fields they are projected from, and their own declarations). Comments mark them 'reserved for Phase-5 wiring'._
  - → Either drop these two reserved-but-unused fields (and their ctor params) until Phase-5 actually consumes them, or leave a TODO — but they currently add plumbing with no consumer.
  - also: `server/Content/FactionsContentProjection.cs:212` · refs: No reads exist. Nearest real reads are of the distinct source type Faction: factions/src/Allegiance.Factions/Validation/CoreValidator.cs:348,350,352; factions/src/Allegiance.Factions/Resolution/TechTreeReport.cs:49-50; tests/FactionsTest/Program.cs:81. Writes into FactionStart: server/Content/FactionsContentProjection.cs:215-216 (reading source Faction f), FactionStart.cs:45-46.

- **[low/med ✅verified]** `factions/src/Allegiance.Factions/Validation/ValidationResult.cs:13` — ValidationResult.Warn is never called — warnings machinery is inert
  - _CoreValidator only ever calls result.Error(...); no call site invokes result.Warn anywhere in the repo, so Warnings is always empty even though the CLI (Program.cs:80,103) prints/counts it._
  - → Either drop Warn()/Warnings (and the CLI's warning display), or convert the softer checks (e.g. dead-data conditions phrased as errors) into warnings so the mechanism earns its keep.
  - refs: Definition: factions/src/Allegiance.Factions/Validation/ValidationResult.cs:13. Only readers of the Warnings list it feeds: factions/src/Allegiance.Factions.Cli/Program.cs:80 and :103 (both read result.Warnings, never write). No call site invokes .Warn( anywhere.

## Duplication (DRY) (62)

- **[high/high ·unverified(capped)]** `shared/Collision/Collide.cs:97` — splitmix64 finalizer copy-pasted across client/shared/server (6+ sites)
  - _Identical mix constants 0xBF58476D1CE4E5B9 / >>30 / 0x94D049BB133111EB / >>27 appear verbatim in shared/Collision/Collide.cs:97-101, shared/MinefieldLayout.cs:28-29, server/Sim/World.cs:1425-1426, client/scripts/Starscape.cs:122-125, client/scripts/WorldRenderer.cs:2185-2187 (all with the 0x9E3779B97F4A7C15 increment)._
  - → Extract one shared static Hash.SplitMix64(ulong) in shared/ and call it everywhere. CAUTION: MinefieldLayout/World.OreMix feed deterministic sim — merging those must be byte-for-byte identical (note-only if any doubt); the client-visual sites (Starscape, WorldRenderer) are safe to consolidate first.
  - canonical: `shared/ (new Hash.SplitMix64)` · also: `shared/MinefieldLayout.cs:28, server/Sim/World.cs:1425, client/scripts/Starscape.cs:122, client/scripts/WorldRenderer.cs:2185`

- **[high/high ·unverified(capped)]** `shared/Collision/Collide.cs:16` — Vec3 Dot/Normalize re-implemented per-file across shared collision + server
  - _Private `float Dot(Vec3,Vec3) => a.X*b.X+...` re-declared in Collide.cs:16, DockFace.cs:205, ConvexHull.cs:405, server/Sim/World.cs:1006; private `Normalize(Vec3)` re-declared in DockFace.cs:207, GlbReader.cs:277, World.cs:1000 — all identical to the Vec3 math that belongs on the shared Vec3/FlightModel type._
  - → Add Dot/Normalize as members/statics on shared Vec3 (shared/FlightModel.cs) and delete the per-file privates.
  - canonical: `shared/FlightModel.cs (Vec3)` · also: `shared/Collision/DockFace.cs:205, shared/Collision/ConvexHull.cs:405, shared/Collision/GlbReader.cs:277, server/Sim/World.cs:1000`

- **[med/high ✅verified]** `client/scripts/WorldRenderer.cs:452` — Sector-meta check re-implemented inline instead of reusing InSector helper
  - _The static helper InSector(node, sector) at line 630 encapsulates `n.HasMeta("sector") && (int)n.GetMeta("sector") == (int)sector`, yet the identical (or negated) expression is hand-written at lines 452 (ShipsInLocalSector), 1053 (SectorTeamStale), 1770/1778 (RefreshSectorVisibility), 1791/1796 (HideForWarp), 1989 (ShipObstacles)._
  - → Route those call sites through InSector (or a `!InSector`), so the metadata contract lives in one place.
  - canonical: `client/scripts/WorldRenderer.cs:630` · also: `WorldRenderer.cs:1053, 1770, 1778, 1791, 1796, 1989` · refs: Canonical helper: client/scripts/WorldRenderer.cs:630 InSector. Directly substitutable duplicate: line 452 (ShipsInLocalSector). Other positive-form duplicates: 1770, 1778 (RefreshSectorVisibility). Negated-form near-duplicates (need HasMeta guard retained): 1791, 1796 (HideForWarp); plus reviewer-cited 1053, 1989.

- **[med/high ✅verified]** `client/scripts/ExplosionEffect.cs:256` — Radial soft-dot GradientTexture2D reimplemented in ~8 VFX files (4 byte-identical)
  - _The identical `RadialDot()` helper — Offsets {0,0.5,1}, Colors white / white@0.4 / white@0, 128x128 Radial fill from center — is copy-pasted in EngineGlow.cs:533, ExplosionEffect.cs:256, HitFlash.cs:65 and BaseModelLoader.cs:404 (all byte-identical), with near-identical variants in ChaffFx.cs:162, DustField.cs:102 and LensFlare.cs:160 (RadialTexture). Several comments even say 'same recipe as EngineGlow.RadialDot'. No shared helper exists._
  - → Add a shared static helper (e.g. VfxTextures.RadialDot(Gradient?)) building the 128x128 radial GradientTexture2D once, and have the byte-identical callers reference it; keep the alpha/stop variants as explicit gradient args.
  - canonical: `client/scripts/EngineGlow.cs:533` · also: `client/scripts/HitFlash.cs:65, client/scripts/BaseModelLoader.cs:404, client/scripts/ChaffFx.cs:162, client/scripts/DustField.cs:102, client/scripts/LensFlare.cs:160` · refs: Byte-identical copies: EngineGlow.cs:533, ExplosionEffect.cs:256, and per claim HitFlash.cs:65, BaseModelLoader.cs:404; near-identical variants in ChaffFx.cs:162, DustField.cs:102, LensFlare.cs:160. No shared helper currently exists.

- **[med/high ✅verified]** `client/scripts/ShipModelLoader.cs:101` — Fallback model length 4.5 duplicated as a magic literal across three files
  - _ShipModelLoader.DefaultModelLength = 4.5f (line 101), CameraRig.DefaultModelLength = 4.5f (CameraRig.cs:56, comment explicitly notes it 'duplicates ShipModelLoader.DefaultModelLength'), and LoadoutPreview.cs:134 hardcodes the same 4.5f. All three are the same cosmetic model-length fallback and can silently drift apart._
  - → Promote a single shared const (e.g. make ShipModelLoader.DefaultModelLength internal/public) and reference it from CameraRig and LoadoutPreview instead of re-declaring / hardcoding 4.5f.
  - canonical: `client/scripts/ShipModelLoader.cs:101` · also: `client/scripts/CameraRig.cs:56, client/scripts/ui/LoadoutPreview.cs:134` · refs: Canonical: client/scripts/ShipModelLoader.cs:101 (DefaultModelLength, consumed in TargetLength at 107-110). Duplicates: client/scripts/CameraRig.cs:56 (with self-documenting "duplicates ShipModelLoader.DefaultModelLength" comment at 54-55); client/scripts/ui/LoadoutPreview.cs:134 (bare 4.5f literal in the same def.ModelLength>0f ternary).

- **[med/high ✅verified]** `client/scripts/SectorOverview.cs:239` — Hollow-diamond + center-dot + tag marker hand-drawn in 3 places
  - _The same 4×DrawLine diamond + DrawCircle center dot + mono tag is copy-pasted at SectorOverview.cs:239-252 (CMD order glyph) and again at 267-279 (BUILD/MINE rock glyph), and a third near-identical copy is in TargetMarkers.DrawWaypoint (1629-1641, 'NAV' diamond). The SectorOverview comment at line 228 even states it is 'the SAME hollow diamond ... the waypoint uses'._
  - → Add a static UiDraw.HollowDiamondMarker(CanvasItem, Vector2 center, float r, Color, string tag) helper and call it from all three sites (UiDraw already hosts CanvasItem-based primitives like Diamond/CornerBrackets).
  - canonical: `client/scripts/TargetMarkers.cs:1613` · also: `client/scripts/SectorOverview.cs:239, client/scripts/SectorOverview.cs:267, client/scripts/TargetMarkers.cs:1629` · refs: All three are client-only HUD draw code (Godot CanvasItem.DrawLine/DrawCircle/DrawString). Real other/canonical site: client/scripts/TargetMarkers.cs:1629-1641 (DrawWaypoint, "NAV"). Duplicate sites: client/scripts/SectorOverview.cs:239-251 ("CMD") and 267-279 ("BUILD"/"MINE"). No wire, protocol, or determinism code is touched by a merge.

- **[med/high ✅verified]** `client/scripts/SectorOverview.cs:295` — ClampToViewportEdge / DrawEdgeArrow re-implement TargetMarkers.ClampToEdge / DrawArrow
  - _SectorOverview.ClampToViewportEdge (295-305) is line-for-line the center-ray edge-clamp of TargetMarkers.ClampToEdge (1717-1727), differing only in the margin constant (RockEdgeMargin 40 vs EdgeMargin 34); the code comment at 294 admits it 'mirrors TargetMarkers.ClampToEdge'. DrawEdgeArrow (308-319) is likewise a duplicate of TargetMarkers.DrawArrow (1730-1737) with a hardcoded 10 vs ArrowSize 13._
  - → Promote both to static UiDraw helpers taking the margin/size as a parameter and a CanvasItem, and have both classes call them.
  - canonical: `client/scripts/TargetMarkers.cs:1717` · also: `client/scripts/SectorOverview.cs:308, client/scripts/TargetMarkers.cs:1730` · refs: Canonical: TargetMarkers.ClampToEdge (client/scripts/TargetMarkers.cs:1717) is the shared edge-clamp used by all HUD edge indicators (live entities, incoming-missile threat arrow, fog ghosts, per the 1711-1716 comment); TargetMarkers.DrawArrow (1730). Duplicate: SectorOverview.ClampToViewportEdge (client/scripts/SectorOverview.cs:295) + DrawEdgeArrow (308), called from the off-screen rock-order-arrow path at SectorOverview.cs:283-286.

- **[med/high ✅verified]** `client/scripts/CameraRig.cs:114` — The 5-flag "inputFree" modal gate is copy-pasted verbatim across the client
  - _`!Chat.Capturing && !SectorOverview.Active && !ShipLoadout.Active && !EscapeMenu.Active && !SettingsDialog.Active` appears identically in CameraRig.cs:114, ZoomView.cs:142, Hud.cs:327, and (negated OR form) in ShipController.cs:666-674 and 724-731; comments even say "Same inputFree idiom the rest of the client gates keys on". Any new modal must be added to every copy or an overlay leaks input._
  - → Add a single shared static predicate (e.g. `UiState.InputFree` / `AnyModalActive`) and replace each copy. No existing canonical helper exists today.
  - also: `client/scripts/ZoomView.cs:142, client/scripts/Hud.cs:327, client/scripts/ShipController.cs:666, client/scripts/ShipController.cs:724` · refs: Duplicated static-flag reads only (Chat.Capturing, SectorOverview.Active, ShipLoadout.Active, EscapeMenu.Active, SettingsDialog.Active); no other legitimate consumer differs in meaning. ShipController's two copies additionally include _autoFly/_hasShip which should remain caller-local when the modal portion is extracted.

- **[med/high ✅verified]** `client/scripts/PredictionController.cs:339` — SetPilotName + nameplate lifecycle duplicated between PredictionController and RemoteShip
  - _PredictionController.SetPilotName (339-358) and RemoteShip.SetPilotName (124-145) are near-identical: same null-coalesce, same short-circuit on unchanged name, same length-0 hide, same lazy Nameplate.Create(Team)+AddChild, same Text assignment (differing only in initial Visible handling). The `_nameplate`/`_pilotName` fields and the per-frame Nameplate.UpdateFovScale call are likewise mirrored._
  - → Factor the create/update/hide logic into a small reusable helper (e.g. a NameplateHolder struct or static Nameplate.SetText(ref Label3D, ...)) that both nodes call.
  - canonical: `client/scripts/RemoteShip.cs:124` · refs: Local-player nameplate visibility is uniquely driven per-frame at client/scripts/PredictionController.cs:698-701 (gated on ShowOwnNameplate/SectorOverview.Active/first-person); RemoteShip has no equivalent per-frame visibility drive (RemoteShip.cs:209-210 only rescales), relying on SetPilotName's Visible=true — this is the one difference a merge must preserve.

- **[med/high ✅verified]** `client/scripts/UserPrefs.cs:64` — Save-and-log-error block copy-pasted across 8 setters
  - _The exact 3-line pattern `var err = Cfg.Save(Path); if (err != Error.Ok) Log.Err($"[UserPrefs] failed to save {Path}: {err}");` is repeated in SetPilotName (64-66), SetLastShip (88-90), SetBusVolume (137-139), SetMouseSensMultiplier (178-180), SetMouseInvertY (190-192), SetFirstPersonView (205-207), SaveBindings (254-256), and SetLongArray (305-307). A private SaveBindings() already encapsulates exactly this._
  - → Rename/extract a single private `void Save()` helper (the SaveBindings body) and call it from every setter instead of re-inlining the Cfg.Save + error-log.
  - canonical: `client/scripts/UserPrefs.cs:252` · also: `client/scripts/UserPrefs.cs:88, client/scripts/UserPrefs.cs:137, client/scripts/UserPrefs.cs:178, client/scripts/UserPrefs.cs:190, client/scripts/UserPrefs.cs:205, client/scripts/UserPrefs.cs:305` · refs: Canonical helper: client/scripts/UserPrefs.cs:252 SaveBindings(). Duplicate sites: lines 64, 88, 137, 178, 190, 205, 305.

- **[med/high ✅verified]** `client/scripts/GameNetClient.cs:232` — Abort() and Disconnect() teardown bodies are near-identical
  - _Abort() (232-251) and Disconnect() (256-289) share an almost identical teardown block: Active=false; LocalShipId=0; LocalClientId=0; _reconnectToken=""; _worldLoaded=false; the four cache Clear() calls; LobbyPlayers reset; HostId=-1; Maps reset; LobbyChanged?.Invoke(); _world.Reset(). They differ only in the pre-steps (Abort drains _tx + bumps _connectSeq; Disconnect queues MsgBye and delays the cancel)._
  - → Extract a private `ResetConnectionState()` covering the shared teardown and have both Abort() and Disconnect() call it after their transport-specific pre-steps, so the two paths can't drift.
  - canonical: `client/scripts/GameNetClient.cs:256` · also: `client/scripts/GameNetClient.cs:256` · refs: client/scripts/GameNetClient.cs:237-250 (Abort teardown) and :275-288 (Disconnect teardown) are the duplicate; :299-304 (GiveUpShip) repeats a subset. Wire-touching pre-steps at :234-236 (Abort) and :263-273 (Disconnect) are separate and stay in place.

- **[med/high ✅verified]** `client/scripts/ui/ResearchTab.cs:1121` — Countdown 'remaining ticks -> mm:ss' block copy-pasted across three widgets
  - _The exact sequence 'float remaining = (start+dur - ServerTick)/FlightModel.TickRate; if(remaining<0) remaining=0; int t=(int)MathF.Ceiling(remaining); text=$"{t/60:00}:{t%60:00}"' is duplicated at NodeCard._Process (ResearchTab.cs:1121-1125), ActiveBanner._Process (ResearchTab.cs:1283-1287) and CommandSidebar.UpdateResearchLines (CommandSidebar.cs:167-172). TechDetailPanel.Mmss (line 285) already formats mm:ss but only from a float-seconds arg, so none of these reuse it._
  - → Add a shared helper (e.g. TechDetailPanel.MmssRemaining(WorldRenderer, uint start, uint dur) returning the formatted string, or a ticks->seconds overload of Mmss) and call it from all three sites.
  - canonical: `client/scripts/ui/TechDetailPanel.cs:285` · also: `client/scripts/ui/ResearchTab.cs:1283, client/scripts/ui/CommandSidebar.cs:167` · refs: Canonical helper: client/scripts/ui/TechDetailPanel.cs:285 (public static string Mmss(float seconds)). Duplicate sites to replace: client/scripts/ui/ResearchTab.cs:1121-1125, client/scripts/ui/ResearchTab.cs:1283-1287, client/scripts/ui/CommandSidebar.cs:167-172.

- **[med/high ✅verified]** `client/scripts/ui/BuildTab.cs:630` — BuildPrereqs is byte-identical between BuildTab and ResearchTab
  - _BuildTab.BuildPrereqs (630-639) and ResearchTab.BuildPrereqs (538-547) are the same body: loop RequiredTechIdx -> (_defs.GetTech(t)?.Name ?? $"TECH {t}", world.TeamOwnsTech), loop RequiredCaps -> (TechDetailPanel.CapName(c), world.TeamOwnsCap), then _detail.SetPrereqs(rows). Only the field source (dev vs s) differs._
  - → Extract one helper taking (ushort[] requiredTechIdx, byte[] requiredCaps) — e.g. a static on TechDetailPanel that both tabs call — instead of maintaining two identical copies.
  - canonical: `client/scripts/ui/ResearchTab.cs:538` · also: `client/scripts/ui/ResearchTab.cs:538` · refs: Shared helper could be substituted at both call sites; both types already carry RequiredTechIdx/RequiredCaps with matching shapes. Related BuildUnlocks methods differ meaningfully (StationCatalogDef vs DevelopmentDef granting logic) and are NOT duplicates — only BuildPrereqs is.

- **[med/high ✅verified]** `client/scripts/ui/ShipLoadout.cs:755` — MigrateTier duplicates WeaponsPanel's weapon-tier successor-chain walk
  - _MigrateTier (755-771) walks the successor chain with a 8-iteration guard and the exact same predicates (SucceededByWeaponId==uint.MaxValue, ObsoletedByTechIdx.Length==0, next.Mass>w.Mass mass guard, TeamOwnsTech). WeaponsPanel.MigratedDispenserName (WeaponsPanel.cs:212-236) reimplements the identical loop. Both are the client display mirror of Simulation.ResolveLoadout._
  - → Extract one shared client helper (e.g. DefRegistry.MigrateWeaponTier(id, team, world) returning the resolved successor id) and call it from both. Note: display-only mirror of the sim migrate — do not touch the server path.
  - canonical: `client/scripts/WeaponsPanel.cs:212` · also: `client/scripts/WeaponsPanel.cs:216` · refs: Both are display mirrors of the server-authoritative migration in Simulation.ResolveLoadout (loadout spawn) and Simulation.SeedDispenserAmmo (dispenser ammo); those server sites are the canonical logic and are intentionally NOT the merge target.

- **[med/high ✅verified]** `server/Sim/Simulation.Pig.cs:248` — Two identical stale-key prune loops in PigBrainStep
  - _Lines 248-256 (prune _pigDecisions) and 259-267 (prune _pigOrders) are structurally identical: _stalePigIds.Clear(); collect keys not in _livePigIds; remove each. Only the target dictionary differs._
  - → Extract a local function `void PruneToLive<T>(Dictionary<ulong,T> dict)` (repo prefers local functions over Func<>) that reuses _stalePigIds, and call it for both _pigDecisions and _pigOrders.
  - canonical: `server/Sim/Simulation.Pig.cs:248` · also: `server/Sim/Simulation.Pig.cs:259` · refs: Both sites are within PigBrainStep in server/Sim/Simulation.Pig.cs: first prune loop at lines 248-256, second at lines 259-267. _stalePigIds and _livePigIds are the shared scratch/live sets used by both.

- **[med/high ✅verified]** `server/Sim/Simulation.Mining.cs:845` — AlignGated + CrossSector gate-align helpers duplicated verbatim between MinerExecute and ConstructorExecute
  - _MinerExecute's local functions CrossSector (Mining.cs 830-838) and AlignGated (Mining.cs 845-857) are byte-for-byte identical to ConstructorExecute's (Constructors.cs 749-757 and 759-771), differing only in the constant name (MinerGateAlignRange vs ConstructorGateAlignRange, both = 200f). The magic numbers 12f*12f and 0.985f are duplicated in both AlignGated bodies too._
  - → Extract a shared private helper on Simulation, e.g. ShipInputState SteerGatedToGate(ShipSim s, Vec3 myPos, Quat myRot, uint destSector, float alignRange, Func<Vec3,Vec3,Vec3> avoid, out bool handled), and call it from both MinerExecute and ConstructorExecute. Collapse the two 200f constants into one. Pure refactor — identical output, no wire/determinism impact.
  - canonical: `server/Sim/Simulation.Constructors.cs:759` · also: `server/Sim/Simulation.Constructors.cs:749` · refs: Duplicate pair: server/Sim/Simulation.Mining.cs:830-857 (CrossSector + AlignGated in MinerExecute) vs server/Sim/Simulation.Constructors.cs:749-771 (CrossSector + AlignGated in ConstructorExecute). Constants: Mining.cs:109 MinerGateAlignRange=200f, Constructors.cs:67 ConstructorGateAlignRange=200f.

- **[med/high ✅verified]** `server/Net/ClientHub.cs:1087` — HandleOrder re-implements CommanderOrWarn's commander gate + warning
  - _HandleOrder (lines 1087-1094) re-does `_lobby.CommanderOf(team) != client.Id` then emits nearly the same warning already produced by CommanderOrWarn (lines 1011-1022). Wording even drifted: CommanderOrWarn says "can direct AI vessels" while HandleOrder says "can command AI vessels" — inconsistent user-facing text for the identical gate._
  - → After the human-subject loop, replace the inline block with `if (CommanderOrWarn(client) is not byte cmdTeam) return;` (team is already known/equal), unifying the gate and the message string.
  - canonical: `server/Net/ClientHub.cs:1011` · refs: Canonical helper: CommanderOrWarn at server/Net/ClientHub.cs:1011-1022; also used to gate MsgBuyMiner. Duplicated inline gate: HandleOrder at server/Net/ClientHub.cs:1087-1094.

- **[med/high ✅verified]** `shared/ContentValidator.cs:118` — Dispenser cargo-id existence check copy-pasted across mine/chaff/probe branches
  - _The identical guard `if (cargoItems is not null && w.CargoId != 0 && !cargoIds.Contains(w.CargoId)) errors.Add($"... CargoId {w.CargoId} resolves to no cargo item")` appears verbatim three times: mine (118-119), chaff (134-137), probe (162-165), differing only in the weapon-kind noun in the message._
  - → Extract a local helper e.g. `void RequireCargo(WeaponDef w, string kind)` and call it from each of the three dispenser branches.
  - canonical: `shared/ContentValidator.cs:118` · also: `shared/ContentValidator.cs:134, shared/ContentValidator.cs:162` · refs: shared/ContentValidator.cs:118-119 (mine), 134-137 (chaff), 162-165 (probe) — three verbatim copies of the CargoId-resolution guard within the same weapon-validation foreach loop.

- **[med/high ✅verified]** `factions/src/Allegiance.Factions/Resolution/TechTreeReport.cs:118` — Buildable-kind switch duplicated verbatim in KindOf and CoreValidator.Describe
  - _TechTreeReport.KindOf (line 118) and CoreValidator.Describe (CoreValidator.cs:409) contain byte-identical switch arms mapping Hull=>"hull"…Drone=>"drone",_=>"buildable". Two copies drift independently when a new Buildable subtype is added._
  - → Add a single source of truth, e.g. a `Buildable.KindName` property or a shared `BuildableKinds.NameOf(Buildable)` helper, and have Describe call it (Describe just wraps it as `$"{kind} '{Id}'"`).
  - canonical: `factions/src/Allegiance.Factions/Validation/CoreValidator.cs:409` · also: `factions/src/Allegiance.Factions/Validation/CoreValidator.cs:409` · refs: Canonical target: extract TechTreeReport.KindOf (factions/src/Allegiance.Factions/Resolution/TechTreeReport.cs:118) into a shared helper; CoreValidator.Describe (factions/src/Allegiance.Factions/Validation/CoreValidator.cs:409) reuses it. No other call sites of the duplicated switch found.

- **[med/high ✅verified]** `public-lobby/PublicLobby.cs:154` — Protocol-version list filter duplicated between GET /servers and the SSE snapshot
  - _`active = [.. active.Where(s => s.ProtocolVersion == protocol)]` at line 154 is byte-identical to the SSE snapshot filter `snap = [.. snap.Where(s => s.ProtocolVersion == protocol)]` at line 192 (comment even says 'same logic as GET /servers'). Two copies drift independently._
  - → Extract a single local function, e.g. `static IReadOnlyCollection<ServerEntry> FilterProtocol(IReadOnlyCollection<ServerEntry> list, int? protocol) => protocol is > 0 ? [.. list.Where(s => s.ProtocolVersion == protocol)] : list;`, and call it from both routes.
  - canonical: `public-lobby/PublicLobby.cs:154` · also: `public-lobby/PublicLobby.cs:192` · refs: Both copies live in the same file, public-lobby/PublicLobby.cs: the canonical/primary at lines 152-154 (GET /servers handler) and the duplicate at lines 190-192 (the /servers/events SSE initial snapshot).

- **[med/high ✅verified]** `client/scripts/MissileView.cs:146` — BasisFacingZ duplicated across three client model files
  - _`private static Basis BasisFacingZ(Vector3 forward)` implemented at MissileView.cs:146, ShipModelLoader.cs:296, and BaseModelLoader.cs:310. MissileView's comment even notes it copies ShipModelLoader's convention._
  - → Hoist BasisFacingZ into one static helper (e.g. a ModelGeom util) and call from all three loaders.
  - canonical: `client/scripts/ShipModelLoader.cs:296 (or a shared client geometry util)` · also: `client/scripts/ShipModelLoader.cs:296, client/scripts/BaseModelLoader.cs:310` · refs: Canonical/substitutable version: client/scripts/ShipModelLoader.cs:296 (used by MakeMarker at line 289). Third copy: client/scripts/BaseModelLoader.cs:310. Primary flagged site: client/scripts/MissileView.cs:146.

- **[med/high ✅verified]** `client/scripts/EngineGlow.cs:533` — Radial soft-dot GradientTexture2D rebuilt in multiple VFX files
  - _RadialDot()-style GradientTexture2D (radial fill soft dot) is hand-built in EngineGlow.cs, BaseModelLoader.cs, Sun.cs and ExplosionEffect.cs several bodies byte-identical._
  - → Extract a single cached RadialDot() texture factory and reuse; avoids rebuilding identical gradient textures per file.
  - canonical: `one shared client texture factory (e.g. UiTex.RadialDot)` · also: `client/scripts/BaseModelLoader.cs (RadialDot), client/scripts/Sun.cs, client/scripts/ExplosionEffect.cs:256` · refs: Byte-identical duplicates: client/scripts/EngineGlow.cs:533, client/scripts/ExplosionEffect.cs:256, client/scripts/HitFlash.cs:65 (all 128x128). Same recipe, size-only diff: client/scripts/BaseModelLoader.cs:404 (64x64). Genuinely different (do NOT merge): client/scripts/Sun.cs:170 (4-stop disc, 256x256), client/scripts/DustField.cs:102 (4-stop falloff, 64x64). No shared UiTex/RadialDot factory currently exists.

- **[med/high ✅verified]** `server/Sim/Simulation.Mining.cs:789` — MinerHoldDistance and ConstructorHoldDistance have identical bodies
  - _`MathF.Max(rockR * 1.1f, rockR + World.ShipRadius + 6f)` identical at Simulation.Mining.cs:789 and Simulation.Constructors.cs:666._
  - → Keep one HoldDistance method (Simulation is one partial class across these files) and drop the duplicate.
  - canonical: `server/Sim/Simulation.Mining.cs:789 (single method on the partial Simulation class)` · also: `server/Sim/Simulation.Constructors.cs:666` · refs: server/Sim/Simulation.Mining.cs:789 (MinerHoldDistance); server/Sim/Simulation.Constructors.cs:666 (ConstructorHoldDistance)

- **[med/high ✅verified]** `client/scripts/ui/BuildTab.cs:630` — BuildPrereqs duplicated between BuildTab and ResearchTab
  - _BuildPrereqs prereq-row builder exists at BuildTab.cs:630 and ResearchTab.cs:538; bodies are byte-identical apart from the def type._
  - → Extract the prereq-row builder into a shared helper parameterized over the def, remove one copy.
  - canonical: `a shared hangar/tech UI helper both tabs call` · also: `client/scripts/ui/ResearchTab.cs:538` · refs: Canonical extraction target: a shared static helper (natural home is TechDetailPanel, which already owns CapName/PriceText/Mmss and receives the result via SetPrereqs). Both call sites already depend on _defs, _world, Team, and _detail.

- **[med/high]** `client/scripts/Lobby.cs:1145` — ChatLine→BBCode formatting duplicated between Lobby.RebuildComms and Chat.FormatLine
  - _Lobby.RebuildComms (1145-1172) and Chat.FormatLine (325-339) independently build the same BBCode: dim timestamp stamp, empty-name '◆' system line in Text2/Mute, scope==2 gold '★ CMDR {name} ▸ {text}', scope==1 '[team]' tag, and name colored by team. The '★ CMDR ... ▸ ...' string is byte-identical across the two files (Lobby.cs:1162 vs Chat.cs:334). Any change to order/directive styling must be edited in two places or they drift._
  - → Extract a shared client-side formatter (e.g. static ChatFormat.ToBbcode(ChatLine line, string time, bool system)) in the ui/ library and call it from both RebuildComms and FormatLine; keep the small per-context differences (Lobby wraps message text in TextHi) as a parameter.
  - canonical: `client/scripts/Chat.cs:325` · also: `client/scripts/Lobby.cs:1145`

- **[med/high ·unverified(capped)]** `client/scripts/ShipModelLoader.cs:296` — BasisFacingZ + MakeMarker byte-identical across three model loaders
  - _Private static Basis BasisFacingZ(Vector3) duplicated in ShipModelLoader.cs:296, BaseModelLoader.cs:310, MissileView.cs:146 (MissileView comment even says 'same convention ShipModelLoader.BasisFacingZ uses'); MakeMarker(HardpointDef) duplicated ShipModelLoader.cs:286 vs BaseModelLoader.cs:193._
  - → Hoist BasisFacingZ and MakeMarker into a shared client helper (e.g. HardpointVisuals) and reuse from all three loaders.
  - canonical: `client/scripts/ShipModelLoader.cs:296` · also: `client/scripts/BaseModelLoader.cs:310, client/scripts/MissileView.cs:146, client/scripts/BaseModelLoader.cs:193`

- **[med/high ·unverified(capped)]** `client/scripts/EngineGlow.cs:533` — Radial soft-dot GradientTexture2D reimplemented in 8 VFX files
  - _GradientTexture2D-based radial soft dot built independently in EngineGlow, ExplosionEffect, DustField, Sun, ChaffFx, LensFlare, HitFlash, BaseModelLoader (8 files); per per-segment reviewers 4 bodies are byte-identical._
  - → Provide one cached UiTex.SoftDot()/RadialGradient helper and reuse; caches the texture so 8 allocations collapse to one.
  - canonical: `client/scripts/EngineGlow.cs:533` · also: `client/scripts/ExplosionEffect.cs:256, client/scripts/DustField.cs, client/scripts/HitFlash.cs, client/scripts/ChaffFx.cs, client/scripts/LensFlare.cs, client/scripts/Sun.cs`

- **[med/high ·unverified(capped)]** `factions/src/Allegiance.Factions/Resolution/TechTreeReport.cs:118` — Buildable-kind switch ladder duplicated verbatim
  - _The exact 11-arm type switch (Hull=>"hull", Weapon=>"weapon", ... Drone=>"drone", _=>"buildable") appears in TechTreeReport.KindOf (line 118) and CoreValidator.Describe (line 409). Byte-identical arms._
  - → Extract one Buildable.KindLabel(this Buildable) extension in the Factions model and have both callers use it (Describe just wraps it with the id).
  - canonical: `factions/src/Allegiance.Factions/Validation/CoreValidator.cs:409` · also: `factions/src/Allegiance.Factions/Resolution/TechTreeReport.cs:118`

- **[med/med ✅verified]** `client/scripts/ui/BuildTab.cs:194` — _Process refresh-gate scaffolding duplicated between the two tabs
  - _ResearchTab._Process (217-244) and BuildTab._Process (194-222) share the same throttle/dirty-check state machine: _refreshTimer -= delta; long sig = ComputeStatusSig(...); bool catalogChanged = count != _catalogCount; if(_refreshTimer<=0 || sig!=_statusSig || catalogChanged){ _refreshTimer=0.25; _catalogCount=count; bool structural=...; _statusSig=sig; if(structural) Rebuild...; UpdateHeader; RefreshDetail; }. Plus both hold identical fields (_refreshTimer/_statusSig/_catalogCount) and near-identical ComputeStatusSig folding (ResearchTab.cs:247, BuildTab.cs:225)._
  - → Factor the throttle+signature+structural-rebuild loop into a shared base Control (or a small RefreshGate helper struct) the two tabs derive from, keeping only ComputeStatusSig/RebuildX per tab.
  - canonical: `client/scripts/ui/ResearchTab.cs:217` · also: `client/scripts/ui/ResearchTab.cs:247, client/scripts/ui/BuildTab.cs:225` · refs: BuildTab.cs:194-222 (_Process gate) / 225-248 (ComputeStatusSig) / 43-45 (fields); ResearchTab.cs:217-244 (_Process gate) / 247-262 (ComputeStatusSig) / 56-58 (fields). Shared preamble: sig = team+1L+count*131L then TeamOwnedTechs fold *2654435761L in both ComputeStatusSig. Divergent (intended extension points): BuildTab folds miner count/cap, build-pipeline depth, RequiredCaps, rock discovery; ResearchTab folds AllResearch active/on-deck. No common base class — both `: Control`.

- **[med/med ✅verified]** `server/Net/ClientHub.cs:1087` — HandleOrder re-implements CommanderOrWarn's commander gate
  - _CommanderOrWarn(client) is the established commander-gate pattern (used at ClientHub.cs:807/822/848/897), but HandleOrder (~1087) re-implements the same commander check + warning inline instead of reusing it._
  - → Route HandleOrder through CommanderOrWarn so the gate/warning logic lives in one place.
  - canonical: `server/Net/ClientHub.cs:1011 (CommanderOrWarn)` · also: `server/Net/ClientHub.cs:1011 (CommanderOrWarn)` · refs: CommanderOrWarn defined at server/Net/ClientHub.cs:1011-1022; already reused as the AI-authority gate at ClientHub.cs:807/822/848/897 per the evidence. Inline duplicate is server/Net/ClientHub.cs:1087-1094 inside HandleOrder.

- **[med/med ·unverified(capped)]** `server/Content/FactionsContentProjection.cs:368` — ProjectLauncher's four expendable branches repeat the shared WeaponDef prefix
  - _Lead flags ProjectLauncher (368) building four expendable branches (mine/chaff/probe/dumbfire) that each re-author the common WeaponDef prefix fields before their kind-specific tail._
  - → Build the shared WeaponDef prefix once, then apply only the per-kind tail in each branch.
  - canonical: `server/Content/FactionsContentProjection.cs:368` · also: `server/Content/FactionsContentProjection.cs:424, server/Content/FactionsContentProjection.cs:470`

- **[med/med ·unverified(capped)]** `client/scripts/Chat.cs:325` — ChatLine→BBCode formatting duplicated between Chat.FormatLine and Lobby.RebuildComms
  - _Same three-branch formatting (mute ◆ / gold ★ CMDR ▸ / name: text with color hex + Escape) built in Chat.cs:330-338 and Lobby.cs:1155-1169, differing only in DesignTokens color source vs local hex constants._
  - → Factor one FormatChatLine(ChatLine, palette) helper (parameterize the color source) used by both comms views.
  - canonical: `client/scripts/Chat.cs:325` · also: `client/scripts/Lobby.cs:1155`

- **[med/med ·unverified(capped)]** `server/Sim/Simulation.Constructors.cs:749` — CrossSector + AlignGated gate-align helpers duplicated between Miner and Constructor execute
  - _Near-identical local functions CrossSector(destSector,out input) / AlignGated(input,target) appear in Simulation.cs:1818, Simulation.Constructors.cs:749/759, and the miner path (Simulation.Mining.cs CrossSector); all steer a slow hull through a gate mouth via AutoSteer.SteerToPoint._
  - → Promote a shared private CrossSectorApproach/AlignGated method on the Simulation partial reused by autopilot, miner, and constructor executors.
  - canonical: `server/Sim/Simulation.Mining.cs:845` · also: `server/Sim/Simulation.cs:1818, server/Sim/Simulation.Constructors.cs:759`

- **[low/high ✅verified]** `client/scripts/MissileView.cs:146` — BasisFacingZ duplicated across MissileView, ShipModelLoader, BaseModelLoader
  - _MissileView.BasisFacingZ (line 146) is the same forward->orthonormal-basis construction as ShipModelLoader.BasisFacingZ (line 296) and BaseModelLoader.BasisFacingZ (line 310); the MissileView comment even notes 'the same convention ShipModelLoader.BasisFacingZ uses'. Only difference is ShipModelLoader's extra zero-length guard._
  - → Hoist one shared BasisFacingZ (e.g. onto GlbLoader or a small geometry util) and have all three call it, keeping the zero-length guard.
  - canonical: `client/scripts/ShipModelLoader.cs:296` · also: `client/scripts/BaseModelLoader.cs:310` · refs: Duplicate definitions at client/scripts/MissileView.cs:146, client/scripts/ShipModelLoader.cs:296, client/scripts/BaseModelLoader.cs:310. MissileView's comment explicitly references 'the same convention ShipModelLoader.BasisFacingZ uses'; BaseModelLoader's comment calls itself a 'Mirror of the ship loader's helper'.

- **[low/high ✅verified]** `client/scripts/BaseModelLoader.cs:310` — BasisFacingZ and MakeMarker are byte-identical between the two loaders (note only — deliberately parallel)
  - _BaseModelLoader.BasisFacingZ (line 310) and MakeMarker (line 193) are identical to ShipModelLoader.BasisFacingZ (line 296) and MakeMarker (line 286). The comments state the two loaders are 'deliberately independent parallel files', so this is acknowledged, not accidental._
  - → Leave as-is unless the parallel-file policy changes; if consolidation is ever wanted, the pure-math BasisFacingZ (no Godot-scene state) is the natural candidate to hoist into GlbLoader. Do not merge the placeholder/FX logic.
  - canonical: `client/scripts/ShipModelLoader.cs:296` · also: `client/scripts/BaseModelLoader.cs:310, client/scripts/BaseModelLoader.cs:193, client/scripts/ShipModelLoader.cs:286` · refs: BasisFacingZ: BaseModelLoader.cs:310 and ShipModelLoader.cs:296. MakeMarker: BaseModelLoader.cs:193 and ShipModelLoader.cs:286. Both are private static helpers local to each loader; no external call sites bind them apart. A merge target would be a shared static class (both files already share GlbLoader.FindHardpoints).

- **[low/high ✅verified]** `client/scripts/GameNetClient.cs:199` — Four-cache clear block (_rows/_missileRows/_minefieldRows/_probeRows) repeated at 5 sites
  - _The block clearing _rows + _missileRows + _minefieldRows + _probeRows appears in BeginConnect (199-202), Abort (242-245), Disconnect (280-283), GiveUpShip (300-303), and (three of the four) in ApplyWelcome reconnect (1390-1392). A new per-connection cache added later must be remembered in all of them — the reconnect path in ApplyWelcome already omits _rows, showing the drift risk._
  - → Extract a `ClearEntityCaches()` helper and call it at every reset site so a new cache is cleared everywhere by construction.
  - canonical: `client/scripts/GameNetClient.cs:199` · also: `client/scripts/GameNetClient.cs:242, client/scripts/GameNetClient.cs:280, client/scripts/GameNetClient.cs:300, client/scripts/GameNetClient.cs:1390` · refs: Full-block sites: client/scripts/GameNetClient.cs:199-202 (BeginConnect), :242-245 (Abort), :280-283 (Disconnect), :300-303 (GiveUpShip). Divergent 3-clear site: :1390-1392 (ApplyWelcome reconnect, intentionally omits _rows). Field decls: :88/:101/:105/:110.

- **[low/high ✅verified]** `client/scripts/ui/ResearchTab.cs:1062` — NodeCard badge StyleBoxFlat construction duplicated in Configure and ConfigureMock
  - _Configure (1028-1036) and ConfigureMock (1062-1065) build the identical badge stylebox: new StyleBoxFlat{BgColor=badgeFilled?badgeColor:new Color(badgeColor,0.12f), BorderColor=new Color(badgeColor,0.9f), AntiAliasing=false}; SetCornerRadiusAll(0); SetBorderWidthAll(1); plus the same _badge font-color override. Both paths also compute StyleFor(status) the same way._
  - → Extract a private ApplyBadge(status) helper used by both Configure and ConfigureMock so the badge glyph/color/stylebox live in one place.
  - canonical: `client/scripts/ui/ResearchTab.cs:1028` · also: `client/scripts/ui/ResearchTab.cs:1028` · refs: Duplicated between ResearchTab.cs:1028-1036 (Configure) and ResearchTab.cs:1062-1065 (ConfigureMock); shared StyleFor(status) at 1080-1087 supplies the same badge inputs to both.

- **[low/high ✅verified]** `client/scripts/ui/GameElements.cs:229` — Diamond polygon re-implemented in 3 components instead of UiDraw.Diamond
  - _RadarFrame.DrawSelfDiamond (GameElements.cs:231) builds the exact 4-point {up,right,down,left} diamond polygon that UiDraw.Diamond(ci,center,size,color) already provides; ContactChip._Draw (GameElements.cs:128) and DiamondDivider._Draw (Surfaces.cs:92) inline the same construction. LinkRadar (ConnectFeedback.cs:110) already uses the shared helper._
  - → Replace the three inline diamond polygons with UiDraw.Diamond(this, center, size, color) and delete the private DrawSelfDiamond helper.
  - canonical: `client/scripts/ui/UiDraw.cs:81` · also: `client/scripts/ui/GameElements.cs:128, client/scripts/ui/Surfaces.cs:92` · refs: Canonical: client/scripts/ui/UiDraw.cs:81 (UiDraw.Diamond). Duplicated sites: client/scripts/ui/GameElements.cs:229 (RadarFrame.DrawSelfDiamond), client/scripts/ui/GameElements.cs:128 (ContactChip._Draw), client/scripts/ui/Surfaces.cs:92 (DiamondDivider._Draw). Existing correct usage: client/scripts/ui/ConnectFeedback.cs:110 (LinkRadar).

- **[low/high ✅verified]** `server/Sim/Simulation.Vision.cs:1022` — Ghost snapshot-then-iterate pattern duplicated back-to-back
  - _The block `_ghostScratch.Clear(); foreach kv in tv.Ghosts add value; foreach g in _ghostScratch {...}` appears at 1022-1043 (invalidation) and again at 1049-1060 (timeout), snapshotting the same tv.Ghosts twice in one apply._
  - → Build the _ghostScratch snapshot once and run both the invalidation and timeout checks in a single pass over it (or a shared local helper), halving the copy and the enumeration.
  - canonical: `server/Sim/Simulation.Vision.cs:1022` · also: `server/Sim/Simulation.Vision.cs:1049` · refs: server/Sim/Simulation.Vision.cs:1022 (invalidation snapshot loop) and server/Sim/Simulation.Vision.cs:1049 (timeout snapshot loop) — same tv.Ghosts, same _ghostScratch buffer

- **[low/high ✅verified]** `server/Sim/Simulation.Constructors.cs:666` — ConstructorHoldDistance and MinerHoldDistance have identical bodies
  - _Constructors.cs:666 `ConstructorHoldDistance(float rockR) => MathF.Max(rockR * 1.1f, rockR + World.ShipRadius + 6f)` is character-identical to Mining.cs:789 `MinerHoldDistance`._
  - → Replace both with a single shared private helper (e.g. RockHoldDistance(float rockR)) so the standoff-shell formula is single-sourced.
  - canonical: `server/Sim/Simulation.Mining.cs:789` · also: `server/Sim/Simulation.Mining.cs:789` · refs: ConstructorHoldDistance at server/Sim/Simulation.Constructors.cs:666; MinerHoldDistance at server/Sim/Simulation.Mining.cs:789

- **[low/high ✅verified]** `client/scripts/Chat.cs:341` — BBCode Escape() helper duplicated verbatim in Chat and Lobby
  - _`private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");` is identical at Chat.cs:341 and Lobby.cs:1230._
  - → Make Chat.Escape internal/static-shared and have Lobby call it.
  - canonical: `client/scripts/Chat.cs:341 (or a shared BBCode util)` · also: `client/scripts/Lobby.cs:1230` · refs: Chat.cs:341 and Lobby.cs:1230 (both private static Escape). Used at Chat.cs:330,334,337,338 and within Lobby.cs display formatting.

- **[low/high ✅verified]** `client/scripts/CameraRig.cs:114` — 5-flag inputFree modal gate copy-pasted across client
  - _`bool n = !Chat.Capturing && !SectorOverview.Active && !ShipLoadout.Active && !EscapeMenu.Active && !SettingsDialog.Active;` appears verbatim in CameraRig.cs, ZoomView.cs, and Hud.cs (comments acknowledge the copied idiom)._
  - → Extract a single static property so a new modal only needs adding once; today a new overlay must be added to every copy.
  - canonical: `one shared static predicate (e.g. InputGate.FlightInputFree)` · also: `client/scripts/ZoomView.cs, client/scripts/Hud.cs` · refs: Canonical target: introduce one shared static bool predicate (e.g. InputGate.FlightInputFree) and call it from CameraRig.cs:114, ZoomView.cs:142, Hud.cs:327. Note: SectorOverview.cs:1105 and ShipController.cs:401/502/517 use OVERLAPPING but DIFFERENT flag subsets (they add/omit flags), so they are NOT part of this exact-duplicate cluster and should not be folded into the same predicate.

- **[low/high ✅verified]** `client/scripts/GameNetClient.cs:200` — Four-cache clear block repeated at multiple teardown sites
  - _`_rows/_missileRows.Clear(); _minefieldRows.Clear(); _probeRows.Clear();` block repeats at GameNetClient.cs lines ~200, 243, 281, 301._
  - → Extract a ClearEntityCaches() helper so adding a future entity cache updates one place.
  - canonical: `client/scripts/GameNetClient.cs (single ClearEntityCaches() method)` · also: `client/scripts/GameNetClient.cs:243, client/scripts/GameNetClient.cs:281, client/scripts/GameNetClient.cs:301` · refs: Duplicate sites: client/scripts/GameNetClient.cs lines 199-202, 242-245, 280-283, 300-303. Non-substitutable partial (exclude): lines 1390-1392. No existing ClearEntityCaches() method (grep confirms none).

- **[low/high]** `client/scripts/WeaponsPanel.cs:279` — Missile lock-state decode + status-string ladder duplicated across two draw methods
  - _The `byte ls = _net.LocalLockState; bool locked = (ls & 0x80)!=0; int prog = ls & 0x7F; int ammo = _net.LocalMissileAmmo;` decode plus the EMPTY/LOCKED/LOCK n%/READY status tuple appears in DrawSecondaryRow (279-289) and again nearly verbatim in DrawHeaderStatus (320-328). TargetMarkers.DrawLockArc (988-990) repeats the same bit decode a third time._
  - → Add a small helper returning (bool locked, int progressPct) from LocalLockState, and a shared LauncherStatus(ammo, locked, prog) -> (string,Color,bool pulse) used by both WeaponsPanel draw sites.
  - canonical: `client/scripts/WeaponsPanel.cs:279` · also: `client/scripts/WeaponsPanel.cs:320, client/scripts/TargetMarkers.cs:988`

- **[low/high]** `client/scripts/Chat.cs:341` — BBCode Escape() helper duplicated verbatim in Chat.cs and Lobby.cs
  - _Identical one-liner `private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");` at Chat.cs:341 and Lobby.cs:1230, and there is no shared BBCode-escape helper anywhere in client/scripts/ui/ (grep found only these two)._
  - → Move Escape into a shared static (UiKit or the extracted ChatFormat helper) and reference it from both files.
  - canonical: `client/scripts/Chat.cs:341` · also: `client/scripts/Lobby.cs:1230`

- **[low/high]** `client/scripts/Lobby.cs:1306` — Solid-accent chip builder duplicated (Lobby.Chip vs ServerLobbyOverlay.MakeChip)
  - _Lobby.Chip (1306-1317) and ServerLobbyOverlay.MakeChip (228-240) both build the same design 'active-tab' chip: StyleBoxFlat{BgColor=DesignTokens.TeamAccent, AntiAliasing=false}, corner radius 0, symmetric content margins, and a Void-colored Label caption. Confirmed both set `BgColor = DesignTokens.TeamAccent`._
  - → Add a single accent-chip factory to the shared ui/ library (UiKit) and call it from both overlays instead of maintaining two near-identical builders.
  - canonical: `client/scripts/ServerLobbyOverlay.cs:228` · also: `client/scripts/Lobby.cs:1306`

- **[low/high]** `server/Sim/Simulation.cs:1890` — AutopilotStep repeats the approach→Arrived→disengage triple across three destination kinds
  - _The enemy-base (L1890-1893), rock (L1905-1908), and waypoint (L1914-1917) cases each call Approach(...), then `if (Arrived(myPos, s.State.Vel, target, standoff*1.2f)) s.ApEngaged = false;` with the same 1.2f standoff multiplier magic constant._
  - → Add a local helper `ShipInputState ArriveAt(Vec3 point, float standoff)` that does the Approach + Arrived-disengage and names the 1.2f arrival-band factor, and call it from the three point-destination cases.

- **[low/high]** `server/Net/ClientHub.cs:1144` — Team-scoped 'build-once-then-send-to-team' loop duplicated
  - _SystemToTeam (1144-1153) and the OrderDirectivesThisStep block (1278-1287) both implement `byte[]? frame=null; foreach (_clients.Values) if (_lobby.TeamOf(c.Id)==team) { frame ??= Protocol.BuildChatRelay(...); SendReliable(c, OutFrame.Whole(frame)); }` — identical structure, only the BuildChatRelay args differ._
  - → Extract `SendToTeam(byte team, Func<byte[]>/localfn buildFrame)` (lazy build inside) and call it from both sites; keeps the single-build-per-team optimization in one place.
  - canonical: `server/Net/ClientHub.cs:1144`

- **[low/high]** `server/Content/FactionsContentProjection.cs:259` — Radar-signature 'authored 0/omitted -> 1.0' rule copy-pasted 4×
  - _The identical `X.Signature <= 0 ? 1f : (float)X.Signature` default appears 4 times (hull RadarSignature:259, base RadarSignature:512, mine MineSignature:424, probe ProbeSignature:470). Same rule authored verbatim each place._
  - → Add one small helper `static float Sig(double v) => v <= 0 ? 1f : (float)v;` and call it at all four sites so the 'neutral 1.0' rule lives once.
  - canonical: `server/Content/FactionsContentProjection.cs:259` · also: `server/Content/FactionsContentProjection.cs:512, 424, 470`

- **[low/high]** `factions/src/Allegiance.Factions/Resolution/TechTreeReport.cs:65` — "Buildables that grant tech t" predicate duplicated in DescribeTech and DescribeBuildable
  - _The predicate `core.AllBuildables().Where(b => b.GrantedTechs.Contains(t) || (b is Station s && s.LocalTechs.Contains(t)))` appears at line 65-66 (DescribeTech) and again at line 93-94 (DescribeBuildable)._
  - → Extract a local function `IEnumerable<Buildable> GrantersOf(string techId)` (or a static helper on TechTreeReport) and call it from both sites.

- **[low/high]** `public-lobby/PublicLobby.cs:66` — Identical name-validation BadRequest body duplicated within POST /servers
  - _The `new { error = $"name must be {NameMin}-{NameMax} characters" }` payload appears verbatim at lines 67 and 78 in the same handler — once for the pre-check and once for the registry.Register null return, which can only be null for the same name-invalid reason._
  - → Hoist the error result into one local (e.g. `var nameErr = Results.BadRequest(new { error = ... });`) or drop the redundant second check, since NormalizeName already gated the name before Register runs.
  - canonical: `public-lobby/PublicLobby.cs:67` · also: `public-lobby/PublicLobby.cs:78`

- **[low/high ·unverified(capped)]** `shared/Collision/Collide.cs:165` — AsteroidSphere / ProbeSphere / BuildSphere static-body factories have byte-identical bodies
  - _All three are `new(null, center, Quat.Identity, 1f, radius, -1, null, null)` (Collide.cs:165, 185, 191). Deliberately parallel per their comments, so this is a note-only observation — the distinct names document intent (asteroid vs probe vs build shell)._
  - → Optional: route the three through one private `TeamlessSphere(center, radius)` factory to keep the shape in sync, retaining the named public entry points. Low value — do not merge the public names.
  - canonical: `shared/Collision/Collide.cs:165` · also: `shared/Collision/Collide.cs:185, shared/Collision/Collide.cs:191`

- **[low/high ·unverified(capped)]** `server/Sim/Simulation.Pig.cs:1181` — NormalizeOr(Vec3,Vec3) duplicated between Simulation.Pig and shared/AutoSteer
  - _`NormalizeOr(Vec3 v, Vec3 fallback)` at server/Sim/Simulation.Pig.cs:1181 is the same length>epsilon normalize-else-fallback as shared/AutoSteer.cs:427._
  - → Reuse the shared AutoSteer/FlightModel normalize helper instead of the server-private copy (identical float math, no sim-output change).
  - canonical: `shared/AutoSteer.cs:427 (NormalizeOr)` · also: `server/Sim/Simulation.Pig.cs:1181`

- **[low/high ·unverified(capped)]** `client/scripts/ui/ResearchTab.cs:1125` — Countdown 'ticks -> mm:ss' formatting copy-pasted across 3 widgets
  - _`$"{t / 60:00}:{t % 60:00}"` at TechDetailPanel.cs:290, ResearchTab.cs:1125 and ResearchTab.cs:1287 (differ only by a leading glyph)._
  - → One static FormatMmss(int seconds) helper in a shared UI util; callers prepend their own glyph.
  - canonical: `client/scripts/ui/TechDetailPanel.cs:290` · also: `client/scripts/ui/ResearchTab.cs:1125, client/scripts/ui/ResearchTab.cs:1287`

- **[low/high ·unverified(capped)]** `client/scripts/UserPrefs.cs:252` — Save-and-log-error block copy-pasted across 8 UserPrefs setters
  - _`var err = Cfg.Save(Path); if (err != Error.Ok) GD.PushError(...)` repeated at lines 64, 88, 137, 177, 190, 205, 254, 305._
  - → Extract one private static SaveOrLog() and call it at the end of each setter.
  - canonical: `client/scripts/UserPrefs.cs:252` · also: `client/scripts/UserPrefs.cs:64, client/scripts/UserPrefs.cs:137, client/scripts/UserPrefs.cs:305`

- **[low/med ✅verified]** `client/scripts/ui/ShipLoadout.cs:726` — Team-can-field predicate (required-tech owned AND not obsoleted) reimplemented across hangar tabs
  - _RefreshArsenal filters with `ObsoletedByTechIdx.Any(TeamOwnsTech)` (726) and `!RequiredTechIdx.All(TeamOwnsTech)` (730). The same 'owned required tech and not obsoleted' availability test is re-written in BuildTab.cs:344-346 and ResearchTab.cs:487-488 for other def kinds._
  - → Provide a shared helper such as `WorldRenderer.TeamCanUse(team, requiredTechIdx, obsoletedByTechIdx)` and call it from the arsenal filter (and the sibling tabs) instead of open-coding the two LINQ tests each time.
  - also: `client/scripts/ui/BuildTab.cs:344, client/scripts/ui/ResearchTab.cs:487` · refs: Canonical/other sites: BuildTab.cs:339-348 (IsAvailable) and ResearchTab.cs:466-491 (StatusOf), both including an extra RequiredCaps clause. Server-side authoritative counterparts (BuildableResolver, Simulation.ResolveLoadout) are separate and untouched by any client-side merge.

- **[low/med ✅verified]** `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs:60` — Identical ModelName property redeclared on all four launcher-fed expendables
  - _`public string? ModelName { get; set; }` is declared separately on Missile.cs:60, Mine.cs:34, Chaff.cs:17 and Probe.cs:37; the Expendable base (Expendable.cs) has none. Only the doc comment's asset folder differs._
  - → Hoist ModelName to the Expendable base record (kebab name `model-name` is unchanged; FuelPod inheriting an omit-when-null field is harmless).
  - canonical: `factions/src/Allegiance.Factions/Model/Expendables/Expendable.cs:44` · also: `factions/src/Allegiance.Factions/Model/Expendables/Mine.cs:34, Chaff.cs:17, Probe.cs:37` · refs: Duplicate declarations: Missile.cs:60, Mine.cs:34, Chaff.cs:17, Probe.cs:37. Hoist target: Expendable.cs runtime-extension block (after line 60). Read sites unaffected by hoist: CoreValidator.cs:219 (probe.ModelName), server-side projection onto WeaponDef.ModelName.

- **[low/med ✅verified]** `client/scripts/ui/GameElements.cs:229` — Diamond polygon re-implemented instead of UiDraw.Diamond
  - _GameElements.DrawSelfDiamond (line 229) hand-builds a diamond polygon while ui/UiDraw.cs:81 already provides `Diamond(CanvasItem, center, size, color)`._
  - → Call UiDraw.Diamond from GameElements (and any other hand-rolled diamond sites).
  - canonical: `client/scripts/ui/UiDraw.cs:81` · also: `client/scripts/ui/UiDraw.cs:81` · refs: Called only within GameElements.cs at lines 224 and 226 (DrawSelfDiamond); canonical UiDraw.Diamond at client/scripts/ui/UiDraw.cs:81.

- **[low/med]** `server/Net/WebRtcListener.cs:167` — RTCPeerConnectionState terminal-state set duplicated across transport and listener
  - _The 'connection is done' predicate `s is RTCPeerConnectionState.closed or .failed or .disconnected` appears in WebRtcTransport's ctor (WebRtcListener.cs:30) and again in AnswerOffer's onconnectionstatechange (WebRtcListener.cs:167-172). Two hand-maintained copies of the same three-state set risk drifting._
  - → Hoist a `static bool IsTerminal(RTCPeerConnectionState s)` helper and use it in both handlers so the terminal-state definition lives once.
  - canonical: `server/Net/WebRtcListener.cs:30`

- **[low/med]** `server/Content/FactionsContentProjection.cs:367` — ProjectLauncher's four expendable branches repeat the shared WeaponDef prefix
  - _The Missile/Mine/Chaff/Probe branches (lines 367-473) each re-assign the same launcher-common fields: WeaponId, Name, RequiredTechIdx, ObsoletedByTechIdx, SucceededByWeaponId, Mass=(float)l.Mass, FireIntervalTicks=l.FireIntervalTicks, MagazineSize=(byte)l.Amount. Eight identical assignments duplicated across four near-parallel object initializers._
  - → Build a base WeaponDef with the launcher-common fields once (or a local `Common()` factory), then set only the kind-specific fields per branch — reduces four ~15-line initializers to their real differences.
  - canonical: `server/Content/FactionsContentProjection.cs:368` · also: `server/Content/FactionsContentProjection.cs:404, 430, 449`

- **[low/med]** `shared/Collision/Collide.cs:165` — AsteroidSphere / ProbeSphere / BuildSphere have byte-identical bodies
  - _StaticBody.AsteroidSphere (165), ProbeSphere (185) and BuildSphere (191) all construct `new(null, center, Quat.Identity, 1f, radius, -1, null, null)` — three identical factories distinguished only by name/comment._
  - → Optional: collapse to a single private `SolidSphere(center, radius, team=-1)` helper that the three named factories delegate to, preserving the documented call-site names. Low priority — the distinct names intentionally document intent, so treat as note-only if readability is valued over dedup.
  - also: `shared/Collision/Collide.cs:185, shared/Collision/Collide.cs:191`

- **[low/med ·unverified(capped)]** `server/Net/Protocol.cs:1108` — Base-health wire record (u64 id + f32 health) hand-inlined with a bare magic '12' size
  - _BuildBases (1108) sizes `2 + Bases.Count * 12` and writes id (o+=8) then BaseHealth (o+=4) by hand; the same id+health pair is written elsewhere via WriteBaseStatic (651/844) and in the fog-gated Welcome loops, so the 12-byte record layout is expressed in more than one place with a bare literal._
  - → Introduce a `WriteBaseHealthRecord(span/writer, id, health)` (and a named `BaseHealthRecordSize = 12`) reused by BuildBases and the static writers so the record can't drift.
  - canonical: `server/Net/Protocol.cs:1108` · also: `server/Net/Protocol.cs:651, server/Net/Protocol.cs:844`

## Messy code (22)

- **[high/high ✅verified]** `client/scripts/TargetMarkers.cs:602` — _Draw is a ~325-line god-method mixing a dozen unrelated draw passes
  - __Draw spans lines 602-926 (~325 lines), far over the ~150 guideline, sequencing focused-base resolution, base pass, focused base/asteroid, rock labels, waypoint, alephs, probes, minefields, ghosts, friendly ships, enemy ships, focus tags, and the ship-centric aim/lead/missile-banner block inline. Nesting reaches 4-5 (foreach → if onScreen → if focused → …)._
  - → Extract cohesive passes into private helpers (DrawBasesPass, DrawRockLabelsPass, DrawShipsPass, DrawFiringSolution) that _Draw calls in order, matching the existing per-feature helper style already used for DrawGhosts/DrawWaypoint/DrawIncomingWarning.
  - refs: client/scripts/TargetMarkers.cs:602-926 (method body); individual passes at lines listed in rationale. Draw work is delegated to helpers (DrawEntity, DrawFocusTag, DrawLockArc, DrawRockDetail, DrawGhosts, DrawIncomingWarning, DrawAutopilotStatus at 932+), but the orchestration/sequencing of all passes is inline in _Draw.

- **[high/high ✅verified]** `client/scripts/ShipController.cs:350` — _Process is a ~265-line god-method mixing spawn, mouse-capture, autopilot, prediction and debug
  - __Process spans lines 350-615 (~265 lines) and interleaves at least six unrelated responsibilities: input sampling, autofly/hangar-demo bootstrap, spawn-gate + buy pre-check, mouse-capture toggling, afterburner + autopilot handback, the fixed-dt prediction/send loop, and the P-key divergence debug hook. Deep nesting inside the spawn branch (430-469) and the prediction while-loop (579-600)._
  - → Extract cohesive helpers called in sequence from _Process: TickSpawn(connected, hasShip, delta), TickAutopilotAndBoost(pc), and StepPrediction(pc, delta). Each currently reads/writes a small, well-defined subset of fields, so the split is mechanical and leaves the ordering intact.
  - refs: client/scripts/ShipController.cs:350-615

- **[high/high ✅verified]** `server/Net/ClientHub.cs:1230` — AfterStep() is a ~600-line god-method mixing a dozen responsibilities
  - _AfterStep spans lines 1230-1831 (~601 lines). It handles queue-pressure logging, phase-transition broadcasts, 5 separate notice-drain loops, record+missile serialization, AOI index rebuild, preparation of ~14 distinct broadcast/per-team frame sets, the entire per-client send loop, and the snapshot fan-out — all inline. Nesting inside the per-client `fog` block reaches depth 5._
  - → Extract cohesive stages into private methods, e.g. DrainStepNotices(), PrepareBroadcastFrames() (returning a per-tick struct), SendPerClientFrames(client, frames), and FanOutSnapshots(n). Keeps AfterStep as a readable orchestration outline.
  - refs: server/Net/ClientHub.cs:1230-1831 (AfterStep); RebuildAoiIndex starts at 1837

- **[med/high ✅verified]** `server/Sim/Simulation.cs:1931` — DockApproach is a ~239-line method with a deeply nested 3-case state machine
  - _DockApproach spans L1931-2169 (~239 lines). Each switch case (Align/Creep/Transit) inlines its own FaceAndRollAnticipated call, facing/roll/lateral-offset math, and demotion guards; the Transit default case alone nests switch>case>if(onAxis)>else>if(SegmentEntersSphere)>else to 5+ levels. One method mixes door-geometry resolution, per-phase steering, and transition guards._
  - → Split each phase into its own helper (e.g. DockAlign/DockCreep/DockTransit) returning the ShipInputState and mutating ApDockPhase, keeping DockApproach as the geometry setup + dispatch. Reduces nesting and isolates each phase's guards.
  - refs: server/Sim/Simulation.cs:1931-2169

- **[med/high ✅verified]** `server/Sim/Simulation.Vision.cs:941` — ApplyVisionResult is ~160 lines with 7 distinct responsibilities
  - _Method spans lines 941-1100 (~159 lines) and mixes: rebuild radar/eyeball sets, leave-diff, set swap, eyeball soft-track, probe visibility swap, mine visibility swap, ghost invalidation, ghost timeout, and static-discovery/base-health merge — nesting reaches 4+ (team loop → foreach ghost → if)._
  - → Extract cohesive private helpers, e.g. SwapStreamedSets, RefreshEyeballGhosts, InvalidateStaleGhosts(tv,tick), ExpireTimedOutGhosts(tv,tick), MergeDiscoveredStatics(tv,r), each taking the per-team tv/r; the outer method becomes a readable sequence.
  - refs: Method body lines 941-1100; nested ghost blocks at 1022-1043 and 1049-1060; discovery lock block 1065-1097

- **[med/high ✅verified]** `server/Sim/Simulation.Mining.cs:351` — MinerBrainStep is ~173 lines with 4-5 levels of nesting in the Prospect case
  - _MinerBrainStep spans lines 351-524 (~173 lines). The Prospect case (436-511) nests foreach > switch-case > if(ProspectPatrol) > if(PickRock)/if(NextProspectPoint) reaching 4-5 indent levels, mixing retreat guards, entry-anchoring, patrol sweep, and journey-arrival logic in one arm._
  - → Extract the Prospect-state handling into a private method (e.g. StepProspectingMiner(slot, s, tick)) and consider pulling the shared retreat/full guards out of the per-state cases.
  - refs: server/Sim/Simulation.Mining.cs:351-524 (method); Prospect case 436-511

- **[med/high ✅verified]** `server/Net/ClientHub.cs:494` — MsgHello parsing is a 5-deep nested-if length-check pyramid
  - _The MsgHello case (lines 494-526) parses secretLen/nameLen/tokLen via `if (count > 1) { if (count >= o+1) { if (count >= o+nameLen) { if (count >= o+1) { if (tokLen>0 ...) }}}}` — nesting depth 5, well past the >4 threshold, inside an already ~384-line ReceiveLoop (481-865)._
  - → Extract a `TryParseHello(ReadOnlySpan<byte> frame, out string secret, out string name, out string token)` helper using a running cursor with early-return guards instead of nested ifs; ReceiveLoop's case then reads flat.
  - refs: server/Net/ClientHub.cs:494-526 (MsgHello parse pyramid); ReceiveLoop 481-865

- **[med/high ✅verified]** `shared/Collision/ConvexHull.cs:70` — ResolveSphere faceIndex overload is unused; its doc comment is stale/misleading
  - _The 5-arg overload `ResolveSphere(..., out int faceIndex)` computes `faceIndex` (line 95) but a repo-wide grep for a 5-arg call (`out int`) returns ZERO callers — every call site uses the 4-arg overload which passes `out _` (server/Assets/SelfTest.cs, client/CollisionWorld indirectly, tests/CollisionTest). The comment lines 70-72 claim 'The base docking gate uses it: a sphere whose contact face is the bay-cap doorway docks instead of bouncing', but docking is actually gated by DockFace geometry (Collide.IntersectsDockFace / DockFaceParser), never by faceIndex._
  - → Delete the `out int faceIndex` overload and fold its body into the 4-arg ResolveSphere (drop the faceIndex bookkeeping), or if kept, correct the stale comment — no caller consumes the contact face index and the described docking behavior was replaced by the DockFace path.
  - also: `shared/Collision/ConvexHull.cs:73` · refs: Only definition at shared/Collision/ConvexHull.cs:73; docking actually gated by Collide.IntersectsDockFace / DockFaceParser (tests/CollisionTest/Program.cs:235,245; BaseDockFaces in tests/MissileTest/Program.cs:1080). No faceIndex consumers exist anywhere.

- **[med/high ✅verified]** `factions/src/Allegiance.Factions/Validation/CoreValidator.cs:12` — CoreValidator.Validate is a ~349-line god-method mixing every validation pass
  - _The single Validate method spans lines 12-361 (~349 lines, well over the ~150 guideline) and interleaves id-uniqueness, hull payload, launcher/expendable-kind dispatch, afterburner/fuel, cargo, station, development-upgrade, drone and faction checks in one body with local dictionaries built inline._
  - → Extract cohesive private passes (ValidateHulls, ValidateLaunchers, ValidateFuelAndCargo, ValidateStations, ValidateDevelopments, ValidateFactions) each taking the Core/result and prebuilt id sets, and call them in sequence from Validate.
  - refs: factions/src/Allegiance.Factions/Validation/CoreValidator.cs:12-361 (single Validate method)

- **[med/high]** `client/scripts/WorldRenderer.cs:3012` — UpdateBuildSpheres is a ~130-line method with mixed responsibilities
  - _UpdateBuildSpheres spans lines 3012-3142 (~130 lines) at nesting depth ~5 and interleaves six concerns in one loop body: sphere creation/growth, build-sphere collision registration, core opacity ramp, constructor debris spray lifecycle, drone HideForBuild latching, and rock-dissolve dimming — plus two separate prune loops._
  - → Extract per-concern private helpers (e.g. UpdateSphereGeometry, UpdateConstructorDebris, DissolveBuildRock) called from the loop, so each responsibility is independently readable.

- **[med/high]** `client/scripts/ExplosionEffect.cs:42` — Stale/contradictory comment on CreateBlast: describes a 'Track-0 stub' that no longer exists
  - _The doc comment (lines 42-45) says 'the Track-0 stub reproduces today's Scout-scale visual EXACTLY regardless of radius, so the impact FX is behaviourally unchanged until Track A lands.' But the code at line 53 actually implements the warhead scaling `_classScale = Mathf.Clamp(blastRadius / 25f, 0.6f, 3.0f)` — i.e. Track A has landed and the FX IS radius-dependent. The outer comment directly contradicts both the code and the inner comment (lines 50-52)._
  - → Delete the stale Track-0/Track-A framing from the summary comment so it just documents the blastRadius/25 reference-radius scaling; optionally name the 25f reference radius as a const (e.g. SeekerReferenceRadius).

- **[med/high]** `client/scripts/SectorOverview.cs:930` — HandleMapClick is ~130 lines with deeply nested command-fan-out branches
  - _HandleMapClick spans 930-1059 with nesting to 5 levels (minimap branch → if engage → if group>0||ownSelected → foreach subject → SendOrder) and three separate order fan-out loops that each repeat the SendOrder + _orderedPoints.Remove + _orderedRocks.Remove bookkeeping._
  - → Extract the minimap-sector command block and the entity/point command block into helpers (e.g. IssueSectorOrder, IssueEntityOrder), and factor the repeated 'SendOrder + clear ordered-point/rock intent' into a single SendAndSupersede(subject, …) local function.

- **[med/high]** `client/scripts/ui/ShipLoadout.cs:338` — OnLaunch/header comments claim loadout+base are display-only and never shipped, but they now ride MsgSpawn
  - _OnLaunch comment (lines 338-343) states 'The local weapon/cargo assignments do NOT ship with the request' and 'The launch-base pick ... is display-only until Phase B wires it into MsgSpawn'. The class header (lines 20-23) and LoadoutState.cs:26-27 repeat this. But ShipController.RequestSpawn (ShipController.cs:449-458) actually ships CargoFor, WeaponOverridesFor, and SelectedBaseId on MsgSpawn — Phase B is done. The comments are stale and directly contradict the live wire path._
  - → Update the OnLaunch comment block, the class-header note (lines 22-23), and LoadoutState.cs:26-27 to reflect that cargo counts, weapon-slot overrides, and the launch-base id now all ride MsgSpawn.
  - also: `client/scripts/ui/LoadoutState.cs:26, client/scripts/ShipController.cs:458`

- **[med/high]** `server/Net/Protocol.cs:391` — BuildProbeGone doc comment says '19 bytes' but the frame is 18 bytes
  - _Comment reads 'A probe was removed (19 bytes, mirrors BuildMissileGone exactly)'. The body allocates `new byte[18]` and ends with `return buf; // o == 18`. The '19' is actually the message-id value (MsgProbeGone=19) leaking into a byte-count claim; BuildMissileGone it mirrors is 18 bytes. Misleading size comment on a wire builder._
  - → Change the prose to '18 bytes' (keep the layout prefix '[19]' which is the message id). No wire change.

- **[med/high]** `shared/ShipKind.cs:17` — Stale 'RESERVED' comment on ShipKind.Constructor — feature now fully implemented
  - _The Constructor member is annotated "RESERVED — enum value + wire round-trip only; no spawn/brain/build behavior yet", but ShipKind.Constructor now drives real spawn/brain/build logic: server/Sim/Simulation.Constructors.cs:483 spawns it, Simulation.cs:1700/2239/3344 branch on it, and the client (GameNetClient.cs:2134, RemoteShip.cs:43, TargetMarkers.cs:1108) renders it. The comment misleads any reader about the enum's status._
  - → Update the comment to describe the constructor drone role (AI base-builder, server/Sim/Simulation.Constructors.cs) instead of claiming it is unimplemented.

- **[med/med ✅verified]** `server/Net/Protocol.cs:1299` — BuildDefs is a ~230-line method serializing six unrelated def catalogs inline
  - _BuildDefs spans lines 1299-1530 (~230 lines, well over the ~150 threshold), writing ships, weapons, cargo, bases, techs, developments, stations and faction identity all in one linear body. Mixed responsibilities make the wire order hard to audit against the client reader._
  - → Extract per-section private writers (WriteShipDefs/WriteWeaponDefs/WriteCargoDefs/WriteBaseDefs/WriteTechCatalog) each taking the BinaryWriter; pure mechanical extraction, no byte change. Keeps each block reviewable against its ApplyDefs mirror.
  - refs: Method at server/Net/Protocol.cs:1299-1530. Small helpers WriteHardpoints/WriteString/WriteTechList/WriteCapList/WriteAttrList are factored out for field-level encoding, but each catalog's iteration and block layout is written inline in BuildDefs itself.

- **[med/med ✅verified]** `server/Sim/World.cs:325` — World constructor is ~168 lines mixing many world-gen phases
  - _The World(...) ctor spans lines 325-492 (~168 lines, over the ~150 guideline). It inlines sector build, garrison placement + team validation, base-health seeding, economy seeding, asteroid seeding, gate/aleph seeding, dust seeding, rock-grid bucketing, ore/variant assignment, and model loading. Several blocks (garrison fallback, team-count validation) are self-contained._
  - → Extract cohesive phases into private methods (e.g. SeedSectors, SeedGarrisons+ValidateTeams, SeedGatesAndDust, BucketRockGrid) so the ctor reads as a sequence of named steps.
  - refs: server/Sim/World.cs:325-492 (constructor body)

- **[med/med ✅verified]** `public-lobby/ServerRegistry.cs:128` — NormalizeState reimplements trim/cap but skips control-char stripping done for every other broadcast label
  - _CleanShortText (line 140) strips control chars for hostedBy and roster names before they reach the public list JSON, but NormalizeState (line 128) only trims and caps at 20 — so a server-supplied State like "lobby" reaches SSE/GET /servers with control chars intact, inconsistent with the stated 'public service' hygiene intent._
  - → Route state through the shared helper: `static string? NormalizeState(string? state) => CleanShortText(state, 20);` (or add control-char stripping) so all broadcast short-text goes through one sanitizer.
  - refs: CleanShortText (line 140) is the shared hygiene helper; called via NormalizeHostedBy (line 135) for hostedBy and SanitizeRoster (line 164) for roster names. State normalization path: NormalizeState (line 128) -> updated.State (line 106) -> _bus.Publish (line 123).

- **[low/high]** `client/scripts/ui/ResearchTab.cs:801` — CommanderName fetches the commander id then discards it, always returning a constant
  - _CommanderName (801-806) calls _net.CommanderIdOf(Team) into id but never uses the value except id>=0, returning the literal "the commander" (or ""). The method name implies it resolves a friendly name; it cannot. The caller at line 695 formats "Only the commander can authorize research."_
  - → Either resolve a real display name from the id, or replace the method with a simple bool HasCommander(Team) and inline the fixed 'the commander' string, dropping the misleading name.

- **[low/high]** `client/scripts/ui/SettingsDialog.cs:8` — Stale header comment states wrong modal dimensions
  - _The class doc comment (l.8-9) describes a 'centred 720×560 bracket panel', but BuildUi sets the panel CustomMinimumSize to new Vector2(1080, 840) (l.140). The comment misleads on the actual layout size._
  - → Update the comment to the real size (1080×840) or drop the specific numbers so the doc can't drift from the code again.

- **[low/high]** `server/Sim/Simulation.Research.cs:300` — Stale comment references nonexistent method Simulation.Constructors.CompleteBuild
  - _MaybePreUpgradeSpawnedBase's comment says 'Called from the constructor build-completion path (Simulation.Constructors.CompleteBuild).' but the actual completion method is CompleteConstruction (Constructors.cs:610); there is no CompleteBuild anywhere in the repo (grep returns only this comment)._
  - → Update the comment to reference Simulation.Constructors.CompleteConstruction.

- **[low/med]** `server/Sim/Simulation.Orders.cs:109` — ApplyCommandOrder is ~185 lines dominated by one large targetKind switch
  - _ApplyCommandOrder spans lines 109-294 (~185 lines); after the miner/constructor dispatch it carries a five-arm switch (OrderTargetShip/Base/Rock/Point/Sector) each building a PigOrder inline, mixing validation, fog gating, directive text, and order construction._
  - → Extract the combat-pig branch into ApplyPigCommandOrder(...) mirroring the existing ApplyMinerCommandOrder / ApplyConstructorCommandOrder split, so each subject type owns one method.

## Refactor opportunities (22)

- **[med/high]** `server/Sim/Simulation.cs:866` — Step's boundary/base-collision/dock inner loop (~87 lines, 5-deep nesting) should be extracted
  - _Inside the 275-line Step() (L718-992), the foreach at L866-952 does boundary erosion, asteroid/build-sphere/deployable collisions, and the enemy-bounce / own-base dock-face-or-solid-shell resolution nested foreach s > foreach b > if(BaseHullOf!=null) > if(IntersectsDockFace) > if(IsMiner) — 5 levels deep in the middle of the tick orchestrator._
  - → Extract the per-ship body of this loop into `ResolveBoundaryCollisionsAndDocking(ShipSim s, uint tick, float dt)` so Step reads as a sequence of pass calls; the own-base branch (L884-946) is itself a good sub-extract.

- **[med/high]** `server/Sim/Simulation.Mining.cs:662` — RockEligible and RockIneligibleReason are parallel if-ladders that must be kept in lockstep
  - _RockIneligibleReason (Mining.cs 662-678) re-implements the exact same six checks in the same order as RockEligible (Mining.cs 643-657), only to phrase them as chat text. The header comment even states they must stay 'the SAME checks in the SAME order' — a hazard: any change to eligibility rules must be edited in two places or they silently diverge._
  - → Make RockIneligibleReason the single source (returns "" when eligible) and define `RockEligible(team, id) => RockIneligibleReason(team, id).Length == 0`, eliminating the duplicated ladder.
  - canonical: `server/Sim/Simulation.Mining.cs:643` · also: `server/Sim/Simulation.Mining.cs:643`

- **[med/med ✅verified]** `client/scripts/ui/ServerPasswordModal.cs:62` — Modal scaffold (scrim + center + BracketPanel + ✕ header) duplicated across three modals
  - _ServerPasswordModal.BuildUi (l.62-96), SettingsDialog.BuildUi (l.121-152) and EscapeMenu._Ready (l.51-79) each repeat the same boilerplate: SetAnchorsAndOffsetsPreset(FullRect) + MouseFilter.Stop + UiTheme.Apply + UiFonts.EnsureLoaded + a full-rect Scrim ColorRect + a CenterContainer(MouseFilter.Ignore) + a BracketPanel(FillOverride=PanelDeep) + a VBox. SettingsDialog.BuildHeader (l.154-170) and ServerPasswordModal.BuildHeader (l.125-149) additionally duplicate the titles-VBox + 34×34 ✕ Icon close-button pattern._
  - → Extract a small ModalScaffold helper (returns the content VBox given options for accent, panel min-size, and click-dismiss) plus a shared header(title, eyebrow, onClose) builder; have the three modals call it instead of re-authoring the frame.
  - canonical: `client/scripts/ui/SettingsDialog.cs:121` · also: `client/scripts/ui/SettingsDialog.cs:154, client/scripts/ui/EscapeMenu.cs:51` · refs: Duplicated scaffold at ServerPasswordModal.cs:62-96, SettingsDialog.cs:121-152, EscapeMenu.cs:51-79; ✕ header sub-pattern at ServerPasswordModal.cs:125-149 and SettingsDialog.cs:154-170.

- **[med/med]** `client/scripts/WorldRenderer.cs:10` — WorldRenderer is a 3342-line god-class spanning many unrelated subsystems
  - _This single file is 3342 lines and mixes world rendering, client collision prediction, proximity audio, fog/ghost tracking, warp sequencing, dust-occluder selection AND pure server-state mirrors (team economy/unlocks/research/constructor rosters, lines ~692-940) that carry no rendering logic._
  - → Extract the per-team state mirrors (economy/unlocks/owned-techs/caps/rock-classes/miners/research/constructor status, ~lines 692-940) into a dedicated TeamStateStore the HUD queries directly, shrinking WorldRenderer toward rendering-only.

- **[med/med]** `client/scripts/ShipModelLoader.cs:117` — AttachEngineGlow bundles three unrelated FX-building responsibilities in one ~115-line method
  - _AttachEngineGlow (lines 117-232, ~115 lines) collects nozzle offsets and builds the EngineGlow, then builds the TeamTrail, then computes the wing-light threshold and builds a BaseBeacon per Light hardpoint — three distinct concerns with three separate hardpoint scans of the same list._
  - → Extract local functions (e.g. BuildEngineGlow, BuildTeamTrail, BuildNavBeacons) so each FX concern reads independently and the single hardpoints list is scanned with clear intent; keeps repo's prefer-local-functions convention.

- **[med/med]** `client/scripts/ui/ResearchTab.cs:733` — Footer action dispatch keyed off the button's display glyph text
  - _PrimaryActionForText (733-736) decides whether the primary button authorizes research by string-matching its label glyph: text.StartsWith("◆") || text.StartsWith("⊕"). DemoAuthorizeCenter (828) repeats the same glyph test. If any AUTHORIZE/QUEUE label text is reworded, the action silently detaches with no compile error._
  - → Pass the intended action (or an enum) alongside the label into SetFooter rather than re-deriving it from the rendered glyph; e.g. have BuildActionFooter set _primaryAction directly when it emits an AUTHORIZE/QUEUE footer.
  - also: `client/scripts/ui/ResearchTab.cs:828`

- **[low/high]** `client/scripts/WorldRenderer.cs:1767` — Group-array literals reallocated on every visibility pass
  - _`new[] { _bases, _asteroids }` and `new[] { _ships, _projectiles, _alephs, _effects }` are allocated fresh inside RefreshSectorVisibility (1767/1774), HideForWarp (1789/1794) and Reset (1608) every call._
  - → Hoist the static-group and transient-group arrays into readonly fields initialized in _Ready and iterate those.
  - also: `WorldRenderer.cs:1608, 1774, 1789, 1794`

- **[low/high]** `client/scripts/ExplosionEffect.cs:116` — New RandomNumberGenerator allocated + reseeded from OS entropy on every explosion
  - __Ready constructs `new RandomNumberGenerator()` and calls `rng.Randomize()` (lines 116-117) solely to pick a random ring orientation, once per blast. Randomize() pulls fresh OS entropy each detonation, and the RNG object is GC'd immediately._
  - → Use GD.Randf() (as the rest of the file already does elsewhere for jitter) or a single shared static RandomNumberGenerator for the ring's three random euler angles instead of allocating+reseeding per blast.

- **[low/high]** `client/scripts/ShipController.cs:341` — Manual-override deadzone 0.25f repeated inline six times
  - _ManualOverride (341-348) hardcodes the same 0.25f threshold across six axis comparisons (Thrust/StrafeX/StrafeY/Yaw/Pitch/Roll)._
  - → Name it a private const (e.g. `ManualOverrideDeadzone = 0.25f`) so the cruise-control handback sensitivity is tunable in one place.

- **[low/high]** `client/scripts/ui/ShipLoadout.cs:455` — `_world.LocalTeam ?? _net.MyTeam` repeated 5 times in this partial
  - _The same local-team resolution appears at lines 406, 455, 509, 613, and 702 of ShipLoadout.cs._
  - → Add a private `byte Team => _world.LocalTeam ?? _net.MyTeam;` property and use it at these five sites.
  - also: `client/scripts/ui/ShipLoadout.cs:406, client/scripts/ui/ShipLoadout.cs:509, client/scripts/ui/ShipLoadout.cs:613, client/scripts/ui/ShipLoadout.cs:702`

- **[low/high]** `client/scripts/ui/ShipLoadout.cs:660` — IsOverCapacity recomputes PayloadUsed that RefreshPayload already computed
  - _RefreshPayload (643-658) computes `used` and `over = used > cap`. IsOverCapacity (660-662) independently re-runs PayloadUsed (a full hardpoint+cargo loop) for the same class, and RefreshLaunchGate calls it every _Process frame alongside RefreshPayload._
  - → Cache the last computed over-capacity flag (or last `used`) in RefreshPayload and have IsOverCapacity/RefreshLaunchGate read it, avoiding a second full payload walk per frame.
  - also: `client/scripts/ui/ShipLoadout.cs:643`

- **[low/high]** `client/scripts/ui/SettingsDialog.cs:89` — Dead-store: Rebind return captured then discarded with `_ = conflict`
  - _Line 89 assigns `string? conflict = InputBindings.Rebind(...)`, and line 95 immediately discards it with `_ = conflict;`. The value is never read between the two, and the following `foreach r.Refresh()` already handles the cleared-binding case the comment describes._
  - → Call InputBindings.Rebind(...) as a statement without capturing, and remove the `_ = conflict;` line.

- **[low/high]** `server/Sim/Simulation.cs:3087` — ContentBaseLookup nested class wraps a single Dictionary for no benefit
  - _ContentBaseLookup (L3087-3090) exists only to hold one `public readonly Dictionary<byte, BaseDef> ByType`. The field `_baseDefByType` and BaseDefForType (L3085-3101) go through this wrapper with no added behavior._
  - → Replace the wrapper with a plain `private Dictionary<byte, BaseDef>? _baseDefByType;` built lazily in BaseDefForType, deleting the nested class.

- **[low/high]** `server/Sim/Simulation.Vision.cs:1188` — Cone cosine recomputed per-viewer inside IsPointVisibleToTeam loop
  - _Line 1188 computes `MathF.Cos(def.VisionConeAngleDeg * (MathF.PI/180f))` inside the per-ship loop (and only within the cone branch), duplicating the same conversion done once at capture (line 529 stores ConeCos on ViewerSnap)._
  - → Hoist the cos() out of the inner branch (compute once per def before the distance tests, or cache a per-def cone-cos alongside VisionDefFor) to avoid the repeated transcendental and mirror the precomputed ConeCos snapshot field.

- **[low/high]** `server/Net/ClientHub.cs:1588` — Per-team lazy frame-cache pattern copy-pasted ~5 times in AfterStep
  - _The idiom `if (!dictByTeam!.TryGetValue(client.Team, out var f)) dictByTeam[client.Team] = f = BuildX(...); SendLossy(client, OutFrame.Whole(f));` recurs for researchFramesByTeam (1590), baseFramesByTeam (1627), contactFramesByTeam (1655), probeFramesByTeam (1708), constructorFramesByTeam (1604), each differing only in builder + dict._
  - → Add a local function `byte[] TeamFrame(Dictionary<byte,byte[]> cache, byte team, Func/localfn builder)` (or generic GetOrBuild) to collapse the five call sites; prefer a local function per repo convention over Func<> fields.

- **[low/high]** `public-lobby/PublicLobby.cs:377` — WsReceiveJsonAsync allocates a fresh JsonSerializerOptions on every WS frame
  - _Line 377 constructs `new JsonSerializerOptions { PropertyNameCaseInsensitive = true }` on each deserialize call; this helper runs per inbound WS frame (auth + every update/ping). JsonSerializerOptions is intended to be cached/reused._
  - → Hoist to a `static readonly JsonSerializerOptions` field (e.g. reuse LobbyJson or a dedicated instance) and pass it in, avoiding a per-frame allocation and internal metadata re-warm.

- **[low/med]** `client/scripts/WorldRenderer.cs:810` — NetUpdateTeamState takes 11 positional parameters
  - _NetUpdateTeamState(byte team, int credits, int score, byte[] unlocked, ushort[]? ownedTechs, byte[]? ownedCaps, byte discoveredRockClasses, int minerCount, int minerCap, int buildQueueLimit) — 10 trailing params, several nullable/defaulted, make call sites error-prone (positional byte/int soup)._
  - → Pass a TeamStateSnapshot struct/record decoded by GameNetClient instead of a long positional list.

- **[low/med]** `client/scripts/TargetMarkers.cs:160` — WaypointArriveRange magic 50 duplicates the server's ProspectArriveRange with no shared source
  - _public const float WaypointArriveRange = 50; with a comment stating it 'Matches the server's arrive bands (miner ProspectArriveRange 50, pig patrol-arrive)'. The value is hand-mirrored on the client and will silently drift if the server band changes; it is not streamed or shared._
  - → If the band is client-cosmetic keep it but drop the misleading 'matches server' guarantee; otherwise source it from a shared constant/streamed def so client dismissal and server arrival cannot diverge.

- **[low/med]** `client/scripts/GameNetClient.cs:1660` — ApplyDefs is a ~240-line inline decoder; extract per-def readers like the existing static helpers
  - _ApplyDefs spans lines 1660-1899 (~240 lines) decoding ships, weapons, cargo, bases, world config, techs, developments, and station catalog inline. The file already establishes the pattern of small static per-record readers (ReadHardpoints 1637, ReadTechList 1902, ReadAttrList 1912, ReadCapList 1922, ReadSectorStatic/ReadBaseStatic/ReadRockStatic)._
  - → Extract ReadShipDef / ReadWeaponDef / ReadCargoItemDef / ReadBaseDef / ReadDevelopmentDef / ReadStationCatalogDef static helpers (each a straight-line mirror of its wire block) so ApplyDefs reads as a short sequence of count-prefixed loops; preserves byte order exactly, no wire change.

- **[low/med]** `client/scripts/Lobby.cs:1232` — Lobby carries a private stylebox/label factory kit that overlaps inline styling in ServerLobbyOverlay
  - _Lobby defines ~15 private static UI factories (Mono, Lbl, Cell, Badge, Diamond, EmptyNote, BarPanel, PaddedRow, Margins, Spacer, Hairline, TabStyle, Chip 1232-1345) that reproduce StyleBoxFlat/Label patterns (corner-radius-0 panels, bordered pills, hairlines) which ServerLobbyOverlay re-implements inline (e.g. team-panel styleboxes 680-689, chip 231-235). The two lobby screens share a visual language but no shared helpers, growing Lobby.cs to 1403 lines._
  - → Promote the reusable, non-Lobby-specific factories (Mono/Hairline/Spacer/bordered-panel/chip) into the ui/ component library so both overlays consume one source, shrinking Lobby.cs and preventing style drift between the two lobby screens.

- **[low/med]** `server/Sim/Simulation.Mines.cs:18` — Mine damage-scaling balance constants hardcoded instead of YAML-authored
  - _MineSpeedRef (40f) and MineMaxSpeedMult (2.5f) at Mines.cs 17-19 directly scale per-tick mine damage (dmg = BlastPower * mult * Dt at DamageFieldVolume:186), yet other mine tuning (BlastPower, arm/life ticks, cloud radius) rides the WeaponDef/YAML. Repo convention keeps gameplay/balance numbers in server/Content/*.yaml._
  - → Move MineSpeedRef and MineMaxSpeedMult onto the mine WeaponDef (or a mining/weapon tuning block in world.yaml) so the speed-damage curve is tunable without a recompile; MineFxIntervalTicks is cosmetic cadence and can stay.

- **[low/med]** `shared/ContentValidator.cs:52` — Validate() is ~240 lines; extract the per-weapon validation block
  - _ContentValidator.Validate spans lines 26-266 (~240 lines). The bulk is an inline per-weapon loop (52-172, ~120 lines) mixing id-uniqueness, missile/mine/chaff/probe stat checks, and the ShieldMult check — the other concerns (ships, bases, tech catalog) are already extracted to helpers, so this block is the odd one out._
  - → Move the loop body into a `ValidateWeapon(WeaponDef w, HashSet<uint> cargoIds, bool haveCargo, List<string> errors)` helper mirroring the existing ValidateFuel/ValidateShield/ValidateVision structure.

## Findings by segment

| Segment | Total | Unused | DRY | Messy | Refactor |
|---|--:|--:|--:|--:|--:|
| C_render | 7 | 2 | 1 | 1 | 3 |
| C_vfx | 4 | 0 | 2 | 1 | 1 |
| C_assets | 6 | 3 | 2 | 0 | 1 |
| C_hud | 7 | 1 | 3 | 2 | 1 |
| C_flight | 4 | 0 | 2 | 1 | 1 |
| C_net | 6 | 2 | 3 | 0 | 1 |
| C_lobby | 4 | 0 | 3 | 0 | 1 |
| C_ui_tabs | 6 | 0 | 4 | 1 | 1 |
| C_ui_loadout | 6 | 1 | 2 | 1 | 2 |
| C_ui_lib | 6 | 2 | 1 | 1 | 2 |
| S_sim_core | 4 | 0 | 1 | 1 | 2 |
| S_sim_ai | 5 | 1 | 2 | 1 | 1 |
| S_sim_econ | 7 | 0 | 2 | 3 | 2 |
| S_net_hub | 7 | 1 | 3 | 2 | 1 |
| S_net_proto | 2 | 0 | 0 | 2 | 0 |
| S_world | 6 | 3 | 2 | 1 | 0 |
| SH_core | 4 | 1 | 1 | 1 | 1 |
| SH_collision | 2 | 0 | 1 | 1 | 0 |
| F_factions | 7 | 3 | 3 | 1 | 0 |
| PL_lobby | 5 | 1 | 2 | 1 | 1 |
| cross-dry | 22 | 0 | 22 | 0 | 0 |

## Appendix — candidates ruled out by verification (22)

_These were flagged by a reviewer but an adversarial verifier found them reachable / not safely mergeable — listed for transparency, do NOT act on them._

- `client/scripts/WorldRenderer.cs:2183` — splitmix64 finalizer duplicated across client/shared/server → **refuted** (Client Hash64 used at WorldRenderer.cs:2158-2165 (cosmetic regolith tint only). Canonical-named site Collide.cs:95-109 is RockSpin, a determinism-critical tuple-returning method with different seed arithmetic — not a substitutable finalizer. Other determinism-critical inlinings: shared/MinefieldLayout.cs:22-70, server/Sim/World.cs:1411-1426 (DetRng, 'ported verbatim so seeds reproduce'), server/Sim/World.cs:514 OreMix, client/scripts/Starscape.cs:117-125.)
- `client/scripts/PredictionController.cs:716` — Rotation-vector<->quaternion and error-blend smoothing reimplemented in two interpolators → **refuted** (MotionInterpolator.RotVec used at client/scripts/MotionInterpolator.cs:307-309 (dead-reckoning attitude extrapolation); error decay at 231-241 (exponential). PredictionController conversion pair used at client/scripts/PredictionController.cs:684-686; spring smoothing at 682-686 and SpringToZero 705-713.)
- `server/Sim/Simulation.cs:2499` — FireBolt and StepMissiles duplicate the whole segment-sweep (grid ships + rocks + alephs) → **refuted** (server/Sim/Simulation.cs:2499 (FireBolt CellsAlongRay sweep) and server/Sim/Simulation.cs:2867 (StepMissiles CellsAlongRay sweep))
- `server/Sim/Simulation.cs:2596` — Aleph-barrier ray scan duplicated verbatim in FireBolt and StepMissiles → **refuted** (server/Sim/Simulation.cs:2596-2609 (FireBolt aleph scan) and server/Sim/Simulation.cs:2955-2968 (StepMissiles aleph scan))
- `server/Sim/Simulation.Vision.cs:1159` — IsPointVisibleToTeam re-implements ClassifyTarget's sphere/cone/dust/occlusion/base/probe logic → **refuted** (server/Sim/Simulation.Vision.cs:812 ClassifyTarget (worker/snapshot, radar+eyeball tiers, sig-scaled, _workerCellBuf); server/Sim/Simulation.Vision.cs:1134 TeamStillSeesShipLive (live, eyeball-sphere copy); server/Sim/Simulation.Vision.cs:1159 IsPointVisibleToTeam (sim-thread/live, bool, no-eyeball, sig 1.0, _pointCellBuf))
- `server/Sim/Simulation.Vision.cs:1364` — CollectCellsAlongRay duplicates CellsAlongRay's DDA cell-walk → **refuted** (Bolt/sim hot-loop consumer of CellsAlongRay lives in Simulation.cs (sim thread, _rayCells scratch); CollectCellsAlongRay consumed at Simulation.Vision.cs:1341 inside the off-thread vision occlusion check.)
- `server/Net/Protocol.cs:1108` — Streamed base-health record (u64 id + f32 health, '12') hand-inlined in two builders with a bare magic size → **refuted** (Both sites in the same file: server/Net/Protocol.cs — BuildBasesFor at 1072-1096 (fog/per-team variant) and BuildBases at 1108-1121 (broadcast variant); both write MsgBases frames as 2-byte header + count*(u64 id + f32 health).)
- `server/Net/Protocol.cs:809` — Fog-gated 'count-then-write matching statics' pattern repeated 4x in BuildWelcome → **refuted** (server/Net/Protocol.cs:809 BuildWelcome — fog-on branch 864-913, fog-off branch 834-853. The named canonical/sibling reuse target is BuildRevealSlice (942+), which already deliberately shares the Write*Static helpers rather than the count/write control-flow, showing the intended factoring boundary is the record encoder, not the count-then-write loop.)
- `server/Sim/World.cs:1006` — Vec3 Dot/Normalize re-implemented per-file across the codebase → **refuted** (shared/FlightModel.cs:104-129 (Vec3 struct — no Dot/Normalize). Dot copies: server/Sim/World.cs:1006, server/Sim/Simulation.cs:3622, server/Assets/SelfTest.cs:241, shared/AutoSteer.cs:423, shared/Collision/ConvexHull.cs:405, shared/Collision/Collide.cs:16, shared/Collision/DockFace.cs:205. Normalize copies (divergent semantics): server/Sim/World.cs:1000, server/Content/HardpointGeometryMerge.cs:215, shared/Collision/GlbReader.cs:277, shared/Collision/DockFace.cs:207.)
- `shared/AutoSteer.cs:43` — Nose-onto-target yaw/pitch bang-bang block duplicated across five steering helpers (note only) → **refuted** (shared/AutoSteer.cs — repeated at lines 43-44 (SteerToPoint), 76-85 (AttackPoint), 135-146 (ApproachPoint), 315-316 (FaceAndRoll), 377-382 (FaceAndRollAnticipated behind-branch); file-level determinism contract at lines 9-11.)
- `shared/Collision/Collide.cs:16` — Private Dot(Vec3,Vec3) re-implemented in every shared collision file → **refuted** (Duplicated (all identical): shared/Collision/Collide.cs:16, shared/Collision/ConvexHull.cs:405, shared/Collision/DockFace.cs:205, shared/AutoSteer.cs:423. Missing canonical method: Vec3 struct at shared/FlightModel.cs:104-129 (has Cross/Length/LengthSquared, no Dot).)
- `shared/Collision/GlbReader.cs:277` — Vec3 Normalize helper duplicated across GlbReader and DockFace → **refuted** (GlbReader.cs:277-281 (fallback (0,0,1)); DockFace.cs:207-211 (fallback default zero, plus AnyPerp consumer at 214); Collide.cs:45 (fallback (0,1,0), eps 1e-6) and Collide.cs:73 (fallback (0,1,0), eps 1e-4); FlightModel.cs:104-129 = Vec3 struct with no Normalized().)
- `client/scripts/WorldRenderer.cs:2183` — splitmix64 finalizer re-implemented in client, shared, and server → **refuted** (client/scripts/WorldRenderer.cs:2183 (Hash64, cosmetic tint); shared/Collision/Collide.cs:95 (RockSpin, inline finalizer with multiply-based seed mix, determinism-critical); server/Sim/World.cs:1421 (DetRng.NextULong, stateful RNG, seed-reproduction critical). No shared Hash64(ulong) helper exists.)
- `shared/Collision/Collide.cs:16` — Private Vec3 Dot re-implemented in every shared collision file → **refuted** (Duplicate Dot definitions: shared/Collision/Collide.cs:16, shared/Collision/DockFace.cs:205, shared/AutoSteer.cs:423, shared/Collision/ConvexHull.cs:405 (internal), server/Sim/World.cs:1006, server/Sim/Simulation.cs:3622, server/Assets/SelfTest.cs:241. Claimed canonical shared/FlightModel.cs Vec3 (lines 104-129) exposes Cross/Length/LengthSquared but NOT Dot.)
- `server/Sim/World.cs:1000` — Vec3 Normalize helper duplicated across server and shared collision files → **refuted** (Canonical proposed site shared/FlightModel.cs:429 NormalizeVec (private, 1e-12f, returns v on degenerate). Flagged copies: server/Sim/World.cs:1000, shared/Collision/GlbReader.cs:277 (both (0,0,1) fallback, 1e-6f), shared/Collision/DockFace.cs:207 (zero-vector fallback, 1e-6f).)
- `server/Sim/Simulation.Mining.cs:845` — AlignGated + CrossSector gate-align helpers duplicated between Miner and Constructor execute → **refuted** (Duplicate pair: server/Sim/Simulation.Mining.cs:830 (CrossSector) & :845 (AlignGated) vs server/Sim/Simulation.Constructors.cs:749 (CrossSector) & :759 (AlignGated). Decoupled constants: Simulation.Mining.cs:109 MinerGateAlignRange=200f, Simulation.Constructors.cs:67 ConstructorGateAlignRange=200f.)
- `server/Sim/Simulation.cs:2499` — FireBolt and StepMissiles duplicate the full segment-sweep (grid ships + rocks + aleph barrier) → **refuted** (server/Sim/Simulation.cs:2457 FireBolt (sweep 2499-2627, aleph 2596-2609, probe scan 2611-2627); server/Sim/Simulation.cs:2777 StepMissiles (sweep 2865-2950, aleph 2952-2968). No shared SweepSegment method exists in the file.)
- `server/Sim/Simulation.cs:2499` — FireBolt and StepMissiles duplicate the entire grid segment-sweep (ships + rocks + aleph scan) → **refuted** (FireBolt sweep at server/Sim/Simulation.cs:2497-2627 (cell-walk 2499, aleph 2596, probe scan 2611); StepMissiles sweep at server/Sim/Simulation.cs:2865-2968 (cell-walk 2867, aleph 2955). No third caller of the pattern.)
- `server/Sim/World.cs:1006` — Vec3 Dot/Normalize re-implemented file-private across server and shared instead of one shared kernel → **refuted** (Dot identical bodies: server/Sim/World.cs:1006, server/Sim/Simulation.cs:3622, shared/Collision/Collide.cs:16, shared/AutoSteer.cs:423. Divergent Normalize semantics: server/Sim/World.cs:1000 (1e-6f, fallback Vec3(0,0,1)) vs shared/FlightModel.cs:429 NormalizeVec (1e-12f, fallback v).)
- `server/Sim/World.cs:1411` — splitmix64 finalizer duplicated verbatim across server, shared, and client → **refuted** (Sites read: server/Sim/World.cs:1411-1435 (DetRng stateful PRNG); shared/Collision/Collide.cs:95-114 (RockSpin inline, prelude +0x632BE59BD9B4E019); shared/MinefieldLayout.cs:22-32 (Mix bare finalizer); client/scripts/WorldRenderer.cs:2183-2189 (Hash64, x += golden); client/scripts/Starscape.cs:119-128 (SeedFor, prelude +0x1234567). Each has distinct semantics and its own prelude; only MinefieldLayout.Mix is a generic finalizer helper, and it is not substitutable for the stateful DetRng.)
- `server/Sim/Simulation.Constructors.cs:666` — ConstructorHoldDistance and MinerHoldDistance have byte-identical bodies → **refuted** (server/Sim/Simulation.Constructors.cs:666 (ConstructorHoldDistance) and server/Sim/Simulation.Mining.cs:789 (MinerHoldDistance) — identical bodies but semantically distinct knobs)
- `server/Sim/Simulation.Mining.cs:845` — AlignGated + CrossSector gate-approach helpers duplicated verbatim between MinerExecute and ConstructorExecute → **refuted** (Duplicate pair: server/Sim/Simulation.Mining.cs:830 (CrossSector) + 845 (AlignGated), constant MinerGateAlignRange=200f at Simulation.Mining.cs:109. Identical pair at server/Sim/Simulation.Constructors.cs:749/759, constant ConstructorGateAlignRange=200f at Constructors.cs:67. A third structurally-identical CrossSector at server/Sim/Simulation.cs:1818.)

