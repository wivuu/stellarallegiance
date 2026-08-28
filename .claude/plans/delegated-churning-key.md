# Match Scoreboard — implementation plan

## Context

The lobby roster has stubbed K/D/EJ/PTS columns (`client/scripts/Lobby.cs:1191-1195`, `ColumnHeader` :1199) and team `Score` is a placeholder that never accrues (`server/Sim/World.cs:158`). Nothing on the server records who damaged whom. The user designed a scoreboard (canvas: https://claude.ai/code/artifact/e0140007-421e-441c-8e12-e6d1c7113df0; working HTML in `/private/tmp/claude-501/-Users-erik-projects-wivuullegiance/84d334c7-b0f8-4387-b3fa-07369881064b/scratchpad/scoreboard/{Main,InMatch,States}.dc.html` — read them for layout/metrics) with two modes:

1. **Post-match** — auto-opens over the Lobby when the match ends. Result band, team-filter cards (team 0 / team 1 / All Pilots), sortable CALLSIGN·K·D·EJ·PTS table, Team Summary comparison (Score / Kills / Ejections / Garrisons), Top Gun callout. **No SHIP column** (a pilot flies many hulls per match).
2. **Mid-match (F5)** — same data over the live sector, both teams side by side, read-only (mouse stays captured, flight input untouched). Has a SHIP/STATUS column that is fog-gated: enemy pilots your team can't see read `· · ·`.

Decisions made with the user:
- **Points** = weighted formula in a new server-only `scoring:` world.yaml block. Kill credit = last enemy *player* whose damage reached the **hull** within a credit window. Team score = Σ its pilots' points (nothing else feeds `TeamState.Score`).
- **Post-match** = a `Scoreboard` overlay stacked on the Lobby; the Lobby's placeholder K/D/EJ/PTS cells get wired to the same data.
- **Team Summary rows** = Score / Kills / Ejections / Garrisons (no He3 / Tech in v1).
- **Semantics (Allegiance):** `EJ` = your combat ship was destroyed and you ejected (`EjectPlayerPod`); `D` = your escape pod was destroyed (`KillPod` on a player pod). Dock/rescue = neither. `K` = any enemy hull you got credit for (player ship, pod, miner/constructor, PIG — weights differ); bases are points, not kills.
- **F5** toggles the board (F3 map, F4 hangar, F9 showcase are taken). Hardcoded like the other menu keys.

Wire proto is `36` (`shared/Net/Wire.cs:118`) → **37**. Highest message id is `MsgShipLoadout = 28`. The `(vNN)` tokens in Protocol.cs field comments are stale feature labels, not protocol versions — ignore them.

Written to be handed to an implementation subagent (Opus). Steps are ordered in §Implementation order; read the *Traps* list first.

## Traps (verified against the code — do not re-derive)

1. **Client ids are `i32` everywhere** (`LobbyEntry.Id`, `BuildLobbyState` writes `w.Write(e.Id)`, `-1` is a live sentinel). The new frame uses `i32 id`.
2. **Do NOT add `Scoreboard.Active` to `InputGate.FlightInputFree`** (`client/scripts/InputGate.cs:9`). The Live board must not freeze steering — the server replays held input, so a frozen client keeps thrusting. Only the post-match board needs Esc; add `|| Scoreboard.Active` to `Lobby._UnhandledKeyInput`'s early-return list (`Lobby.cs:846-855`).
3. **`Phase == Ended` arrives while you're still flying.** `ApplyBaseDamage` latches `Ended` and schedules `_returnToLobbyAtTick = tick + EndedToLobbyTicks` (~6 s); ships are torn down only in `ReturnToLobby`. At the Active→Ended edge the Lobby is hidden (`Lobby._Process` needs `LocalShip == null`) and the cursor is captured. The post-match board must free the cursor itself on open.
4. **The local ship is never in `Ships.Nodes`** (`ShipRenderer.LocalShip` is a `PredictionController`; `_nodes` holds only `RemoteShip`s). Resolve the local pilot's live row separately.
5. **A departed pilot loses name AND team server-side** (`ClientHub` disconnect path calls `_lobby.Remove` and `_players.OnDisconnect`). The stats frame must carry name+team itself, memoised in the hub.
6. **The death pass runs in every phase** (`ResolveDeath` is reached from the unconditional structural loop, `Simulation.cs:~890`), so scoring must guard `Phase == PhaseActive` or kills in the Ended window mutate the final board.
7. **Reconnect reclaim remaps to a NEW client id** (`Simulation.cs:1091-1103` moves `_clientInfo`/`_clientRespawn`); stats must migrate too, and the hub memo must drop the old id.
8. **`World.Bases` grows mid-match** (constructors) and `World` is swapped at `StartMatch` — key base kill-credit by `BaseSite.Id`, not index.
9. Two extra `ApplyDamage` call sites beyond the obvious ones: `Simulation.cs:947` (boundary erosion) and `:3581` (second half of the ship-ship pair). Both stay unattributed.
10. **No dotnet suite loads the Godot client**, so a writer↔reader field-order bug in the new frame only shows at runtime — the `--autofly` smoke is mandatory.

---

## Part A — Server

### A1. `scoring:` world.yaml block

Mirror the `constructor:`/`build:` pattern exactly.

`server/Content/core/world.yaml` — after the `build:` block:
```yaml
# Per-pilot scoring (the match scoreboard). Server-side only — never streamed; clients see the
# resulting K/D/EJ/PTS rows on MsgMatchStats. A pilot's PTS is the weighted sum below and a TEAM's
# score is exactly the sum of its pilots' points. Kill credit goes to the LAST enemy PILOT whose
# damage reached a HULL within credit-window-seconds — a PIG, a collision, the sector boundary or an
# ownerless mine never credits anyone (the loss still counts against the victim). Allegiance
# semantics: losing your COMBAT SHIP is an EJECTION (you fly out in a pod), losing the POD is a
# DEATH — so EJ >= D; a pod that docks or is rescued is neither. Set a weight to 0 to switch it off.
scoring:
  credit-window-seconds: 10
  kill-ship: 100        # enemy player's combat hull
  kill-pod: 25          # enemy escape pod (player or PIG)
  kill-drone: 50        # enemy miner / constructor
  kill-pig: 50          # enemy AI combat drone
  kill-garrison: 500    # killing blow on an enemy win-condition base
  kill-outpost: 250     # killing blow on an enemy forward base
  ejection: 0           # penalty when YOUR combat ship is destroyed
  death: -25            # penalty when YOUR escape pod is destroyed
```

- `shared/Defs.cs`: `public sealed class WorldScoringTuning` next to `WorldBuildTuning` (~:1015) with those fields as initialised stock values (`float CreditWindowSeconds = 10f; int KillShip = 100; …`); `public WorldScoringTuning Scoring = new();` on `WorldConfig` after `Build` (~:747).
- `server/Content/WorldLoader.cs`: DTO `WorldScoringDef` (all nullable, XML-doc'd like `WorldConstructorDef` :503), `public WorldScoringDef? Scoring { get; set; }` on `WorldDef` after `Build` (:134), projection block after `if (w.Build is { } bd)` (~:730) using `F()` for the float and `??` for ints.
- Regenerate **from repo root**: `dotnet run --project server -- --gen-schemas` → `schemas/world.schema.json` (`additionalProperties:false`).

### A2. Attacker attribution (highest risk — do with zero behaviour change, prove with existing suites before A3)

`ShipSim` (`Simulation.cs`, after `LastCollisionTick` ~:271):
```csharp
// The last ENEMY PILOT whose damage reached this ship's HULL, and the tick it landed. Stamped in the
// single ApplyDamage seam; -1 = nobody holds credit. Unowned damage (PIG / collision / boundary /
// ownerless mine) deliberately does NOT clear the stamp — shoving a wounded foe into a rock still
// credits the shooter; the stamp simply ages out after Scoring.CreditWindowSeconds. Never serialized.
public int LastHitByClient = -1;
public uint LastHitTick;
```
`MakePod` (:2521) builds a fresh `ShipSim` and must NOT copy these (comment it) — the pod is independently killed for a D.

`ApplyDamage(ShipSim s, float dmg, uint tick, float shieldMult = 1f, int attackerClientId = -1)` (:118): after the shield block, immediately before `s.Health -= dmg`:
```csharp
if (attackerClientId >= 0 && attackerClientId != s.OwnerClientId)
{
    s.LastHitByClient = attackerClientId;
    s.LastHitTick = tick;
}
```
(Friendly fire never reaches here: blasts/mines skip same-team, same-team bolts route to `ApplyHeal`. A shield-only hit does not stamp — matches the agreed "damaged the hull".)

Threading table:

| Site | Change |
|---|---|
| `PendingShot` (:403) | trailing `int AttackerClientId = -1` |
| `FireBolt` (~:2896) | pass `ship.OwnerClientId` |
| `ResolveDueShots` (:3427 base, :3438 ship) | pass `shot.AttackerClientId` to `ApplyBaseDamage` / `ApplyDamage` |
| `MissileSim` (~:365) | `public int OwnerClientId = -1;` set in `TryFireMissile` (~:2996) next to `OwnerShipId` |
| missile direct hit (:3230) + base hit (:3232) | pass `mis.OwnerClientId` |
| `ApplyBlast` (:3259) | add `int attackerClientId` after `byte team`; pass at :3314; both callers (:3233 and the chaff-decoy detonation ~:3084) pass `mis.OwnerClientId` |
| `MineFieldSim` (`Simulation.Mines.cs:~25`) | `public int OwnerClientId = -1;` set from `ship.OwnerClientId` in `TryDeployMine` (~:65); pass at :180 |
| collisions/boundary (:947, :3580, :3581, :3731, :3755, :3799) | untouched (default −1) |
| `DamageProbe` | untouched |

`ApplyBaseDamage(int baseIndex, float damage, uint tick, int attackerClientId = -1)` (:3375): stamp `_baseLastHit[World.Bases[baseIndex].Id] = (attackerClientId, tick)` when `>= 0`. On the `hp <= 0 && wasAlive && Phase != PhaseEnded` edge, **before** the Winner/PhaseEnded latch: bump the *other* team's garrison/outpost tally (always — even if no pilot holds credit), then if the stamp is within the window award `KillGarrison`/`KillOutpost` to that pilot (`BaseKills++`, `AddPoints`). Ordering matters: the garrison that ends the match must still score.

### A3. `server/Sim/Simulation.Scoring.cs` (new partial)

```csharp
public sealed class PilotStats { public int Kills, Deaths, Ejects, Points; public int PodKills, DroneKills, PigKills, BaseKills; /* breakdown for tests; not on the wire */ }
private readonly Dictionary<int, PilotStats> _stats = new();          // keyed by client id; OUTLIVES a leaver
public IReadOnlyDictionary<int, PilotStats> MatchStats => _stats;
private readonly int[] _teamGarrisonsDestroyed = new int[2], _teamOutpostsDestroyed = new int[2];
public int GarrisonsDestroyed(byte t); public int OutpostsDestroyed(byte t);
private readonly Dictionary<ulong, (int client, uint tick)> _baseLastHit = new();
public bool StatsChangedThisStep { get; private set; }               // cleared at top of Step
public readonly List<(int oldClientId, int newClientId)> ReclaimsThisStep = new(); // drained by the hub
private uint CreditWindowTicks => (uint)MathF.Round(_scoring.CreditWindowSeconds * TickHz); // LIVE, not cached (tests retune it)
```
- `StatsFor(cid)` get-or-create; `TeamOfClient(cid)` = live ship's team else `_clientInfo[cid].team` else `Wire.NoTeam`.
- `AddPoints(PilotStats st, byte team, int pts)`: `st.Points += pts`; if `pts != 0` also `World.TeamStates[team].Score += pts; TeamStateChangedThisStep = true`; set `StatsChangedThisStep`. This is the ONLY writer of `TeamState.Score`, so the existing lobby/HUD score labels light up via the unchanged MsgTeamState.
- `CreditedKiller(victim, tick)` → `LastHitByClient` if `tick - LastHitTick <= CreditWindowTicks` else −1.
- `ScoreDeath(ShipSim victim, uint tick)`: `if (Phase != PhaseActive) return;` Killer side: `Kills++` + weight by victim kind, **pod tested first** (`IsPod` → KillPod; `IsMiner || Kind == Constructor` → KillDrone; `IsPig` → KillPig; else KillShip). Victim side only when `OwnerClientId >= 0 && !IsPig`: pod → `Deaths++` + `Death`; else `Ejects++` + `Ejection`; always flag `StatsChangedThisStep` (AddPoints is a no-op at weight 0).
- `ResetMatchStats()`: clear `_stats`, `_baseLastHit`, tallies; flag changed. Called from **`StartMatch` only** (not `ReturnToLobby`), so F5 in the lobby still shows the finished match. `World.SeedEconomy` already zeroes `Score`.
- `MigrateStats(old, new)`: move the row, flag changed, push to `ReclaimsThisStep`.

Hooks in `Simulation.cs`: field `_scoring = content.World.Scoring` next to `_combat` (:35 / :590); `StatsChangedThisStep = false; ReclaimsThisStep.Clear();` at the top of `Step` next to `TeamStateChangedThisStep = false` (:757); `ResetMatchStats()` in `StartMatch` next to `SeedEconomy` (:1204); `ScoreDeath(s, tick)` as the first line after `s.ApEngaged = false` in `ResolveDeath` (:2492) so every death form scores in one place; `MigrateStats(orphan.oldClientId, newCid)` inside the reclaim block (:1097).

### A4. Wire — `MsgMatchStats = 29`, proto 37

`server/Net/Protocol.cs` after `MsgShipLoadout = 28`:
```
MsgMatchStats = 29:
  u8 nPilots, n × { i32 clientId | str name | u8 team | u8 flags (bit0 = connected) | u16 kills | u16 deaths | u16 ejects | i32 points }
  u8 nTeams,  n × { u8 team | u8 garrisonsDestroyed | u8 outpostsDestroyed }
```
`public readonly record struct StatsEntry(int Id, string Name, byte Team, bool Connected, int Kills, int Deaths, int Ejects, int Points);` + `BuildMatchStats(IReadOnlyList<StatsEntry>, IReadOnlyList<(byte Team,int Garrisons,int Outposts)>)` next to `BuildLobbyState` (~:1634), using the existing string writer; clamp u16/u8, points stay i32 (negative). Team score is NOT repeated (rides MsgTeamState).

`shared/Net/Wire.cs`: `ProtocolVersion = 37` + a `// (2026-08-28) match scoreboard: …` changelog block in the existing style describing the layout, the reliable/on-change cadence, why name/team ride on the frame, and the EJ/D semantics.

### A5. `server/Net/ClientHub.cs`

- `private readonly ConcurrentDictionary<int, (string Name, byte Team)> _pilotIdentity = new();` — every pilot seen this match incl. leavers (`BroadcastLobby` runs from socket threads and the sim thread).
- `BroadcastLobby()` (:401): capture `var roster = _lobby.Snapshot(...)` once, fold each entry into `_pilotIdentity`, then build the frame from `roster`.
- `OnMatchStart()` (~:393): `_pilotIdentity.Clear()`.
- `BroadcastMatchStats()`: rows = every `_sim.MatchStats` entry joined with `_pilotIdentity` (fallback `$"Pilot{cid}"`, `NoTeam`), `Connected = _clients.ContainsKey(cid)`; plus a zero row for any identity with no ledger entry yet; sort by id; `SendReliable` to all.
- Call sites: `AfterStep` right after `HandlePhaseTransition()` (:1356) — first prune `_pilotIdentity` of `_sim.ReclaimsThisStep` old ids, then `if (_sim.StatsChangedThisStep) BroadcastMatchStats();`; inside `HandlePhaseTransition` after `BroadcastLobby()`; in the join handshake next to the `BroadcastLobby()` at ~:635. (Rename/team-change sites optional in v1.)

### A6. Tests — `tests/ScoreboardTest` (new; csproj copied from `tests/ShieldTest`)

Copy ShieldTest's `BootSim`/`JoinShip`/`SetupDuel`/seeker helpers and MissileTest's `SetupBaseSiege`; boot with pigs/miners/attributes off. Reliable pattern: `victim.Shield = 0; victim.Health = 1f;` then ONE attributed hit.
1. Kill credited: attacker `Kills==1, Points==KillShip`, `TeamStates[t].Score==KillShip`; victim `Ejects==1, Deaths==0`, pod exists owned by victim.
2. Then kill the pod: attacker `Kills==2, Points==KillShip+KillPod`; victim `Deaths==1, Points==Death`.
3. No-credit death: set `Health = 0f` directly → no kills anywhere, victim `Ejects==1`.
4. Window expiry: non-lethal attributed hit, step past `CreditWindowTicks`, die → no credit.
5. Unowned damage doesn't clear credit: attributed hit, then a collision hit, die → shooter credited.
6. Garrison kill via torpedo on `BaseHealth[i]=1f` → `GarrisonsDestroyed(taker)==1`, `Points==KillGarrison`, `JustEnded`, `Winner` correct.
7. Invariant: `TeamState.Score == Σ pilots' Points` per side after 1/2/6.
8. Reclaim: `EnqueueDetach(1,tok)` → step → `EnqueueReclaim(9,tok)` → step → row under 9 only, counters intact, `ReclaimsThisStep` has `(1,9)`.
9. Leaver's row survives `EnqueueLeave`.
10. `ReturnToLobby` keeps the ledger; `StartMatch` clears it (+ Score 0, tallies 0).
11. A kill during the Ended window doesn't score.

Regression gate: ShieldTest, MissileTest, MineTest, RescueTest, LobbyTest, MiningTest, ConstructorTest, StrategyTest, TeamStateStoreTest, ContentTest. Pre-existing failures to ignore: CollisionTest×4, AutopilotTest×3, FogTest×1, CommanderTest (time-seeded flaky).

---

## Part B — Client

### B1. `client/scripts/world/MatchStatsStore.cs` (new, pure POCO — no Godot types)

`record struct PilotStat(int ClientId, string Name, byte Team, bool Connected, int Kills, int Deaths, int Ejects, int Points)`, `record struct TeamTally(byte Team, int Garrisons, int Outposts)`, `enum SortKey { Name, Kills, Deaths, Ejects, Points }`, `const byte AllTeams = 2`. API: `Apply(pilots, teams)` (full replace, `Version++`), `Clear()`, `For(clientId)`, `Garrisons(t)`, `Outposts(t)`, `TeamPoints/TeamKills/TeamDeaths/TeamEjects/PilotCount(t | AllTeams)`, `TopGun()` (max kills, tie → points), `Sorted(teamFilter, key, descending)` with stable tiebreak (Points desc, Kills desc, Name). Sorting/filtering lives here so it is testable.

- `WorldRenderer`: `public MatchStatsStore MatchStats { get; } = new();` next to `TeamState`; `NetApplyMatchStats(...)` forwarder; `MatchStats.Clear()` in the Welcome `Reset` path.
- `GameNetClient`: `case 29: ApplyMatchStats(r);` in the dispatch switch (~:900); `public event Action? MatchStatsChanged;` next to `LobbyChanged` (:68); reader mirrors the writer field-for-field (existing `ReadStr`), then forwards + fires the event.
- `tests/MatchStatsStoreTest` (new; csproj compiles only the store, copy `tests/TeamStateStoreTest`): full-reconcile Apply, unknown id, aggregates incl. AllTeams, every sort key × direction, tiebreak stability, TopGun, Clear, Version.

### B2. `client/scripts/ui/RosterCells.cs` — promotion, **own commit**

Move verbatim from `Lobby.cs:1351-1495` into `public static class RosterCells` (`StellarAllegiance.Ui`): `Mono`, `Lbl`, `Cell`, `Badge`, `Diamond`, `Spacer`, `Hairline`, `Margins`, `PaddedRow`, `BarPanel`, `TabStyle`, `EmptyNote`; move `LobbyControlExt.With` (:1500) to the namespace as `UiControlExt` (UiKit has a private duplicate at `UiKit.cs:191` — make that one use the shared version). Leave `Fmt`, `StatCol`, `CurrentMap` in Lobby but make `CurrentMap()` (`Lobby.cs:466`) `internal` (or expose the map-name lookup) so the Scoreboard can label the result band. Add two new builders both screens use: `RowPanel(bool isMe, Color team, int vPad = 11)` (24px sides, hairline bottom, 2px left bar + 10% tint for "me") and `HeaderPanel()` (8px vertical, 4% TeamAccent wash, hairline bottom). Rewrite the ~90 Lobby call sites mechanically; screenshot the Lobby before/after (`godot --path client -- --ui-shot=…`) — must be pixel-identical.

### B3. `client/scripts/ui/Scoreboard.cs` (new)

`public partial class Scoreboard : Control` with `enum Mode { Live, PostMatch }`, `public static bool Active`, `Init(world, net, defs, cm)`, `Open(mode)`, `Close()`, `Toggle(mode)`. **Persistent** child of the Hud created LAST in `Hud._Ready` (after Chat) so it draws above Lobby+Chat but under ConnectLayer 150 / ModalHost 200; visibility-toggled, not freed, so sort/filter survive an F5 toggle. `UiTheme.Apply(this)`.

Layout (metrics from the mocks; all colours/fonts via `DesignTokens`/`UiKit`/`RosterCells`):
- **Live**: scrim `Void@.55`; centred 1240×600 `BracketPanel` (`FillOverride = PanelFill-ish @ .88`); title row = 12px cyan square, "SCOREBOARD" (SairaBold 16, +3 glyph spacing like the Lobby brand), `StatusPill("● LIVE", Danger, pulse)`, mono 22 match clock, garrison tally `TeamName0 n — n TeamName1` in faction colours + "GARRISONS" caption, map name, `F5 CLOSE` keycap; two team columns (header strip `Faction(t)@.10` + 3px left bar + diamond + name + `n PILOTS · n KILLS` + team pts) each with `HeaderPanel` + rows `CALLSIGN · SHIP/STATUS · K · D · EJ · PTS` at ratios `1.5/1.25/.45/.45/.45/.7`; footer legend. `MouseFilter.Ignore` on everything; **never touch `Input.MouseMode`**.
- **PostMatch**: full-screen; `MouseFilter.Stop`; `Input.MouseMode = Visible` on open (trap 3). Header `BarPanel` (brand + `UiChips.AccentChip("MATCH RESULT")`, `● N ONLINE`, gear → `SettingsDialog.Open(this)`, LEAVE → `_cm.Leave()`); result band (`StatusPill("ENDED", Warn)`, duration mono 22, `{TeamName(winner)} WINS` at DisplaySize 34 in faction colour + TextHi, reason line mono 12 Text2 "ALL WIN-CONDITION GARRISONS DESTROYED · {map} · CONQUEST", garrison tally mono 44, `F5`/`ESC` keycaps, `BACK TO LOBBY` Primary `ChamferButton` → `Close()`); body = 228px filter column (three cards using `TabStyle`; "ALL PILOTS" uses the hollow `Diamond` and Text2, never faction colour) + centre (`Title` 22 in team colour + mono 11 sub, `StatCol`-style TEAM PTS / KILLS / LOSSES, sortable `HeaderPanel`, rows `CALLSIGN · K · D · EJ · PTS` at `1.6/.5/.5/.5/.7` in a `ScrollContainer`) + 320px right column (`HairlinePanel{Title="TEAM SUMMARY"}` with Score/Kills/Ejections/Garrisons rows: blue value / caps label / red value + 3px split bar sized by share; `AlertBox`-style Top Gun with `Kind.Data`: "TOP GUN — {name}" / "{k} KILLS · {d} LOSSES · {pts} PTS" / "{team}"; footer hint). Default sort PTS desc; same header again flips; Name sorts asc first.
- Row chrome: `RowPanel` (me = faction tint + 2px bar + `Badge("YOU")`), commander ★ in `CmdrGold` via `_net.CommanderIdOf(team)`, leaver → `LEFT` badge in TextDim (flags bit0 clear).
- Sortable header cell = `Button{Flat, FocusMode=None}` wrapping `Lbl` + a 7×5 triangle `Polygon2D`/custom draw; active = `TeamAccent` + arrow (flipped when ascending), others `TextDim` + hidden arrow — the four states in `States.dc.html`.
- **Live SHIP/STATUS** (`LiveFor(LobbyPlayer p, byte myTeam)`): `ShipId == 0` → `—`/`DOCKED`; local ship (`_world.Ships.LocalShip.ShipId == p.ShipId`) → its Class/IsPod (trap 4); enemy && fog on && `!_world.IsRadarVisible(p.ShipId)` → `· · ·` TextDim, no badge (find the client fog-enabled flag the way `ShipRenderer.EnemyShips` does); not in `Ships.Nodes` → `· · ·`; else `_defs.TryGetShipDef((byte)rs.Class).Name`, badge `POD` (Warn fill) / `CONTACT` (Danger@.16) for enemies. Own team always readable.
- Esc: `_UnhandledKeyInput` (Lobby's callback), PostMatch only, ignored when `EscapeMenu.Active || SettingsDialog.Active`; `Close()` + `SetInputAsHandled`. Plus the Lobby guard (trap 2).
- Rebuild discipline: `_dirty` on `MatchStatsChanged`/`LobbyChanged`/sort/filter/open; Live mode refreshes only the ship/status labels every ~0.25 s in `_Process`. Never rebuild per frame.

### B4. `client/scripts/Hud.cs`

- Create after Chat (~:187): `_scoreboard = new Scoreboard{Name="Scoreboard"}; AddChild; Init(_world,_net,_defs,_cm)`.
- `_ShortcutInput` after the F4 block (:257): F5 → `_scoreboard.Toggle(_world.Phase == MatchPhase.Ended ? PostMatch : Live)` guarded on `_defs != null && !Chat.Capturing && !EscapeMenu.Active && !SettingsDialog.Active && !ShipLoadout.Active`; `SetInputAsHandled`. In Lobby phase with an empty ledger, Toggle is a no-op.
- `_Process` at the `if (_world.Phase == MatchPhase.Ended) DeployRequested = false;` line (:374): add `_prevPhase` edge → `_scoreboard.Open(PostMatch)` on Active→Ended (detected here, not in Lobby, because the Lobby is hidden at that edge — trap 3). Close a Live board when phase leaves Active or when `ShipLoadout.OpenedForSpawn` becomes true.

### B5. `client/scripts/Lobby.cs`

- `_net.MatchStatsChanged += () => _bodyDirty = true;` in `Init` (+ unsubscribe in `_ExitTree`).
- `RosterRow` (:1191-1195): four cells from `_world.MatchStats.For(p.Id)` (`—` when null), same ratios.
- `TeamTab` (~:1136): `"— KILLS"` → `$"{_world.MatchStats.TeamKills((byte)team)} KILLS"`.
- Header comment (:22-25): K/D/EJ/PTS and team kills are no longer placeholders. Note the ledger persists between matches (matches the score labels' existing behaviour).

### B6. Docs + gallery

- `GLOSSARY.md`: *Networking & Protocol* `### MsgMatchStats`; *Client Rendering & UI* `### Scoreboard`; *Weapons & Combat* `### Kill Credit` (LastHitBy stamp, window, EJ vs D); add `scoring:` to the World Tuning Blocks list. Use the existing entry format (one-liner, Frequency, Key Files, Related, Notes).
- `DESIGN.md`: Components — `Scoreboard` (two-mode overlay) and `RosterCells` primitives; add Scoreboard to the per-overlay theme list.
- `client/scripts/ui/UiShowcase.cs`: new "SCOREBOARD" section rendering the `States.dc.html` fixtures (row states, four header sort states, three filter cards). Verify with `godot --path client res://scenes/UiShowcase.tscn -- --ui-shot=/tmp/ui.png`.
- `shared/Net/Wire.cs` changelog (A4).

---

## Implementation order

| # | Step | Risk |
|---|---|---|
| 1 | A1 tuning end-to-end + schema regen; `tests/ContentTest` green | low |
| 2 | A2 attribution threading, **no scoring yet**; ShieldTest/MissileTest/MineTest byte-identical | **highest** — the damage seam every weapon uses |
| 3 | A3 `Simulation.Scoring.cs` + hooks | medium — phase guard + ResolveDeath ordering |
| 4 | A6 `tests/ScoreboardTest` green **before touching the wire** | low |
| 5 | A4 protocol + version bump + changelog | low |
| 6 | A5 hub memo + broadcast + call sites | medium — thread safety |
| 7 | B1 store + reader + `WorldRenderer` wiring | low |
| 8 | B1 `tests/MatchStatsStoreTest` | low |
| 9 | B2 `RosterCells` promotion — **own commit**, screenshot-gated | medium/wide |
| 10 | B5 Lobby cells — first visible end-to-end proof | low |
| 11 | B3 Live mode + B4 F5 + Lobby Esc guard | medium — layering/mouse mode |
| 12 | B3 PostMatch mode + auto-open edge | medium — fires while still flying (trap 3); test by actually ending a match |
| 13 | B6 docs + showcase | low |
| 14 | **Protocol smoke** (below) | **high — no automated coverage** |
| 15 | Format only touched files: `dotnet csharpier format <file>` (pinned 1.2.6; HEAD has ~163 dirty files — never blanket-format) | — |

## Verification

- `dotnet build wivuullegiance.slnx` clean; `dotnet run --project server -- --selftest` passes.
- New suites green: `dotnet run --project tests/ScoreboardTest`, `tests/MatchStatsStoreTest`. Regression gate from A6 green (known failures excepted).
- Lobby screenshot before/after step 9 pixel-identical; `--ui-showcase` shows the new section.
- Live smoke (`/verify` skill or manual; `AUTOFLY_TEAM` env, memory: hold ≥1 connection or the sim won't tick): server + two `--autofly` clients on opposite teams. Confirm (a) no v36/v37 mismatch at Welcome; (b) F5 in flight shows the Live board, flight input still works, F5 closes, Esc still does its two-step; (c) a kill → K on one row, EJ on the other, Lobby score labels and `/score` agree with the board; (d) an enemy out of radar reads `· · ·`, a teammate never does; (e) destroy the enemy garrison → PostMatch board auto-opens over the still-flying view with the cursor freed, WINS banner + tally correct, Esc/BACK TO LOBBY closes, F5 reopens in the lobby; (f) a reconnect mid-match keeps the pilot's row (no duplicate "LEFT" row); (g) a leaver stays on the end board flagged LEFT; (h) the next `StartMatch` zeroes everything.
