# Add "Miner Drone" to the Build pane (purchase up to N per team)

## Context

Today the docked **Build** pane (`client/scripts/ui/BuildTab.cs`) only surfaces
constructor-built **stations** (from streamed `StationCatalogDef` entries). **Miners** are a
different thing entirely — AI drone *ships* (any hull with `OreCapacity > 0`) that a team buys via
the commander-only **`/buyminer` chat command**. The server already enforces a per-team cap
(`WorldMiningTuning.MaxMinersPerTeam`, default **4**, authored `max-miners-per-team` in
`world.yaml`) and all buy validation (cap / cost / match-phase / kill-switch) in
`Simulation.Mining.TryBuyMiner`.

The gap: miners aren't purchasable from the Build UI. This change surfaces a **"MINER DRONE" card**
in the Build grid with a **live `X / N` owned-vs-cap readout**, a **commander-gated BUY button**,
and **removes the now-redundant `/buyminer` chat command**. All server-side purchase logic already
exists and is reused unchanged — the work is a new typed buy message, streaming the live count to
the client, and the UI card.

### Decisions (confirmed with user)
- **Card with live `X / N`** owned/cap readout (per the mock).
- **Commander only** — matches `/buyminer` and the existing constructor BUILD button.
- **Remove the `/buyminer` chat command** — the Build-pane button replaces it via a dedicated typed
  message (chat reuse is off the table since the command is being deleted).

### Protocol convention (no version bump)
Follow the established practice in this codebase: `MsgBuildConstructor=14` / `MsgConstructorCancel=15`
and the `MsgTeamState` `discoveredRockClasses` tail were all added **without** bumping
`Wire.ProtocolVersion` (still **34**) — new message ids and appended tail fields are tagged with a
`// vNN`-style comment instead. We do the same: new inbound `MsgBuyMiner=16` + two appended
`MsgTeamState` tail bytes, **no `Wire.ProtocolVersion` change**. (Compat implication is identical to
those prior additions: old clients simply ignore the extra tail bytes; the deleted `/buyminer`
command is the only behavior removed.)

---

## Changes

### 1. Wire message — `server/Net/Protocol.cs`
- Add inbound const after line 77: `public const byte MsgBuyMiner = 16;` (next free client→server id;
  outbound ids are a separate space) with a comment noting it's commander-gated, no body, team
  inferred server-side.
- `BuildTeamState` (line 1121): change signature to take the `Simulation` (Protocol methods like
  `BuildMinerTargets(_sim)` already do this) so it can read the live count. In the per-team loop,
  **after** `DiscoveredRockClasses` (line 1155) append two bytes:
  `w.Write((byte)sim.MinerCount(team));` and `w.Write((byte)sim.World.Mining.MaxMinersPerTeam);`.
- Update the `MsgTeamState = 10` header comment (line 89) to document the new `u8 minerCount, u8 minerCap` tail.

### 2. Server ingest — `server/Net/ClientHub.cs`
- In `HandleMessage`, add `case Protocol.MsgBuyMiner:` mirroring `MsgBuildConstructor` (~line 816):
  `if (CommanderOrWarn(client) is byte team) _sim.EnqueueMinerBuy(team);`. No payload to read.
- **Remove** the `case "buyminer":` block in `HandleCommand` (lines 877–882) and update the method's
  header comment (line 854) that references `/buyminer`.
- Update the `BuildTeamState` call site (line 1391) to pass `_sim` instead of `_sim.World, _sim.Content`.

### 3. Client send + team-state read — `client/scripts/GameNetClient.cs`
- Add `SendBuyMiner()` mirroring `SendBuildConstructor` (line 379): write a 1-byte frame `[16]`.
- In the `MsgTeamState` parser (per-team loop that currently ends by reading `discoveredRockClasses`),
  read the two new trailing bytes `minerCount`, `minerCap` (guarded for older/short frames the same
  way `discoveredRockClasses` is), and pass them into `_world.NetUpdateTeamState(...)`.

### 4. Team-state store — `client/scripts/WorldRenderer.cs`
- Extend `NetUpdateTeamState` (line 810) with two new optional params `int minerCount = 0, int minerCap = 0`
  (optional, mirroring the `discoveredRockClasses = 0xFF` precedent). Store per-team.
- Add accessors `TeamMinerCount(byte team)` and `TeamMinerCap(byte team)` next to `TeamCredits`
  (line 903). Reuse the existing `TeamCredits(team)` / spawn-affordability pattern (line 930) for the
  card's credit check.

### 5. Miner hull lookup — `client/scripts/DefRegistry.cs`
- Add `public ShipClassDef? MinerShipDef()` returning the lowest-`ClassId` ship def with
  `OreCapacity > 0` (mirrors the server's `MinerClassId` selection in `Simulation.Mining.cs:127`).
  Gives the card its **cost** (`ShipClassDef.Cost`) and existence check.

### 6. Build pane card — `client/scripts/ui/BuildTab.cs`
Add a synthetic miner card to the station grid (it is a *drone*, not a `StationCatalogDef`, so it's
special-cased throughout):
- Sentinel id constant, e.g. `private const string MinerCardId = "__miner__";`.
- **Grid** (`RebuildGrid`, line 331): when `_defs.MinerShipDef()` exists (and cap > 0), prepend one
  `StationCard` for the miner: glyph `◈`, name `"MINER DRONE"`, kind word `"DRONE"`, class `"MINING"`,
  price from the miner def cost, and status text = `$"{count} / {cap}"` from `TeamMinerCount/TeamMinerCap`.
  Wire `Pressed` to `SelectStation(MinerCardId)`.
- **StationCard.Configure** (line 616): add two optional params — `string kindWord = "STRUCTURE"`
  (replaces the hardcoded `"STRUCTURE · "` prefix at line 621) and `string? statusText = null`
  (when set, shown verbatim in `_status` instead of the derived AVAILABLE/LOCKED). Miner card passes
  `kindWord: "DRONE"`, `statusText: "{X} / {N}"`.
- **Detail panel** (`RefreshDetail`, line 361): when `_selectedId == MinerCardId`, populate from miner
  data instead of `Catalog()` — title "MINER DRONE", cost, no build-time (show `X / N` in meta), a
  short description ("Auto-harvests helium-3 into team credits.").
- **Footer** (new `UpdateMinerFooter`, modeled on `UpdateFooter` line 402), in priority order:
  not commander → disabled "⊘ COMMANDER AUTHORIZATION REQUIRED"; `count >= cap` → disabled
  "⊘ MINER CAP REACHED ({N})"; insufficient credits (via `TeamCredits` vs miner cost) → disabled
  "⊘ INSUFFICIENT CREDITS"; else enabled Primary **"◈ BUY MINER"**.
- **Buy action** (new `OnMinerBuyPressed`): re-check commander + cap client-side, then
  `_net.SendBuyMiner(); SfxManager.Instance?.PlayUi(UiClick);`. Route `_detail.PrimaryPressed` to the
  miner handler when the miner card is selected (branch inside the existing `OnBuildPressed`, or a
  small dispatch on `_selectedId`).
- **Live refresh**: fold miner count + cap into `ComputeStatusSig` (line 219) so a purchase/loss
  re-triggers `RebuildGrid` + `RefreshDetail` (cheap at the 0.25s poll) and the `X / N` stays current.

### 7. Remove the chat command — `client/scripts/Chat.cs`
- Delete the `/buyminer` help line (line 227) and remove `/buyminer` from the relay `case` list
  (line 232–238) that forwards it via `SendChat`. Leave `/pigs` and `/commander` intact. (`/mine`
  targeting, if present, is unrelated to purchasing — do **not** remove it.)

---

## Verification

1. **Build**: `dotnet build` the server; import the Godot client
   (`godot --headless --import` per the GLB-import gotcha) and build the client.
2. **Tests** (regression from command removal + team-state layout):
   `dotnet test` the `MiningTest` and `CommanderTest` suites — they drive `EnqueueMinerBuy` directly,
   so they should stay green. Grep tests for the literal `"/buyminer"` first to confirm nothing sends
   the removed command (`grep -rn "buyminer" tests/`).
3. **End-to-end** (use the `/verify` skill or manual): launch a headless server + a client, take
   commander, open the docked screen → **Build** tab. Confirm:
   - The **MINER DRONE** card appears with `1 / 4` (the free match-start miner) and the correct cost.
   - **BUY MINER** enqueues a buy; the readout ticks to `2 / 4`; a miner drone launches from the home
     garrison.
   - At `4 / 4` the button shows **MINER CAP REACHED (4)** and is disabled.
   - As a **non-commander**, the button shows **COMMANDER AUTHORIZATION REQUIRED**.
   - `/buyminer` is gone from `/help` and typing it does nothing special (relayed as normal chat, or
     unknown — confirm it no longer buys).
4. Confirm the client `ProtocolVersion` is unchanged (still 34) and a stock client still connects.
