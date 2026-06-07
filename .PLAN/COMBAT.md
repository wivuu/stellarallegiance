# Plan — Escape pods, base docking, and PIG gamification

## Context

The two-ship prototype is complete; the `.PLAN/README.md` "Gamification" quicknotes
(lines 17-24) want the near-term game to feel more alive. This plan implements that
block plus a follow-up the user added in conversation:

1. **Escape pods** — on ship death a player ejects a *player-piloted* pod (weak,
   slow, unarmed). The player cannot respawn until the pod resolves by **any** of:
   dying, being rescued by a teammate, or reaching a friendly base. (Per the user's
   choices: pods are player-flown, and any outcome unlocks respawn — no cooldown tiers.)
2. **PIGs are podded too** — a dead PIG ejects a pod that *auto-flies* (AI) to the
   nearest friendly base.
3. **Dock at friendly base** (user follow-up) — any player ship that intersects its
   **own** team's base despawns and reopens the spawn menu. This is the same code
   path as a pod "reaching base," and lets players voluntarily re-ship.
4. **PIG squad respawn cadence** — replace the per-slot 30 s respawn with squad waves:
   a team's whole squad must be wiped before the next squad spawns, with a 10 s delay
   after the last drone of a squad dies.
5. **PIG behavior** — prioritize *aggressive* enemies (those that **recently fired**,
   reusing `Ship.LastFireTick`) over non-aggressive ones (pods, passive players); if no
   aggressive target is nearby, pursue non-aggressive ones instead of going idle; if no
   enemies at all are nearby, **rescue friendly pods**; and if there's nothing to do,
   **patrol** rather than idling at base.

### Core design decision: a Pod is a `Ship` with an `IsPod` flag

Rather than a new table + new client renderer/prediction/camera path, model pods by
reusing the existing `Ship` table with a new `bool IsPod` (mirroring how PIGs reuse
`Ship` via `IsPig`). This gives player-piloted pods the existing prediction,
reconciliation, camera-follow, collision, sector, and rendering paths essentially for
free. Pods differ only by: slow "impulse" flight stats, low hull, no weapons, and a
resolution check (die / rescued / docked).

- **Player pod:** `IsPod=true, IsPig=false`, `Player.ShipId` points at it, player flies it.
- **PIG pod:** `IsPod=true, IsPig=true`, driven by a small `PodThink` that steers to the
  nearest friendly base; despawns on arrival/rescue/death.

---

## Server changes (`module/spacetimedb/`)

### Schema (`Lib.cs`, Ship table ~L75-112)
- Add `public bool IsPod;` to `Ship`. Default `false` everywhere ships are inserted
  (`SpawnShipInternal` L1525, `SpawnPig` L243).
- Add a `PigSquad` table keyed by team for wave timing:
  ```csharp
  [SpacetimeDB.Table(Accessor = "PigSquad", Public = true)]
  public partial struct PigSquad { [PrimaryKey] public byte Team; public uint NextSquadTick; public bool Active; }
  ```
- Extend `PigState` enum (L21-22) with `Patrol` and `Rescue` (append-only to keep the
  existing 0/1/2 values stable). Regenerates client bindings.

### Pod flight stats (shared — see FlightModel section)
Pods need slow, boost-less stats both the server *and* the client predict with.

### Eject a pod on player-ship death (`Lib.cs`, Pass C, ~L1369-1377 + `KillShip` L1429)
Currently `s.Health <= 0` for a non-PIG calls `KillShip` (deletes ship, clears
`Player.ShipId` → spawn menu). Change so that a dying **combat** ship (`!IsPig && !IsPod`)
instead **ejects a pod**: spawn a `Ship` with `IsPod=true`, low `Health` (e.g. a new
`PodMaxHull` const ~20), pod mass, at the dead ship's position/orientation/sector,
inheriting team and owner, and set `Player.ShipId = pod.ShipId`. A dying **pod**
(`IsPod`) falls through to the existing `KillShip` (clears `Player.ShipId` → spawn menu)
— pods don't eject pods. Reuse the existing offset/sector logic; factor a small
`SpawnPodFor(ctx, deadShip)` helper next to `SpawnShipInternal`.

### Dock at friendly base + pod "reached base" (`Lib.cs`, Pass C base loop L1365-1367)
Today the base loop skips your own base (`b.Team != s.Team`). Add: if `b.Team == s.Team
&& b.SectorId == s.SectorId` and the ship center is within a dock radius
(`< BaseRadius * 0.9f`, comfortably inside the `BaseRadius + ShipRadius` spawn offset so
fresh ships don't instantly re-dock), then **dock**: for a player ship call the
`KillShip`-style cleanup *without* the "destroyed" semantics — delete the ship + inputs
and clear `Player.ShipId` (spawn menu reappears). For a PIG pod, despawn + free handling
(see PodThink). Enemy bases keep the existing `ResolveCollision` damage/bounce. Factor
`DockShip(ctx, s)` so the pod-reached-base and voluntary-dock cases share it.

### Rescue by teammate (`Lib.cs`, new small pass or fold into Pass C)
After collisions, scan pods: a pod (`IsPod`) is **rescued** if any friendly non-pod ship
is within a `RescueRadius` (~`BaseRadius`?, tune ~30-40u) in the same sector. Rescue =
same resolution as docking (`DockShip` for players → spawn menu; despawn for PIG pods).
This is what makes the PIG "rescue pods" behavior (below) actually resolve a pod.

### PIG pods + `PodThink` (`PigAI.cs`)
- In `KillPig` (L273): instead of just freeing the slot, **eject a PIG pod** (`Ship`
  with `IsPod=true, IsPig=true`, low hull, at death pos, team), and free the slot
  (`ShipId=null`). Do **not** set a per-slot respawn timer anymore (squad logic owns
  respawns).
- Routing input: Pass A in `Lib.cs` (L1039-1118) currently routes `IsPig` ships through
  `PigThink`. Add: if `ship.IsPod && ship.IsPig` → `PodThink` (new, in `PigAI.cs`); else
  if `ship.IsPig` → `PigThink`. `PodThink` reuses `PigSteerTo` to fly toward the nearest
  friendly `Base` (find via the existing iterate-and-match-team pattern, e.g. PigAI
  L462-465). On reaching the base (the Pass C dock check) or rescue/death the pod ship is
  removed. Pods never fire.

### Squad respawn cadence (`PigAI.cs`, rewrite `SimulatePigLifecycle` L163-214)
Replace per-slot scramble/trickle + `PigRespawnTicks` with squad waves, per team:
- A team's squad = its `MaxPigsPerTeam` slots. Compute `alive = Pig slots with ShipId != null`.
- If `PigSquad.Active` and `alive == 0` (squad just wiped): set
  `NextSquadTick = tick + PigSquadDelayTicks` (`10 * SimTickHz`), `Active = false`.
- If `!Active`, `tick >= NextSquadTick`, a threat is present (`EnemyInSector`, L583) and
  players are alive: spawn the **whole squad** (all empty slots), `Active = true`. Keep a
  short intra-squad stagger (existing `PigSpawnStaggerTicks`) so they don't pop on one tick.
- `KillPig` no longer schedules respawns; `DespawnAllPigs` (L139) also resets `PigSquad`
  rows (`Active=false, NextSquadTick=0`). Remove/retire `PigRespawnTicks`.

### PIG target priority + patrol/rescue (`PigAI.cs`, `PigThink` L296-454)
Add an aggression test and restructure target selection:
- `bool IsAggressive(Ship enemy, uint tick)` → `!enemy.IsPod && enemy.LastFireTick != 0
  && tick - enemy.LastFireTick <= PigAggroWindowTicks` (`~3 * SimTickHz`).
- In the in-sector enemy scan (L341-351), classify contacts into aggressive vs
  non-aggressive (a pod is always non-aggressive). Selection order:
  1. **Aggressive present** → pick best by existing `PigThreatScore` (keeps current
     threat/hysteresis behavior among aggressive targets).
  2. **Else non-aggressive present** → pursue the nearest one (chase pods/passive players)
     using the same Seek/Attack steering.
  3. **Else no enemies, but a friendly pod is nearby** → `State = Rescue`, `PigSteerTo`
     toward that pod (proximity triggers the rescue pass above).
  4. **Else** → `State = Patrol`, `PigPatrolInput` (below) instead of `PigIdleInput`.
- `PigPatrolInput`: steer toward a roaming waypoint instead of loitering at base. Simplest
  deterministic-enough approach (server-only, no determinism contract): a slowly-rotating
  point on a ring around the sector center (phase keyed off `PigId` + `tick`) via
  `PigSteerTo`, advancing when reached. Keeps drones moving and visible.

---

## Shared (`shared/FlightModel.cs`)

Add pod flight stats so client prediction and server authority agree bit-for-bit:
- Add `public static readonly ShipStats Pod` — slow `ThrustAccel`/`MaxSpeed` (impulse
  feel, e.g. ~20 / ~25), modest drag, `BoostThrustMult = BoostSpeedMult = 1f` (no boost),
  a light `Mass`.
- The caller selects stats. Today both sides call `FlightModel.StatsFor(ship.Class)`
  (server Pass A; client `PredictionController`/`RemoteShip`). Add a selection that returns
  `Pod` when the ship is a pod. Cleanest: a helper `StatsFor(byte shipClass, bool isPod)`
  overload, and have both server and client pass `ship.IsPod`.

---

## Client changes (`client/`)

Regenerate bindings first (`spacetime generate`) so `Ship.IsPod`, `PigSquad`, and the new
`PigState` values appear.

- **Pod flight stats in prediction** — `PredictionController.cs` and `RemoteShip.cs`
  currently derive stats from `ship.Class`; make them pass `row.IsPod` into the new
  `StatsFor` overload so predicted/interpolated pod motion matches the server.
- **Render pods distinctly** — `WorldRenderer.cs` `BuildShipMesh(team, cls, isPig)`
  (L680-707): add an `isPod` arg (or branch) selecting a small pod mesh/material so a pod
  reads visually as an escape pod, not a fighter. `OnShipInsert` (L425-464) already routes
  local (`IsLocal && !IsPig`) → `PredictionController` and others → `RemoteShip`; a local
  pod is `IsLocal && !IsPig` so it's followed/predicted automatically.
- **Suppress firing in a pod** — gate the local fire input on `!IsPod` so the player can't
  shoot an unarmed pod and the client doesn't predict tracers the server won't make
  (`ShipController.cs` input build; server also ignores fire when `IsPod` as the authority).
- **Spawn-menu gating already works** — `Hud.cs` (L91-100) shows the menu when
  `teamedInMatch && LocalShip == null`. While podded, `Player.ShipId`/`LocalShip` points at
  the pod, so the menu stays hidden until the pod resolves (clears `ShipId`). No change
  needed, but optionally show a "EJECTED — reach a base or get rescued" hint and pod HP in
  the HUD readout (L125-129).
- **Camera/death-cam** — `CameraRig.cs` follows `LocalShip`. Change the death-cam so it
  fires **only when the local POD is destroyed**, not on combat-ship death:
  - When the local **combat ship** is destroyed (`OnShipDelete`, L518-519), do **not** start
    the death-cam. Because the pod is ejected the same tick, cut straight to the escape-pod
    view — let `OnShipInsert` for the local pod set `LocalShip = pod` so the camera hands off
    immediately (suppress/skip the `_deathCamUntil` trigger when the dead ship was a combat
    ship that's being replaced by a pod). Net effect: ship dies → instantly in the pod.
  - When the local **pod** is destroyed, trigger the existing death-cam (hold on the death
    point, then pull back to overview) — this is the only case that should show it. Gate the
    `_deathCamUntil` set in `OnShipDelete` on `row.IsPod` (local pod) so combat-ship deaths
    skip it and pod deaths keep it.
- **Optional HUD markers** — `TargetMarkers.cs`/`Minimap.cs` can later flag pods; not
  required for the core loop.

---

## Suggested implementation order

1. Schema: `Ship.IsPod`, `PigSquad`, `PigState` additions; `FlightModel.Pod` + `StatsFor`
   overload. Publish with `--delete-data` (new column/table) and `spacetime generate`.
2. Player pods: eject-on-death + dock/rescue resolution + voluntary friendly-base dock.
   Verify the full die→pod→base→spawn-menu loop solo.
3. Client: pod stats in prediction, pod mesh, fire gating, optional HUD hint.
4. PIG pods + `PodThink` (AI to nearest base).
5. Squad respawn cadence (`PigSquad`, rewrite `SimulatePigLifecycle`, gut `KillPig` timer).
6. PIG target priority (aggressive/non-aggressive), rescue, and patrol.
7. `graphify update .` to refresh the knowledge graph.

---

## Verification

Local loop (per `module/CLAUDE.md` + memory `headless-sim-testing`): `spacetime start`,
`spacetime publish wivuullegiance --server local --delete-data always -y`, then hold a
`--server`/`--anonymous` connection so `SimTick` keeps ticking.

- **Pods:** join a team, spawn, fly into the enemy to die. Confirm via
  `spacetime sql wivuullegiance "SELECT ShipId, IsPod, Health, Owner FROM Ship"` that a pod
  ship appears, `Player.ShipId` points to it, and the spawn menu does **not** show. Fly the
  pod into your own base → ship row gone, `Player.ShipId` null, spawn menu returns. Repeat
  letting the pod die instead → same unlock. Repeat with a teammate flying adjacent → rescue
  unlock.
- **Dock:** while in a normal combat ship, fly into your own base → despawn + spawn menu.
- **PIG squad:** `/pigs on`; confirm a full squad spawns, that **no** new drones spawn until
  all are dead (`SELECT Team, count(*) FROM Pig WHERE ShipId IS NOT NULL`), and that the next
  squad appears ~10 s after the last death (watch `PigSquad.NextSquadTick`).
- **PIG behavior:** with a passive (not firing) player + a firing player in sector, confirm
  drones converge on the shooter; stop firing and confirm they still pursue rather than idle;
  leave the sector and confirm they patrol (moving) instead of parking at base; kill a drone,
  eject its pod, and confirm idle drones move to rescue the friendly pod.
- Build/headless integration per `tests/` and the Docker note (memory `docker-build-mount-shared`)
  before considering it done.

## Open assumptions (sensible defaults; flag if wrong)
- Pod hull ~5, pod speed ~25 u/s ("impulse"), rescue radius ~30-40u, aggro window ~3 s,
  squad delay 10 s, squad size = `MaxPigsPerTeam` (5). All single-constant tunables.
- A pod is unarmed. PIG pods don't restore a drone on arrival (squad logic owns respawns);
  they're rescue targets + flavor.
