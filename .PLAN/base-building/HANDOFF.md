# Base Building + Constructors — handoff (2026-07-14, proto v38)

## Status

The Outpost is buildable end-to-end. Bases are now per-type data (like ship hulls); a constructor
drone raises a forward base on an asteroid. **Server + client must deploy together (v37 ↔ v38 refuse).**

## v38 polish pass (2026-07-14)

- **Production before launch.** Buying a constructor no longer spawns it instantly — it PRODUCES at
  the garrison first (`ConstructorState.Producing`, `ConstructorProductionSeconds`), then launches. The
  Build tab shows a live progress bar + a commander **✕ CANCEL** (full refund) per producing drone, and
  read-only status rows for launched drones — driven by the new per-team `MsgConstructorState=26` stream
  (`ConstructorStatesView` → `BuildConstructorState`), reusing `ActiveBanner`. Cancel = `MsgConstructorCancel=15`.
- **Move orders.** Constructors now accept waypoint (kind 3) and sector (kind 4) `MsgOrder`s
  (`ConstructorState.MoveTo`), so the shared gold destination-diamond visual applies (client-side,
  role-agnostic). An in-progress build (Sinking/Building) is committed and won't divert.
- **Build-sphere lifecycle.** The sphere now appears only at SINKING (phase 1, meshes intersecting),
  grows an opaque `blend_mix` core (`SetCover`) that hides the drone (the drone mesh is also set
  invisible at Building), and FADES out (`BeginFade`, ~1.2 s) when the build drops from the stream —
  instead of sticking on the finished base. `BuildConstructorBuilds` emits a 0-count keepalive for ~1.5 s
  after builds end so the lossy client reliably sees the drop.
- **Base naming.** CommandSidebar names bases **TYPE · SECTOR** (e.g. "OUTPOST · CINDER BELT", numeric
  suffix only on same-type/same-sector collision) — `_baseType` map + `KnownBases()` gained `TypeId`.

## MVP status (v37, unchanged below)

## What shipped (file map)

**Wire (v37)** — `shared/Net/Wire.cs` changelog. New messages in `server/Net/Protocol.cs`:
`MsgBuildConstructor=14` (c→s: `[14][u8 stationTypeId][u64 launchBaseId]`, commander-gated),
`MsgConstructorBuilds=25` (s→c: per-constructor `shipId,rockId,phase,f16 progress` — build-sphere VFX).
`WriteBaseStatic` appends `u8 baseTypeId` + streams the per-type radius; `BaseDef` block appends
`str ModelName + u8 winCondition + u8 buildRockClass`; ship-def block appends `u8 isConstructor`;
station-catalog appends `u8 buildRockClass`. `ShipFlagConstructor=128` now emitted.
`BuildBaseReveal` = the fog-off one-slice MsgReveal for a mid-match base.

**Defs / content** — `shared/Defs.cs` (`BaseDef.ModelName/WinCondition/BuildRockClass`,
`StationCatalogDef.BuildRockClass`, `ShipClassDef.IsConstructor`); `factions/.../Model/Station.cs`
(`BuildOnRockClass`); `FactionsContentProjection.cs` (WinCondition = the `start` ability,
`IsConstructor` = `HullAbility.IsBuilder`, `ParseRockClass`); `server/Content/core/stations.yaml`
(outpost → `base-type-id: 1`, `model-name: Outpost`, `build-on-rock-class: regolith`, radius 55);
`hulls.yaml` (constructor, class-id 5, `utl11`, `is-builder`). `schemas/*.json` regenerated.

**Server sim** — `server/Sim/World.cs`: `BaseSite.BaseTypeId`; growable `BaseHealth`/`ResearchByBase`
(append-only preserves indices); per-type `_baseModels` dict + `BaseHullOf/BaseRadiusOf/...`
accessors (garrison keeps its exact mesh; outpost shrink-wraps Outpost.glb, no docking); `CreateBase`,
`ResetMatchBases`, `GarrisonCount`. `Simulation.cs`: `ApplyBaseDamage` ends the match only when a
team's LAST win-condition base dies; `IsConstructorClass`; the base-collision call-site sweep to
per-type accessors; `PlaceAtBase` per-type. **`Simulation.Constructors.cs`** (new): the whole drone —
FSM (Idle→ToRock→Aligning→Sinking→Building), buy/charge, brain (5 Hz phase timers), execute (20 Hz
AutoSteer), completion (`CreateBase` + grant caps + reveal + despawn), order handling, the build stream
view. `Simulation.Orders.cs` routes constructor subjects. `ClientHub.cs`: MsgBuildConstructor handler,
frame broadcast, notices relay, `/build`/`/constructors` chat verbs.

**Client** — `BaseModelLoader.cs` + `CollisionWorld.cs` load the base mesh by `ModelName` per type;
`WorldRenderer.InsertBase` uses `row.BaseTypeId`. `ui/BuildTab.cs` shows forward bases (BaseTypeId ≠ 0),
enables the footer for a commander to buy a constructor (`GameNetClient.SendBuildConstructor`).
**`BuildSphere.cs`** (new, cloned from `ShieldFlash`): the enveloping VFX; `WorldRenderer.
UpdateBuildSpheres` drives one per active build from `MsgConstructorBuilds`. `TargetMarkers.cs` tags a
focused constructor "CONSTRUCTOR".

## How to verify

```sh
dotnet build server/SimServer.csproj -c Release && dotnet build client/stellarallegiance.csproj
dotnet run --project tests/ConstructorTest -c Release   # 10/10 — full loop + rock gate + win flag
for t in tests/*/; do dotnet run --project "$t" -c Release; done   # baseline below, no NEW fails
dotnet run --project server/SimServer.csproj -c Release -- --selftest   # garrison hull unchanged
dotnet test factions/tests/Allegiance.Factions.Tests   # 51/51
```

Pre-existing baseline (no NEW failures): AutopilotTest 3, CollisionTest 4, ContentTest 2 (fighter
fuel + garrison vision drift), FactionsTest 4, FogTest 1, ShieldTest 1. Live v37 handshake confirmed:
client decodes 2 bases / 6 ship classes / 8 stations, no crashes.

### Manual visual sign-off (recommended, not yet done)

Run `scripts/run-server.sh --local --autostart &` + a windowed `scripts/run-client.sh --local`; as
commander open the docked **BUILD** tab → Outpost → BUILD (or `/build outpost`); a constructor launches
from the garrison. Press F3, left-click the constructor, right-click a Regolith rock. Watch it align,
sink, the build sphere envelop the asteroid, and the outpost appear. (Shorten the outpost's
`build-time-seconds` in a /tmp content copy for a fast loop — see the `verify` skill's content-probe.)

## Gotchas / decisions locked

- Win-condition = the `start` station ability (garrison-only in stock). A team loses only when ALL
  its win-condition bases are destroyed. Non-win bases (outposts) at 0 HP stay in the list (health 0)
  and never end the match — the list is APPEND-ONLY (never compacted) so base indices stay valid.
- Constructor cost = the STATION price (the hull is the free delivery mechanism), charged on buy.
- Fog-on reveals a new base via the owning team's reveal LOG (`RevealBaseToTeam`); fog-off broadcasts
  `BuildBaseReveal`. Enemies discover it by the normal vision scan.
- Outpost collision = convex hull from Outpost.glb (no COL_ parts, no dock faces) → ships bounce but
  can't dock. Docking/repair/rearm at outposts is deferred.
- `ConstructorAlignSeconds`/`SinkSeconds`/`Standoff`/`SinkDepthFrac` are consts in
  `Simulation.Constructors.cs` (build time is the station's authored `build-time-seconds`). A
  `WorldConstructorTuning` YAML block was deferred — promote these when live tuning is wanted.
