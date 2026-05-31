# 04 — Reducers (Server Logic API)

Reducers are the **only** way to write to the database. They are defined in `module/Lib.cs`
with `[SpacetimeDB.Reducer]` and receive a `ReducerContext ctx` giving access to `ctx.Db`
(table handles), `ctx.Sender` (the calling Identity), and timing utilities. Confirm exact
signatures against the functions reference linked in `02`.

There are three categories: **lifecycle** (connect/disconnect), **player actions** (called
by clients), and the **scheduled simulation tick** (called by the database on an interval).

---

## Lifecycle reducers

### `Init` — `[SpacetimeDB.Reducer(ReducerKind.Init)]`
Runs once when the module is first published. Responsibilities:
- Insert the singleton `Match` row (Tick=0, Phase=Lobby).
- Insert the two `Base` rows at fixed opposite positions in the sector.
- Insert the static `Asteroid` field (e.g. 20–40 asteroids at scattered positions; a fixed
  seed is fine and preferable for reproducibility).
- Schedule the recurring `SimTick` (see scheduling note below).

### `ClientConnected` — `[SpacetimeDB.Reducer(ReducerKind.ClientConnected)]`
- Create or reactivate a `Player` row for `ctx.Sender`.
- Assign `Team` to balance counts (whichever team has fewer online players; ties → team 0).
- Do **not** spawn a ship here; spawning is an explicit action (`SpawnShip`).
- If `Match.Phase == Lobby` and both teams now have ≥1 player, set `Phase = Active`.

### `ClientDisconnected` — `[SpacetimeDB.Reducer(ReducerKind.ClientDisconnected)]`
- Set the player's `Online = false`.
- Destroy their active `Ship` row (if any) and clear `Player.ShipId`.
- Do not delete the Player row (keeps team balance stable for the match).

---

## Player action reducers (called by clients)

### `SetName(ReducerContext ctx, string name)`
Cosmetic. Sets `Player.Name` for the sender.

### `SpawnShip(ReducerContext ctx, ShipClass shipClass)`
- Reject if `Match.Phase != Active`, or the player already controls a ship, or the player
  is offline. (Log and return; do not throw for expected rejections.)
- Create a `Ship` row at the player's team `Base` position, with class stats from the shared
  constants, full health, zero velocity, identity orientation.
- Create the matching `ShipInput` row (all zeros).
- Set `Player.ShipId`.

### `ApplyInput(ReducerContext ctx, float thrust, float strafeX, float strafeY, float yaw, float pitch, float roll, bool firing, uint clientTick)`
- Look up the sender's `Ship`. Reject if none.
- Overwrite that ship's `ShipInput` row with the new values and `ClientTick = clientTick`.
- **Do not integrate motion here.** Integration happens only in `SimTick`, so all ships
  advance on the same authoritative clock. This reducer just records intent.
- This is the highest-frequency client→server call. Clients send it at the input tick rate
  (see `07`), not every render frame.

### `Respawn(ReducerContext ctx, ShipClass shipClass)`
- Allowed only when the player has no live ship (i.e. was destroyed).
- Equivalent to `SpawnShip` but may apply a short cooldown later (not in prototype).

---

## Scheduled simulation reducer

### `SimTick(ReducerContext ctx, <scheduling arg>)`
The heartbeat. Runs at a fixed interval (target **20 Hz** for the prototype; see `07`).
Scheduling in SpacetimeDB 2.0 is done with a scheduled-reducer table; consult the functions
reference for the exact 2.0 mechanism and wire it up in `Init`. Each invocation:

1. Increment `Match.Tick`.
2. **Integrate ships.** For every `Ship`, read its `ShipInput`, run the shared
   `FlightModel.Integrate(...)` with the fixed timestep `dt = 1/20`, and write back
   `Pos*`, `Vel*`, `Rot*`. Set `Ship.LastInputTick = ShipInput.ClientTick`.
3. **Spawn projectiles.** For each ship with `Firing == true` and whose fire cooldown has
   elapsed (track via class fire interval against `Match.Tick`), insert a `Projectile` row
   at the ship's nose with muzzle velocity + ship velocity, `ExpiresAtTick = Tick + lifespan`.
4. **Advance projectiles.** Integrate each `Projectile` position by `dt`. Cull any with
   `ExpiresAtTick <= Tick`.
5. **Resolve hits.** For each projectile, test against enemy ships and enemy bases (simple
   sphere checks). On hit: subtract damage, delete the projectile. If a ship's `Health <= 0`,
   delete the ship and clear the owner's `Player.ShipId`. If a base's `Health <= 0`, set
   `Match.Phase = Ended`, `Match.Winner = otherTeam`.
6. **Asteroid collision (optional for first pass).** If a ship overlaps an asteroid sphere,
   apply simple damage or a velocity reflection. Can be deferred to after first playable;
   record the choice in `99`.

> Determinism requirement: `SimTick` must integrate using the **fixed** `dt`, never a
> wall-clock delta. The client's prediction uses the same `dt`. Any divergence here produces
> constant misprediction. See `06` and `07`.

> Performance note: the prototype has a handful of ships and a few dozen projectiles —
> naive O(n²) hit checks are completely fine. Do not add spatial partitioning yet.

---

## Reducer call surface summary (what the client can invoke)

| Reducer | Direction | Frequency |
|---------|-----------|-----------|
| `SetName` | client → server | rare |
| `SpawnShip` | client → server | on spawn |
| `Respawn` | client → server | on death |
| `ApplyInput` | client → server | input tick rate (~20 Hz) |
| `SimTick` | DB-scheduled | 20 Hz |
| `Init` / `ClientConnected` / `ClientDisconnected` | lifecycle | automatic |

The client subscribes to read state; it never reads by polling. See `05` and `07`.
