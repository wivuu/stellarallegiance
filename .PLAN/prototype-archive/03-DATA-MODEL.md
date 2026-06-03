# 03 — Data Model (SpacetimeDB Tables)

This is the authoritative schema for the prototype. All tables live in `module/Lib.cs`.
The syntax below follows the **SpacetimeDB 2.0 C# module API** (`[SpacetimeDB.Table]`,
`[SpacetimeDB.Reducer]`, `ReducerContext`). Confirm exact attribute parameters against the
C# quickstart and tables reference linked in `02` — treat the code below as the intended
shape, not a guaranteed-compiling copy-paste.

## Design principles

- Keep every observable piece of state in a table. Nothing lives "only on the client."
- Use small fixed-size numeric fields; this is hot data synced often.
- Separate slow data (teams, bases) from fast data (ship transforms) so subscriptions and
  reasoning stay clean. They are different tables even though they could be merged.
- One enum, not magic numbers, for ship class and team.

## Tables

### `Player`
One row per connected human. Keyed by the SpacetimeDB `Identity`.

| Field | Type | Notes |
|-------|------|-------|
| `Identity` | `Identity` | Primary key. Provided by SpacetimeDB on connect. |
| `Team` | `byte` | 0 or 1. Assigned on join (balance teams). |
| `ShipId` | `ulong?` | The ship this player currently controls; null when docked/dead. |
| `Online` | `bool` | Set false on disconnect; row retained for the match. |
| `Name` | `string` | Display name; cosmetic. |

### `Ship`
One row per live ship in the world. This is the hot table.

| Field | Type | Notes |
|-------|------|-------|
| `ShipId` | `ulong` | Primary key, auto-increment. |
| `Owner` | `Identity` | The controlling player. |
| `Team` | `byte` | Denormalized from Player for fast team checks in sim. |
| `Class` | `ShipClass` | Scout or Fighter (enum). |
| `PosX/PosY/PosZ` | `float` | Authoritative position. |
| `VelX/VelY/VelZ` | `float` | Authoritative velocity. |
| `RotX/RotY/RotZ/RotW` | `float` | Orientation as a quaternion. |
| `Health` | `float` | Current hull; ≤ 0 → destroyed. |
| `LastInputTick` | `uint` | Highest sim tick whose input has been integrated for this ship. Used by client reconciliation. |

### `ShipInput`
The latest pending input for each ship. One row per ship, overwritten each time the client
sends input. The `SimTick` reducer reads and applies these.

| Field | Type | Notes |
|-------|------|-------|
| `ShipId` | `ulong` | Primary key (one input row per ship). |
| `Thrust` | `float` | -1..1, forward/back. |
| `StrafeX` | `float` | -1..1, left/right. |
| `StrafeY` | `float` | -1..1, up/down. |
| `Yaw` | `float` | -1..1. |
| `Pitch` | `float` | -1..1. |
| `Roll` | `float` | -1..1. |
| `Firing` | `bool` | Trigger held. |
| `ClientTick` | `uint` | The client's sim tick when this input was produced. |

### `Base`
One row per team base. Spawn/dock point and the win-condition target.

| Field | Type | Notes |
|-------|------|-------|
| `BaseId` | `ulong` | Primary key. |
| `Team` | `byte` | Owning team. |
| `PosX/PosY/PosZ` | `float` | Fixed position in the sector. |
| `Health` | `float` | ≤ 0 → base destroyed → match ends. |

### `Asteroid`
Static environment geometry. Written once at match init, never mutated.

| Field | Type | Notes |
|-------|------|-------|
| `AsteroidId` | `ulong` | Primary key. |
| `PosX/PosY/PosZ` | `float` | Position. |
| `Radius` | `float` | Collision + render scale. |

### `Projectile`
Live shots. Kept minimal; lifespan is short.

| Field | Type | Notes |
|-------|------|-------|
| `ProjectileId` | `ulong` | Primary key, auto-increment. |
| `Team` | `byte` | So friendly fire can be ignored. |
| `PosX/PosY/PosZ` | `float` | Position. |
| `VelX/VelY/VelZ` | `float` | Velocity. |
| `ExpiresAtTick` | `uint` | Sim tick at which it is culled. |

### `Match`
Singleton row holding match-level state.

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `uint` | Always 0 (singleton). |
| `Tick` | `uint` | The authoritative sim tick counter, incremented each `SimTick`. |
| `Phase` | `MatchPhase` | Lobby / Active / Ended (enum). |
| `Winner` | `byte?` | Team id when ended, else null. |

## Enums

```csharp
[SpacetimeDB.Type] public enum ShipClass : byte { Scout = 0, Fighter = 1 }
[SpacetimeDB.Type] public enum MatchPhase : byte { Lobby = 0, Active = 1, Ended = 2 }
```

## Ship class stats (constants, not a table)

Defined as constants in the shared flight code so client and server agree. Tune later.

| Stat | Scout | Fighter |
|------|-------|---------|
| Max hull | 60 | 120 |
| Thrust accel | 45 | 30 |
| Max speed | 70 | 50 |
| Linear drag coefficient | 1.2 | 1.0 |
| Angular accel | 3.5 | 2.5 |
| Weapon damage / shot | 4 | 10 |
| Fire interval (ticks) | 4 | 8 |

(These mirror the Allegiance feel: Scout = fast, fragile, harassing; Fighter = tanky,
slower, hits hard. Exact numbers are placeholders to be tuned during playtest.)

## Public vs private tables

Mark tables that clients must see as `Public = true` (Ship, Base, Asteroid, Projectile,
Match, Player). `ShipInput` can be public for simplicity in the prototype, but conceptually
it is write-only-by-owner; revisit access control after the prototype (record in `99`).
