# Prune miner text commands to `/buyminer` only (commander-gated)

## Context

The mining feature grew three text/chat commands: `/buyminer`, `/mine <sector>`, and
`/miners`. Now that **commanders can direct AI vessels with the mouse** (select + right-click
issues a `MsgOrder`, whose miner path `ApplyMinerCommandOrder` already authorizes the target
sector into `AuthorizedMiningSectors` — `server/Sim/Simulation.Orders.cs:441`), the text-based
`/mine` authorization and the `/miners` status readout are redundant chat surface.

**Goal:** keep `/buyminer` as the single miner text command, remove `/mine` and `/miners`, and
do a **deep cleanup** of the now-dead sim methods behind them. Prune/re-point the affected
tests.

Two clarifications from exploration:
- **`/buyminer` is already commander-only.** It is gated through `CommanderOrWarn`
  (`server/Net/ClientHub.cs:791`). The request's second bullet ("remove /buyminer access for
  non-commanders") is **already satisfied** — no gating change needed, just verify it holds.
- Removing `/mine`'s `ApplyMineOrder` does **not** strand miners: mining authorization still
  comes from match-start base-sector seeding (`server/Sim/World.cs:704`) and the mouse-order
  path (`Simulation.Orders.cs:441`).

## Changes

### 1. Server command router — `server/Net/ClientHub.cs`
- Delete the `case "mine":` block (~795–811) and the `case "miners":` block (~841–846) from
  `HandleCommand`. Keep `case "buyminer":` and `case "pigs":`/`case "commander":` unchanged.
- Delete the now-orphaned private helpers `ResolveSector` (~989–1035) and `SectorNames`
  (~1037…), used only by `/mine`.
- Update the `HandleCommand` header comment (~765–767) so it no longer describes `/mine`/`/miners`.
- Update the `CommanderOrWarn` comment (~862–863) — drop the `/mine` reference (keep `/buyminer`
  and the `MsgOrder` AI-subject mention).

### 2. Client chat — `client/scripts/Chat.cs`
- Remove `case "/mine":` and `case "/miners":` from the relay `switch` (~233–234). Keep
  `case "/buyminer":`, `case "/pigs":`, `case "/commander":`.
- Remove the `/mine` and `/miners` lines from the `/help` text (~226–227). Keep the
  `/buyminer … (commander only)` line (225).

### 3. Sim — `server/Sim/Simulation.Mining.cs`
- Delete `EnqueueMineOrder` (~165–169), `EnqueueMinerStatus` (~171–175), and the
  `_minerStatusQueue` field (~177). Keep `EnqueueMinerBuy` (drives `/buyminer`).
- Delete the `_mineOrderQueue` field (~113).
- In `DrainMinerQueues` (~180–191): keep the `_minerBuyQueue` drain; remove the `_mineOrderQueue`
  and `_minerStatusQueue` drain loops.
- Delete `ReportMinerStatus` (~195–229) and `ApplyMineOrder` (~270–297).
- Sanity-scan the file afterward for stray `/mine`/`/miners` prose in comments (e.g. the
  "authorized: none — /mine <sector>" string lived in `ReportMinerStatus`, removed with it).

### 4. Tests — `tests/MiningTest/Program.cs`
- **Test 25** (`/mine` unknown-sector, ~1108–1120): delete the whole block — it only validated
  the removed command's arg handling.
- **Test 28** (`/miners` status, ~1199–1214): delete the whole block — it only exercised
  `EnqueueMinerStatus`.
- **Harvest/transit test** (~933–959, "the /mine order sends the miner TWO hops out"): this is a
  genuine harvest-loop test that merely *used* `/mine` to authorize sector 2. Re-point it:
  replace `sim.EnqueueMineOrder(0, 2);` with `w.TeamStates[0].AuthorizedMiningSectors.Add(2);`
  (public set on `TeamState`, `server/Sim/World.cs:129`) and reword the two `Check` message
  strings that say "the /mine order" (keep the assertions).
- Grep the test file once more for `EnqueueMineOrder`/`EnqueueMinerStatus` to confirm no other
  callers remain.

## Out of scope (leave intact)
- The commander system itself (`Lobby._commander`, `CommanderOf`, `/commander` handoff, CMDR
  badge) — independent of mining, still gates `/buyminer` and the mouse `MsgOrder` path.
- `EnqueueMinerBuy` / `TryBuyMiner` / `NewMinerSlot` / mouse-order mining
  (`ApplyMinerCommandOrder`) — all still live.

## Verification
1. **Build:** `dotnet build` the server + client (no unresolved refs from removed helpers/fields).
2. **Tests:** run the mining suite — `dotnet run --project tests/MiningTest` — expect green after
   the test edits. Also run `tests/MineTest` and `tests/CommanderTest` (CommanderTest:479 touches
   this area) to confirm no fallout.
3. **Command surface:** in-game (or via `--autofly` smoke), `/help` no longer lists `/mine` or
   `/miners`; typing `/mine 2` or `/miners` produces the generic unknown-command response, not a
   miner action; `/buyminer` still works for a commander and still rejects a non-commander with
   "Only the commander can direct AI vessels."
4. **Mining still works:** confirm miners still harvest — match-start seeding + a mouse
   select-and-right-click order authorizes a sector and the miner transits/harvests (covered by
   the re-pointed harvest test; optionally verify live with the `verify` skill).
