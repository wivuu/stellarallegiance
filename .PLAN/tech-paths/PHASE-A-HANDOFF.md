# Tech Paths — Phase A handoff (2026-07-14)

## Status

The docked screen is now a real three-tab shell — **HANGAR / BUILD / RESEARCH** — with a shared
**CommandSidebar** (Command Network mini-map + "Your Bases" list) on every tab, a horizontal
ship-class card strip above the 3D preview, and a launch-base pick that flows into the top bar
("LAUNCH BASE: GARRISON 01 · <sector>") and the launch footer ("FROM GARRISON 01").
**Client-only; protocol untouched (still v35)** — compatible with live servers. BUILD/RESEARCH
render an "awaiting server catalog" guard until Phase B streams the catalog. The launch-base pick
is display-only (`LoadoutState.Shared.SelectedBaseId`); Phase B puts it on the wire.

Verified end-to-end against a local server: full `--hangar-demo` run (8 screenshots: tabs, sidebar
selection, card strip, preview rotate, slot select, equip, over-capacity, reset, launch) ending in
a real spawn with `Reconciles: 0` and correct credit deduction (1000 + 100 paycheck − 200 fighter = 900).

## Shipped (file map)

- `client/scripts/ui/ShipLoadout.cs` — tab shell rewrite: real `MakeSegmented(["HANGAR","BUILD","RESEARCH"], 0, OnTabSelected)`,
  body = `[CommandSidebar | tab content]`, top-bar/footer base readouts. Launch-bar controls are
  built BEFORE the sidebar is wired (sidebar auto-select fires `OnBaseSelected → UpdateBaseReadouts`
  which needs `_fromReadout`).
- `client/scripts/ui/ShipLoadout.Hangar.cs` — new partial: hangar center/right columns, horizontal
  `ShipCard` strip (`RebuildShipCards`/`RefreshShipCardStates`), the `--hangar-demo` harness
  (steps updated for the new geometry), `HoloBackdrop`/`HardpointMarkerOverlay`.
- `client/scripts/ui/CommandSidebar.cs` — new shared 340px component. `Init(world,net)` +
  `Refresh()` + `SetData(entries, map)` (showcase mocks) + `BaseSelected` event + `SelectedBaseId`
  / `SelectedTitle` / `SelectedSectorName`. Filters to local team; auto-selects first friendly base.
- `client/scripts/ui/ResearchTab.cs`, `client/scripts/ui/BuildTab.cs` — stub Controls with the
  awaiting-catalog guard; Phase C/D fill them.
- `client/scripts/ui/SectorMapPreview.cs` — `uint? HighlightSector` + pulsing ring (animates only
  while `IsVisibleInTree()`).
- `client/scripts/ui/LoadoutState.cs` — `SelectedBaseId : ulong` (0 = server default).
- `client/scripts/WorldRenderer.cs` — `_baseList` tuples now carry `SectorId` (captured at
  `InsertBase`); new `KnownBases()` → `(ulong Id, uint Sector, byte Team, bool Alive)`.
- `client/scripts/ShipController.cs` — `--hangar-demo` support: QuickJoin (team+ready) like
  `--autofly` but WITHOUT auto-spawn; raises `Hud.RequestDeploy()` once the match is Active
  (the Lobby's Lobby→Active auto-deploy edge can lose the race against our own team-set
  round-trip); **90 s failsafe `GetTree().Quit()`** so a broken demo never hangs a window.
- `client/scripts/ui/UiShowcase.cs` — registered the ship-card strip (normal/selected/locked/
  unaffordable) + CommandSidebar (mock rows + mock map, 340×560).

## Decisions locked (do not re-derive)

- Sidebar map = **`SectorMapPreview` reuse**, not the mock's diamond node-map (embeddable, honors
  the secret-in-sector-base-position rule). Cosmetic diamond styling can come later.
- `--hangar-demo` is a **UI-harness flag: it goes AFTER `--`** (`GetCmdlineUserArgs`), like
  `--ui-shot`. Invocation: `scripts/run-client.sh --local -- --hangar-demo=<dir>` with
  `scripts/run-server.sh --local --autostart` running.
- No new color tokens were needed in Phase A; the mock's amber/green/data-blue status hexes map to
  existing `DesignTokens.Warn/Ok/Data` and land properly in Phase C.
- Headless (`--headless`) screenshot capture does NOT work (dummy rendering server → null viewport
  texture); run windowed on the Mac (Metal) — the demo/ui-shot flows quit by themselves.

## How to verify

```sh
dotnet build client/stellarallegiance.csproj   # 0 errors
dotnet build server/SimServer.csproj           # 0 errors (untouched)
scripts/run-server.sh --local --autostart &    # wait for :8090 LISTEN
scripts/run-client.sh --local -- --hangar-demo=/tmp/hangar-demo   # quits itself; 8 PNGs
# UiShowcase gallery: godot --path client res://scenes/UiShowcase.tscn -- --ui-shot=/tmp/ui.png --ui-scroll=1680
kill $(lsof -tnP -iTCP:8090 -sTCP:LISTEN)
```

### Pre-existing test-failure baseline (recorded 2026-07-14 on `tech-tree` @ be6fdcd — gate = no NEW failures)

| Suite | Failures |
|---|---|
| AutopilotTest | 3 (docking-door parse ×1, base-sphere cut ×2 — looks asset-load dependent) |
| CollisionTest | 4 (ship-ship impulse asserts) |
| ContentTest | 2 (fighter fuel, garrison vision — content drift) |
| FactionsTest | 4 (content drift) |
| FogTest | 1 (sector-leak, known) |
| ShieldTest | 1 (bomber bolt shield dmg, known) |
| all other suites (incl. StrategyTest, CommanderTest, FlightModelTest, MiningTest) | 0 |

Run them per-suite: `dotnet run --project tests/<Suite> -c Release`. Factions unit tests:
`dotnet test factions/tests/Allegiance.Factions.Tests` (note: `dotnet test factions` fails — no sln).

## Known issues / deferred

- Launch-base pick is cosmetic until Phase B (server still picks the spawn base).
- Sidebar base rows show only ACTIVE/DESTROYED; live research/order banners arrive in Phase C.
- Hangar arsenal still shows the hardcoded "TECH TREE (SOON)" locked placeholder — replaced in
  Phase D by real `WeaponDef.RequiredTechIdx` locks.
- `_activeTab` is stored but only used for tab swapping; Phase C may want to lazy-refresh tabs on
  activation.

## Next phase entry points (Phase B)

- Wire the base pick: `GameNetClient.RequestSpawn` (~:332) reads `LoadoutState.Shared.SelectedBaseId`.
- Catalog/team-tech/research-state stores: `DefRegistry` + `WorldRenderer` (see plan
  `.claude/plans/frolicking-wobbling-raven.md` Phase B for byte layouts and file list).
- `CommandSidebar.SetData` is the seam for feeding live research status without touching plumbing.
- Watch out: `ClientHub.cs:589` hardcodes `if (cls > 2) cls = 0;` — must become def-driven before
  any researched hull with class-id ≥ 3 can spawn.
