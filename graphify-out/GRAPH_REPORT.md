# Graph Report - .  (2026-06-09)

## Corpus Check
- 25 files · ~61,343 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 781 nodes · 1359 edges · 44 communities (38 shown, 6 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 18 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

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

## God Nodes (most connected - your core abstractions)
1. `Module` - 60 edges
2. `WorldRenderer` - 49 edges
3. `ReducerContext` - 40 edges
4. `Chat` - 32 edges
5. `Module` - 32 edges
6. `ShipController` - 23 edges
7. `Lobby` - 17 edges
8. `ReducerContext` - 16 edges
9. `RemoteShip` - 15 edges
10. `Reducer` - 15 edges

## Surprising Connections (you probably didn't know these)
- `Starscape` --references--> `string`  [EXTRACTED]
  client/scripts/Starscape.cs → module/spacetimedb/Lib.cs
- `Asteroids CI Workflow` --semantically_similar_to--> `Build Godot Client CI Workflow`  [INFERRED] [semantically similar]
  .github/workflows/asteroids.yml → .github/workflows/build-godot-client.yml
- `Chat` --inherits--> `Control`  [EXTRACTED]
  client/scripts/Chat.cs → client/scripts/Hud.cs
- `ConnectionManager` --inherits--> `Node`  [EXTRACTED]
  client/scripts/ConnectionManager.cs → client/scripts/WorldRenderer.cs
- `Lobby` --inherits--> `Control`  [EXTRACTED]
  client/scripts/Lobby.cs → client/scripts/Hud.cs

## Import Cycles
- None detected.

## Communities (44 total, 6 thin omitted)

### Community 0 - "SpaceTimeDB Module Core"
Cohesion: 0.07
Nodes (25): DetRng, Filter, Identity, long, byte, float, int, Player (+17 more)

### Community 1 - "World Rendering & Projectiles"
Cohesion: 0.05
Nodes (34): Asteroid, AuthoredRadius, Base, double, float, Vector3, Aleph, bool (+26 more)

### Community 2 - "Visual FX & Materials"
Cohesion: 0.06
Nodes (23): float, StandardMaterial3D, Texture2D, WorldRenderer, Color, CurveTexture, float, GradientTexture2D (+15 more)

### Community 3 - "Client Prediction Controller"
Cohesion: 0.08
Nodes (21): double, EngineGlow, float, int, List, Ship, ShipClass, ShipInputState (+13 more)

### Community 4 - "AI Ship (Pig) Logic"
Cohesion: 0.15
Nodes (11): Aleph, float, int, Quat, ReducerContext, Ship, ShipInputState, uint (+3 more)

### Community 5 - "Chat System"
Cohesion: 0.08
Nodes (19): ChatMessage, bool, Color, DbConnection, double, EventContext, float, HBoxContainer (+11 more)

### Community 6 - "Asteroid Shape Generation"
Cohesion: 0.10
Nodes (34): _base_params(), _bumps(), _colour_params(), _crystal(), _cull(), _detail_params(), eval_base(), _eval_shape() (+26 more)

### Community 7 - "Camera & Target Markers"
Cohesion: 0.11
Nodes (16): Basis, Camera3D, Vector3, bool, Camera3D, float, IReadOnlyList, List (+8 more)

### Community 8 - "HUD & Connection UI"
Cohesion: 0.07
Nodes (19): Button, CanvasLayer, Button, Color, ConnectionManager, double, Label, ConnectionManager (+11 more)

### Community 9 - "Flight Model"
Cohesion: 0.16
Nodes (15): Cross(), byte, float, Quat, ShipInputState, ShipState, ShipStats, Vec3 (+7 more)

### Community 10 - "Aleph View & Starscape"
Cohesion: 0.12
Nodes (12): ArrayMesh, ShaderMaterial, string, Environment, float, int, AlephView, Starscape (+4 more)

### Community 11 - "Player Ship Controller"
Cohesion: 0.12
Nodes (13): bool, ConnectionManager, double, float, int, ShipClass, ShipInputState, uint (+5 more)

### Community 12 - "Lobby System"
Cohesion: 0.14
Nodes (13): Action, Button, Color, ConnectionManager, DbConnection, HBoxContainer, Identity, Label (+5 more)

### Community 13 - "Explosion Effects"
Cohesion: 0.14
Nodes (10): byte, Color, CurveTexture, double, float, GradientTexture1D, GradientTexture2D, MeshInstance3D (+2 more)

### Community 14 - "Composite Route Planner"
Cohesion: 0.19
Nodes (13): INavGraph, GraphWithEndpoints, StellarAllegiance.Shared.Navigation, PathQuery, Dictionary, int, IReadOnlyList, List (+5 more)

### Community 15 - "Nav Graph Structure"
Cohesion: 0.19
Nodes (12): INavGraph, NavGraph, NavGraphBuilder, NavGraphBuildOptions, StellarAllegiance.Shared.Navigation, float, int, IReadOnlyList (+4 more)

### Community 16 - "Build & Project Config"
Cohesion: 0.12
Nodes (12): net8.0, net8.0, net8.0, Microsoft.NET.Sdk, SpacetimeDB.ClientSDK (2.3.0), SpacetimeDB.Runtime (2.3.*), Godot.NET.Sdk/4.6.3, Microsoft.NET.Sdk (+4 more)

### Community 17 - "Navigation Test Suite"
Cohesion: 0.26
Nodes (6): Program, int, List, NavSphereObstacle, PathResult, Vec3

### Community 18 - "Remote Ship Interpolation"
Cohesion: 0.17
Nodes (9): bool, double, EngineGlow, float, int, List, Ship, Vector3 (+1 more)

### Community 19 - "Navigation Adapter (STDB)"
Cohesion: 0.18
Nodes (11): Dictionary, float, int, List, NavGraph, ReducerContext, ulong, Vec3 (+3 more)

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
Cohesion: 0.20
Nodes (7): Color, double, float, int, List, MeshInstance3D, ImmediateMesh

### Community 25 - "Nav Geometry Utilities"
Cohesion: 0.36
Nodes (5): Contains(), StellarAllegiance.Shared.Navigation, IReadOnlyList, NavSphereObstacle, Vec3

### Community 26 - "Asteroid GLB Export"
Cohesion: 0.27
Nodes (8): build_glb(), _normalize(), Assemble a Godot-ready GLB from the shape field + baked PBR textures.  The low-p, Per-vertex glTF TANGENT (vec4): T = normalize(d u / d lon), w = handedness., Return GLB bytes for the asteroid described by ``params``., _tangents(), Image, ndarray

### Community 27 - "Asteroid Generator"
Cohesion: 0.42
Nodes (8): _add_output_flags(), _file_info(), generate(), _generate_star(), main(), _opts(), _sizes(), Path

### Community 28 - "Sector Route Planner"
Cohesion: 0.36
Nodes (5): StellarAllegiance.Shared.Navigation, SectorLink, Dictionary, IReadOnlyList, List

### Community 29 - "Local Publish Scripts"
Cohesion: 0.50
Nodes (7): login_local(), publish_with_auth_retry(), recreate_volume(), start_server(), stdb(), wait_for_server(), publish-local.sh script

### Community 30 - "CSharpier Tooling"
Cohesion: 0.33
Nodes (6): commands, rollForward, isRoot, tools, csharpier, version

### Community 31 - "A* Planner"
Cohesion: 0.38
Nodes (4): StellarAllegiance.Shared.Navigation, Dictionary, INavGraph, List

### Community 32 - "Path Smoother"
Cohesion: 0.29
Nodes (5): StellarAllegiance.Shared.Navigation, IReadOnlyList, List, NavSphereObstacle, Vec3

### Community 33 - "Asteroid Gen Workflows"
Cohesion: 0.50
Nodes (4): Asteroid Mesh/Normal-Map Generator Tool, Godot Client Export (Multi-Platform), Asteroids CI Workflow, Build Godot Client CI Workflow

### Community 34 - "Client Export Scripts"
Cohesion: 0.50
Nodes (3): DOTNET_CLI_USE_MSBUILD_SERVER, MSBUILDDISABLENODEREUSE, export-clients.sh script

### Community 35 - "SpaceTimeDB Global SDK"
Cohesion: 0.50
Nodes (3): sdk, rollForward, version

## Knowledge Gaps
- **217 isolated node(s):** `ShaderMaterial`, `Vector3`, `Basis`, `Color`, `float` (+212 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **6 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Control` connect `HUD & Connection UI` to `Lobby System`, `Chat System`, `Camera & Target Markers`?**
  _High betweenness centrality (0.149) - this node is a cross-community bridge._
- **Why does `Module` connect `SpaceTimeDB Module Core` to `Aleph View & Starscape`, `Asteroid Shape Generation`?**
  _High betweenness centrality (0.142) - this node is a cross-community bridge._
- **Why does `WorldRenderer` connect `World Rendering & Projectiles` to `Camera & Target Markers`?**
  _High betweenness centrality (0.140) - this node is a cross-community bridge._
- **What connects `ShaderMaterial`, `Vector3`, `Basis` to the rest of the system?**
  _237 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `SpaceTimeDB Module Core` be split into smaller, more focused modules?**
  _Cohesion score 0.07245386192754613 - nodes in this community are weakly interconnected._
- **Should `World Rendering & Projectiles` be split into smaller, more focused modules?**
  _Cohesion score 0.054414414414414414 - nodes in this community are weakly interconnected._
- **Should `Visual FX & Materials` be split into smaller, more focused modules?**
  _Cohesion score 0.06155632984901278 - nodes in this community are weakly interconnected._