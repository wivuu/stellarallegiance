# Graph Report - wivuullegiance  (2026-06-06)

## Corpus Check
- 46 files · ~50,820 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 710 nodes · 1230 edges · 41 communities (34 shown, 7 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 12 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `64bdc4cf`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_SpaceTimeDB Module Core|SpaceTimeDB Module Core]]
- [[_COMMUNITY_AlephView World State|AlephView World State]]
- [[_COMMUNITY_Chat System|Chat System]]
- [[_COMMUNITY_Asteroid Shape Generation|Asteroid Shape Generation]]
- [[_COMMUNITY_Connection Overlay UI|Connection Overlay UI]]
- [[_COMMUNITY_PigAI NPC Behavior|PigAI NPC Behavior]]
- [[_COMMUNITY_Client-Side Prediction|Client-Side Prediction]]
- [[_COMMUNITY_Shared Flight Model|Shared Flight Model]]
- [[_COMMUNITY_Ship Controller Input|Ship Controller Input]]
- [[_COMMUNITY_Engine Glow Effects|Engine Glow Effects]]
- [[_COMMUNITY_Lobby UI|Lobby UI]]
- [[_COMMUNITY_Hit Flash Effect|Hit Flash Effect]]
- [[_COMMUNITY_Explosion Effect|Explosion Effect]]
- [[_COMMUNITY_Target Markers HUD|Target Markers HUD]]
- [[_COMMUNITY_Remote Ship Networked|Remote Ship Networked]]
- [[_COMMUNITY_Project Build Config|Project Build Config]]
- [[_COMMUNITY_Connection & World Renderer|Connection & World Renderer]]
- [[_COMMUNITY_Dust Field Environment|Dust Field Environment]]
- [[_COMMUNITY_Ship Math Utilities|Ship Math Utilities]]
- [[_COMMUNITY_Team Trail Effects|Team Trail Effects]]
- [[_COMMUNITY_Flight Model Tests|Flight Model Tests]]
- [[_COMMUNITY_Asteroid Normal Baking|Asteroid Normal Baking]]
- [[_COMMUNITY_GLB Export Pipeline|GLB Export Pipeline]]
- [[_COMMUNITY_Sun Visual|Sun Visual]]
- [[_COMMUNITY_Asteroid Generator CLI|Asteroid Generator CLI]]
- [[_COMMUNITY_Starscape Background|Starscape Background]]
- [[_COMMUNITY_DotNet Tools Config|DotNet Tools Config]]
- [[_COMMUNITY_Local Publishing Scripts|Local Publishing Scripts]]
- [[_COMMUNITY_CI Workflow Pipeline|CI Workflow Pipeline]]
- [[_COMMUNITY_Client Export Scripts|Client Export Scripts]]
- [[_COMMUNITY_SpaceTimeDB SDK Config|SpaceTimeDB SDK Config]]
- [[_COMMUNITY_SpaceTimeDB Module Config|SpaceTimeDB Module Config]]
- [[_COMMUNITY_Asteroid Build Script|Asteroid Build Script]]
- [[_COMMUNITY_CSharpier Formatting|CSharpier Formatting]]
- [[_COMMUNITY_Local SpaceTimeDB Config|Local SpaceTimeDB Config]]
- [[_COMMUNITY_Database Population Script|Database Population Script]]
- [[_COMMUNITY_Cloud Publishing Script|Cloud Publishing Script]]
- [[_COMMUNITY_Client Start Script|Client Start Script]]
- [[_COMMUNITY_Community 41|Community 41]]

## God Nodes (most connected - your core abstractions)
1. `Module` - 56 edges
2. `WorldRenderer` - 50 edges
3. `ReducerContext` - 36 edges
4. `ReducerContext` - 36 edges
5. `Chat` - 32 edges
6. `TargetMarkers` - 28 edges
7. `Module` - 28 edges
8. `PredictionController` - 27 edges
9. `ShipController` - 23 edges
10. `EngineGlow` - 19 edges

## Surprising Connections (you probably didn't know these)
- `TargetMarkers` --references--> `float`  [EXTRACTED]
  client/scripts/TargetMarkers.cs → client/scripts/AlephView.cs
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

## Hyperedges (group relationships)
- **CI Pipeline: Build and Upload Game Artifacts** — workflows_asteroids, workflows_build_godot_client, concept_asteroid_gen [INFERRED 0.85]

## Communities (41 total, 7 thin omitted)

### Community 0 - "SpaceTimeDB Module Core"
Cohesion: 0.08
Nodes (31): DetRng, Filter, Identity, long, byte, float, Identity, int (+23 more)

### Community 1 - "AlephView World State"
Cohesion: 0.06
Nodes (38): Aleph, Asteroid, AuthoredRadius, Base, Aleph, bool, byte, ConnectionManager (+30 more)

### Community 2 - "Chat System"
Cohesion: 0.08
Nodes (20): ChatMessage, bool, Color, ConnectionManager, DbConnection, double, EventContext, float (+12 more)

### Community 3 - "Asteroid Shape Generation"
Cohesion: 0.10
Nodes (34): _base_params(), _bumps(), _colour_params(), _crystal(), _cull(), _detail_params(), eval_base(), _eval_shape() (+26 more)

### Community 4 - "Connection Overlay UI"
Cohesion: 0.07
Nodes (21): CanvasLayer, Button, Color, ConnectionManager, double, Label, Button, ConnectionManager (+13 more)

### Community 5 - "PigAI NPC Behavior"
Cohesion: 0.16
Nodes (11): Aleph, float, int, Quat, ReducerContext, Ship, ShipInputState, uint (+3 more)

### Community 6 - "Client-Side Prediction"
Cohesion: 0.11
Nodes (15): double, EngineGlow, float, int, List, Quaternion, Ship, ShipClass (+7 more)

### Community 7 - "Shared Flight Model"
Cohesion: 0.15
Nodes (17): Cross(), byte, float, Quat, ShipInputState, ShipState, ShipStats, Vec3 (+9 more)

### Community 8 - "Ship Controller Input"
Cohesion: 0.11
Nodes (14): bool, ConnectionManager, double, float, InputEvent, int, ShipClass, ShipInputState (+6 more)

### Community 9 - "Engine Glow Effects"
Cohesion: 0.12
Nodes (14): Color, CurveTexture, float, GradientTexture1D, GradientTexture2D, StandardMaterial3D, Texture2D, Vector3 (+6 more)

### Community 10 - "Lobby UI"
Cohesion: 0.14
Nodes (13): Action, Button, Color, ConnectionManager, DbConnection, HBoxContainer, Identity, Label (+5 more)

### Community 11 - "Hit Flash Effect"
Cohesion: 0.21
Nodes (5): double, float, Projectile, Vector3, ProjectileView

### Community 12 - "Explosion Effect"
Cohesion: 0.13
Nodes (11): byte, Color, CurveTexture, double, float, GradientTexture1D, GradientTexture2D, MeshInstance3D (+3 more)

### Community 13 - "Target Markers HUD"
Cohesion: 0.08
Nodes (27): Basis, bool, Camera3D, Vector3, WorldRenderer, bool, Camera3D, Color (+19 more)

### Community 14 - "Remote Ship Networked"
Cohesion: 0.17
Nodes (9): bool, double, EngineGlow, float, int, List, Ship, Vector3 (+1 more)

### Community 15 - "Project Build Config"
Cohesion: 0.14
Nodes (11): wivuullegiance, net8.0, net8.0, Microsoft.NET.Sdk, SpacetimeDB.ClientSDK (2.3.0), SpacetimeDB.Runtime (2.3.*), Godot.NET.Sdk/4.6.3, net8.0 (+3 more)

### Community 16 - "Connection & World Renderer"
Cohesion: 0.26
Nodes (5): DbConnection, Identity, string, Exception, ConnectionManager

### Community 17 - "Dust Field Environment"
Cohesion: 0.20
Nodes (7): float, GradientTexture2D, StandardMaterial3D, Texture2D, WorldRenderer, GpuParticles3D, DustField

### Community 18 - "Ship Math Utilities"
Cohesion: 0.18
Nodes (8): Quat, Quaternion, Ship, ShipState, Vec3, Vector3, Quaternion, ShipMath

### Community 19 - "Team Trail Effects"
Cohesion: 0.18
Nodes (8): Color, double, float, int, List, MeshInstance3D, ImmediateMesh, TeamTrail

### Community 20 - "Flight Model Tests"
Cohesion: 0.27
Nodes (5): Program, float, int, ShipInputState, ShipState

### Community 21 - "Asteroid Normal Baking"
Cohesion: 0.24
Nodes (9): bake_normal(), bake_surface(), _normalize(), Bake equirectangular texture maps from the analytic shape field.  Two passes, bo, Apply ``fn(y0, y1)`` over row-bands, optionally across ``jobs`` threads.      Ba, Directions + spherical tangent basis + latitude band for rows [y0, y1)., _row_geometry(), _run_bands() (+1 more)

### Community 22 - "GLB Export Pipeline"
Cohesion: 0.27
Nodes (8): build_glb(), _normalize(), Assemble a Godot-ready GLB from the shape field + baked PBR textures.  The low-p, Per-vertex glTF TANGENT (vec4): T = normalize(d u / d lon), w = handedness., Return GLB bytes for the asteroid described by ``params``., _tangents(), Image, ndarray

### Community 23 - "Sun Visual"
Cohesion: 0.20
Nodes (7): Camera3D, float, ShaderMaterial, StandardMaterial3D, string, Vector3, Sun

### Community 24 - "Asteroid Generator CLI"
Cohesion: 0.42
Nodes (8): _add_output_flags(), _file_info(), generate(), _generate_star(), main(), _opts(), _sizes(), Path

### Community 25 - "Starscape Background"
Cohesion: 0.10
Nodes (14): ArrayMesh, float, int, ShaderMaterial, ShaderMaterial, string, uint, Environment (+6 more)

### Community 27 - "DotNet Tools Config"
Cohesion: 0.25
Nodes (7): commands, rollForward, version, isRoot, tools, csharpier, version

### Community 28 - "Local Publishing Scripts"
Cohesion: 0.50
Nodes (7): login_local(), publish_with_auth_retry(), recreate_volume(), start_server(), stdb(), wait_for_server(), publish-local.sh script

### Community 29 - "CI Workflow Pipeline"
Cohesion: 0.50
Nodes (4): Asteroid Mesh/Normal-Map Generator Tool, Godot Client Export (Multi-Platform), Asteroids CI Workflow, Build Godot Client CI Workflow

### Community 30 - "Client Export Scripts"
Cohesion: 0.50
Nodes (3): DOTNET_CLI_USE_MSBUILD_SERVER, MSBUILDDISABLENODEREUSE, export-clients.sh script

### Community 31 - "SpaceTimeDB SDK Config"
Cohesion: 0.50
Nodes (3): sdk, rollForward, version

### Community 41 - "Community 41"
Cohesion: 0.22
Nodes (6): double, float, GradientTexture2D, MeshInstance3D, StandardMaterial3D, HitFlash

## Knowledge Gaps
- **206 isolated node(s):** `float`, `int`, `ShaderMaterial`, `Vector3`, `Basis` (+201 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **7 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `WorldRenderer` connect `AlephView World State` to `Target Markers HUD`?**
  _High betweenness centrality (0.364) - this node is a cross-community bridge._
- **Why does `TargetMarkers` connect `Target Markers HUD` to `Starscape Background`, `Connection Overlay UI`?**
  _High betweenness centrality (0.222) - this node is a cross-community bridge._
- **Why does `Aleph` connect `AlephView World State` to `PigAI NPC Behavior`?**
  _High betweenness centrality (0.197) - this node is a cross-community bridge._
- **What connects `float`, `int`, `ShaderMaterial` to the rest of the system?**
  _226 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `SpaceTimeDB Module Core` be split into smaller, more focused modules?**
  _Cohesion score 0.07721518987341772 - nodes in this community are weakly interconnected._
- **Should `AlephView World State` be split into smaller, more focused modules?**
  _Cohesion score 0.060528559249786874 - nodes in this community are weakly interconnected._
- **Should `Chat System` be split into smaller, more focused modules?**
  _Cohesion score 0.07965860597439545 - nodes in this community are weakly interconnected._