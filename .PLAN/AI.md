# Plan — AI Pathfinding for Free-Flying Ships

## Context

This project already has the right separation for a pathfinding system that is independent of rendering and networking:

- `shared/FlightModel.cs` is a pure shared library used by server authority, client prediction, and tests.
- `module/spacetimedb/PigAI.cs` currently does direct homing plus local asteroid avoidance, but not full path planning.
- `Sector` and `Aleph` already define a coarse world graph for cross-sector movement.
- Asteroids are static for a match.
- For v1, a waypoint path followed by the current steering system is acceptable.

The goal is to add a general-purpose pathfinding layer for free-flying ships that works for:

- open-space dogfighting
- navigating around asteroids and stations
- docking corridors and structured approaches

The pathfinding code should be:

- pure C#
- independent of graphics and networking
- easy to unit test
- usable from server-side AI without pulling in Godot or client-only code

Math types for the navigation library should reuse the existing shared `Vec3` and `Quat`
already defined in `shared/FlightModel.cs`, rather than introducing parallel types. As a
follow-up cleanup, those shared math primitives should be moved out of `FlightModel.cs`
into dedicated shared math files so flight integration and navigation can depend on the
same small math layer without the file becoming a grab bag.

## Decision

Do not use a navmesh library such as DotRecast for this feature.

Use a small pure C# navigation library in `shared/` built around `A*` on layered graphs:

- sector graph for macro routing through `Aleph` links
- per-sector 3D waypoint graph for local routing around static obstacles
- existing steering logic for actually flying the route

This fits the project better than a surface-based navmesh because ships move freely in 3D space rather than along walkable surfaces.

## Goals

- Give AI ships a real planner when a direct line to the goal is blocked.
- Support same-sector detours around asteroid fields.
- Support multi-sector travel through aleph chains.
- Support future docking and corridor-style movement with the same planner.
- Keep the planner pure and testable outside the game runtime.

## Non-Goals For V1

- full kinodynamic or turn-radius-constrained planning
- dynamic obstacle planning for moving ships and projectiles
- replacing the current combat steering and aiming logic
- client-side AI pathfinding or prediction of AI decisions
- a heavyweight third-party pathfinding dependency

## Core Design

### 1. Layered Navigation

Use three layers instead of one universal algorithm.

1. Strategic routing
Plan from sector to sector using the existing `Sector` and `Aleph` topology.

2. Local routing
Within a sector, plan through a sparse 3D waypoint graph that avoids static obstacles.

3. Tactical flight
Use the existing steering logic to fly toward the next waypoint, keep local avoidance, aim, fire, strafe, and attack behavior separate from the planner.

This keeps the planner simple and lets combat movement remain responsive.

### 2. Planner Only When Needed

Do not run full pathfinding for every AI decision.

Use direct steering when:

- target is in the same sector
- the straight segment to the goal is clear
- the ship is already in a close-range dogfight

Use the planner when:

- the straight path is blocked by static obstacles
- the target is in another sector
- the ship is approaching a dock, gate, base, or corridor entrance
- patrol or rescue needs a structured route through clutter

This keeps v1 cheap and preserves the current combat feel.

### 3. Pure Shared Library

Put the navigation core in `shared/` so it stays engine-agnostic.

The shared code should know nothing about:

- Godot nodes
- networking
- database access
- UI
- rendering

The server module should translate table rows into pure navigation inputs, call the planner, and turn the result back into waypoint targets for AI steering.

## World Representation

### Strategic Graph

Build a graph from existing world data:

- `Sector` is a node
- `Aleph` connections are edges

This graph solves:

- path from sector A to sector B
- which aleph to take next
- fallback when multiple hops are needed

This part is already naturally present in the game data.

### Local 3D Graph

Within each sector, use a sparse waypoint graph.

Node sources for v1:

- each aleph position
- each base position
- patrol anchors
- rescue anchors
- generated samples around static obstacles
- optional future authored corridor or docking nodes

Edge rules:

- connect nodes only if the straight segment between them is collision-free
- keep a max edge length so the graph stays sparse
- prefer shorter and clearer edges

Obstacle model for v1:

- asteroids as inflated spheres
- bases as inflated spheres
- optional safety margin per obstacle type

This matches the current sim model well, because asteroids and bases are already treated as simple collision volumes.

### Future Corridor Support

Docking corridors should not require a new planner.

Instead, future corridor support should add special waypoint nodes and edges:

- dock entrance node
- corridor lane nodes
- dock interior or staging nodes
- exit node

The same `A*` planner can then route through those nodes naturally.

## Graph Construction Strategy

### V1 Graph Builder

Use a deterministic graph builder with fixed sampling.

Inputs:

- sector radius
- asteroid positions and radii
- base positions and radii
- aleph positions
- optional authored anchor nodes

Candidate waypoint generation:

- create anchor nodes at important destinations
- create a fixed set of sample nodes around each obstacle shell
- create a few open-space support nodes per sector
- reject nodes that land inside inflated obstacles

Edge generation:

- for each node, consider nearby candidate neighbors
- keep an edge only if the segment is obstacle-free
- cap the number of neighbors per node
- store edge cost as Euclidean distance, optionally with a small clearance penalty

Because obstacles are static, this graph can be reused for many queries within the same world state.

### Why Not A Dense 3D Grid

A full 3D voxel grid is the wrong default here because:

- open space is mostly empty
- sparse obstacles are better handled by sparse waypoints
- memory and search cost grow much faster in a volume grid
- docking corridors can be modeled more cleanly with explicit nodes

A sparse waypoint graph gives better cost/performance for this game.

## Pathfinding Algorithm

### Local and Strategic Search

Use `A*` for both levels.

For the sector graph:

- nodes are sectors
- edge cost is hop count or aleph travel distance
- heuristic can be zero at first, or sector-center distance later

For the local graph:

- nodes are 3D waypoints
- edge cost is Euclidean distance
- heuristic is straight-line distance to the goal

Implementation details:

- use `.NET`'s `PriorityQueue<TElement, TPriority>`
- keep the implementation generic enough to test directly
- return both the raw path and a smoothed path

### Path Smoothing

After `A*`, run a simple visibility-based smoothing pass.

Smoothing rule:

- if waypoint `A` can see waypoint `C` directly, drop intermediate waypoint `B`

This reduces jagged routes without changing the underlying planner.

For v1, this is enough. No funnel algorithm is needed because this is not a navmesh.

## Suggested Shared API

The exact names can change, but the shape should stay close to this.

### Core types

- existing shared `Vec3`
- existing shared `Quat` where orientation helpers are needed
- `NavSphereObstacle`
- `NavNode`
- `NavEdge`
- `NavGraph`
- `NavPath`
- `PathQuery`
- `PathResult`

### Pure services

- `LineOfSight`
- `NavGraphBuilder`
- `AStarPlanner`
- `PathSmoother`
- `SectorRoutePlanner`
- `CompositeRoutePlanner`

### Query flow

1. Convert the current world data into a pure navigation snapshot.
2. If the destination is in another sector, plan the sector route first.
3. Pick the next local objective.
4. Build or fetch the sector-local waypoint graph.
5. Insert temporary start and goal nodes.
6. Run local `A*`.
7. Smooth the path.
8. Hand the first useful waypoint to the AI steering layer.

## SpacetimeDB-Specific Integration

### Determinism and State

The navigation library should remain pure.

The module should avoid hidden mutable global state. For v1, use one of these two approaches:

- build navigation snapshots and graphs inside the reducer from current table rows
- or generate/stash a deterministic graph representation in tables during map generation

Start with the simpler option unless profiling proves it too expensive.

Because obstacles are static and PIG counts are small, v1 can likely get away with deterministic per-tick or per-query graph construction.

### Recommended V1 Integration Point

Integrate pathfinding into `PigAI.cs`, not the client.

Keep this flow:

- `PigThink` decides what the goal is
- the planner returns a waypoint path if needed
- `PigSteerTo` follows the next waypoint
- current aim/fire/juke logic stays intact

This keeps the planner focused on navigation instead of combat behavior.

### Multi-Sector Behavior

When a target is in another sector:

- run sector-level `A*`
- choose the next aleph along the route
- run local pathfinding to that aleph
- once warped, repeat for the next sector

This replaces the current direct `AlephTo(...)` behavior with a real multi-hop route.

## Proposed File Layout

### Shared

- keep using the existing `Vec3`/`Quat` from `shared/FlightModel.cs` rather than adding new math structs
- future cleanup: move `Vec3`, `Quat`, and any shared math helpers into dedicated shared math files
- `shared/Navigation/NavGeometry.cs`
- `shared/Navigation/NavGraph.cs`
- `shared/Navigation/AStarPlanner.cs`
- `shared/Navigation/PathSmoother.cs`
- `shared/Navigation/SectorRoutePlanner.cs`
- `shared/Navigation/CompositeRoutePlanner.cs`

### Server module

- edit `module/spacetimedb/PigAI.cs`
- possibly add `module/spacetimedb/NavigationAdapter.cs` to map DB rows into pure nav inputs
- optionally split more navigation-specific helpers out of `PigAI.cs` if it starts getting too large

### Tests

- `tests/NavigationTest/` as a standalone test project
- or a formal unit test project if the repo standard moves that way

To stay close to the existing repo style, a small dedicated test project similar in spirit to `tests/FlightModelTest/` is fine.

## Milestones

### M1 — Pure geometry and graph primitives

Implement the pure building blocks:

- 3D point and distance helpers
- segment-vs-sphere intersection
- line-of-sight checks
- graph nodes and edges
- generic `A*`

Verification:

- geometry tests
- trivial graph tests
- shortest-path correctness tests

### M2 — Sector routing

Implement the strategic planner over `Sector` and `Aleph`.

Verification:

- same-sector no-hop path
- one-hop route
- multi-hop route
- unreachable-sector result

### M3 — Local sector graph builder

Implement the per-sector sparse waypoint graph builder.

Verification:

- no obstacle straight-through route
- single-asteroid detour
- multiple-asteroid detour
- start or goal near obstacle
- no-path case when enclosed

### M4 — Path smoothing and transient start/goal nodes

Add:

- start/goal insertion into the graph
- path smoothing
- waypoint pruning

Verification:

- smoothed path is shorter or equal
- smoothing never creates an obstacle intersection
- direct path collapses to one segment when visible

### M5 — Server integration for AI ships

Integrate with `PigAI.cs`.

Behavior changes:

- use direct steering when clear
- use local pathfinding when blocked
- use sector routing for multi-sector goals
- continue using existing steering to fly the next waypoint

Verification:

- pigs route around asteroid clusters
- pigs travel across multiple sectors
- pigs still fight normally in open space
- pods can navigate home more reliably than raw direct steering

### M6 — Docking and structured approach support

Add path targets for bases, gates, and future docking corridors.

Verification:

- approach path terminates at a safe staging waypoint
- corridor nodes are respected when present
- docking path is stable and repeatable

## Test Plan

The navigation library must be testable without graphics or networking.

### Unit tests

Cover:

- segment-sphere collision
- line-of-sight checks
- node rejection inside obstacles
- edge generation
- `A*` optimality on small known graphs
- path smoothing correctness
- sector routing correctness
- composite route stitching

### Scenario tests

Create a few fixed test scenes with hard-coded obstacles:

- open dogfight space
- asteroid belt
- dense cluster around a base
- two-sector gate route
- docking corridor path

Each scene should assert:

- path found or not found as expected
- path does not intersect obstacles
- path length is within a reasonable bound
- smoothing does not break validity

### Integration tests

At the server/module level, verify:

- a pig chasing a target behind asteroids detours instead of ramming repeatedly
- a pig moving to another sector chooses the correct aleph chain
- a pig pod returns home through clutter better than raw direct steering
- pathfinding is only invoked when the direct route is blocked or strategically needed

## Performance Notes

Expected v1 costs are manageable because:

- obstacle fields are static
- AI counts are low
- not every tick requires a full replan
- the graph is sparse rather than volumetric

Optimization rules for v1:

- prefer direct steering when line-of-sight is clear
- reuse sector-local graphs within a tick or query scope
- only replan when the goal changes meaningfully, the sector changes, or the current path becomes invalid

If performance later becomes an issue:

- persist generated nav graph data in private tables
- precompute graph data during world generation
- cache nearest visible nodes per anchor
- add path request throttling

## Risks

- Too few waypoint samples will create false "no path" failures.
- Too many samples will make the graph expensive.
- If pathfinding fully replaces combat steering, AI may feel robotic.
- If docking corridors are added later without explicit corridor nodes, routes may cut corners in ugly ways.
- If the server builds graphs ad hoc in hot loops without profiling, pathfinding may become more expensive than expected.

## Risk Mitigations

- Start with fixed deterministic sample counts and tune from tests.
- Keep pathfinding separate from combat steering.
- Use smoothing only after validating visibility.
- Add explicit corridor anchors for docking instead of trying to infer them from geometry alone.
- Profile before adding schema or persistence complexity.

## Success Criteria

This plan is successful when:

- the codebase has a pure shared navigation library with no graphics or networking dependencies
- AI ships can route around static asteroid fields
- AI ships can navigate across multiple sectors through aleph links
- the planner can support future docking corridors by adding nodes rather than replacing the system
- the pathfinding code is covered by standalone tests
- open-space combat still uses responsive direct steering where appropriate

## Recommended First Implementation Slice

Build the smallest end-to-end slice first:

1. pure geometry helpers
2. generic `A*`
3. sector graph planner
4. same-sector sparse waypoint planner around asteroid spheres
5. one `PigAI` integration path for "goal blocked by asteroid"
6. standalone tests for the above

That slice delivers immediate gameplay value without forcing the full corridor/docking system upfront.
