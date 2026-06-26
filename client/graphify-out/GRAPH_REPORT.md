# Graph Report - client  (2026-06-25)

## Corpus Check
- 39 files · ~49,002 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 831 nodes · 1301 edges · 44 communities (41 shown, 3 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `d4ede195`
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
- [[_COMMUNITY_Community 36|Community 36]]
- [[_COMMUNITY_Community 37|Community 37]]
- [[_COMMUNITY_Community 38|Community 38]]
- [[_COMMUNITY_Community 39|Community 39]]
- [[_COMMUNITY_Community 40|Community 40]]
- [[_COMMUNITY_Community 41|Community 41]]
- [[_COMMUNITY_Community 42|Community 42]]
- [[_COMMUNITY_Community 43|Community 43]]

## God Nodes (most connected - your core abstractions)
1. `WorldRenderer` - 83 edges
2. `GameNetClient` - 47 edges
3. `Chat` - 35 edges
4. `SectorOverview` - 34 edges
5. `PredictionController` - 33 edges
6. `TargetMarkers` - 32 edges
7. `EngineGlow` - 30 edges
8. `ShipController` - 28 edges
9. `ConnectionManager` - 22 edges
10. `ServerInputOverlay` - 22 edges

## Surprising Connections (you probably didn't know these)
- `Chat` --inherits--> `Control`  [EXTRACTED]
  scripts/Chat.cs → scripts/Hud.cs
- `Hud` --inherits--> `CanvasLayer`  [EXTRACTED]
  scripts/Hud.cs → scripts/SectorOverview.cs
- `LensFlare` --inherits--> `Control`  [EXTRACTED]
  scripts/LensFlare.cs → scripts/Hud.cs
- `Lobby` --inherits--> `Control`  [EXTRACTED]
  scripts/Lobby.cs → scripts/Hud.cs
- `ServerInputOverlay` --inherits--> `Control`  [EXTRACTED]
  scripts/ServerInputOverlay.cs → scripts/Hud.cs

## Import Cycles
- None detected.

## Communities (44 total, 3 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.09
Nodes (15): Dictionary, bool, byte, ConnectionManager, DefRegistry, Dictionary, double, float (+7 more)

### Community 1 - "Community 1"
Cohesion: 0.07
Nodes (23): ChatLine, ChatMessage, LineEdit, MouseModeEnum, Chat, bool, Color, ConnectionManager (+15 more)

### Community 2 - "Community 2"
Cohesion: 0.09
Nodes (21): PredictedShot, bool, DefRegistry, double, EngineGlow, float, int, IReadOnlyList (+13 more)

### Community 3 - "Community 3"
Cohesion: 0.10
Nodes (15): Dictionary<uint, double>, Key, bool, ConnectionManager, double, float, GameNetClient, InputEvent (+7 more)

### Community 4 - "Community 4"
Cohesion: 0.12
Nodes (15): Kind, PredictionController, bool, Camera3D, Color, float, IReadOnlyList, List (+7 more)

### Community 5 - "Community 5"
Cohesion: 0.08
Nodes (20): AudioStreamPlayer3D, List<GpuParticles3D>, List<Node3D>, List<ParticleProcessMaterial>, List<ShaderMaterial>, List<StandardMaterial3D>, OmniLight3D, Color (+12 more)

### Community 6 - "Community 6"
Cohesion: 0.12
Nodes (16): Action, LobbyPlayer, MatchPhase, Button, Color, ConnectionManager, DbConnection, GameNetClient (+8 more)

### Community 7 - "Community 7"
Cohesion: 0.12
Nodes (13): CurveTexture, GradientTexture1D, byte, Color, CurveTexture, double, float, GradientTexture1D (+5 more)

### Community 8 - "Community 8"
Cohesion: 0.07
Nodes (23): ConnectionOverlay, Button, Color, ConnectionManager, double, Label, Control, Color (+15 more)

### Community 9 - "Community 9"
Cohesion: 0.13
Nodes (13): bool, DefRegistry, double, EngineGlow, float, int, Label3D, List (+5 more)

### Community 10 - "Community 10"
Cohesion: 0.21
Nodes (7): ArrayMesh, AlephView, float, int, Shader, ShaderMaterial, Shader

### Community 11 - "Community 11"
Cohesion: 0.13
Nodes (13): Color, ConnectionManager, HttpClient, Label, LineEdit, List, RichTextLabel, string (+5 more)

### Community 12 - "Community 12"
Cohesion: 0.13
Nodes (8): Exception, ConnectionManager, DbConnection, GameNetClient, Identity, string, Node, ServerInputOverlay

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
Cohesion: 0.22
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
Cohesion: 0.17
Nodes (8): Basis, Camera3D, CameraRig, Basis, float, InputEvent, Vector3, WorldRenderer

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
Nodes (17): BaseBeacon, BaseBeacon, BaseModelLoader, Basis, bool, Color, DefRegistry, float (+9 more)

### Community 26 - "Community 26"
Cohesion: 0.17
Nodes (3): Node3D, Ship, ShipClass

### Community 27 - "Community 27"
Cohesion: 0.19
Nodes (11): Basis, DefRegistry, HardpointDef, List, Marker3D, Material, MeshInstance3D, Node3D (+3 more)

### Community 28 - "Community 28"
Cohesion: 0.12
Nodes (14): BaseDef, hp, Node, byte, Dictionary, HardpointDef, IReadOnlyList, List (+6 more)

### Community 29 - "Community 29"
Cohesion: 0.29
Nodes (13): Aleph, Asteroid, Base, bool, byte, float, ShipClass, string (+5 more)

### Community 30 - "Community 30"
Cohesion: 0.26
Nodes (3): Base, BaseHealthBar, Color

### Community 31 - "Community 31"
Cohesion: 0.23
Nodes (6): Aabb, HashSet<string>, List<(string Name, Transform3D Local)>, Node, Node3D, GlbLoader

### Community 32 - "Community 32"
Cohesion: 0.21
Nodes (5): Asteroid, AuthoredRadius, Axis, Mesh, Speed

### Community 33 - "Community 33"
Cohesion: 0.20
Nodes (6): Frac, Pos, IReadOnlyList, LobbyPlayer, RemoteShip, Team

### Community 34 - "Community 34"
Cohesion: 0.31
Nodes (3): Aleph, EventContext, Projectile

### Community 36 - "Community 36"
Cohesion: 0.14
Nodes (11): CanvasLayer, bool, Button, ConnectionManager, Label, Player, ShipClass, ShipController (+3 more)

### Community 37 - "Community 37"
Cohesion: 0.33
Nodes (4): ConfigFile, int, string, UserPrefs

### Community 38 - "Community 38"
Cohesion: 0.40
Nodes (3): Color, Label3D, Nameplate

### Community 39 - "Community 39"
Cohesion: 0.13
Nodes (10): AudioStream, AudioStreamPlayer, Dictionary, HashSet, int, List, string, Vector3 (+2 more)

### Community 40 - "Community 40"
Cohesion: 0.19
Nodes (9): Gradient, Camera3D, Color, float, GradientTexture2D, int, Texture2D, Vector2 (+1 more)

### Community 41 - "Community 41"
Cohesion: 0.26
Nodes (6): HttpClient, string, Task, UpdateInfo, UpdateChecker, Version

## Knowledge Gaps
- **270 isolated node(s):** `float`, `int`, `ShaderMaterial`, `bool`, `MeshInstance3D` (+265 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **3 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `WorldRenderer` connect `Community 0` to `Community 32`, `Community 33`, `Community 34`, `Community 35`, `Community 42`, `Community 26`, `Community 30`?**
  _High betweenness centrality (0.349) - this node is a cross-community bridge._
- **Why does `SectorOverview` connect `Community 24` to `Community 26`, `Community 36`?**
  _High betweenness centrality (0.325) - this node is a cross-community bridge._
- **Why does `Control` connect `Community 8` to `Community 1`, `Community 36`, `Community 4`, `Community 6`, `Community 40`, `Community 11`?**
  _High betweenness centrality (0.308) - this node is a cross-community bridge._
- **What connects `float`, `int`, `ShaderMaterial` to the rest of the system?**
  _270 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.08923076923076922 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.07200929152148665 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.08558558558558559 - nodes in this community are weakly interconnected._