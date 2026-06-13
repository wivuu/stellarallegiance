# NATIVE-SIM — remaining work on the 200-player architecture

Status of the scaling effort (plan origin: the 2026-06-12 "scale to 200 players" session;
full option analysis in that session's plan file). **Done and verified:**

- **Phase 0** — in-STDB relief (commit `d0eb6b7`): no Projectile table (clients synthesize
  bolts from `LastFireTick` + deterministic spread), batched `ShotResolution` drain inside
  `SimTick`, held-input replay with on-change client sends, PigBrain world snapshot.
  Bench: 50-drone combat dropped from ~80–90% to ~60–70% of a core.
- **Phase 1** — native sim server (`server/`, `tools/simbot/`): 20 Hz authoritative sim in
  process memory, WebSocket AOI snapshots. Bench: 200 ships / 200 firing connections at
  20 Hz exact, step p50 3.3 ms, egress ~122 KB/s/client.
- **Phase 1b** — Godot client adapted (`client/scripts/GameNetClient.cs` + WorldRenderer
  `Net*` entry points + per-call STDB gating). `SIM_URI` dev override; prediction stack
  untouched; tick-stamped input ring on the server preserves replay parity.
- **Phase 1c** — lobby handoff: `SimEndpoint`/`JoinToken`(RLS)/`SimConfig` tables,
  `set_sim_endpoint` owner reducer, `shared/JoinTokens.cs` offline validation
  (`--secret`/`SIM_SECRET` on the server), client auto-activation on token arrival.
  Local dev: `scripts/start-native-client.sh` boots a fresh sim server + SIM_URI client;
  `scripts/start-client.sh` is the plain STDB path (no sim server).

Everything below remains. Steps are ordered by recommended sequence.

---

## 1. Two-machine human playtest - DONE

Before building more, validate prediction feel over a real network.

- Host: `scripts/publish-local.sh`, `dotnet run --project server -c Release -- --secret S`,
  `spacetime call stellar-allegiance set_sim_endpoint '"ws://<host-ip>:8090/game"' '"S"'`.
- Both machines: `STDB_URI=ws://<host-ip>:3001 scripts/start-client.sh`
  (plain STDB client — no local sim server; the lobby flow hands both clients to the host's
  sim server via their JoinTokens once the match starts).
- Watch for: reconcile frequency under mouse steering (no ping echo yet → fixed lead 3),
  remote-bolt timing (shot-mask lead), death/respawn flow without the lobby UI.

## 2. Match lifecycle on the sim server + result writeback — DONE (2026-06-13)

Implemented; builds clean (server/client/simbot + module via docker) and protocol
smoke-tested under 20-bot load. The actual base-destruction → end → writeback → lobby
loop is logic-reviewed but not yet integration-tested end-to-end (a base reaching 0 HP
needs the two-machine playtest in step 1, or a directed-fire harness).

- Win condition: `Simulation.ResolveDueShots` flips Phase→Ended / Winner=other-team when a
  base hits 0 HP (latches on the first base to fall); `JustEnded` fires for exactly one step.
- Snapshot header carries `phase` + `winner` bytes (`ClientHub.BuildSnapshotFor`); client
  `WorldRenderer.NetSetMatch` drives the banner (replaced the old `NetSetTick` that pinned
  Active). Base health streams via a new broadcast `MsgBases` frame (5) on change + every
  coarse tick (`Protocol.BuildBases`), rendered through `NetUpdateBaseHealth`.
- Writeback: `server/Net/ResultReporter.cs` POSTs `report_match_result(winner)` over STDB's
  HTTP API (Bearer from `STDB_TOKEN`; `STDB_HTTP`/`STDB_DB` configurable; absent token =
  logged skip). New owner-gated module reducer `ReportMatchResult` flips Match→Ended/Winner
  (idempotent — no-ops unless Active). `RestartMatch` now clears all JoinTokens.
- Lobby return: on a phase=Ended snapshot, `GameNetClient.BeginLobbyReturn` drops the game
  socket (per-connection CTS, node survives), calls `WorldRenderer.DisableNativeMode`
  (re-renders the static STDB world from the live subscription cache), and flips `Active`
  off so the STDB-driven post-match/RestartMatch UI resumes; a fresh JoinToken next match
  re-activates a new socket.
- Server self-reset: `Simulation.ResetMatch` refills bases / clears win state / drains the
  shot ring; `Program.cs` calls it once the server empties out, so back-to-back matches reuse
  one process. (Full per-match isolation = step 6.)

**Known follow-ups noted for later:** the module's own `SimTick` still runs in parallel while
native mode is active, so the post-match screen can briefly show module-sim ships after
`DisableNativeMode` re-enables STDB rendering — clean up under step 6 (gate `SimTick` behind
the `--stdb-sim` fallback).

## 3. Port remaining gameplay into server/Sim — DONE (2026-06-13)

Ported from the module onto the native sim's in-memory ship list. Builds clean (server/
client/simbot); smoke-tested at 8 bots (drones scrambled 8→38 ships in staggered squad
waves, step p50 ≤0.17 ms / p99 <6 ms, clean DespawnAllPigs teardown when the server
emptied, no sim exceptions); FlightModel golden tests still pass (shared physics untouched).

- **PigAI** → `server/Sim/Simulation.Pig.cs` (a `partial class Simulation`). Full port:
  per-team slots + squad-wave lifecycle (`PigSquadDelayTicks`, staggered launches), the
  5 Hz decide / 20 Hz execute split (`PigBrainStep` gated on `tick % PigBrainEvery`,
  `PigExecute` every Pass-A tick from a cached `PigPlan`), threat-scored target selection
  with hysteresis + base-defense + cross-sector aleph pursuit, per-slot aim skill
  (lead/turn-gain/wobble), juking, patrol, enemy-base shelling, asteroid-avoidance steering.
  Drones are `IsPig` ShipSim with `OwnerClientId=-1`; the brain reads `_order`/`_ships`/World
  directly (no scratch snapshot needed — it's all in process memory).
- **Pods / rescue / docking** → `Simulation.cs`. A player combat death ejects a player-flown
  pod (`EjectPlayerPod`), a PIG death ejects a PodThink-autopilot pod (`KillPigCombat`); pods
  resolve via friendly-base dock (`DockShip`), friendly hull-contact rescue (rescue pass), or
  destruction (`KillPod`). This required a **player-ship ownership refactor**: a client's
  controlled ship now changes ID across combat→pod→respawn, so `_byClient` tracks the current
  ship, `_clientInfo`/`_clientRespawn` drive a respawn scheduler, structural changes are
  deferred via `_toRemove`/`_toAdd`, and the hub re-issues `YouAre` whenever the client's
  ship flips (id 0 = dead/awaiting respawn). IsPod/IsPig now ride a **ShipRecord flags byte**
  (`Protocol.Version` bumped 2→3; client mirror in GameNetClient sets `Ship.IsPig/IsPod`, and
  WorldRenderer already keys mesh/material/HUD/FX off those flags).
- **Warp exit jitter**: `Simulation.TryWarp` now jitters the exit cone per-axis by
  `World.WarpExitJitter` (0.12) via a server-only `Random` (baked into ship state — clients
  read it, never reproduce it).
- Boundary/kill parity: the physics/collision/warp/fire path is the *shared* FlightModel
  (unchanged), so the golden tests are the parity check — they pass. The new pig/pod logic is
  server-only with no module-bit-identical contract (plain MathF + plain RNG), so it's a
  behavioural port, not a golden one.

**Known follow-ups for later:** spawn-menu reopen on player-pod resolution is still
server-auto-respawn (the wire round-trip to the STDB spawn menu is step 5); pig count is
`MaxPigsPerTeam=25` (so up to 50 drones) — tune if a playtest wants fewer.

## 4. Protocol v2 — quantization + pooling — DONE (2026-06-13)

Both levers landed and re-benched at 200 bots / ~248 ships (PigAI drones scrambling).
**Protocol.Version 3→4** (client `GameNetClient.ProtocolVersion` mirrored).

- **Quantization** (`shared/WireQuant.cs`, single source of truth both ends): the snapshot
  ship record dropped **83→47 B** — pos→int16 sector-local (sectors are origin-centred so
  Pos already IS sector-local; ±8192 range, 0.25 u step), rot→smallest-three u32, vel/angvel/
  abpower/health→f16 (System.Half), id/ticks kept full. The static Welcome stays full-float
  (sent once). Codec round-trip verified over 200k random samples: max pos err 0.21 u, rot
  0.004 rad, vel 0.06 u/s — all an order of magnitude inside the client reconcile tolerances
  (PosTolerance 0.5 u, RotTolerance 0.05 rad), so quantizing the local player's OWN record
  doesn't trigger spurious reconciles.
- **Pooling** (`ClientHub`): each alive ship's record is serialized ONCE per tick into a
  reused `_recordScratch` (no longer re-serialized per viewer); each client's snapshot is a
  `Buffer.BlockCopy` of its AOI slices into an `ArrayPool`-rented frame, returned to the pool
  after `SendAsync`. The outbound channel now carries an `OutFrame{buf,len,pooled}`
  (handshake/broadcast frames are exact-sized & not pooled; a channel-dropped pooled frame
  just isn't returned — ArrayPool falls back to alloc, the pre-pool behaviour).
- **Measured (200 bots, M-series, Release):** egress **122→~73 KB/s/bot** (under the <100
  budget); step **p50 3.3→2.5 ms**, **p99 7–40→6–19 ms** steady (one 29 ms transient at the
  200-simultaneous-connect storm + JIT warmup). Meets the acceptance recap below.
- NOT done (deferred, not needed at 200): delta-encoding vs last-acked snapshot; ENet/UDP
  transport (reserved for if TCP head-of-line blocking shows under real loss in the playtest).

## 5. Client polish for native mode — DONE (2026-06-13)

Both projects build clean; v5 Welcome + snapshot path smoke-tested at 30 bots (no
serialization errors). **Protocol.Version 4→5** (client mirror bumped).

- **Ping echo — DONE.** New `MsgPing`(3, client→server u32 nonce) / `MsgPong`(6, echo).
  The hub bounces the nonce through the same outbound channel snapshots use (so RTT reflects
  real send-side latency). `ShipController` probes at 4 Hz in native mode (`PingIntervalSec`),
  keys send-times in `_sentAt` by nonce, and `OnPong` feeds the same `RecordRtt` EWMA the STDB
  reducer-ack path (`OnInputAck`) uses — so `UpdateAdaptiveLead` now tracks the link in native
  mode instead of pinning the default lead. (`_sentAt` holds only ping nonces in native mode,
  only reducer ticks in STDB mode — the modes never coexist.)
- **Asteroid visuals — DONE.** Variant index + 3 orientation floats now ride the Welcome
  asteroid record (one-time, unquantized — egress irrelevant). Server `World.NextShape` draws
  them from the same `DetRng` sequence the module's `NextAsteroidShape` uses (kept the draw
  order, so map positions stay byte-stable AND the cosmetic shape matches). The canonical name
  list moved to `shared/AsteroidShapes.cs` (server uses `.Length` for the draw count; client
  maps the index → `res://assets/asteroids/<name>.glb`; out-of-range → "" → sphere fallback).
  The module keeps its own copy of the list for now (comment says keep in sync).
- **Lobby/HUD coherence — DONE (client gating).** `WorldRenderer.NativeMode` is now public;
  `Lobby` and `Hud` defer to a live native match even with no STDB team (dev `SIM_URI` play),
  so the lobby no longer sits on top of the game and the spawn menu appears in native mode.
  Because the menu keys off "not flying", it already reopens when a ship is lost. **Caveat:**
  the server still AUTO-respawns players (step 3's `_clientRespawn`), so the menu won't actually
  linger on pod resolution until the server is changed to wait for a fresh Hello instead — a
  small server-side respawn-flow change, deferred to the playtest / step 6.
- **Death FX parity — VERIFIED (no change).** `NetDeleteShip → DeleteShip` is the identical
  path as STDB mode; the explosion spawns BEFORE `QueueFree()` from the last-snapshot row coords
  (remote) or the predicted node position (local). In native mode `ApplyShipGone` passes the
  last `_rows` entry, so the blast lands at the last authoritative position. Correct as-is.

## 6. Production deployment shape

- Sim server: systemd/container next to the self-hosted STDB; `--secret` from env -- use docker compose for local dev.
  one process per match initially (the Simulation class is per-match already —
  multi-match = multiple Simulation+hub instances or processes; decide when needed).
- STDB stays the durable spine: accounts/lobby/chat/defs/results. Maincloud cannot host
  the sim server — production = self-hosted STDB + sim server on the same box/VPC
  (decided in the original plan: self-host OK).
- Module cleanup once native is the only path: the in-module SimTick/PigAI hot path can
  be deleted (or kept behind PigsEnabled for the `--stdb-sim` fallback) — decide after
  the playtest signs off.

## Acceptance recap (from the original plan)

- 20 Hz held at 200 firing clients, p99 step < 20 ms (needs step 4's pooling),
  per-client egress < 100 KB/s (needs step 4's quantization).
- Two-machine playtest: flight feel ≈ current STDB build or better; reconciles rare
  under mouse steering (needs step 5's ping echo for WAN).
