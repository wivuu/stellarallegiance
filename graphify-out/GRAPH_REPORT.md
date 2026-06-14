# Graph Report - wivuullegiance  (2026-06-14)

## Corpus Check
- 62 files · ~64,668 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1123 nodes · 1864 edges · 61 communities (51 shown, 10 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 14 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `5abb5f13`
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
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Dust Field Environment|Dust Field Environment]]
- [[_COMMUNITY_Ship Math Utilities|Ship Math Utilities]]
- [[_COMMUNITY_Team Trail Effects|Team Trail Effects]]
- [[_COMMUNITY_Flight Model Tests|Flight Model Tests]]
- [[_COMMUNITY_Asteroid Normal Baking|Asteroid Normal Baking]]
- [[_COMMUNITY_GLB Export Pipeline|GLB Export Pipeline]]
- [[_COMMUNITY_Sun Visual|Sun Visual]]
- [[_COMMUNITY_Asteroid Generator CLI|Asteroid Generator CLI]]
- [[_COMMUNITY_Starscape Background|Starscape Background]]
- [[_COMMUNITY_Community 26|Community 26]]
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
- [[_COMMUNITY_Community 41|Community 41]]
- [[_COMMUNITY_Community 42|Community 42]]
- [[_COMMUNITY_Community 43|Community 43]]
- [[_COMMUNITY_Community 44|Community 44]]
- [[_COMMUNITY_Community 45|Community 45]]
- [[_COMMUNITY_Community 46|Community 46]]
- [[_COMMUNITY_Community 47|Community 47]]
- [[_COMMUNITY_Community 48|Community 48]]
- [[_COMMUNITY_Community 51|Community 51]]
- [[_COMMUNITY_Community 52|Community 52]]
- [[_COMMUNITY_Community 53|Community 53]]
- [[_COMMUNITY_Community 54|Community 54]]
- [[_COMMUNITY_Community 56|Community 56]]
- [[_COMMUNITY_Community 60|Community 60]]
- [[_COMMUNITY_Community 61|Community 61]]
- [[_COMMUNITY_Community 63|Community 63]]
- [[_COMMUNITY_Community 64|Community 64]]
- [[_COMMUNITY_Community 65|Community 65]]
- [[_COMMUNITY_Community 66|Community 66]]
- [[_COMMUNITY_Community 67|Community 67]]

## God Nodes (most connected - your core abstractions)
1. `WorldRenderer` - 80 edges
2. `Simulation` - 52 edges
3. `GameNetClient` - 46 edges
4. `Simulation` - 39 edges
5. `Chat` - 35 edges
6. `PredictionController` - 31 edges
7. `ShipController` - 29 edges
8. `TargetMarkers` - 28 edges
9. `ClientHub` - 26 edges
10. `ShipSim` - 25 edges

## Surprising Connections (you probably didn't know these)
- `TargetMarkers` --inherits--> `Control`  [EXTRACTED]
  client/scripts/TargetMarkers.cs → client/scripts/Hud.cs
- `TargetMarkers` --references--> `float`  [EXTRACTED]
  client/scripts/TargetMarkers.cs → client/scripts/AlephView.cs
- `Asteroids CI Workflow` --semantically_similar_to--> `Build Godot Client CI Workflow`  [INFERRED] [semantically similar]
  .github/workflows/asteroids.yml → .github/workflows/build-godot-client.yml
- `AlephView` --inherits--> `Node3D`  [EXTRACTED]
  client/scripts/AlephView.cs → client/scripts/ShipModelLoader.cs
- `BaseBeacon` --inherits--> `Node3D`  [EXTRACTED]
  client/scripts/BaseModelLoader.cs → client/scripts/ShipModelLoader.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **CI Pipeline: Build and Upload Game Artifacts** — workflows_asteroids, workflows_build_godot_client, concept_asteroid_gen [INFERRED 0.85]

## Communities (61 total, 10 thin omitted)

### Community 0 - "SpaceTimeDB Module Core"
Cohesion: 0.09
Nodes (13): IAuthenticator, IMatchmaker, IMatchResultSink, InMemoryPlayerDirectory, IPlayerDirectory, LoggingMatchResultSink, OpenAuthenticator, ReadyUpMatchmaker (+5 more)

### Community 1 - "AlephView World State"
Cohesion: 0.08
Nodes (15): bool, byte, ConnectionManager, DefRegistry, Dictionary, double, float, List (+7 more)

### Community 2 - "Chat System"
Cohesion: 0.07
Nodes (23): ChatLine, ChatMessage, bool, Color, ConnectionManager, DbConnection, double, EventContext (+15 more)

### Community 3 - "Asteroid Shape Generation"
Cohesion: 0.10
Nodes (34): _base_params(), _bumps(), _colour_params(), _crystal(), _cull(), _detail_params(), eval_base(), _eval_shape() (+26 more)

### Community 4 - "Connection Overlay UI"
Cohesion: 0.05
Nodes (29): Button, CanvasLayer, Button, Color, ConnectionManager, double, Label, Button (+21 more)

### Community 5 - "PigAI NPC Behavior"
Cohesion: 0.35
Nodes (4): Base, Base, Color, EventContext

### Community 6 - "Client-Side Prediction"
Cohesion: 0.07
Nodes (25): bool, DefRegistry, double, EngineGlow, float, int, List, Quaternion (+17 more)

### Community 7 - "Shared Flight Model"
Cohesion: 0.13
Nodes (18): Conjugate(), Create(), Cross(), byte, float, Quat, ShipInputState, ShipState (+10 more)

### Community 8 - "Ship Controller Input"
Cohesion: 0.09
Nodes (17): bool, ConnectionManager, double, float, GameNetClient, InputEvent, int, ShipClass (+9 more)

### Community 9 - "Engine Glow Effects"
Cohesion: 0.11
Nodes (15): Color, CurveTexture, float, GradientTexture1D, GradientTexture2D, OmniLight3D, StandardMaterial3D, Texture2D (+7 more)

### Community 10 - "Lobby UI"
Cohesion: 0.12
Nodes (16): Action, Action, Button, Color, ConnectionManager, DbConnection, GameNetClient, HBoxContainer (+8 more)

### Community 11 - "Hit Flash Effect"
Cohesion: 0.23
Nodes (5): double, float, Projectile, Vector3, ProjectileView

### Community 12 - "Explosion Effect"
Cohesion: 0.13
Nodes (11): byte, Color, CurveTexture, double, float, GradientTexture1D, GradientTexture2D, MeshInstance3D (+3 more)

### Community 13 - "Target Markers HUD"
Cohesion: 0.08
Nodes (26): Basis, Camera3D, Basis, Vector3, WorldRenderer, bool, Camera3D, Color (+18 more)

### Community 14 - "Remote Ship Networked"
Cohesion: 0.15
Nodes (11): bool, DefRegistry, double, EngineGlow, float, int, List, Quaternion (+3 more)

### Community 15 - "Project Build Config"
Cohesion: 0.12
Nodes (12): wivuullegiance, net8.0, SpacetimeDB.ClientSDK (2.3.0), Godot.NET.Sdk/4.6.3, Microsoft.NET.Sdk.Web, net8.0, net8.0, Microsoft.NET.Sdk (+4 more)

### Community 16 - "Community 16"
Cohesion: 0.09
Nodes (16): BaseDef, BaseDef, byte, ConnectionManager, DbConnection, Dictionary, HardpointDef, IReadOnlyList (+8 more)

### Community 17 - "Dust Field Environment"
Cohesion: 0.20
Nodes (7): float, GradientTexture2D, StandardMaterial3D, Texture2D, WorldRenderer, GpuParticles3D, DustField

### Community 18 - "Ship Math Utilities"
Cohesion: 0.15
Nodes (7): DbConnection, GameNetClient, Identity, string, Exception, ConnectionManager, ServerInputOverlay

### Community 19 - "Team Trail Effects"
Cohesion: 0.18
Nodes (8): Color, double, float, int, List, MeshInstance3D, ImmediateMesh, TeamTrail

### Community 20 - "Flight Model Tests"
Cohesion: 0.21
Nodes (7): Program, float, int, Quat, ShipInputState, ShipState, ShipStats

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
Nodes (15): ArrayMesh, float, int, ShaderMaterial, ShaderMaterial, string, uint, Environment (+7 more)

### Community 26 - "Community 26"
Cohesion: 0.19
Nodes (15): HardpointKind, BaseDef, bool, byte, float, List, ShipStats, string (+7 more)

### Community 27 - "DotNet Tools Config"
Cohesion: 0.25
Nodes (7): commands, rollForward, version, isRoot, tools, csharpier, version

### Community 28 - "Local Publishing Scripts"
Cohesion: 0.12
Nodes (10): LobbyEntry, Lobby, Rec, bool, byte, Dictionary, Func, List (+2 more)

### Community 29 - "CI Workflow Pipeline"
Cohesion: 0.50
Nodes (4): Asteroid Mesh/Normal-Map Generator Tool, Godot Client Export (Multi-Platform), Asteroids CI Workflow, Build Godot Client CI Workflow

### Community 30 - "Client Export Scripts"
Cohesion: 0.50
Nodes (3): DOTNET_CLI_USE_MSBUILD_SERVER, MSBUILDDISABLENODEREUSE, export-clients.sh script

### Community 31 - "SpaceTimeDB SDK Config"
Cohesion: 0.29
Nodes (13): bool, byte, float, ShipClass, string, uint, ulong, Aleph (+5 more)

### Community 32 - "SpaceTimeDB Module Config"
Cohesion: 0.47
Nodes (3): EventContext, Projectile, Projectile

### Community 41 - "Community 41"
Cohesion: 0.22
Nodes (6): double, float, GradientTexture2D, MeshInstance3D, StandardMaterial3D, HitFlash

### Community 42 - "Community 42"
Cohesion: 0.07
Nodes (23): IEnumerable, object, Queue, Random, Action, bool, byte, Dictionary (+15 more)

### Community 43 - "Community 43"
Cohesion: 0.17
Nodes (12): Basis, DefRegistry, HardpointDef, List, Marker3D, Material, MeshInstance3D, ShipClass (+4 more)

### Community 44 - "Community 44"
Cohesion: 0.08
Nodes (28): CancellationToken, Channel, ConcurrentDictionary, IAuthenticator, IMatchmaker, IPlayerDirectory, Lobby, long (+20 more)

### Community 45 - "Community 45"
Cohesion: 0.11
Nodes (15): BaseBeacon, Basis, Color, DefRegistry, float, GradientTexture2D, HardpointDef, Marker3D (+7 more)

### Community 46 - "Community 46"
Cohesion: 0.12
Nodes (22): DetRng, Rock, rx, ry, rz, DetRng, Dictionary, float (+14 more)

### Community 47 - "Community 47"
Cohesion: 0.13
Nodes (10): BinaryWriter, List<HardpointDef>, Protocol, byte, int, IReadOnlyList<LobbyEntry>, ShipSim, Vec3 (+2 more)

### Community 51 - "Community 51"
Cohesion: 0.06
Nodes (24): BinaryReader, CancellationTokenSource, bool, byte, CancellationToken, Channel, ClientWebSocket, ConnectionManager (+16 more)

### Community 53 - "Community 53"
Cohesion: 0.11
Nodes (18): Gate, PigPlan, PigState, bool, byte, Dictionary, float, int (+10 more)

### Community 56 - "Community 56"
Cohesion: 0.27
Nodes (6): Asteroid, AuthoredRadius, Axis, Asteroid, Mesh, Speed

### Community 60 - "Community 60"
Cohesion: 0.29
Nodes (4): int, uint, HmacSha256, Sha256

### Community 63 - "Community 63"
Cohesion: 0.23
Nodes (5): MeshInstance3D, Node, ShipClass, Vector3, MeshInstance3D

### Community 65 - "Community 65"
Cohesion: 0.21
Nodes (5): Node3D, Ship, StandardMaterial3D, Node3D, BaseHealthBar

### Community 67 - "Community 67"
Cohesion: 0.40
Nodes (3): DbConnection, DbConnection, Match

## Knowledge Gaps
- **329 isolated node(s):** `float`, `int`, `ShaderMaterial`, `Material`, `MeshInstance3D` (+324 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **10 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `TargetMarkers` connect `Target Markers HUD` to `Starscape Background`, `Connection Overlay UI`?**
  _High betweenness centrality (0.135) - this node is a cross-community bridge._
- **Why does `WorldRenderer` connect `AlephView World State` to `SpaceTimeDB Module Config`, `Community 65`, `Community 66`, `Community 67`, `PigAI NPC Behavior`, `Target Markers HUD`, `Community 56`, `Community 63`?**
  _High betweenness centrality (0.132) - this node is a cross-community bridge._
- **Why does `Node3D` connect `Community 65` to `AlephView World State`, `PigAI NPC Behavior`, `Client-Side Prediction`, `Engine Glow Effects`, `Community 41`, `Hit Flash Effect`, `Explosion Effect`, `Community 45`, `Remote Ship Networked`, `Community 43`, `Dust Field Environment`, `Team Trail Effects`, `Starscape Background`?**
  _High betweenness centrality (0.113) - this node is a cross-community bridge._
- **What connects `float`, `int`, `ShaderMaterial` to the rest of the system?**
  _349 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `SpaceTimeDB Module Core` be split into smaller, more focused modules?**
  _Cohesion score 0.09116809116809117 - nodes in this community are weakly interconnected._
- **Should `AlephView World State` be split into smaller, more focused modules?**
  _Cohesion score 0.08262108262108261 - nodes in this community are weakly interconnected._
- **Should `Chat System` be split into smaller, more focused modules?**
  _Cohesion score 0.07200929152148665 - nodes in this community are weakly interconnected._