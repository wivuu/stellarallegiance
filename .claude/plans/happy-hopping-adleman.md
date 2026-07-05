# Radar revisions + shared protocol constant

## Context

The fog-of-war branch (proto 23) shipped per-team vision, radar contacts, and passive probes. Five follow-ups tighten it up:

1. The protocol version is hand-duplicated in `server/Net/Protocol.cs:25` and `client/scripts/GameNetClient.cs:879` and must be bumped in lockstep — move it to one shared constant.
2. New radar contacts appear silently — play a contact sound (assets from `pick-assets`).
3. Radar signature is a static per-hull value; firing weapons should temporarily and significantly boost it.
4. The full sector list is sent to every client up front; a team should not know other sectors exist until it discovers an aleph leading to them.
5. Probes are visually tiny (1.2 u) and invulnerable; they need to be bigger and destructible.

All land on `fog-of-war` under a **single protocol bump 23 → 24**. Constraint (existing invariant): the **fog-off `MsgWelcome` world block stays byte-identical** to today. New vision sources/targets must be registered in **both** `CaptureVisionInput` and `IsPointVisibleToTeam`.

**User decisions (already made):** probes get a small YAML-authored HP pool (not one-hit-kill); undiscovered sectors are hidden entirely (no "?" nodes); contact sound distinguishes enemy vs neutral tones; sound triggers on **radar-tier contacts only** (ghosts silent).

---

## 1. Shared protocol version constant (do first — carries the bump)

- New `shared/Net/Wire.cs` (dir exists, holds WebRtcSdp.cs; note `Shared.csproj` has `<Compile Remove="Net/**/*.cs" />` — **remove/adjust that exclusion so Wire.cs compiles into the shared lib** while WebRtcSdp.cs stays link-included; simplest: scope the exclusion to `Net/WebRtcSdp.cs`), namespace `StellarAllegiance.Shared`:
  ```csharp
  public static class Wire { public const byte ProtocolVersion = 24; public const byte NoTeam = 0xFF; }
  ```
  Setting 24 here IS the branch's bump.
- `server/Net/Protocol.cs:25,31`: `public const byte Version = Wire.ProtocolVersion;` / `NoTeam = Wire.NoTeam;` — const-to-const aliasing keeps all call sites (`BuildWelcome`, `LobbyRegistrar.cs:206`) untouched.
- `client/scripts/GameNetClient.cs:879,884`: same aliasing (keeps `ServerLobbyOverlay.cs:440`, `Chat.cs:237` untouched).
- Update the "must match" comments at both sites to point at the shared constant.

## 2. Radar contact sound (client-only)

Radar ids arrive via `MsgContacts`(17) → `GameNetClient.ApplyContacts:1021` → `WorldRenderer.NetSetContacts` (`client/scripts/WorldRenderer.cs:156`), which wholesale-replaces `_radarVisible`. Server builds the radar set from `VisibleEnemyShips` (`Protocol.cs:607-640`), so radar contacts are hostiles by construction.

- Assets: copy `pick-assets/sound-effects/newtargetenemy.ogg` → `client/assets/audio/contact_enemy.ogg` and `newtargetneutral.ogg` → `contact_neutral.ogg`; import via `tools/godot-import.sh`.
- `client/scripts/SfxManager.cs`: add `SfxId.ContactEnemy` / `SfxId.ContactNeutral` to the enum (:28-48) and `Files` dict (:50-70). Not loops.
- `WorldRenderer.NetSetContacts:156` — before the `Clear()`:
  - Diff incoming radar ids against the previous `_radarVisible` (allocation-free loop, no LINQ). **Radar tier only** — ghost changes never chirp.
  - Tone per new id: look up the contact's team (streamed ship row, else the ghost record in the same frame); team != local team → `ContactEnemy`, else `ContactNeutral`. Unknown team defaults to enemy (the set is enemy ships by construction; neutral is future-proofing).
  - Batch debounce: at most ONE `SfxManager.Instance?.PlayUi(...)` per MsgContacts frame (enemy tone wins if the batch is mixed).
  - Time debounce: `_lastContactSfxSec` field, ≥ ~1.5 s between plays (vision runs at 2 Hz; contacts flickering across the detection edge must not machine-gun).
  - Join/reconnect suppression: `bool _contactsPrimed` cleared in `Reset()` — skip the sound on the first contacts frame after a world (re)build so reconnects don't chirp for the whole existing set.

No wire change. Fog off ⇒ no MsgContacts ⇒ no sound.

## 3. Shooting boosts radar signature (server-only)

Signature is captured per-target as `TargetSnap.Sig` at `server/Sim/Simulation.Vision.cs:411` and multiplies each viewer's sphere/eyeball/cone range (:536/:543/:547). `ShipSim.LastFireTick` (set in `TryFire`, `Simulation.cs:1312`) and `LastMissileTick` already exist and are readable at capture (sim thread). Scope: guns + missile launches boost; chaff/mine/probe deploys do not.

Knobs are **world-level, server-only, YAML-authored** — `FogEyeballMultiplier` (`shared/Defs.cs:254`, never streamed) is the exact precedent:

1. `factions/src/Allegiance.Factions/Model/RuntimeData.cs` WorldConfig runtime-extension block: add `double FireSignatureBoost`, `double FireSignatureWindow` (omit-when-default; kebab-case keys automatic).
2. `shared/Defs.cs` `WorldConfig:238`: add `float FireSignatureBoost; float FireSignatureWindow;` with a "server-side only — NOT streamed" comment.
3. `server/Content/FactionsContentProjection.cs` `ProjectWorld` (~:329): defaults when omitted/≤0 → boost 2.5, window 4.0 s.
4. `shared/ContentValidator.cs`: `FireSignatureBoost >= 1`, `FireSignatureWindow >= 0`.
5. `server/Content/factions/world.yaml`: `fire-signature-boost: 2.5`, `fire-signature-window: 4.0`; bump manifest `version:` in `core.manifest.yaml` (one bump shared with §5).
6. **Do NOT touch `BuildDefs`** — MsgDefs world block unchanged by this feature.

Sim change — `Simulation.Vision.cs`: pass `tick` into `CaptureVisionInput`; in the ship-target loop (:402-413):

```csharp
uint lastFire = Math.Max(s.LastFireTick, s.LastMissileTick); // 0 = never fired
float sig = def.RadarSignature > 0f ? def.RadarSignature : 1f;
if (lastFire != 0 && tick >= lastFire) {
    float age = (tick - lastFire) * FlightModel.Dt;
    if (age < world.FireSignatureWindow)
        sig *= 1f + (world.FireSignatureBoost - 1f) * (1f - age / world.FireSignatureWindow);
}
```

Linear decay from full boost back to 1× over the window. `IsPointVisibleToTeam` needs NO change (boost is target-side, not a vision source). PIGs fire through `TryFire` so they boost too. Comment the ≤500 ms visibility latency (2 Hz vision cadence) as intended.

## 4. Sector discovery

Sectors ride the exact pattern alephs already use: per-team discovered set + append-only reveal log + `MsgReveal` slice. World shape: `World.Sector(Id,Radius)` / `Gate(Id,SectorId,DestSectorId,…)` (`server/Sim/World.cs:53,71`), 2 sectors today.

**Server vision (`Simulation.Vision.cs`):**
- `TeamVision:80`: add `HashSet<uint> DiscoveredSectors` + `List<uint> RevealLogSectors` (under `DiscoverLock`).
- `ResetVision:236`: clear both; seed each team's home sector(s) from its bases **without logging** (same idiom as seeded `DiscoveredBases:277-286`). If any boot path reaches Welcome before `ResetVision` runs, seed in `InitVision` too (verify — `StartMatch`/`ReturnToLobby` both reset per `ClientHub:664-672`).
- Aleph discovery reveals both endpoints: in `ApplyVisionResult`'s aleph merge (:696-698), when a new aleph id is added, resolve the `Gate` and add **both** `SectorId` and `DestSectorId` to `DiscoveredSectors` (log only on fresh `Add`).
- Warp reveals the arrival sector (belt-and-braces): in `TryWarp` (`Simulation.cs:2209`) add the destination for `s.Team` under `DiscoverLock` — safe immediately, the vision worker never reads this set (comment this; contrast with `_warpRevealPending` for rocks).

**Server wire (`server/Net/Protocol.cs`):**
- `BuildWelcome:419` — move the sector block (:444-449) inside the fog branches:
  - fog OFF: full list, **byte-identical** encoding/order to today (u16 count, `u32 id + f32 radius`).
  - fog ON, no vision (NoTeam/spectator): `(ushort)0` — consistent with empty statics; client re-Welcomes on team pick.
  - fog ON with vision: under `DiscoverLock`, only sectors whose `Id ∈ DiscoveredSectors`, iterating `world.Sectors` in list order.
  - Rewrite the ":417-418 never fog-gated" comment.
- `BuildRevealSlice:540`: add `RevealMaxSectors = 16`, a `sectorCur`/`out nextSector` cursor, and a **trailing** block after alephs: `u8 count`, then `u32 id + f32 radius` per record — factor a `WriteSectorStatic` helper shared with Welcome so encodings can't drift. (MsgReveal is fog-on only, so fog-off is untouched.)
- `server/Net/ClientHub.cs`: `Client` gains `RevealSectorCur` (:115); `SendWelcome:313-340` seeds it from `RevealLogSectors.Count`; the reveal pump (:907-916) passes/advances it.

**Client (`client/scripts/GameNetClient.cs`):**
- `ApplyWelcome` sector loop (:926-935): unchanged — just reads fewer records.
- `ApplyReveal:1004`: after alephs, read the sector block → `_world.NetAddSector(...)` (`WorldRenderer.NetAddSector` is an idempotent upsert).

**UI falls out automatically:** `Minimap` draws only known sectors and only draws an aleph edge when both endpoint nodes exist; server gating reveals an aleph and its destination sector in the same vision apply, so links never dangle. `SectorOverview` retargets only via minimap clicks on drawn nodes. (Minimap ring re-layout when a node appears is acceptable at 2 sectors — note in PR.) `ui/SectorMapPreview.cs` (lobby browser) is fed by lobby data — out of scope.

## 5. Probes: bigger, enemy-visible, destructible

Prereq insight: **enemies can't shoot what they can't see** — `MsgProbes`(18) streams to the owning team only (`ClientHub.cs:1213`). Destructibility requires probes to become vision *targets* and stream to enemies who can see them.

### 5a. Content (one pass with §3's YAML/manifest bump)

- `factions/.../Model/Expendables/Probe.cs`: add runtime-extension `double HitRadius`, `double ModelSize` next to `SightRadius` (base `Expendable` already has `Signature`/`HitPoints`, so those keys already deserialize).
- `CoreValidator` probe-launcher block (~:162-169): `hit-radius > 0` when `hit-points > 0`; `model-size >= 0`; `signature >= 0`.
- `shared/Defs.cs` `WeaponDef` (after `BoltLength:195`): `float ProbeHitPoints; float ProbeHitRadius; float ProbeSignature; float ProbeModelSize;`.
- `FactionsContentProjection.cs` probe launcher branch (:259-274): project all four; `Signature ≤ 0 → 1f` (hull-sig rule).
- `shared/ContentValidator.cs`: probe weapons — `ProbeSignature > 0`; `ProbeHitRadius > 0` when `ProbeHitPoints > 0`.
- Wire (**MsgDefs weapon record +8 B at tail**): `BuildDefs` appends `ProbeHitRadius`, `ProbeModelSize` after `BoltLength` (:884); `ApplyDefs` mirrors in position (~:1169). `ProbeHitPoints`/`ProbeSignature` stay server-only (FogEyeballMultiplier precedent).
- `server/Content/factions/expendables.yaml` `recon-probe` (:136): add `hit-points: 40` (≈2 fighter twin-gun volleys or 2 bomber bolts), `signature: 1.0`, `hit-radius: 12`, `model-size: 4.0` (vs hardcoded 1.2); update the "Passive and invulnerable" description + the block comment at :131.

### 5b. Enemy visibility (server)

- `Simulation.Vision.cs`:
  - New `_inProbeTargets` list (id, team, sector, pos, sig from `ProbeSignature`) filled in `CaptureVisionInput` beside the probe-viewer loop (:434-451).
  - `TeamResult` + `TeamVision`: add `HashSet<ulong> VisibleEnemyProbes`. `ComputeVision` classifies enemy probes via `ClassifyTarget` at **radar tier only** — no eyeball, no ghosts (a fogged-out probe just disappears). Swap the set wholesale in `ApplyVisionResult`; drop ids for dead probes; on any change flag a resend (small `MarkProbesChanged()` internal on the Probes partial). Clear in `ResetVision`.
  - Probes are already vision *sources* in both `CaptureVisionInput` and `IsPointVisibleToTeam` — unchanged; the target role does not touch `IsPointVisibleToTeam` (sig is target-side).
- `ClientHub.BuildProbesFor:1217`: fog ON → `p.Team == team || vision.VisibleEnemyProbes.Contains(p.ProbeId)`; fog OFF → **all probes to all clients** (symmetric counterplay; the fog-off byte-identical constraint covers the Welcome world block, and this recipient-set change is deliberate under the bump). Update the owner-only comments here, at `Protocol.cs:90-92,:353-356`, and the `Simulation.Probes.cs` header.
- `MsgProbeGone` routing (:966-969): broadcast to all clients (client `NetProbeGone` is a no-op for unknown ids).
- Client reconcile: enemy probes can fog out with no gone-message, so `ApplyProbes` (`GameNetClient.cs:715`) becomes a **wholesale reconcile** — the frame is the complete visible set; remove any `_probeRows` id not in the frame via a silent local reason (255). Update the ":712-714 no-reconcile" comment.

### 5c. Damage path (server) — HP pool per user decision

- `Simulation.Probes.cs`: `ProbeSim` gains `float Health` (seeded from `w.ProbeHitPoints` in `TryDeployProbe`; 0 = authored-invulnerable). New `DamageProbe(p, dmg, tick)`: on ≤0 remove, push `ProbeGoneThisStep` **reason 2 = destroyed** (0 expired, 1 reserved cleanup — document at `:38` and in `BuildProbeGone`), flag probe resend.
- `Simulation.cs`:
  - `PendingShot:287`: add `ulong TargetProbeId`.
  - `FireBolt:1337`: after the base scan, linear-scan `_probes` (same sector, `p.Team != ship.Team`, `Health > 0`) with `FirstEntryTime(..., ProbeHitRadius + World.ProjectileRadius, ...)`; a closer probe hit clears ship/base targets. Probes are few — no grid; list order keeps determinism.
  - `ResolveDueShots:1870`: `TargetProbeId != 0` → find by id (skip if already gone) → `DamageProbe` with the weapon's damage.
  - `StepMissiles` blast loop (~:1808): splash probes within `BlastRadius` with the same falloff (missiles stay a valid counter).

### 5d. Client rendering

- `ProbeView.cs:17`: normalize to `def.ProbeModelSize > 0 ? def.ProbeModelSize : 1.2f` (guard, not fallback — def-gated per client-no-baked-tuning rule).
- `WorldRenderer.NetProbeGone:677`: reason 2 → explosion effect + `PlayAt(SfxId.Explosion, pos)`; reasons 0/1/255 silent.
- `WorldRenderer.CheckBoltImpacts:1470`: sweep visible probe nodes too (radius from `ProbeHitRadius`) → hit flash + impact sound + consume bolt (client-side visual interception, same as ships).

---

## Ordering

1. §1 shared constant (sets 24).
2. Content pass: §3 knobs + §5a defs/YAML together (one manifest bump; fix ContentTest/FactionsTest expectations for the +8 B weapon tail).
3. §3 sim change.
4. §4 sector discovery.
5. §5b → §5c → §5d.
6. §2 sound (any time, independent).

## Wire changes (complete, all under 23→24)

| Message | Change |
|---|---|
| MsgDefs (7) | weapon record +`f32 ProbeHitRadius` +`f32 ProbeModelSize` at tail |
| MsgWelcome (1) | fog-ON only: sector list filtered to DiscoveredSectors (0 for NoTeam). Fog-OFF world block byte-identical |
| MsgReveal (16) | trailing `u8 nSectors × (u32 id, f32 radius)` after alephs |
| MsgProbes (18) | layout unchanged; recipients widen (enemy-visible fog-on, everyone fog-off); client = wholesale reconcile |
| MsgProbeGone (19) | layout unchanged; broadcast; reason 2 = destroyed |

## Verification

- `dotnet test factions/tests/Allegiance.Factions.Tests` (new validator rules).
- `dotnet run -c Release --project tests/FactionsTest` and `tests/ContentTest` (MsgDefs determinism — expectations change).
- `tests/FogTest` (boots real content, `VisionSynchronous = true`) — extend:
  - fire-boost: target parked just outside `sphere × sig` becomes a radar contact after `LastFireTick = tick`, fades after the window (read boost/window from loaded ContentSet per FogTest's retuning-proof idiom);
  - sectors: fresh fog-on Welcome carries only home sector (update its `WelcomeCounts` parser); scouting the gate reveals both sectors + a well-formed reveal slice;
  - probes: enemy probe undetected beyond range / detected within `range × ProbeSignature`; bolt through a probe → gone reason 2.
- Regression: `tests/MineTest`, `tests/MissileTest`, `tests/ShieldTest`, `tests/FlightModelTest`, `tests/StrategyTest` (PendingShot/StepMissiles touched). All suites pass as of 2026-06-12 — any failure is real.
- Builds: `dotnet build server/SimServer.csproj -c Release && dotnet build client/stellarallegiance.csproj`.
- Headless/manual (sim ticks only with a held connection — `--server` + `--autofly --anonymous` client; protocol bumps get a client `--autofly` smoke):
  - fire near an idle enemy scout → it pops onto radar, fades ~4 s after ceasefire;
  - several contacts appearing together → one blip (enemy tone); reconnect → no chirp storm;
  - fresh fog match → one minimap node; scout the gate → second node + link appear together;
  - probe visibly larger; enemy finds it on radar, kills it in ~2 volleys, sees explosion; owner loses the vision sphere;
  - fog-off sanity: Welcome world block byte-identical (probe streaming now reaches everyone by design).
