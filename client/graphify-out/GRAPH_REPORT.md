# Graph Report - client  (2026-06-10)

## Corpus Check
- 24 files · ~27,852 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 420 nodes · 615 edges · 23 communities
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `11f33d37`
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

## God Nodes (most connected - your core abstractions)
1. `WorldRenderer` - 49 edges
2. `Chat` - 32 edges
3. `PredictionController` - 27 edges
4. `ShipController` - 23 edges
5. `TargetMarkers` - 21 edges
6. `EngineGlow` - 19 edges
7. `ExplosionEffect` - 18 edges
8. `Lobby` - 17 edges
9. `RemoteShip` - 15 edges
10. `TeamTrail` - 12 edges

## Surprising Connections (you probably didn't know these)
- `Chat` --inherits--> `Control`  [EXTRACTED]
  scripts/Chat.cs → scripts/Hud.cs
- `Lobby` --inherits--> `Control`  [EXTRACTED]
  scripts/Lobby.cs → scripts/Hud.cs
- `TargetMarkers` --inherits--> `Control`  [EXTRACTED]
  scripts/TargetMarkers.cs → scripts/Hud.cs
- `ShipController` --inherits--> `Node`  [EXTRACTED]
  scripts/ShipController.cs → scripts/WorldRenderer.cs
- `ConnectionManager` --inherits--> `Node`  [EXTRACTED]
  scripts/ConnectionManager.cs → scripts/WorldRenderer.cs

## Import Cycles
- None detected.

## Communities (23 total, 0 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.06
Nodes (30): Aleph, Asteroid, AuthoredRadius, Base, Dictionary, Match, Mesh, Node3D (+22 more)

### Community 1 - "Community 1"
Cohesion: 0.08
Nodes (20): ChatMessage, LineEdit, MouseModeEnum, Chat, bool, Color, ConnectionManager, DbConnection (+12 more)

### Community 2 - "Community 2"
Cohesion: 0.11
Nodes (15): PredictedShot, double, EngineGlow, float, int, List, Quaternion, Ship (+7 more)

### Community 3 - "Community 3"
Cohesion: 0.11
Nodes (14): Dictionary<uint, double>, Key, bool, ConnectionManager, double, float, InputEvent, int (+6 more)

### Community 4 - "Community 4"
Cohesion: 0.14
Nodes (12): bool, Camera3D, Color, float, IReadOnlyList, List, RemoteShip, Vector2 (+4 more)

### Community 5 - "Community 5"
Cohesion: 0.12
Nodes (14): List<GpuParticles3D>, List<Node3D>, List<ParticleProcessMaterial>, List<StandardMaterial3D>, OmniLight3D, Color, CurveTexture, float (+6 more)

### Community 6 - "Community 6"
Cohesion: 0.14
Nodes (13): Action, MatchPhase, Button, Color, ConnectionManager, DbConnection, HBoxContainer, Identity (+5 more)

### Community 7 - "Community 7"
Cohesion: 0.13
Nodes (11): byte, Color, CurveTexture, double, float, GradientTexture1D, GradientTexture2D, MeshInstance3D (+3 more)

### Community 8 - "Community 8"
Cohesion: 0.13
Nodes (12): ConnectionOverlay, Button, Color, ConnectionManager, double, Label, Control, Color (+4 more)

### Community 9 - "Community 9"
Cohesion: 0.17
Nodes (9): bool, double, EngineGlow, float, int, List, Ship, Vector3 (+1 more)

### Community 10 - "Community 10"
Cohesion: 0.22
Nodes (6): ArrayMesh, AlephView, float, int, ShaderMaterial, Shader

### Community 11 - "Community 11"
Cohesion: 0.16
Nodes (9): CanvasLayer, Button, ConnectionManager, Label, Player, ShipClass, ShipController, WorldRenderer (+1 more)

### Community 12 - "Community 12"
Cohesion: 0.23
Nodes (6): Exception, ConnectionManager, DbConnection, Identity, string, Node

### Community 13 - "Community 13"
Cohesion: 0.20
Nodes (7): GpuParticles3D, float, GradientTexture2D, StandardMaterial3D, Texture2D, WorldRenderer, DustField

### Community 14 - "Community 14"
Cohesion: 0.18
Nodes (8): ImmediateMesh, Color, double, float, int, List, MeshInstance3D, TeamTrail

### Community 15 - "Community 15"
Cohesion: 0.20
Nodes (7): Quat, Quaternion, Ship, ShipState, Vector3, ShipMath, Vec3

### Community 16 - "Community 16"
Cohesion: 0.21
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
Cohesion: 0.25
Nodes (5): Basis, Camera3D, CameraRig, Vector3, WorldRenderer

### Community 21 - "Community 21"
Cohesion: 0.40
Nodes (4): net8.0, SpacetimeDB.ClientSDK (2.3.0), Godot.NET.Sdk/4.6.3, wivuullegiance

## Knowledge Gaps
- **140 isolated node(s):** `float`, `int`, `ShaderMaterial`, `Vector3`, `Basis` (+135 more)
  These have ≤1 connection - possible missing edges or undocumented components.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Node` connect `Community 12` to `Community 0`, `Community 3`?**
  _High betweenness centrality (0.099) - this node is a cross-community bridge._
- **Why does `PredictionController` connect `Community 2` to `Community 0`?**
  _High betweenness centrality (0.082) - this node is a cross-community bridge._
- **What connects `float`, `int`, `ShaderMaterial` to the rest of the system?**
  _140 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.06060606060606061 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.07965860597439545 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.11264367816091954 - nodes in this community are weakly interconnected._
- **Should `Community 3` be split into smaller, more focused modules?**
  _Cohesion score 0.11 - nodes in this community are weakly interconnected._