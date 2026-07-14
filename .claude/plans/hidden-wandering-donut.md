# Hard sector cuts + cross-sector minefield leak fix

## Context

Two related bugs make sectors feel like one shared game board instead of separate places:

1. **The warp transition shows its own seams.** Sector switching is client-intensive (a large single-thread hitch), and the WarpFlash exists to mask it — but the warp branch (`WorldRenderer.UpdateShip` ~1959) runs the entire swap synchronously in the frame the warp snapshot arrives: `ApplySectorEnv` (starscape/sun/dust repaint) + `RefreshSectorVisibility`, and only *then* fires `Warped`. `WarpFlash.Play()` merely starts a 0.12s alpha ramp from zero, so the hitch stalls on the last un-covered frame and the asteroid teardown/appearance renders before the flash has any cover on screen — the flash arrives *after* the jank it was meant to hide. Additionally, three non-warp transition paths still run the 0.55s crossfade (F3 `SetViewSector`, death-cam→home reset, spawn/respawn), so old-sector asteroids visibly dissolve into the new view there too. Old-sector content must never be visible in the new sector, and the heavy swap must happen fully under the flash: **cover → swap → reveal**.
2. **Mines persist across sectors.** `MinefieldViews` is the only streamed entity type with zero sector awareness: `FieldView` has no sector, no visibility gating, and no removal API — fields die only by TTL or full reconnect `Clear()`. The client cache (`GameNetClient.ApplyMinefields`) only prunes rows matching the incoming frame's sector (inferred from the first record; an empty frame can't prune at all), and the server only sends minefield frames on global change or the coarse keepalive — never when a client's own anchor sector changes.

Every other entity (ships, bolts, missiles, probes, chaff, effects, rocks, bases) is already sector-gated; mines are the outlier. Fix both, making all sector transitions hard cuts.

**Execution note (per user): delegate the implementation steps to an Opus subagent(s); keep review/verification in the main loop.**

## Step 1 — Sector-gate minefield rendering (client, no wire change)

`client/scripts/MinefieldViews.cs`:
- Add `public int Sector;` to `FieldView`; set from `row.SectorId` in `Upsert` (insert + refresh paths).
- Copy the ChaffFx gating pattern (`client/scripts/ChaffFx.cs:155-159`): `SectorOf()` via `GetParentOrNull<WorldRenderer>()?.ViewSector`, `SectorVisible(int)`. MinefieldViews is a direct child of WorldRenderer, so it works verbatim.
- `_Process`: resolve view sector once per frame, then `fv.Node.Visible = SectorVisible(fv.Sector)` per field. Also set initial `node.Visible` on fresh insert in `Upsert` so a field never renders for one frame.
- Add `public void Remove(ulong fieldId)` wrapping the private `FreeField` (used by Step 2 reconcile).
- `VisibleMinefields()`: skip fields failing `SectorVisible` so HUD glyphs (`TargetMarkers`) don't leak.
- `MineGone(...)`: accept the sector and skip blast/SFX when not in view (kills phantom cross-sector explosions).

`client/scripts/WorldRenderer.cs` (~1245-1249):
- `NetMineGone` passes sector through to `MinefieldViews.MineGone`.
- Add `public void NetMinefieldGone(ulong fieldId) => _minefieldViews.Remove(fieldId);`

This alone makes mines vanish instantly on warp and handles F3 overview in both directions.

## Step 2 — Wire change: MsgMinefields header sector + reconcile-all (proto 34 → 35)

- `shared/Net/Wire.cs:29`: `ProtocolVersion = 34` → `35` (server, client, and lobby filter all alias it).
- `server/Net/ClientHub.cs` `BuildMinefieldsFor` (~1687-1709): frame becomes `[13][u16 anchorSector][u8 count] + count × 41B records`. Records and `MinefieldRecordSize = 41` unchanged (`WriteMinefield` untouched — keep the per-record sector; the client still feeds it into `FieldView.Sector`). Update layout comments (ClientHub ~1681, Protocol.cs:42-44, 88).
- `client/scripts/GameNetClient.cs` `ApplyMinefields` (964-1019): read `ushort frameSector` before count. Since minefields only ever stream for the client's anchor sector, treat each frame as the authoritative full set: prune **every** cached `_minefieldRows` entry not in `seen` (drop the sector-inference/`haveSector` logic) and forward each removal to `_world.NetMinefieldGone(id)`. Fix the stale comment claiming MinefieldViews self-reconciles.

Empty frames now identify their sector and purge stale rows.

## Step 3 — Server: send minefield frame on anchor-sector change

`server/Net/ClientHub.cs`:
- `Client` class (~115): add `public uint LastMinefieldAnchor = uint.MaxValue;` (sentinel ⇒ first AfterStep after Hello always sends — also fixes fresh joins waiting up to 10 coarse ticks for their first field set).
- Per-client send (~1456-1462): `bool wantMinefields = sendMinefields || client.AnchorSector != client.LastMinefieldAnchor;` — send, then advance `LastMinefieldAnchor` only on successful `TryWrite` (reveal-cursor convention). `AnchorSector` is refreshed earlier in the same sequential pre-pass (lines ~1317/1349), so ordering is safe.
- **Fog hazard**: `mineVisByTeam` (~1234-1253) is precomputed only when `sendMinefields` is true; an anchor-change-triggered frame would otherwise pass `null` and drop revealed enemy fields. Make it lazily computed per team on demand (cache dict, same style as `baseFramesByTeam`'s lazy build). Runs post-Step on the sim thread — vision reads stay safe.

## Step 4 — Warp sequencing: cover → swap → reveal

`client/scripts/WorldRenderer.cs`, warp branch of `UpdateShip` (~1953-1966), split into two phases:

**Phase A — frame the warp snapshot arrives (all cheap):**
- Snap prediction (`pc.OnAuthoritative(newRow, warped: true)`) and set `_localSector` as today.
- **Hide pass only**: set `Visible = false` on every sector-tagged node NOT in the destination sector (statics and transients). Pure visibility toggles — no env repaint, no shows. Old rocks/mines/ships are gone this same frame; positions are sector-local, so nothing from the old sector may render at new-sector coordinates.
- Fire `Warped` (flash starts rising) and arm a deferred swap: `_pendingWarpSector = newSector; _warpCoverAtSec = now + CoverDelay` where `CoverDelay = WarpFlash.RiseDur` (make the const public/shared) plus one frame of margin.
- A second warp while one is pending just re-targets `_pendingWarpSector` (hide pass runs again for the newer sector).

**Phase B — in `_Process`, once the flash is at peak (`now >= _warpCoverAtSec`):**
- Run the heavy work fully covered: `ApplySectorEnv(_pendingWarpSector)` + the show pass of `RefreshSectorVisibility` + `BeginWarpSettle()`. The hitch now stalls on a fully-opaque flash frame — invisible. `TickWarpSettle`/`WarpSettled` then release the flash as today once rock streaming quiesces.

`client/scripts/ui/WarpFlash.cs`: expose `RiseDur` (public const) so WorldRenderer's cover delay can't drift from the actual ramp; no behavior change.

**Non-warp transitions hard-cut too:**
- `RefreshSectorVisibility` (~1400): remove the `bool instant` param and the `FadeNode` branch — statics always go through `ShowNodeInstant`. Callers: 421 (F3 `SetViewSector`), 674 (`RehomePreLaunch`), 1900 (`InsertShip`), 2405 (death-cam home reset), plus the new Phase B show pass. Update comments (1038-1041, 1396-1399). F3/death/respawn don't get a flash — they keep their existing (pre-existing) hitch, just without the crossfade leak.
- Keep `FadeNode`/`AdvanceFades`/`_fades` — still used for same-sector fog-reveal fade-ins (`SetNodeSectorFading`) and stale-base ghost dim. `ShowNodeInstant` already cancels in-flight fades.
- `SetNodeSectorFading` (~1087): when `_warpSettling && sector == ViewSector` (or a warp swap is pending), use `ShowNodeInstant(n, true)` instead of fading — entities streaming in under the held WarpFlash appear instantly (extends the existing rock special-case at 1845-1850 to bases; keep the rock path's `_warpLastRockSec` settle-window push).

MinefieldViews' per-frame `ViewSector` gate (Step 1) composes with this: mines vanish in Phase A's frame automatically since `ViewSector` already points at the destination.

## Step 5 — Tests + verification

Server-side (dotnet): extend `tests/MineTest/Program.cs` with a hub section modeled on FogTest #18/#19 (copy the file-local `FakeHubTransport` from `tests/FogTest/Program.cs:1823`):
1. Drop field in sector A → assert `MsgMinefields` frame with header sector A, count 1, record offsets correct (guards the +2 header shift).
2. Warp ship to sector B on a non-coarse, no-change tick → assert an immediate frame with header sector B, count 0 (the anchor-change trigger).
3. First frame after Hello carries the garrison anchor (covers the `uint.MaxValue` sentinel).
4. Fog-on: LOS-revealed enemy field still appears in an anchor-change frame (guards the lazy `mineVisByTeam` refactor).

Run the full dotnet suite; known pre-existing failures: ShieldTest/ContentTest/FactionsTest content-drift (6), FogTest sector-leak.

Godot client isn't covered by dotnet suites — smoke via the `verify` skill / `--autofly`: drop mines in sector A, warp A→B through a gate; capture a movie/frame sequence across the warp confirming the ordering (flash fully up **before** any world change, no old rocks or mines visible in any frame after the hide pass, reveal only after the swap); flip F3 overview both ways.

## Risks / notes

- **Cover gap (~0.12s)**: between Phase A and Phase B the ship rides new-sector coordinates while the backdrop is still the old sector's and no statics are shown — this happens under the rapidly rising flash (sine ease-out, 0.9 peak) and nothing sector-identifiable (rocks/mines/bases) is visible during it. If it reads poorly in the smoke test, shorten `RiseDur` rather than reordering.
- **The hitch itself is not fixed, only hidden** — the flash now guarantees it lands on a covered frame. Optional follow-up (out of scope): profile `ApplySectorEnv` (Starscape repaint is the likely single-thread cost) and amortize.
- **Version bump lockstep**: old client vs new server refuses at Welcome; lobby browser filters by protocol — deploy both together (Railway).
- **In-flight frames during warp**: either ordering is safe — the per-frame `ViewSector` gate keeps visuals correct regardless of cache contents; the next frame reconverges the cache.
- **Prune-all semantics**: a pruned field's later `MsgMineGone` no-ops on unknown id (existing behavior).
- **Reconnect**: `_world.Reset()` already clears MinefieldViews and `_minefieldRows`; the fresh `LastMinefieldAnchor` sentinel guarantees an immediate repopulating frame.
- **F3 while viewing a remote sector shows no fields even if some exist there** — pre-existing (minefields stream anchor-sector-only), unchanged.
- Repo auto-commits/pushes mid-session — get changes final before ending a turn.

## Critical files

- `client/scripts/MinefieldViews.cs`, `client/scripts/WorldRenderer.cs`, `client/scripts/GameNetClient.cs`
- `server/Net/ClientHub.cs`, `shared/Net/Wire.cs`
- `tests/MineTest/Program.cs` (+ harness pattern from `tests/FogTest/Program.cs`)
