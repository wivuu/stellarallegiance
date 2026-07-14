# Tech Paths — Phase D handoff (2026-07-14) — FEATURE COMPLETE

## Status

The Tech Paths feature is complete across all four phases (protocol v36). The docked screen has
HANGAR / BUILD / RESEARCH tabs over a shared CommandSidebar. Research is fully playable
(commander authorizes YAML-authored developments at a base; slots + on-deck queue; cancel/refund;
completion grants techs/caps and unlocks content mid-match — stock: bomber behind heavy-ordnance,
heavy-cannon behind cannon-tier-2). The BUILD tab renders the YAML station catalog as
placeholders (7 future structures, real gating states, action permanently disabled:
"CONSTRUCTORS OFFLINE — construction arrives with the base-building update"). The hangar arsenal
shows real tech-locked weapon rows (`⚿ LOCKED · REQUIRES <tech>`, click does not equip) instead
of the old hardcoded placeholder. Launch base selection is real (MsgSpawn base id).

## Shipped this phase (file map)

- NEW `client/scripts/ui/TechDetailPanel.cs` — the shared 400px detail column (schematic / title /
  status pill / description / COST-TIME-AT tri-cells / prereqs / unlock chips / sticky footer).
  Pure presentation: `SetSchematic/SetTitle/SetStatus/SetDescription/SetMeta/SetPrereqs/SetUnlocks/
  SetFooter` + `PrimaryPressed`/`SecondaryPressed`; static `PriceText`/`Mmss`/`CapName`.
  ResearchTab's footer STATE MACHINE stays in ResearchTab — the panel only presents.
- `client/scripts/ui/BuildTab.cs` — placeholder construction catalog: responsive card grid of
  `DefRegistry.AllStationCatalog()` entries with `BaseTypeId == -1`; `StationCard` (glyph by
  StationClass, status, name, kind line, blurb, price + BUILD mm:ss); same owned-tech/cap gating
  rules as research for the lock state; detail via TechDetailPanel; footer ALWAYS disabled.
- `client/scripts/ui/ResearchTab.cs` — now drives TechDetailPanel (zero visual change, re-verified
  live through the full authorize→complete→unlock loop).
- `client/scripts/ui/ShipLoadout.cs` — BuildTab Init/SetBase wiring; arsenal tech-locks: within the
  slot-compatible weapon loop, a weapon with unowned `RequiredTechIdx` renders as a dim locked row
  with the tech names and NO Pressed handler; becomes a normal equippable row once researched.
- `client/scripts/ui/ShipLoadout.Hangar.cs` — `--hangar-demo` gained BUILD-tab + locked-arsenal
  steps (shots 13-15; after-launch now 16).
- `client/scripts/ui/UiShowcase.cs` — section 08 — BUILD (cards available/selected/locked,
  offline footer, locked arsenal row). `GLOSSARY.md` — "Tech paths / research" + "Build tab"
  entries.

## How to verify (final sweep — all green 2026-07-14)

```sh
dotnet build server/SimServer.csproj -c Release && dotnet build client/stellarallegiance.csproj
for t in tests/*/; do dotnet run --project "$t" -c Release; done
dotnet test factions/tests/Allegiance.Factions.Tests    # 51/51
scripts/run-server.sh --local --autostart &
scripts/run-client.sh --local -- --hangar-demo=/tmp/demo   # 16 shots; self-quits
kill $(lsof -tnP -iTCP:8090 -sTCP:LISTEN)
```

Baseline unchanged end-to-end: AutopilotTest 3, CollisionTest 4, ContentTest 2, FactionsTest 4,
FogTest 1, ShieldTest 1 (all pre-existing on master); every other suite 0.

## Deferred (future work, in .PLAN/README.md terms)

- Constructor logic / actually building the catalog stations — the base-building roadmap item
  (BuildTab's footer seam + StationCatalogDef carry everything it needs).
- Multi-select research queue depth > 1 on-deck; per-site base TYPES (SlotsFor seam in
  Simulation.Research.cs); research-time/cost attribute modifiers (AttributeModifiers unwired).
- `obsoleted-by-techs` is plumbed end-to-end but unauthored in stock — author tier-2 weapon
  replacements when weapon tiers land.
- Hull unlock chips in the detail panel (ShipClassDef carries no RequiredTechIdx on the wire).
- Commander name in the non-commander affordance (needs a roster-name accessor).
