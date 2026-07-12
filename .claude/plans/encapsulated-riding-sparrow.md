# Commander — per-team AI order authority + F3 select-and-command

## Context

Stage-3 item from `.PLAN/README.md:294`. Today AI authority is "bootstrap-simple, no commander" (`Simulation.cs:1256`, `Simulation.Mining.cs:241`): any teammate can `/mine`/`/buyminer`, and combat PIGs are fully autonomous. This feature introduces the **commander** — one designated player per team who can direct AI vessels — plus an RTS-style select-and-command interaction on the F3 tactical map. It closes the deferred "commander-gated authority" mining item and lays the state foundation for the later promote/mutiny items.

**User decisions (locked in):**
- Commander = **explicit per-team state** (not derived): seeded to first player joining the team; on leave/drop falls to next-lowest client id on the team; **manually reassignable** (so it can't be purely computed). A dropped commander who reconnects simply lost rank (re-promote manually).
- **Any entity** is left-click selectable on F3 (friendly/enemy ships, bases, rocks); click-away deselects.
- Right-click commands the selected friendly ship. Orders to **human** teammates are advisory: a team-chat directive rendered in **gold**. AI ships execute orders only from the commander; everyone else gets a warn.
- Order lifetime: **until complete, then revert** to autonomy (attack target dies/contact lost → revert; goto-idle holds position with self-defense until new order/explicit clear).
- `/mine` and `/buyminer` become **commander-only**; right-click with a miner selected maps to mining semantics.
- Existing F3 right-click UX (own-ship autopilot engage) preserved when no friendly ship is selected.

## Architecture

- **Commander state lives in `Lobby`** (connection layer, like host/leader); all authorization happens in `ClientHub` (same pattern as `LeaderOf`-gates-rename at `ClientHub.cs:616` and `_hostId`-gates-map). The sim never learns who the commander is — it only receives already-authorized orders.
- **One new client→server message `MsgOrder = 12`** (34-byte: `u64 subjectShipId, u8 targetKind [0 ship/1 base/2 rock/3 point/255 clear], u64 targetId, u32 sector, 3×f32 pos`) — same target vocabulary as `MsgSetAutopilot`. **Verb inferred server-side** from target kind + team (server must re-validate against authoritative state anyway; no client/server verb drift).
- **No new server→client message.** Directives ride `MsgChatRelay` with **new scope `2` = commander order directive** (existing scopes: 0 all, 1 team); rejections/acks via existing `SystemTo`. Commander ids stream on the **`MsgLobbyState` tail** (append after `selectedMap` — prefix stays byte-stable).
- **AI orders stored sim-side** in `Dictionary<ulong /*ShipId*/, PigOrder>`, consumed by a new top-priority `TryObeyOrder` in the `PigDecide` chain emitting only **existing** plan kinds (`Chase`/`AttackPoint`/`SteerPoint`) — `PigExecute` untouched, steering stays in the deterministic `AutoSteer` subset. Miner orders translate immediately into existing `MinerSlot`/team state.
- Protocol bump: `Wire.ProtocolVersion` 33 → **34** + changelog line.

## Implementation

### 1. Commander state — `server/Net/Lobby.cs` (+ unit test)
- `int[] _commander = { -1, -1 }` inside the existing lock; `CommanderOf(byte team)`; `SetCommander(byte team, int clientId)` (validates membership).
- Private `FixCommander(byte team)` called from `Add`/`SetTeam`/`Remove`: if the current commander is no longer on the team, fall to lowest client id on that team (reuse `LeaderOf`'s scan at `Lobby.cs:73`), −1 if empty. First joiner seeds automatically; manual assignments stick while the assignee stays on the team.
- New `tests/LobbyTest` (plain-class unit test, `Check()` PASS/FAIL idiom, register in `wivuullegiance.slnx`): seed-first-joiner, fall-on-leave, fall-on-team-switch, manual set sticks, non-member refused, empty team = −1.

### 2. Wire — `server/Net/Protocol.cs`, `shared/Net/Wire.cs`, `client/scripts/GameNetClient.cs`
- `MsgOrder = 12` const (client→server block); `MsgChatRelay` scope-2 comment.
- `BuildLobbyState` (`Protocol.cs:1204`): add `int commander0, int commander1` params, write both `i32` after `selectedMap`; `ClientHub.BroadcastLobby` (~line 349) passes `_lobby.CommanderOf(0/1)`.
- `Wire.ProtocolVersion = 34` + changelog: "v34: MsgOrder=12; chat scope 2 = commander directive; MsgLobbyState tail += i32 commander0/commander1".
- Client: `GameNetClient.SendOrder(ulong subject, byte targetKind, ulong targetId, uint sector, Vector3 pos)` modeled on `SetAutopilot` (line 387); `ApplyLobbyState` (line 1516) reads the two i32s; new props next to `HostId`: `Commander0Id/Commander1Id`, `CommanderIdOf(byte team)`, `IsCommander`.

### 3. Hub routing + authorization — `server/Net/ClientHub.cs`
- New `case Protocol.MsgOrder` in the receive switch (model: `MsgSetAutopilot` case at line 710) → `HandleOrder(client, ...)`:
  1. `TeamOrWarn` (NOAT rejected).
  2. **Human subject?** Scan `_lobby.Snapshot(id => _sim.ShipIdOf(id))` for `ShipId == subjectShipId` on the issuer's team. If so: commander → gold scope-2 team directive ("CMDR ▸ Erik: attack Reaver-3"); non-commander → plain scope-1 team chat with "▸" prefix (advisory, not gold). Nothing reaches the sim.
  3. **AI subject:** require `_lobby.CommanderOf(team) == client.Id`, else `SystemTo(client, "Only the commander can command AI vessels — ask {name}.")`.
  4. Commander + AI → `_sim.EnqueueCommandOrder(client.Id, team, subject, targetKind, targetId, sector, pos)`. Ack directive is emitted **after sim validation** (via notices list, step 4) so fog-invalid orders never announce.
- **`/commander <name>`** in `HandleCommand` (line 740): no arg → report current commander; with arg → resolve name (case-insensitive unique prefix against issuer's team roster, like `ResolveSector`); authorize current commander OR `_hostId`; apply via `_lobby.SetCommander`, `BroadcastLobby()`, team system message. Relay `/commander` client-side like `/mine` (`client/scripts/Chat.cs` `HandleCommand` ~line 226) + `/help` text.
- **`CommanderOrWarn(client, team)`** helper; apply to `/buyminer` (line 761) and `/mine` (line 767); `/miners` status stays open. Update the "no commander yet" comments at `Simulation.Mining.cs:241` / `Simulation.cs:1256`.
- Feedback relay in `AfterStep` next to the miner-notice loop (~line 925): per-client notices via `SystemTo`-by-id, team directives via scope-2 `BuildChatRelay`.

### 4. Sim order handling — `server/Sim/Simulation.cs` + `Simulation.Pig.cs` + `Simulation.Mining.cs`
- Queue + `EnqueueCommandOrder` under `_qLock`, drained in the existing drain block (~line 909) → `ApplyCommandOrder` on the sim thread.
- `ApplyCommandOrder` validation: subject exists/alive/on-team and is AI (`IsPig && !IsPod`, or a `_miners` slot ship); **verb inference**: enemy ship → AttackShip (requires `TeamRadarSees(team, targetId)` at issue — the fog gate `GatherPigContext` uses; reject "No radar contact"); enemy base → AttackBase (accept even without base-damaging weapon, but notice "…has no base-damaging weapon"); friendly ship/base/rock/point → GotoIdle at its position; `targetKind 255` → remove order, notice "released to autonomy". Rejections → issuer notice; accepted orders → team directive.
- **Storage:** `Dictionary<ulong, PigOrder> _pigOrders` keyed by ShipId (orders die with the drone — never inherited by respawn); `struct PigOrder { byte Kind; ulong TargetShipId, TargetBaseId; uint Sector; Vec3 Pos; bool Holding; }`. Prune with the stale-decision prune in `PigBrainStep` (Pig.cs:247), clear in kill/despawn/phase-transition paths.
- **`TryObeyOrder`** inserted in `PigDecide` chain (Pig.cs:527) **after `TryRescue`** (rescue outranks orders — don't strand player pods; document):
  - AttackShip: target dead/missing/contact-lost → complete (remove, return null → autonomy). Same sector → chase plan (`PigKindChase`); cross-sector → `SteerPoint` toward `World.NextGateTo` gate (multi-hop).
  - AttackBase: base destroyed → complete. Same sector → `AttackPoint` plan exactly as `TryAttackBase` emits (Pig.cs:719: base pos, `Radius`, `TargetBaseLockId = GameContent.BaseLockId(id)`); cross-sector → gate `SteerPoint`.
  - GotoIdle: en-route `SteerPoint` (gate-hopping cross-sector); on arrival set `Holding`. While holding: if an aggressor is within a defend radius (new const `PigOrderDefendRange ≈ 2× fire range`) → chase it this brain tick; else station-keep at `Pos`. Persists until replaced/cleared.
- **Miner subjects** (`ApplyCommandOrder` branch): rock target → `ApplyMineOrder(team, rock.SectorId)` + pin `slot.TargetRockId`, `State = ToRock`, `Idle = false` (steal a teammate-miner's claim — commander intent wins); point/sector → `ApplyMineOrder(team, sector)` + clear `TargetRockId`/`LastRockId` so next brain tick re-picks there; friendly base → `State = ToBase` offload; enemy target → reject "Miners don't fight."
- Feedback plumbing: `OrderNoticesThisStep` (per-client) + `OrderDirectivesThisStep` (per-team) lists on `Simulation`, cleared where `MinerNoticesThisStep` clears (~line 638).

### 5. Client F3 — `client/scripts/SectorOverview.cs`
- New `public static ulong SelectedId` (FocusedId encoding; 0 = none) — **separate from `TargetMarkers.FocusedId`**. Cleared on `Close()` and when the entity vanishes (revalidate in `_Process`).
- `TryPickEntity` (line 480): add `_world.FriendlyShips()` (WorldRenderer.cs:750) to the nearest-within-`PickRadiusPx` competition (bases already scan any team).
- `HandleMapClick` (line 449), left click: entity hit → `SelectedId = encoded` AND keep legacy `SetFocus`+`ClearWaypoint`; grid miss → `SelectedId = 0` (click-away deselect) + legacy waypoint drop.
- Right click: if `SelectedId` is a **friendly, non-local ship** → resolve the right-click pick (`TryPickEntity` → kind/id with flags stripped, else `TryGridPoint` → kind 3 + `ViewSector` + pos); pick == the selected ship itself → `SendOrder(..., targetKind: 255)` (clear); otherwise `SendOrder(subject, kind, id, sector, pos)`; **do not** touch focus/own autopilot. Else → legacy path unchanged (`SetFocus`/`SetWaypoint` + `EngageAutopilot`). Minimap precedence (`TryMinimapClick`) untouched. Works pre-launch (no local ship needed).
- Selection bracket: small `MouseFilter.Ignore` Control on the existing `_hudLayer`, reprojecting `SelectedId`'s world pos through `_cam` each frame; corner brackets — gold for friendly commandable ships, cyan otherwise.

### 6. Gold rendering + badge — client
- `client/scripts/ui/DesignTokens.cs`: add `CmdrGold` token (≈ `#FFD24D`; `Secondary` is amber and semantically credits/highlight — keep intent separate). Reused by chat directive, CMDR badge, selection bracket.
- `client/scripts/Chat.cs` `FormatLine` (line 321): scope-2 branch → whole line gold BBCode, e.g. `★ CMDR {name} ▸ {text}`. Mirror in `client/scripts/Lobby.cs` `RebuildComms`/`ChannelShows` (~lines 1139/1163, show on team channel).
- CMDR badge: `Lobby.cs` `RosterRow` — replace the placeholder comment at line 1062 with `Badge("CMDR", CmdrGold)` beside the `YOU` badge when `p.Id == _net.CommanderIdOf(p.Team)`.
- Optional (last, skippable): "MAKE CMDR" `ChamferButton` on teammates' rows for the commander/host, sending `/commander <name>` via `SendChat`.

### 7. Tests — new `tests/CommanderTest` (model: `tests/AutopilotTest`)
Drives `Simulation` directly via `EnqueueCommandOrder` (hub auth covered by LobbyTest + manual smoke). Expose order state via a `PigOrdersView()` test accessor (mirror `MinerSlotsView()`). Scenarios:
1. Attack-ship order overrides autonomy within one brain tick; 2. target killed → order removed, autonomy resumes; 3. fog rejection (no radar contact); 4. goto-idle arrives and holds; 5. hold self-defense (chases nearby aggressor, resumes hold); 6. explicit clear; 7. cross-sector goto via gates; 8. miner rock order pins `TargetRockId` + authorizes sector; 9. determinism guard (same scenario twice → bit-identical positions).

## Verification
1. `dotnet build` server + client projects; run `tests/CommanderTest`, `tests/LobbyTest`, plus `AutopilotTest`/`MiningTest`/`PigTest`-adjacent suites for regressions. Known pre-existing failures to ignore: ShieldTest/ContentTest/FactionsTest content-drift, FogTest sector-leak.
2. Protocol bump smoke (dotnet suites don't cover the Godot client): `verify` skill / `--autofly` with two clients — (a) CMDR badge on first joiner + `/commander` handoff; (b) F3: select friendly pig → right-click enemy → gold directive + pig converges; (c) non-commander order → warn; (d) right-click with nothing selected still engages own autopilot; (e) `/mine` as non-commander warns; (f) select human teammate → right-click → gold advisory in team chat.

## Risks / gotchas
- **PIG determinism:** `TryObeyOrder` emits only existing plan kinds via existing steering wrappers; no RNG. Guarded by CommanderTest scenario 9.
- **Fog wallhack:** attack orders gated on `TeamRadarSees` at issue AND during execution (contact lost ⇒ complete). Goto into undiscovered sectors allowed (miners already roam authorized sectors).
- **Id disambiguation:** `MsgOrder.targetId` is disambiguated by `targetKind`, never flag bits — strip `BaseLockFlag`/`AsteroidFocusFlag` client-side (same contract as `SetAutopilot`).
- **Thread safety:** commander reads under `Lobby`'s lock; hub→sim via locked queue; sim→hub via `*ThisStep` lists in `AfterStep` (all established patterns).
- **Order lifecycle:** keyed by ShipId so respawned drones never inherit orders; clear `_pigOrders` on despawn-all/phase transitions.
- Version bump forces client/server lockstep (Welcome check refuses skew) — old clients can't misread the new tail/scope.
