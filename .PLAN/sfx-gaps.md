# Sound-effect gaps — events that should have audio

An audit of gameplay events and state transitions in the Godot client that currently
play **no** sound. Each entry names a proposed `SfxManager.SfxId`, the hook point
(`file:line`) where the event is already detected, and the trigger.

**How to add one:** every gap needs (1) a new `SfxId` + entry in `SfxManager.Files`
(and `Loops` if it's a bed), (2) a placeholder `.ogg` in `client/assets/audio/`, and
(3) a `PlayAt(...)` / `PlayUi(...)` call at the cited hook. Positional world events use
`PlayAt`; interface/HUD cues use `PlayUi`. See `client/scripts/SfxManager.cs`.

**Already covered (for reference):** weapon fire, explosion, bolt/shield impact,
ship–ship/asteroid collision, missile launch/lock/warning, lock-warning, missile-empty,
contact blips, engine/booster loops, mining loop, probe ping, asteroid ambient, chaff
deploy, mine deploy, UI clicks / menu open-close, hangar ambient hum.

**NOTE**:
Samples of the missing sound effects can be found in the 'pick-assets/sound-effects' folder; prompt developer if unsure.

---

## Tier 1 — high impact (core game-feel moments)

| Proposed SfxId | Event | Hook | Trigger |
|---|---|---|---|
| `Dock` / `Landed` | **Own ship docks at base** (the example ask) | `WorldRenderer.cs:1980` `DeleteShip` w/ `reason == GoneClean` | voluntary dock / pod rescue — currently explicitly silent |
| `Launch` | **Own ship launches from base** | `WorldRenderer.cs:1867` `SetMeta("Launched", true)` | base spawn/respawn/pod-eject; launch cinematic runs silently |
| `Spawn` | **Local ship spawned/materialized** | `WorldRenderer.cs:1878` `LocalShip = pc` | fresh deploy into a sector |
| `PlayerDeath` | **Local ship destroyed** (distinct from remote explosion) | `WorldRenderer.cs:2011` (generic `Explosion` today) | your ship dies — needs a "you were killed" sting vs. generic boom |
| `HullAlarm` | **Low / critical hull alarm** (own ship, threshold cross) | `SystemRing.cs:82` `hullFrac`; `Hud.cs` local handle | HP drops below a warn/critical fraction — looping or one-shot klaxon |
| `MatchStart` | **Match start** (Lobby → Active edge) | `Lobby.cs:785` phase edge-detect | round begins — countdown/horn |
| `Victory` / `Defeat` | **Match end**, win vs. lose differentiated | `Hud.cs:266` / `Lobby.cs:860` (`_world.Winner`) | end screen — no sting today |

## Tier 2 — combat & survival feedback

| Proposed SfxId | Event | Hook | Trigger |
|---|---|---|---|
| `ShieldDown` | **Own shield fully depleted** | `SystemRing.cs:89` `shieldFrac`; `WorldRenderer.cs:95` `_shipShield` | shield crosses to 0 |
| `ShieldRestored` | **Shield back online / recharge complete** | `WorldRenderer.cs:1126/1132` `_shipShield` | shield climbs back to full |
| `LockAcquired` | **Player acquires a target lock** (player-side tone) | `TargetMarkers.cs:371/375` `HandleFocusCycle` | you lock a target — only *incoming* lock warnings sound today |
| `TargetCycle` | **Tab-target cycle blip** | `TargetMarkers.cs:170` `SetFocus`, `:371` cycle | cycling targets / F3-map pick |
| `PodEject` | **Escape-pod ejection** (ship dies → pod same tick) | `WorldRenderer.cs:2017-2021` | silent transition into pod |
| `BaseDestroyed` | **Base destroyed / win-condition objective** | `WorldRenderer.cs:780` `BaseIsDead`, `:987` `NetUpdateBaseHealth` | base health → 0 — objective alarm |

## Tier 3 — navigation, economy, notifications

| Proposed SfxId | Event | Hook | Trigger |
|---|---|---|---|
| `Warp` / `GateJump` | **Own ship warps between sectors** | `WorldRenderer.cs:1884` `_localSector = row.SectorId`; `:394` distinguishes warp from spawn | gate jump / sector transition |
| `RockDepleted` | **Rock mined out / depleted** | `GameNetClient.cs:781` (orePct→"DEPLETED"), `:1264` | asteroid fully mined |
| `CreditsEarned` / `PurchaseConfirm` | **Team credits change / purchase commit** | `GameNetClient.cs:1047` `NetUpdateTeamState`; `RequestSpawn` `:318`; `Hud.cs:308` | credits gained or spent (e.g. `/buyminer`) |
| `ChatReceived` | **Incoming chat message chime** | `Chat.cs:107/111` `ChatReceived += OnChat`; `Lobby.cs:675` | new message arrives |
| `ActionDenied` | **Error / denied action buzz** | `ui/ShipLoadout.cs:616-626` (`Locked`/`OverCapacity`); `GameNetClient.cs:120` reject reason | launch refused (locked / can't afford), join rejected |
| `MinerDeploy` | **Miner drone deployed/spawned** | `WorldRenderer.cs:1893` remote-ship path; `IsMiner` at `GameNetClient.cs:1668` | AI miner enters the sector |
| `MinerLost` | **Miner drone destroyed** | `WorldRenderer.cs:2011` (generic `Explosion` today) | distinct "miner lost" cue |

---

## Notes & non-gaps

- **Cargo full / ore collected** — no client-side signal exists: the client tracks no
  local cargo hold (economy is team-credits only). Would need a new server→client field
  before it can be hooked. `MiningBeam.cs` only owns the mining loop.
- **Countermeasures already sound**: chaff deploy (`ChaffFx.cs:73`, reuses `Impact`),
  mine-field deploy (`MinefieldViews.cs:105`). No gap.
- **Contact-lost toast** (`WorldRenderer.cs:1992`) is silent — minor, optional.
- **Taking non-bolt hull damage** (collision / missile-splash / sector erosion) has no
  damage cue; only bolt/shield hits sound (`WorldRenderer.cs:2595/2600`). Sector-boundary
  "HULL FAILING" plays a one-shot `UiNotify` (`Hud.cs:381`) — could use a dedicated alarm.
- Several deaths funnel through the same generic `Explosion` at `WorldRenderer.cs:2011`
  (local death, pod eject, miner loss) — differentiating them is the theme of Tiers 1–3.
