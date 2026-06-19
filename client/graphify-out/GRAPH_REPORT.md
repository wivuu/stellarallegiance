# Graph Report - client  (2026-06-19)

## Corpus Check
- 33 files · ~42,412 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 735 nodes · 1160 edges · 36 communities (34 shown, 2 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `396cefcf`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 21|Community 21]]
- [[_COMMUNITY_Community 23|Community 23]]
- [[_COMMUNITY_Community 24|Community 24]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 26|Community 26]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]
- [[_COMMUNITY_Community 29|Community 29]]
- [[_COMMUNITY_Community 30|Community 30]]
- [[_COMMUNITY_Community 31|Community 31]]
- [[_COMMUNITY_Community 32|Community 32]]
- [[_COMMUNITY_Community 33|Community 33]]
- [[_COMMUNITY_Community 34|Community 34]]
- [[_COMMUNITY_Community 35|Community 35]]

## God Nodes (most connected - your core abstractions)
1. `WorldRenderer` - 82 edges
2. `GameNetClient` - 46 edges
3. `Chat` - 35 edges
4. `SectorOverview` - 34 edges
5. `TargetMarkers` - 31 edges
6. `PredictionController` - 30 edges
7. `ShipController` - 28 edges
8. `EngineGlow` - 24 edges
9. `ConnectionManager` - 21 edges
10. `Lobby` - 19 edges

## Surprising Connections (you probably didn't know these)
- `Chat` --inherits--> `Control`  [EXTRACTED]
  scripts/Chat.cs → scripts/Hud.cs
- `ConnectionManager` --inherits--> `Node`  [EXTRACTED]
  scripts/ConnectionManager.cs → scripts/WorldRenderer.cs
- `Hud` --inherits--> `CanvasLayer`  [EXTRACTED]
  scripts/Hud.cs → scripts/SectorOverview.cs
- `Lobby` --inherits--> `Control`  [EXTRACTED]
  scripts/Lobby.cs → scripts/Hud.cs
- `ServerInputOverlay` --inherits--> `Control`  [EXTRACTED]
  scripts/ServerInputOverlay.cs → scripts/Hud.cs

## Import Cycles
- None detected.

## Communities (36 total, 2 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.08
Nodes (16): Dictionary, Match, bool, byte, ConnectionManager, DbConnection, DefRegistry, Dictionary (+8 more)

### Community 1 - "Community 1"
Cohesion: 0.07
Nodes (23): ChatLine, ChatMessage, LineEdit, MouseModeEnum, Chat, bool, Color, ConnectionManager (+15 more)

### Community 2 - "Community 2"
Cohesion: 0.10
Nodes (18): PredictedShot, bool, DefRegistry, double, EngineGlow, float, int, List (+10 more)

### Community 3 - "Community 3"
Cohesion: 0.10
Nodes (15): Dictionary<uint, double>, Key, bool, ConnectionManager, double, float, GameNetClient, InputEvent (+7 more)

### Community 4 - "Community 4"
Cohesion: 0.12
Nodes (15): Kind, PredictionController, bool, Camera3D, Color, float, IReadOnlyList, List (+7 more)

### Community 5 - "Community 5"
Cohesion: 0.10
Nodes (16): AudioStreamPlayer3D, List<GpuParticles3D>, List<Node3D>, List<ParticleProcessMaterial>, List<StandardMaterial3D>, OmniLight3D, Color, CurveTexture (+8 more)

### Community 6 - "Community 6"
Cohesion: 0.12
Nodes (15): Action, LobbyPlayer, MatchPhase, Button, Color, ConnectionManager, DbConnection, GameNetClient (+7 more)

### Community 7 - "Community 7"
Cohesion: 0.13
Nodes (11): byte, Color, CurveTexture, double, float, GradientTexture1D, GradientTexture2D, MeshInstance3D (+3 more)

### Community 8 - "Community 8"
Cohesion: 0.06
Nodes (25): CanvasLayer, ConnectionOverlay, Button, Color, ConnectionManager, double, Label, bool (+17 more)

### Community 9 - "Community 9"
Cohesion: 0.15
Nodes (11): bool, DefRegistry, double, EngineGlow, float, int, List, Quaternion (+3 more)

### Community 10 - "Community 10"
Cohesion: 0.22
Nodes (6): ArrayMesh, AlephView, float, int, ShaderMaterial, Shader

### Community 11 - "Community 11"
Cohesion: 0.16
Nodes (11): Color, ConnectionManager, HttpClient, Label, LineEdit, List, string, Task (+3 more)

### Community 12 - "Community 12"
Cohesion: 0.14
Nodes (7): Exception, ConnectionManager, DbConnection, GameNetClient, Identity, string, ServerInputOverlay

### Community 13 - "Community 13"
Cohesion: 0.20
Nodes (7): GpuParticles3D, float, GradientTexture2D, StandardMaterial3D, Texture2D, WorldRenderer, DustField

### Community 14 - "Community 14"
Cohesion: 0.14
Nodes (11): ImmediateMesh, bool, Color, double, float, ImmediateMesh, int, List (+3 more)

### Community 15 - "Community 15"
Cohesion: 0.20
Nodes (7): Quat, Quaternion, Ship, ShipState, Vector3, ShipMath, Vec3

### Community 16 - "Community 16"
Cohesion: 0.23
Nodes (5): double, float, Projectile, Vector3, ProjectileView

### Community 17 - "Community 17"
Cohesion: 0.22
Nodes (6): MeshInstance3D, Camera3D, float, StandardMaterial3D, Vector3, Sun

### Community 18 - "Community 18"
Cohesion: 0.22
Nodes (6): double, float, GradientTexture2D, MeshInstance3D, StandardMaterial3D, HitFlash

### Community 19 - "Community 19"
Cohesion: 0.28
Nodes (5): Environment, ShaderMaterial, string, uint, Starscape

### Community 20 - "Community 20"
Cohesion: 0.22
Nodes (6): Basis, Camera3D, CameraRig, Basis, Vector3, WorldRenderer

### Community 21 - "Community 21"
Cohesion: 0.33
Nodes (5): net8.0, SIPSorcery (10.0.9), SpacetimeDB.ClientSDK (2.3.0), Godot.NET.Sdk/4.6.3, wivuullegiance

### Community 23 - "Community 23"
Cohesion: 0.06
Nodes (24): BinaryReader, CancellationToken, CancellationTokenSource, Channel, ClientWebSocket, ConcurrentQueue, HashSet, IceServerDto (+16 more)

### Community 24 - "Community 24"
Cohesion: 0.10
Nodes (15): List<Vector3>, Minimap, bool, Camera3D, Color, float, ImmediateMesh, InputEvent (+7 more)

### Community 25 - "Community 25"
Cohesion: 0.11
Nodes (16): BaseBeacon, BaseBeacon, BaseModelLoader, Basis, Color, DefRegistry, float, GradientTexture2D (+8 more)

### Community 27 - "Community 27"
Cohesion: 0.18
Nodes (11): Basis, DefRegistry, HardpointDef, List, Marker3D, Material, MeshInstance3D, Node3D (+3 more)

### Community 28 - "Community 28"
Cohesion: 0.06
Nodes (22): AudioStream, AudioStreamPlayer, BaseDef, Node, byte, Dictionary, HardpointDef, IReadOnlyList (+14 more)

### Community 29 - "Community 29"
Cohesion: 0.29
Nodes (13): Aleph, Asteroid, Base, bool, byte, float, ShipClass, string (+5 more)

### Community 30 - "Community 30"
Cohesion: 0.21
Nodes (4): Base, Color, EventContext, Projectile

### Community 31 - "Community 31"
Cohesion: 0.23
Nodes (6): Aabb, HashSet<string>, List<(string Name, Transform3D Local)>, Node, Node3D, GlbLoader

### Community 32 - "Community 32"
Cohesion: 0.21
Nodes (5): Asteroid, AuthoredRadius, Axis, Mesh, Speed

### Community 33 - "Community 33"
Cohesion: 0.16
Nodes (6): Frac, Pos, IReadOnlyList, RemoteShip, Vector3, Team

### Community 35 - "Community 35"
Cohesion: 0.29
Nodes (4): BaseHealthBar, MeshInstance3D, Node, StandardMaterial3D

## Knowledge Gaps
- **238 isolated node(s):** `float`, `int`, `ShaderMaterial`, `MeshInstance3D`, `HardpointDef` (+233 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **2 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `WorldRenderer` connect `Community 0` to `Community 32`, `Community 33`, `Community 34`, `Community 35`, `Community 26`, `Community 30`?**
  _High betweenness centrality (0.384) - this node is a cross-community bridge._
- **Why does `SectorOverview` connect `Community 24` to `Community 8`, `Community 34`?**
  _High betweenness centrality (0.328) - this node is a cross-community bridge._
- **Why does `Control` connect `Community 8` to `Community 1`, `Community 11`, `Community 4`, `Community 6`?**
  _High betweenness centrality (0.286) - this node is a cross-community bridge._
- **What connects `float`, `int`, `ShaderMaterial` to the rest of the system?**
  _238 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.082010582010582 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.07200929152148665 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.10037878787878787 - nodes in this community are weakly interconnected._