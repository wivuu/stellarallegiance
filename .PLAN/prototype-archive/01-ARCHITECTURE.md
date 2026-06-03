# 01 — Architecture

## The two halves

```
┌─────────────────────────────┐         WebSocket          ┌──────────────────────────────┐
│      Godot 4 client (C#)     │ ◄────── subscriptions ─────►│        SpacetimeDB 2.0         │
│                             │                             │      (module = WASM)           │
│  • Renders the 3D sector     │ ──────  reducer calls ─────►│                                │
│  • Reads input               │                             │  Tables:  authoritative state  │
│  • Predicts local ship       │                             │  Reducers: the only writers    │
│  • Interpolates remote ships │                             │  Scheduled reducer: sim tick   │
└─────────────────────────────┘                             └──────────────────────────────┘
        (one process per player)                                  (one shared process)
```

Each player runs their own Godot client. There is exactly one SpacetimeDB module instance
hosting the match. Clients talk to it over a WebSocket the SDK manages for you.

## Authority model — read this twice

**The server is authoritative for everything observable by more than one player.** That means:

- Ship positions, velocities, orientations: server-owned.
- Ship health, alive/dead state: server-owned.
- Base health: server-owned.
- Team assignment: server-owned.
- Weapon fire and hit resolution: server-owned.

**The client is authoritative for nothing**, but it is allowed to *predict* one thing: the
motion of the local player's own ship, so that flight feels instant rather than waiting a
round-trip. Prediction is a rendering convenience, not a source of truth. When the server's
authoritative position for the local ship arrives, the client reconciles (see `07`).

This is the single most important rule in the project. A bug where the client "knows better"
than the server is the kind of bug that looks fine in single-player testing and falls apart
the moment two humans disagree.

## Why SpacetimeDB instead of hand-rolled netcode

SpacetimeDB collapses the database and the server into one WASM module. You define **tables**
(the world state) and **reducers** (the only functions allowed to mutate tables). Clients
**subscribe** to a query over the tables and receive a live, automatically-synced view; when
a reducer changes a row, every subscribed client is pushed the delta. This removes the need
to write a WebSocket server, a serialization layer, and a state-replication system by hand —
which for an Allegiance-like game is the bulk of the hard engineering.

The cost is a mental shift: you are writing your backend *as database modules*, and all
writes go through reducers. There is no "server tick loop" in the traditional sense — instead
there is a **scheduled reducer** that the database calls on a fixed interval, which is where
the simulation step lives (see `04-REDUCERS.md` → `SimTick`).

## Language choice

- **Module (server):** C#. Compiles to WebAssembly via the WASI workload.
- **Client:** Godot 4 with C# (.NET). The SpacetimeDB C# client SDK is consumed as a NuGet
  package and the generated bindings are dropped into the Godot project.

Using C# on both sides means the flight-model math (`06`) can live in a small shared file
copied into both projects, so prediction (client) and authority (server) use *identical*
integration code. This is critical: if the two sides integrate motion differently, prediction
will constantly mispredict and ships will jitter.

> ⚠️ The flight math must be deterministic and identical on both sides. Use fixed timestep
> integration, never `delta`-varying integration, for the authoritative path. See `06`.

## Process & deployment topology for the prototype

- **Local dev:** `spacetime start` runs a standalone DB on `localhost:3000`. Two Godot
  client instances (run the project twice, or export and launch two copies) connect to it.
- **Shared testing:** publish the module to SpacetimeDB Maincloud and point clients at the
  Maincloud host. Not required until the multi-client acceptance test in `09`.

## Data flow for one player input (the canonical loop)

1. Player holds "thrust forward" in Godot.
2. Client appends the input to its local input buffer **and** applies it to the predicted
   local ship immediately (instant feel).
3. Client calls the `ApplyInput` reducer with the input + the client's current sim tick.
4. Server stores the input; on the next `SimTick` it integrates all ships authoritatively
   and writes new transforms to the `Ship` table.
5. The row change is pushed to every subscribed client.
6. Local client compares the authoritative transform to its prediction for that tick and
   reconciles (snap or smooth-correct). Remote clients interpolate the new transform.
