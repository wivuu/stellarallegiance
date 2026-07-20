# Tech Paths — Phase B handoff (2026-07-14)

## Status

**Protocol v36.** The server now runs a full per-base research engine: a commander starts (or
queues) YAML-authored developments at a friendly base; credits deduct at start AND at
queue-reservation; after `build-time-seconds` the team gains the granted techs/capabilities and the
unlock set re-resolves mid-match. The stock bundle gates the **bomber behind `heavy-ordnance`**
(research `dev-heavy-ordnance`, 400cr/90s) and ships a locked `heavy-cannon` (weapon-id 9, requires
`cannon-tier-2`). The full tech catalog + per-team owned techs/caps + live research state stream to
the client and land in DefRegistry/WorldRenderer stores (no new UI yet — Phase C renders it). The
hangar launch-base pick is now REAL (MsgSpawn carries the base id; server validates + falls back).
Verified live: v36 client vs v36 server, bomber card renders ⚿ TECH LOCKED from streamed state,
fighter spawn from the picked base works. **Server+client must deploy together (v35↔v36 refuse).**

## Shipped (file map)

Factions library (+ tests, schemas):
- `factions/.../Model/Buildable.cs` — `ObsoletedByTechs : TechSet` (`obsoleted-by-techs`; ANY-owned
  ⇒ item no longer offered — future "Gatling II retires Gatling I").
- `factions/.../Model/Station.cs` — `ResearchSlots : int` (`research-slots`, 0⇒1 at projection).
- `factions/.../Resolution/BuildableResolver.cs` — obsoleted-by filter; `TechResolver` reachability
  deliberately ignores it. `TechTreeReport` surfaces it. `CoreValidator` checks refs + slots ≥ 0.
- `schemas/allegiance-core.schema.json` regenerated. Library tests 51/51.

Stock content (`server/Content/core/`, manifest version `2026.07.14-tech-tree`):
- NEW `techs.yaml` (heavy-ordnance, cannon-tier-2, expansion-1, tactical-1) + `developments.yaml`
  (dev-heavy-ordnance 400/90s → dev-cannon-tier-2 300/60s; dev-expansion, dev-tactical 500/120s;
  all `tech-only`, grouped WEAPONS/EXPANSION/TACTICAL).
- `stations.yaml` — garrison `research-slots: 1`; catalog-only (NO base-type-id ⇒ never a runtime
  BaseDef) stations: outpost, shipyard, refinery, tech-lab (research-slots 2), supremacy-center,
  expansion-complex, teleport-receiver.
- `hulls.yaml` — bomber `required-techs: [heavy-ordnance]`. `weapons.yaml`/`projectiles.yaml` —
  `heavy-cannon` (id 9) + `heavy-bolt`, mounted by no hull (Phase-D arsenal lock display).
- `.PLAN/tech-tree-stock.yaml` regenerated.

Shared + projection:
- `shared/Defs.cs` — `TechDef`/`DevelopmentDef`/`StationCatalogDef`, `CapabilityId` byte enum
  (mirror of the library's closed Capability enum — append-only, cast at projection),
  `BaseDef.ResearchSlots`, `WeaponDef.RequiredTechIdx`.
- `server/Content/FactionsContentProjection.cs` — tech index map from **Core.Techs list order**
  (= the wire index space), development/station-catalog projection, weapon/launcher RequiredTechIdx;
  HashSet-sourced lists are SORTED for byte determinism.
- `server/Content/ContentSet.cs` — `Techs/Developments/StationCatalog/TechIndexById`.
- `shared/ContentValidator.cs` — catalog rules (dup ids, index range, positive build-time, runtime
  station ↔ BaseDef link); wired in `server/Program.cs`.

Sim engine:
- `server/Sim/World.cs` — `ResearchByBase : BaseResearchState[]` parallel to `Bases`
  (`Active` list of `(DevIndex, StartTick, DurationTicks)` + `OnDeck`).
- `server/Sim/Simulation.Research.cs` — `EnqueueResearchOp` (drained in DrainQueues),
  `ResearchStep` (PhaseActive, after AccrueTeamCredits), `CompleteResearch` → `ResolveTeamUnlocks`
  (comment there updated: it now runs mid-match), on-deck promotion, dead-base loss,
  `ResearchChangedThisStep` + `ResearchNoticesThisStep`/`ResearchTeamNoticesThisStep`.
- `server/Sim/Simulation.cs` — join queue/_clientInfo carry `launchBaseId`; `ResolveLaunchBase`
  (friendly+alive else null→default) → `PlaceAtBase(at:)`; `IsPlayerSpawnableClass` (def-driven,
  replaces the `cls > 2` clamp); StartMatch clears research slate even when the World is reused.

Wire (writers `server/Net/Protocol.cs`, readers `client/scripts/GameNetClient.cs`, mirrored):
- **MsgSpawn (4, c→s)**: `[4][u8 cls][u64 launchBaseId][u8 nCargo][n×(u32,u8)]` — 0 = default.
- **MsgDefs (7)**: BaseDef +`u8 researchSlots` (after hardpoints); WeaponDef +TechList requiredTechs
  (after probeModelSize); catalog tail after world cfg: `u16 nTechs×(id,name,desc)`,
  `u16 nDevs×(id,name,desc,group,i32 price,i32 buildTimeSec,u8 techOnly,TechList req/granted/
  obsoletedBy,CapList reqCaps/grantedCaps)`, `u16 nStations×(id,name,desc,i32 price,i32 buildTime,
  u8 stationClass,i16 baseTypeId(-1=catalog-only),u8 researchSlots, same 5 lists)`.
  `TechList = u8 n × u16 techIdx`; `CapList = u8 n × u8 CapabilityId`.
- **MsgTeamState (10)**: per team appends `u16 nOwnedTechs×u16` + `u8 nOwnedCaps×u8` (sorted).
  Builder is now `BuildTeamState(world, content)`.
- **NEW MsgResearch = 13 (c→s)**: `[13][u8 op][u64 baseId][u16 devIndex]`; op 0 start-or-queue /
  1 cancel-active / 2 cancel-on-deck. Hub-gated by `CommanderOrWarn`.
- **NEW MsgResearchState = 24 (s→c)**: `[24][u8 nBases]×([u64 baseId][u8 nActive×(u16 devIdx,
  u32 startTick,u32 durationTicks)][u8 hasOnDeck][?u16 onDeck])`. PER-TEAM (own bases only,
  fog-safe), sent on `ResearchChangedThisStep || coarse`, lossy; **idle bases are omitted —
  reconcile by omission**; progress derives client-side from ServerTick.
- `ClientHub` — MsgResearch case, research frames lazily per team in AfterStep, research notices
  relay, `/research <dev-id>` commander chat verb (lists catalog when bare; targets first alive base).

Client data layer:
- `GameNetClient` — catalog/base/weapon reader tails, `ApplyResearchState` (case 24), team-state
  techs/caps, `RequestSpawn(cls, cargo, launchBaseId)`, `SendResearch(op, baseId, devIndex)`.
- `DefRegistry` — `AllTechs/AllDevelopments/AllStationCatalog/GetTech/GetDevelopment` (empty until
  defs land — guard, never bake).
- `WorldRenderer` — `_teamOwnedTechs/_teamOwnedCaps` (+`TeamOwnsTech/TeamOwnsCap/TeamOwnedTechs`),
  `BaseResearch` record + `NetUpdateResearch/ResearchAt/AllResearch`,
  `ResearchProgress(start, duration)` from ServerTick.
- `ShipController` — spawn send carries `LoadoutState.Shared.SelectedBaseId`.

## Decisions locked (do not re-derive)

- Tech wire index = position in streamed tech list = Core.Techs authored order. Never reorder.
- Credits deduct at start AND queue (reservation); cancel = 100% refund; dead base = loss, no refund.
- Research frame encodes startTick+duration; omission = idle. Lossy + coarse keepalive.
- MsgResearch client→server id 13 (collides with s→c MsgMinefields 13 — spaces are independent).
- `CapabilityId` bytes mirror the library enum by declaration order (Base=0 … SupremacyAllowed=4).
- Occupancy nuance: in ResearchOpStart the credit check precedes the occupancy check, so
  "base occupied" is only reported when affordable (tests account for this).
- Slot count resolves from `Content.Bases[0]` (all bases type 0 today) — `SlotsFor` carries the
  TODO seam for per-site base types (base-building stage).
- Test seeding pattern for gated hulls: `content.Start.BaseTechs.Add("heavy-ordnance")` BEFORE
  StartMatch (SeedEconomy clones BaseTechs) — used by ShieldTest/MissileTest/MineTest/FogTest.

## How to verify

```sh
dotnet build server/SimServer.csproj -c Release && dotnet build client/stellarallegiance.csproj
for t in tests/*/; do dotnet run --project "$t" -c Release; done   # see baseline below
dotnet test factions/tests/Allegiance.Factions.Tests               # 51/51
scripts/run-server.ps1 -Local --autostart &                        # wait for :8090
scripts/run-client.ps1 -Local -- --hangar-demo=/tmp/hd             # 8 shots; bomber card = ⚿ TECH LOCKED
# chat (as commander): /research            -> lists the 4 developments
#                      /research dev-heavy-ordnance -> team notice, 90s later bomber unlocks
kill $(lsof -tnP -iTCP:8090 -sTCP:LISTEN)
```

Baseline (unchanged from Phase A — no NEW failures): AutopilotTest 3, CollisionTest 4,
ContentTest 2, FactionsTest 4, FogTest 1, ShieldTest 1; every other suite 0.
StrategyTest gained 10 research scenarios (locked-at-start, deduct, complete→unlock, dependency,
slot+queue+promote, refunds, duplicate, too-poor, spawnable-class, restart slate); ContentTest
gained catalog spot-checks; BuildDefs byte-determinism now covers the catalog tail.

## Known issues / deferred

- No UI renders the catalog yet — Research tab (Phase C) + Build tab (Phase D) read the stores.
- `/research` chat verb always targets the team's first alive base; base-specific starts are the
  UI's job (MsgResearch carries the base id).
- Non-commander research: server rejects with the standard commander warning; the Phase-C UI should
  pre-empt with a disabled affordance (`GameNetClient.IsCommander`).
- ObsoletedByTechs is plumbed end-to-end but unauthored in stock (v1 decision).
- PIG drones bypass the economy/unlock gate by design — pigs can still field bombers pre-research.

## Next phase entry points (Phase C)

- `client/scripts/ui/ResearchTab.cs` (stub) — build per the plan's design reference. Data:
  `DefRegistry.AllTechs()/AllDevelopments()` (+`Group` for clusters), `WorldRenderer.TeamOwnsTech/
  TeamOwnsCap/TeamOwnedTechs`, `WorldRenderer.ResearchAt(baseId)/AllResearch()/ResearchProgress`,
  `DefRegistry.GetDevelopment(idx)`. Actions: `GameNetClient.SendResearch(op, baseId, devIdx)`;
  commander check `GameNetClient.IsCommander`; base pick from `CommandSidebar.SelectedBaseId`.
- Client-side availability rule (mirror of BuildableResolver): required RequiredTechIdx all owned
  AND RequiredCaps all owned AND NOT (ObsoletedByTechIdx any owned) AND NOT (tech-only with all
  grants owned = done).
- `CommandSidebar.SetData` is the seam for live "RESEARCHING <dev> · mm:ss" rows.
- Gotcha: `WorldRenderer.ServerTick` freezes when disconnected; research progress helpers already
  clamp. MsgResearchState reconciles by omission — clear per-base UI state when a base id vanishes.
