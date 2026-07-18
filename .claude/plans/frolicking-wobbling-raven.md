# Tech Paths — team research tree + docked-screen rework

## Context

Stage-4 roadmap item (.PLAN/README.md:339-358): a YAML-authored team investment tree with research-over-time, plus the docked-screen UI rework from the Claude Design project (`Ship Loadout.dc.html`, `Tech Tree v2.dc.html`, `CommandSidebar.dc.html` — already fetched via DesignSync from project `28bf0d21-5959-4554-8bfc-a1f92113ea28`; re-fetch with `DesignSync get_file` if needed). Credits, per-team `TechSet`/`Capability` gating, and `BuildableResolver` forward-closure already exist (Stage 2); the factions `Development` model already carries `Price`, `BuildTimeSeconds`, required/granted techs — **no core schema redesign needed**. The stock bundle authors zero techs today; `ResolveTeamUnlocks` runs only at match start; nothing tech-related streams to the client.

**User decisions (locked):**
- Research concurrency = **configurable slots per base (`research-slots` on Station, garrison=1) + ONE on-deck queue slot per base**, commander cancel/promote.
- Launch-base pick is **real**: MsgSpawn carries a base id; server validates + spawns there.
- **Unlock-only tree** in v1 (no stat modifiers); add an **`obsoleted-by-techs`** field on Buildable now (future "Gatling II replaces Gatling I") — plumbing + resolver + schema only, no stock authoring yet.
- Phased delivery; each phase ends with a handoff doc in `.PLAN/tech-paths/PHASE-<X>-HANDOFF.md`. Delegate mechanical chunks to subagents; keep protocol/determinism work central.

**Delivery decisions (from planning):**
- **One protocol bump only: v35→36, all in Phase B** (MsgSpawn base id included there, so Phase A stays deployable against live servers).
- `obsoleted-by-techs` semantics: **ANY listed tech owned ⇒ item no longer offered**.
- Research credits **deducted at start AND at queue-reservation** (promotion can never fail on funds); **cancel = 100% refund**; base destroyed = research lost.
- Research state encodes **startTick+duration** (frame changes only on start/complete/cancel/promote; client derives live progress from ServerTick).
- Sidebar map **reuses `SectorMapPreview`** (embeddable, honors the secret-base-position rule) instead of the mock's diamond node-map.

## Design reference (from the fetched mocks)

- **Tabs**: HANGAR / BUILD / RESEARCH in the top bar; active = solid cyan fill w/ void text. BUILD and RESEARCH are two modes of one screen (shared sidebar + center + 400px right detail column).
- **CommandSidebar (340px, shared by all tabs)**: "COMMAND NETWORK" mini-map (selected node pulse ring) + "YOUR BASES" rows: glyph tile, base type, sector; below a divider either amber active-order line (verb + name + mm:ss + progress bar), "⊕ ON DECK <name>" line, or dim "◌ IDLE" note. Selection = cyan border + tint.
- **Hangar**: horizontal ship-class chip strip (icon, name, role · cost; selected = cyan border) above the preview; top bar shows `<BASE TYPE> · <LOCATION>`; launch footer shows UNIT COST / PAYLOAD / FROM <base> + chamfered RESET/LAUNCH.
- **Research tab**: wrapping discipline clusters (collapsible headers w/ avail/total count); nested nodes with 22px rail/elbow lines (cyan when parent done); node = 32px badge + name + status label + `He³ <price>`; statuses: done=green `#4dffa6` filled, in-progress=amber `#ffb347` pulse + progress painted as background gradient, available=cyan glow, locked=dim 0.6 opacity. Right detail: schematic frame (hatch + scan line), COST/BUILD/AT tri-cells, prerequisites rows (✓ green / ⊘ amber left-border), amber-orange UNLOCKS chips, action footer: `◆ AUTHORIZE RESEARCH` / `⊕ QUEUE ON DECK` / `◷ BASE OCCUPIED` / `✕ CANCEL` (commander) / `INSUFFICIENT FUNDS` / `⊘ LOCKED + reason`; non-commander = disabled + "needs commander sign-off". Toast bottom-center, chamfer clip-path `polygon(10px 0,100% 0,100% calc(100%-10px),calc(100%-10px) 100%,0 100%,0 10px)`.
- **Build tab**: card grid `minmax(232px,1fr)`: 40px glyph + status label, name, kind label, effect text, footer `He³ <price>` + `BUILD <mm:ss>`; locked = 0.62 opacity; empty-state dashed box.
- New hexes (amber #ffb347, green #4dffa6, data-blue #9fd6ff…) go in as **named `DesignTokens`** documented in DESIGN.md — never inline.

## Phase A — Docked-screen shell rework (client-only, no wire)

1. **Tab host** — `client/scripts/ui/ShipLoadout.cs`: replace cosmetic `UiKit.MakeSegmented(["HANGAR","TECH TREE"], 0, null)` (~:172) with `["HANGAR","BUILD","RESEARCH"]` + a real `OnTabSelected` swapping the body. Body = `[CommandSidebar | tab content]`. Extract hangar center+right columns into a partial (`ShipLoadout.Hangar.cs`); keep Hud/F4/spawn-gating and `--hangar-demo` harness intact. New stubs `client/scripts/ui/ResearchTab.cs` / `BuildTab.cs` rendering an "awaiting server catalog" guard (same pattern as the empty ship list, no baked data).
2. **`client/scripts/ui/CommandSidebar.cs`** (new): SectorMapPreview (~340×170; add `uint? HighlightSector` + pulse ring to `SectorMapPreview.cs`) + "YOUR BASES" rows (LoadoutSlot-style in HairlinePanel). Feed via new `WorldRenderer.KnownBases()` accessor `(ulong Id, uint Sector, byte Team, bool Alive)` — extend `_baseList` tuples to carry SectorId at `InsertBase`; map model from `MapSectors/MapBaseTeams/MapAlephLinks` (lobby pattern). Row status in Phase A: ACTIVE/DESTROYED only. `BaseSelected(ulong)` event.
3. **Hangar strip** — horizontal `ScrollContainer` card strip above `LoadoutPreview` replacing `BuildShipListColumn`/`RebuildShipList` (:187-225); cards show glyph/name/role/cost + lock badge from `CheckSpawnGate`; keep 1-9 hotkeys. Launch-base pick stored in `LoadoutState.Shared.SelectedBaseId` (display-only this phase) and flows into top bar + launch footer readouts.
4. Register CommandSidebar + card strip in `UiShowcase.cs`; update `--hangar-demo` script steps for new geometry.

**Verify:** `dotnet build` client; F9 UiShowcase shot; `--hangar-demo`; `verify` skill against a local server (tabs switch, sidebar populated from Welcome). Write `.PLAN/tech-paths/PHASE-A-HANDOFF.md` (+ record the pre-existing test-failure baseline: ShieldTest/ContentTest/FactionsTest carry 6 content-drift failures on master).

## Phase B — Server engine + content + ALL wire changes (v36) — central work

1. **Factions library**: `Buildable.ObsoletedByTechs : TechSet` (Model/Buildable.cs) + ANY-owned filter in `Resolution/BuildableResolver.GetBuildables` (`owned.Techs.Overlaps(...)`); `Station.ResearchSlots : int` (omit-when-default, 0⇒1 at projection); CoreValidator checks (dangling refs, slots ≥ 0). Regen schemas: `dotnet run --project factions/src/Allegiance.Factions.Cli -- schema --output schemas/allegiance-core.schema.json`. Library tests for resolver + validator.
2. **Stock YAML** (`server/Content/core/`, add to `core.manifest.yaml` catalog, bump version): new `techs.yaml` (heavy-ordnance, cannon-tier-2, expansion-1, tactical-1) + `developments.yaml` (dev-heavy-ordnance 400cr/90s → dev-cannon-tier-2 300cr/60s chain; dev-expansion, dev-tactical 500cr/120s; all `tech-only: true`, unlock-only, no `attributes:`). `hulls.yaml`: bomber gains `required-techs: [heavy-ordnance]` (**bomber locked at match start by design**). `weapons.yaml`: new `heavy-cannon` (next free weapon-id — verify vs launchers too) with `required-techs: [cannon-tier-2]`, mounted by no hull. `stations.yaml`: garrison `research-slots: 1`; append **catalog-only** stations (NO `base-type-id` ⇒ never projected to BaseDef per `FactionsContentProjection.cs:60-63`, no in-world effect): outpost, shipyard, refinery, tech-lab, supremacy-center, expansion-complex, teleport-receiver — price/build-time/required-techs/granted-capabilities. Regen dump: `... Cli -- dump server/Content/core/core.manifest.yaml -o .PLAN/tech-tree-stock.yaml`.
3. **Shared defs + projection**: `shared/Defs.cs` new `TechDef` / `DevelopmentDef` / `StationCatalogDef` (tech refs as u16 indices into the streamed tech list, ordered by Core list order — deterministic); `BaseDef.ResearchSlots`; `WeaponDef.RequiredTechIdx` (for Phase-D arsenal locks). Project in `server/Content/FactionsContentProjection.cs` onto new `ContentSet` lists; `shared/ContentValidator.cs` rules (dup ids, index range, positive build-time).
4. **Wire v36** (`shared/Net/Wire.cs:34` + changelog; writers `server/Net/Protocol.cs`, readers `client/scripts/GameNetClient.cs`, mirrored field-for-field):
   - **MsgSpawn (4, c→s)**: `[4][u8 cls][u64 launchBaseId][u8 nCargo][…]` — 0 = server default. Server validates on sim thread (base in `World.Bases`, same team, alive) → `PlaceAtBase(..., at: site)` (overload exists, Simulation.cs:1164), else fallback. Also **replace the hardcoded `if (cls > 2) cls = 0;` clamp (ClientHub.cs:589) with a def-driven check** — prerequisite for any researched hull with class-id ≥ 3.
   - **MsgDefs (7)**: append catalog LAST — `u16 nTechs×(id,name,desc)`, `u16 nDevs×(id,name,desc,i32 price,i32 buildTimeSec,u8 techOnly, TechList req/granted/obsoletedBy, CapList reqCaps/grantedCaps)`, `u16 nStations×(…, u8 stationClass, i16 baseTypeId(-1=catalog-only), u8 researchSlots, same lists)`; `BaseDef` +`u8 researchSlots`; `WeaponDef` +TechList requiredTechs. `TechList = u8 n × u16`; `CapList = u8 n × u8`.
   - **MsgTeamState (10)**: per-team append `u16 nOwnedTechs × u16 techIndex` (`BuildTeamState` Protocol.cs:983 / `ApplyTeamState` GameNetClient.cs:1087).
   - **NEW MsgResearch = 13 (c→s)**: `[13][u8 op][u64 baseId][u16 devIndex]` — op 0 start-or-queue, 1 cancel-active, 2 cancel-on-deck. Hub-gated by `CommanderOrWarn` (ClientHub.cs:859) → thread-safe enqueue.
   - **NEW MsgResearchState = 24 (s→c, per-team, fog-safe)**: `[24][u8 nBases]×([u64 baseId][u8 nActive×(u16 devIndex,u32 startTick,u32 durationTicks)][u8 hasOnDeck][?u16 onDeck])`; sent on `ResearchChangedThisStep || coarse` beside team-state in `ClientHub.AfterStep` (~:1235).
5. **Sim engine** — new partial `server/Sim/Simulation.Research.cs` (Mining.cs structure): `World.ResearchByBase : BaseResearchState[]` parallel to `Bases` (auto-reset on match-start world swap; `Active` list + `OnDeck`); `EnqueueResearchOp` drained in `DrainQueues`; validation (base team/alive, dev available per BuildableResolver incl. obsoletes, not already active/queued team-wide, slot vs queue accounting, credits); `ResearchStep(tick)` in the PhaseActive block right after `AccrueTeamCredits` (Simulation.cs:662): complete when `tick >= start+duration` → union GrantedTechs/Caps → `ResolveTeamUnlocks()` (update its match-start-only comment, :994) → `TeamStateChangedThisStep=true`, promote OnDeck, set `ResearchChangedThisStep`. Notices via a `ResearchNoticesThisStep` list → team chat (MinerNotices pattern). Pure integer tick math, no RNG, catalog list-order only. PIG/miner brains untouched.
6. **Client data layer (no new UI)**: `GameNetClient` — catalog tail in `ApplyDefs`, tech indices in `ApplyTeamState`, `case 24: ApplyResearchState`, `RequestSpawn(+baseId)`, `SendResearch(op, baseId, devIndex)`. `DefRegistry` — `AllTechs/AllDevelopments/AllStationCatalog/GetTech/GetDevelopment` (guard-empty until defs arrive; **no baked fallback**). `WorldRenderer` — `_teamOwnedTechs`, `TeamOwnsTech`, `_baseResearch` + `NetUpdateResearch` + progress helper from `ServerTick`. Hook Phase-A `SelectedBaseId` into the real MsgSpawn field.

**Tests:** extend `tests/StrategyTest` (bomber locked at start; start deducts; completion after `seconds×TickHz` grants tech + unlocks class 2; queue→promote; cancel refunds; dupe/invalid rejected; slot cap); `tests/ContentTest` (catalog projection spot-checks; BuildDefs byte-determinism now covers new section); `tests/FactionsTest` YAML checks. Audit any test/harness assuming bomber buyable at start. **Verify:** all dotnet suites vs baseline; `verify` skill (non-commander research warns; research completes → bomber unlocks in hangar). Server+client must deploy together (single compat window). Write PHASE-B-HANDOFF.md with exact byte layouts.

## Phase C — Research tab UI live (delegate-heavy)

- `ResearchTab.cs` full build per design reference above: clusters (group via `Development.Group`, fallback bucketing by granted caps) + rail-line node tree (custom `_Draw`); center = selected-base header + ACTIVE/ON DECK/IDLE banners; right detail panel + action footer sending `SendResearch` ops with optimistic "PENDING…" until the next state frame (spawn-pending pattern). Node status client-side from streamed data only: done (granted techs owned) / in-progress / on-deck / available / locked(+first unmet requirement name). Commander gating from `IsCommander` (v34); non-commander disabled affordance.
- CommandSidebar rows go live: amber "RESEARCHING <dev> · mm:ss" progress, "ON DECK", "IDLE".
- New DesignTokens (amber/green/data-blue statuses) + DESIGN.md note; register node/detail/status variants in UiShowcase.
- **Verify:** F9 shots; `verify` skill end-to-end (authorize → progress → completion unlocks bomber; cancel refunds; non-commander view). PHASE-C-HANDOFF.md.

## Phase D — Build tab placeholders + polish (delegate-heavy)

- `BuildTab.cs`: card grid from `DefRegistry.AllStationCatalog()` (BaseTypeId −1 entries), design card styling, shared detail panel, lock states from owned techs; action **always disabled**: "CONSTRUCTORS OFFLINE — construction arrives with base building". No wire.
- Hangar arsenal: replace hardcoded "TECH TREE (SOON)" placeholder (ShipLoadout.cs:829-832) with real `⚿ LOCKED · REQUIRES <tech>` rows from `WeaponDef.RequiredTechIdx` (heavy-cannon appears here); refuse equip while locked.
- Polish + full sweep: all dotnet suites vs baseline, `--hangar-demo`, `--autofly` smoke, `verify` screenshots of all three tabs, F9 gallery, GLOSSARY.md entry for the research system. PHASE-D-HANDOFF.md.

## Handoff-doc skeleton (each phase)

`.PLAN/tech-paths/PHASE-<X>-HANDOFF.md`: Status (what is now true, proto version) / Shipped file map / Decisions locked (don't re-derive) / Wire contracts (B+) / Content authored / How to verify (exact commands + expected pass counts vs recorded baseline) / Known issues-deferred / Next-phase entry points (file:symbol + gotchas).

## Risks

1. **Writer/reader drift across 5 frames** — all wire work central in one chunk; bump `Wire.cs` version LAST so the tree never sits half-edited (auto commit+push hook fires mid-session); server+client deployed together.
2. **Sim determinism** — integer tick math, list-order indices, no RNG; StrategyTest asserts exact outcomes; PIG/AutoSteer untouched.
3. **Bomber-locked-at-start** ripples into tests/self-play — audit and seed `heavy-ordnance` where gating isn't the subject.
4. **Pre-existing 6 test failures** — record baseline in Phase A; gate every phase on "no NEW failures".
5. **Client fallback temptation** — Research/Build tabs guard on empty DefRegistry like the ship list does; never bake catalog data.
