# Graph Report - wivuullegiance  (2026-06-09)

## Corpus Check
- 55 files · ~62,083 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 844 nodes · 1615 edges · 46 communities (39 shown, 7 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 18 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `86db6740`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_SpaceTimeDB Module Core|SpaceTimeDB Module Core]]
- [[_COMMUNITY_World Rendering & Projectiles|World Rendering & Projectiles]]
- [[_COMMUNITY_Visual FX & Materials|Visual FX & Materials]]
- [[_COMMUNITY_Client Prediction Controller|Client Prediction Controller]]
- [[_COMMUNITY_AI Ship (Pig) Logic|AI Ship (Pig) Logic]]
- [[_COMMUNITY_Chat System|Chat System]]
- [[_COMMUNITY_Asteroid Shape Generation|Asteroid Shape Generation]]
- [[_COMMUNITY_Camera & Target Markers|Camera & Target Markers]]
- [[_COMMUNITY_HUD & Connection UI|HUD & Connection UI]]
- [[_COMMUNITY_Flight Model|Flight Model]]
- [[_COMMUNITY_Aleph View & Starscape|Aleph View & Starscape]]
- [[_COMMUNITY_Player Ship Controller|Player Ship Controller]]
- [[_COMMUNITY_Lobby System|Lobby System]]
- [[_COMMUNITY_Explosion Effects|Explosion Effects]]
- [[_COMMUNITY_Composite Route Planner|Composite Route Planner]]
- [[_COMMUNITY_Nav Graph Structure|Nav Graph Structure]]
- [[_COMMUNITY_Build & Project Config|Build & Project Config]]
- [[_COMMUNITY_Navigation Test Suite|Navigation Test Suite]]
- [[_COMMUNITY_Remote Ship Interpolation|Remote Ship Interpolation]]
- [[_COMMUNITY_Navigation Adapter (STDB)|Navigation Adapter (STDB)]]
- [[_COMMUNITY_Flight Model Tests|Flight Model Tests]]
- [[_COMMUNITY_Asteroid Bake Pipeline|Asteroid Bake Pipeline]]
- [[_COMMUNITY_Connection Manager|Connection Manager]]
- [[_COMMUNITY_Sun Visual|Sun Visual]]
- [[_COMMUNITY_Team Trail VFX|Team Trail VFX]]
- [[_COMMUNITY_Nav Geometry Utilities|Nav Geometry Utilities]]
- [[_COMMUNITY_Asteroid GLB Export|Asteroid GLB Export]]
- [[_COMMUNITY_Asteroid Generator|Asteroid Generator]]
- [[_COMMUNITY_Sector Route Planner|Sector Route Planner]]
- [[_COMMUNITY_Local Publish Scripts|Local Publish Scripts]]
- [[_COMMUNITY_CSharpier Tooling|CSharpier Tooling]]
- [[_COMMUNITY_A Planner|A* Planner]]
- [[_COMMUNITY_Path Smoother|Path Smoother]]
- [[_COMMUNITY_Asteroid Gen Workflows|Asteroid Gen Workflows]]
- [[_COMMUNITY_Client Export Scripts|Client Export Scripts]]
- [[_COMMUNITY_SpaceTimeDB Global SDK|SpaceTimeDB Global SDK]]
- [[_COMMUNITY_Module Config|Module Config]]
- [[_COMMUNITY_Asteroid Build Script|Asteroid Build Script]]
- [[_COMMUNITY_CSharpier Config|CSharpier Config]]
- [[_COMMUNITY_Local Module Config|Local Module Config]]
- [[_COMMUNITY_Cloud Publish Script|Cloud Publish Script]]
- [[_COMMUNITY_Start Client Script|Start Client Script]]
- [[_COMMUNITY_DB Populate Script|DB Populate Script]]
- [[_COMMUNITY_Community 45|Community 45]]
- [[_COMMUNITY_Community 46|Community 46]]

## God Nodes (most connected - your core abstractions)
1. `Module` - 61 edges
2. `WorldRenderer` - 52 edges
3. `ReducerContext` - 40 edges
4. `Module` - 37 edges
5. `Chat` - 34 edges
6. `PredictionController` - 27 edges
7. `ShipController` - 24 edges
8. `TargetMarkers` - 21 edges
9. `EngineGlow` - 19 edges
10. `ExplosionEffect` - 18 edges

## Surprising Connections (you probably didn't know these)
- `Starscape` --references--> `string`  [EXTRACTED]
  client/scripts/Starscape.cs → module/spacetimedb/Lib.cs
- `Asteroids CI Workflow` --semantically_similar_to--> `Build Godot Client CI Workflow`  [INFERRED] [semantically similar]
  .github/workflows/asteroids.yml → .github/workflows/build-godot-client.yml
- `Chat` --inherits--> `Control`  [EXTRACTED]
  client/scripts/Chat.cs → client/scripts/Hud.cs
- `Chat` --inherits--> `Control`  [EXTRACTED]
  client/scripts/Chat.cs → client/scripts/Hud.cs
- `ConnectionManager` --inherits--> `Node`  [EXTRACTED]
  client/scripts/ConnectionManager.cs → client/scripts/WorldRenderer.cs

## Import Cycles
- None detected.

## Communities (46 total, 7 thin omitted)

### Community 0 - "SpaceTimeDB Module Core"
Cohesion: 0.07
Nodes (28): DetRng, Filter, Identity, long, byte, float, Identity, int (+20 more)

### Community 1 - "World Rendering & Projectiles"
Cohesion: 0.06
Nodes (34): Asteroid, AuthoredRadius, Base, Aleph, Asteroid, bool, byte, Color (+26 more)

### Community 2 - "Visual FX & Materials"
Cohesion: 0.08
Nodes (28): float, GradientTexture2D, StandardMaterial3D, Texture2D, WorldRenderer, Color, CurveTexture, float (+20 more)

### Community 3 - "Client Prediction Controller"
Cohesion: 0.16
Nodes (16): double, EngineGlow, float, int, List, Quaternion, Ship, ShipClass (+8 more)

### Community 4 - "AI Ship (Pig) Logic"
Cohesion: 0.14
Nodes (13): Aleph, Asteroid, Dictionary, float, int, List, Quat, ReducerContext (+5 more)

### Community 5 - "Chat System"
Cohesion: 0.08
Nodes (20): ChatMessage, bool, Color, ConnectionManager, DbConnection, double, EventContext, float (+12 more)

### Community 6 - "Asteroid Shape Generation"
Cohesion: 0.10
Nodes (35): _base_params(), _bumps(), _colour_params(), _crystal(), _cull(), _detail_params(), eval_base(), _eval_shape() (+27 more)

### Community 7 - "Camera & Target Markers"
Cohesion: 0.11
Nodes (25): Basis, Camera3D, Vector3, WorldRenderer, bool, Camera3D, Color, float (+17 more)

### Community 8 - "HUD & Connection UI"
Cohesion: 0.08
Nodes (24): Button, CanvasLayer, Button, Color, ConnectionManager, double, Label, Button (+16 more)

### Community 9 - "Flight Model"
Cohesion: 0.18
Nodes (16): Cross(), byte, float, Quat, ShipInputState, ShipState, ShipStats, Vec3 (+8 more)

### Community 10 - "Aleph View & Starscape"
Cohesion: 0.10
Nodes (16): ArrayMesh, float, int, ShaderMaterial, ShaderMaterial, string, uint, Environment (+8 more)

### Community 11 - "Player Ship Controller"
Cohesion: 0.11
Nodes (15): bool, ConnectionManager, double, float, InputEvent, int, ShipClass, ShipInputState (+7 more)

### Community 12 - "Lobby System"
Cohesion: 0.14
Nodes (13): Action, Button, Color, ConnectionManager, DbConnection, HBoxContainer, Identity, Label (+5 more)

### Community 13 - "Explosion Effects"
Cohesion: 0.21
Nodes (11): byte, Color, CurveTexture, double, float, GradientTexture1D, GradientTexture2D, MeshInstance3D (+3 more)

### Community 14 - "Composite Route Planner"
Cohesion: 0.19
Nodes (14): INavGraph, CompositeRoutePlanner, GraphWithEndpoints, StellarAllegiance.Shared.Navigation, PathQuery, Dictionary, int, IReadOnlyList (+6 more)

### Community 15 - "Nav Graph Structure"
Cohesion: 0.18
Nodes (12): INavGraph, NavGraph, NavGraphBuilder, NavGraphBuildOptions, StellarAllegiance.Shared.Navigation, float, int, IReadOnlyList (+4 more)

### Community 16 - "Build & Project Config"
Cohesion: 0.12
Nodes (15): wivuullegiance, net8.0, net8.0, net8.0, Microsoft.NET.Sdk, SpacetimeDB.ClientSDK (2.3.0), SpacetimeDB.Runtime (2.3.*), Godot.NET.Sdk/4.6.3 (+7 more)

### Community 17 - "Navigation Test Suite"
Cohesion: 0.26
Nodes (6): Program, int, List, NavSphereObstacle, PathResult, Vec3

### Community 18 - "Remote Ship Interpolation"
Cohesion: 0.17
Nodes (9): bool, double, EngineGlow, float, int, List, Ship, Vector3 (+1 more)

### Community 19 - "Navigation Adapter (STDB)"
Cohesion: 0.17
Nodes (13): Dictionary, float, int, List, NavGraph, ReducerContext, uint, ulong (+5 more)

### Community 20 - "Flight Model Tests"
Cohesion: 0.27
Nodes (5): Program, float, int, ShipInputState, ShipState

### Community 21 - "Asteroid Bake Pipeline"
Cohesion: 0.24
Nodes (9): bake_normal(), bake_surface(), _normalize(), Bake equirectangular texture maps from the analytic shape field.  Two passes, bo, Apply ``fn(y0, y1)`` over row-bands, optionally across ``jobs`` threads.      Ba, Directions + spherical tangent basis + latitude band for rows [y0, y1)., _row_geometry(), _run_bands() (+1 more)

### Community 22 - "Connection Manager"
Cohesion: 0.29
Nodes (5): DbConnection, Identity, string, Exception, ConnectionManager

### Community 23 - "Sun Visual"
Cohesion: 0.20
Nodes (7): Camera3D, float, ShaderMaterial, StandardMaterial3D, string, Vector3, Sun

### Community 24 - "Team Trail VFX"
Cohesion: 0.33
Nodes (8): Color, double, float, int, List, MeshInstance3D, ImmediateMesh, TeamTrail

### Community 25 - "Nav Geometry Utilities"
Cohesion: 0.39
Nodes (6): Contains(), NavGeometry, StellarAllegiance.Shared.Navigation, IReadOnlyList, NavSphereObstacle, Vec3

### Community 26 - "Asteroid GLB Export"
Cohesion: 0.27
Nodes (8): build_glb(), _normalize(), Assemble a Godot-ready GLB from the shape field + baked PBR textures.  The low-p, Per-vertex glTF TANGENT (vec4): T = normalize(d u / d lon), w = handedness., Return GLB bytes for the asteroid described by ``params``., _tangents(), Image, ndarray

### Community 27 - "Asteroid Generator"
Cohesion: 0.42
Nodes (8): _add_output_flags(), _file_info(), generate(), _generate_star(), main(), _opts(), _sizes(), Path

### Community 28 - "Sector Route Planner"
Cohesion: 0.38
Nodes (6): SectorRoutePlanner, StellarAllegiance.Shared.Navigation, SectorLink, Dictionary, IReadOnlyList, List

### Community 29 - "Local Publish Scripts"
Cohesion: 0.50
Nodes (7): login_local(), publish_with_auth_retry(), recreate_volume(), start_server(), stdb(), wait_for_server(), publish-local.sh script

### Community 30 - "CSharpier Tooling"
Cohesion: 0.29
Nodes (7): commands, rollForward, version, isRoot, tools, csharpier, version

### Community 31 - "A* Planner"
Cohesion: 0.39
Nodes (5): AStarPlanner, StellarAllegiance.Shared.Navigation, Dictionary, INavGraph, List

### Community 32 - "Path Smoother"
Cohesion: 0.29
Nodes (6): PathSmoother, StellarAllegiance.Shared.Navigation, IReadOnlyList, List, NavSphereObstacle, Vec3

### Community 33 - "Asteroid Gen Workflows"
Cohesion: 0.50
Nodes (4): Asteroid Mesh/Normal-Map Generator Tool, Godot Client Export (Multi-Platform), Asteroids CI Workflow, Build Godot Client CI Workflow

### Community 34 - "Client Export Scripts"
Cohesion: 0.50
Nodes (3): DOTNET_CLI_USE_MSBUILD_SERVER, MSBUILDDISABLENODEREUSE, export-clients.sh script

### Community 35 - "SpaceTimeDB Global SDK"
Cohesion: 0.50
Nodes (3): sdk, rollForward, version

### Community 45 - "Community 45"
Cohesion: 0.29
Nodes (6): double, float, Projectile, Vector3, Projectile, ProjectileView

### Community 46 - "Community 46"
Cohesion: 0.20
Nodes (7): Quat, Quaternion, Ship, ShipState, Vec3, Vector3, ShipMath

## Knowledge Gaps
- **198 isolated node(s):** `float`, `int`, `ShaderMaterial`, `WorldRenderer`, `Color` (+193 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **7 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `WorldRenderer` connect `World Rendering & Projectiles` to `Visual FX & Materials`, `Camera & Target Markers`?**
  _High betweenness centrality (0.145) - this node is a cross-community bridge._
- **Why does `Module` connect `SpaceTimeDB Module Core` to `Aleph View & Starscape`, `AI Ship (Pig) Logic`, `Asteroid Shape Generation`?**
  _High betweenness centrality (0.138) - this node is a cross-community bridge._
- **Why does `Control` connect `HUD & Connection UI` to `Lobby System`, `Chat System`, `Camera & Target Markers`?**
  _High betweenness centrality (0.134) - this node is a cross-community bridge._
- **What connects `float`, `int`, `ShaderMaterial` to the rest of the system?**
  _218 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `SpaceTimeDB Module Core` be split into smaller, more focused modules?**
  _Cohesion score 0.06553041434028799 - nodes in this community are weakly interconnected._
- **Should `World Rendering & Projectiles` be split into smaller, more focused modules?**
  _Cohesion score 0.06169772256728778 - nodes in this community are weakly interconnected._
- **Should `Visual FX & Materials` be split into smaller, more focused modules?**
  _Cohesion score 0.07955596669750231 - nodes in this community are weakly interconnected._