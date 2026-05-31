# 07 — Net-Sync Protocol

This document fixes the timing and synchronization rules so client prediction and server
authority agree. These numbers are starting points; tune during the multi-client acceptance
test, but change them in **one** place and keep client and server consistent.

## Clocks and rates

| Quantity | Value | Where |
|----------|-------|-------|
| Sim tick rate | **20 Hz** (`dt = 0.05s`) | server `SimTick`, client prediction |
| Input send rate | **20 Hz** | client `ApplyInput` calls |
| Render rate | display refresh (e.g. 60 Hz) | Godot `_Process` |
| Interpolation delay (remote ships) | **100 ms** (≈2 ticks) | `RemoteShipInterpolator` |

Render is decoupled from sim. The client renders every frame by interpolating/predicting
between 20 Hz authoritative states, so motion is smooth even though state updates at 20 Hz.

## The tick counter

- The server's `Match.Tick` is the canonical tick.
- The client maintains its own `clientTick`, advanced at 20 Hz. It does **not** need to be
  perfectly equal to the server tick; it is a monotonically increasing label the client
  attaches to its inputs so the server can echo it back via `Ship.LastInputTick`. That echo
  is what lets the client know "the server has now accounted for my input up through tick N,"
  which is the anchor point for reconciliation.

## Subscriptions (what each client reads)

Subscribe once on connect to a query covering the whole small world (the prototype is tiny,
so subscribe broadly):

- All `Asteroid` rows (static; arrive once).
- All `Base` rows.
- All `Ship` rows.
- All `Projectile` rows.
- The `Match` singleton.
- All `Player` rows (for names/teams/HUD).

`ShipInput` does **not** need to be subscribed by clients in the prototype — clients send it,
they don't read others' inputs.

## Client → server (reducer calls)

- `ApplyInput` at 20 Hz with the current `clientTick`. If input hasn't changed, you may still
  send to keep the server's `ShipInput` fresh; at this scale that's fine.
- `SpawnShip` / `Respawn` / `SetName` as user actions.

## Server → client (automatic via subscription)

Every `SimTick`, the server writes updated `Ship`, `Projectile`, and possibly `Match` rows.
Subscribed clients receive the deltas automatically. There is no manual snapshot message —
the table sync *is* the protocol.

## Prediction & reconciliation (local ship)

1. Each input tick, the client integrates its local ship with `FlightModel` and stores
   `(clientTick, input, predictedState)` in a ring buffer (keep ~1 second = 20 entries).
2. When an authoritative `Ship` update for the local ship arrives, read its `LastInputTick`
   (call it `N`).
3. Find the client's predicted state for tick `N`.
4. Compute position error = distance(authoritativePos, predictedPos[N]).
   - If error ≤ **0.25 units** and rotation error small → accept prediction, discard buffer
     entries ≤ N.
   - If error > tolerance → **reconcile**: set the ship's state to the authoritative state at
     tick `N`, then re-integrate every buffered input from `N+1` to the latest client tick to
     re-derive the present predicted state (rollback). Discard entries ≤ N.
5. Smooth any visible correction over ~3 render frames so the ship doesn't visibly pop.

> Tolerance and interpolation delay are the two knobs that most affect feel under latency.
> Loosen tolerance if you see constant micro-corrections; tighten interpolation delay if
> remote ships feel laggy (at the cost of more visible interpolation hitches).

## Remote ship interpolation

- Buffer the last two authoritative `(tick, transform)` samples per remote ship.
- Render at `now - 100ms`: lerp position, slerp rotation between the bracketing samples.
- If only one sample exists (just spawned), render at that sample.
- No forward extrapolation in the prototype — accept slight latency for stability.

## Latency assumptions

The prototype targets LAN / Maincloud round-trips in the tens of milliseconds. The model
above tolerates that comfortably. Do not build lag compensation for hit detection yet
(server resolves hits on authoritative positions; that's acceptable for a prototype). Record
"add lag-compensated hit detection" as a future item in `99`.

## Failure modes to watch (and their usual cause)

| Symptom | Likely cause |
|---------|--------------|
| Local ship jitters constantly | client/server `dt` or integration math differ (see `06`) |
| Local ship rubber-bands on turns | reconciliation re-sim not replaying buffered inputs |
| Remote ships teleport/stutter | interpolation delay too low, or only snapping to latest |
| Everything lags behind input | predicting against server tick instead of applying input locally first |
