# Tech Paths — Phase C handoff (2026-07-14)

## Status

The RESEARCH tab is fully live (client-only phase; protocol stays v36). A commander selects a base
in the shared sidebar, browses the research canvas (clusters by `DevelopmentDef.Group`, nested
nodes with rail lines, status-coded cards), inspects a node in the right detail panel
(schematic / status pill / COST-TIME-AT tri-cells / prerequisites / unlocks chips), and authorizes
research via the action footer (`MsgResearch` ops). Active/on-deck orders render as banners with
countdowns + commander cancel; the CommandSidebar shows live per-base research progress. Verified
live end-to-end against a short-build-time content copy: authorize → credits deducted → amber
progress everywhere → RESEARCH COMPLETE → dependent node unlocks (cyan connector) → bomber card in
the hangar flips from ⚿ TECH LOCKED to buyable. Non-commanders get a disabled amber
"COMMANDER AUTHORIZATION REQUIRED" affordance.

## Shipped (file map)

- `client/scripts/ui/ResearchTab.cs` — the full screen (replaced the stub): sub-controls
  `ClusterHeader`, `RailStrip` (custom-draw rails; connector cyan when parent done), `NodeCard`
  (status badge/pulse/progress-gradient underlay), `ProgressUnderlay`, `ActiveBanner`; right
  detail column `BuildDetail()` with the action-footer state machine; `Init(defs, world, net)` +
  `SetBase(id, title, sector)`; 0.25s status refresh while visible; optimistic "◷ PENDING…"
  (cleared on derived-status change or 3s).
- `client/scripts/ui/ShipLoadout.cs` — ResearchTab gets Init + initial SetBase; sidebar
  `BaseSelected` forwards live; sidebar Init passes DefRegistry.
- `client/scripts/ui/CommandSidebar.cs` — live research rows per base (amber "◷ RESEARCHING <dev>
  · mm:ss" + progress + "+N more"; data-blue "⊕ ON DECK <dev>"); 0.5s visible timer; showcase
  mock fields on `BaseEntry`.
- `client/scripts/ui/ShipLoadout.Hangar.cs` — `--hangar-demo` extended (steps 15-23): RESEARCH tab
  → node select → authorize → complete → hangar unlock (shots 08-12; after-launch now 14). Helpers
  `ClickTab`/`FindButtonByText`/`ClickResearchNode`/`ClickAuthorize`.
- `client/scripts/ui/UiShowcase.cs` — new "07 — RESEARCH" section (all node statuses, cluster
  header, footer states, sidebar research-row variants); later sections renumbered.

No new DesignTokens (Ok/Warn/Data/TeamAccent/TextDim/Danger/Secondary cover the design); DESIGN.md
unchanged. Currency reads "₡ / CR / CREDITS" (not the mock's He³) — deliberate.

## Decisions locked

- Node NESTING: within a Group, a dev nests under the FIRST other dev (list order) whose
  GrantedTechIdx intersects its RequiredTechIdx; single-parent; others are roots.
- Status precedence: Done → InProgress/OnDeck (from `AllResearch()`) → Available → Locked;
  Done = all GrantedTechIdx owned; Available = required techs+caps owned AND no obsoleted-by owned.
- Cancel ops target the base that actually RUNS the item (found via AllResearch), not the
  sidebar-selected base; authorize targets the selected base.
- UNLOCKS chips scan developments/station-catalog/weapons by RequiredTechIdx intersection —
  **hulls are skipped** (ShipClassDef carries no RequiredTechIdx on the wire); researched hulls
  surface via the hangar card unlock instead.
- Commander name in the non-commander affordance is generic ("the commander") — GameNetClient has
  no roster-name accessor for CommanderIdOf.

## How to verify

```sh
dotnet build client/stellarallegiance.csproj   # 0 errors
# Gallery: Godot --path client res://scenes/UiShowcase.tscn -- --ui-shot=/tmp/ui.png --ui-scroll=<px>
# Live loop (shortened research): copy server/Content/core to /tmp, drop build-time-seconds to ~7,
SIM_PUBLIC_NAME="" dotnet run --project server -c Release -- --port 8099 --autostart \
  --content /tmp/core/core.manifest.yaml &
$env:SIM_PORT=8099; scripts/run-client.ps1 -Local -- --hangar-demo=/tmp/research-demo
# expect shots 08-12: research tab, node detail, authorized (credits -400, amber banners),
# complete (green + dependent available), bomber card unlocked in hangar; client self-quits.
kill $(lsof -tnP -iTCP:8099 -sTCP:LISTEN)
```

Test suites: untouched this phase (client-only) — baseline stands as recorded in PHASE-B-HANDOFF.

## Known issues / deferred

- Hangar arsenal still shows the hardcoded "TECH TREE (SOON)" placeholder — Phase D replaces it
  with real `WeaponDef.RequiredTechIdx` locks (heavy-cannon appears there).
- BUILD tab is still the awaiting-catalog stub — Phase D.
- Research canvas doesn't cross-link nodes ACROSS groups (a dev requiring another group's tech
  shows as a root with prereqs in the detail panel — acceptable for the stock tree shape).

## Next phase entry points (Phase D)

- **Extract the detail panel**: `ResearchTab.BuildDetail()` + helpers (`TriCell`, `PrereqRow`,
  `Chip`, `CapName`, `Mmss`, `PriceText`) → a shared `TechDetailPanel` control; BuildTab reuses it
  with an always-disabled action ("CONSTRUCTORS OFFLINE — construction arrives with base building").
- BuildTab data: `DefRegistry.AllStationCatalog()` — catalog-only entries have `BaseTypeId == -1`;
  status from the same owned-tech/cap rules (`WorldRenderer.TeamOwnsTech/TeamOwnsCap`).
- Arsenal locks: `WeaponDef.RequiredTechIdx` non-empty && not all owned ⇒ row renders
  `⚿ LOCKED · REQUIRES <tech name>` and refuses equip; heavy-cannon (weapon-id 9) is the stock case.
- `NodeCard.ConfigureMock` is the pattern for showcase entries without live world data.
