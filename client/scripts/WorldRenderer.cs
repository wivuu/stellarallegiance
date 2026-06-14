using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Maps DB rows -> scene nodes. For T2 only the static world (bases + asteroids)
// is rendered; ships/projectiles arrive in later tasks. The client never
// mutates state here — it only mirrors whatever the subscription delivers.
public partial class WorldRenderer : Node3D
{
	// Every base is this single base type this phase (mirror of the module's
	// DefaultBaseTypeId); the BaseDef supplies radius/health/hardpoints.
	private const byte DefaultBaseTypeId = 0;

	// Floating damage bar above each base. BaseMaxHealth mirrors the module's win-condition
	// hull (Lib.cs BaseMaxHealth) so the bar can show a 0..1 fraction; keep the two in sync.
	private const float BaseMaxHealth = 2000f;
	private const float BaseHealthBarWidth = 110f;
	private const float BaseHealthBarHeight = 10f;

	// A pod removed while a friendly non-pod ship is in (roughly) hull contact was RESCUED
	// — picked up by a teammate/drone — not destroyed, so it should simply vanish without a
	// blast (the server's rescue pass deletes the row exactly like a kill does, so the row
	// alone can't tell them apart; we mirror its rule client-side). The threshold is looser
	// than the server's tight RescueRadius (6 units) to absorb the gap between the rescuer's
	// rendered position (predicted for the local ship, interpolated for remotes) and the pod's.
	private const float RescuePickupDist = 12f;

	private Node3D _bases = null!;
	private Node3D _asteroids = null!;
	private Node3D _ships = null!;
	private Node3D _projectiles = null!;
	private Node3D _alephs = null!;
	private Node3D _effects = null!;   // transient FX (explosions, hit flashes); self-freeing

	private readonly Dictionary<ulong, Node3D> _baseNodes = new();

	// Floating damage bar per base, keyed by BaseId. Hidden at full health; it appears,
	// shrinks (left-anchored), and reddens as the base is hit. The parts are children of
	// the base node so they cull/move with it; the root is screen-aligned each frame in
	// _Process (manual billboard) so the bar always faces the camera.
	private readonly Dictionary<ulong, BaseHealthBar> _baseHealthBars = new();

	private sealed class BaseHealthBar
	{
		public Node3D Root = null!;            // screen-aligned anchor above the base
		public MeshInstance3D Fill = null!;    // colored fill, scaled/offset by the health fraction
		public StandardMaterial3D FillMat = null!;
	}
	private readonly Dictionary<ulong, Node3D> _asteroidNodes = new();
	// Purely cosmetic lazy tumble: each rock spins slowly about a fixed pseudo-random axis,
	// derived once from its id (stable across frames; the sim treats rocks as static spheres).
	// Applied each frame in _Process; entries mirror _asteroidNodes' lifetime.
	private readonly Dictionary<ulong, (Node3D Node, Vector3 Axis, float Speed)> _asteroidSpins = new();
	private readonly Dictionary<ulong, Node3D> _shipNodes = new();
	private readonly Dictionary<ulong, Node3D> _alephNodes = new();

	// Static-geometry caches for the bolt-TTL clip (replaces the old STDB table scans). Filled
	// once from the Welcome frame; each entry is (sector-local position, collision radius, sector).
	private readonly List<(Vector3 Pos, float Radius, uint Sector)> _asteroidClip = new();
	private readonly List<(Vector3 Pos, uint Sector)> _baseClip = new();

	// Map data for the Minimap (formerly read straight from STDB tables). Filled from Welcome.
	private readonly List<(uint Sector, uint Dest)> _alephLinks = new();
	private readonly List<(uint Sector, byte Team)> _baseTeams = new();
	public IReadOnlyCollection<Sector> MapSectors => _sectors.Values;
	public IReadOnlyList<(uint Sector, uint Dest)> MapAlephLinks => _alephLinks;
	public IReadOnlyList<(uint Sector, byte Team)> MapBaseTeams => _baseTeams;
	public string SectorName(uint id) => _sectors.TryGetValue(id, out var s) ? s.Name : "";

	// Every live bolt, all client-synthesized (no Projectile rows exist): the local ship's
	// from fire prediction, remote ships' from LastFireTick advancing on their row. Culled
	// on TTL expiry (_Process) or on visually striking a ship (CheckBoltImpacts).
	private readonly List<ProjectileView> _bolts = new();

	// Mirror of the module's AsteroidCollisionScale (Lib.cs): the fraction of a rock's
	// circumscribing radius the sim treats as solid. Keep in sync — used to clip a bolt's
	// TTL where the SERVER's analytic solve would have stopped it on a rock.
	private const float AsteroidCollisionScale = 0.82f;

	// Sector partitioning. The world is split into sectors (see module Sector/Aleph
	// tables); the client subscribes to everything but only SHOWS objects in the
	// player's current sector, toggled by node visibility (each node stashes its
	// sector id in metadata). _localSector follows the local ship as it warps; it
	// defaults to the home/battlefield sector so the pre-spawn overview shows it.
	private const uint HomeSector = 0;
	private uint _localSector = HomeSector;
	private readonly Dictionary<uint, Sector> _sectors = new();

	// Local sector boundary, read by the HUD for the out-of-bounds warning. Radius 0
	// (sector not yet known) disables the warning.
	public uint LocalSector => _localSector;

	// Number of ships currently in the local player's sector: every remote ship node tagged
	// with this sector (each carries a "sector" meta, see SetNodeSector) plus the local ship
	// itself (which is always in _localSector while flying; it lives in LocalShip, not _shipNodes).
	public int ShipsInLocalSector()
	{
		int n = LocalShip != null ? 1 : 0;
		foreach (var node in _shipNodes.Values)
			if (node.HasMeta("sector") && (int)node.GetMeta("sector") == (int)_localSector)
				n++;
		return n;
	}

	public float LocalSectorRadius => _sectors.TryGetValue(_localSector, out var s) ? s.Radius : 0f;
	public Vector3 LocalSectorCenter =>
		_sectors.TryGetValue(_localSector, out var s) ? new Vector3(s.CenterX, s.CenterY, s.CenterZ) : Vector3.Zero;

	private byte? _localTeam;

	// Scratch reused by EnemyShips() so the per-frame marker pass allocates nothing.
	private readonly List<RemoteShip> _enemyScratch = new();

	// Client-side hit-spark tuning. A bolt sparks when its swept path this frame passes within
	// VisualHitRadius of a ship's rendered centre, but not until it has travelled MuzzleClearance
	// from its spawn — so a shot never sparks on the ship that fired it. Team-agnostic by design
	// (friendly fire sparks too). Tune to taste against the ship silhouette size.
	private const float VisualHitRadius = 5f;
	private const float MuzzleClearance = 10f;

	private StandardMaterial3D _asteroidMat = null!;
	private StandardMaterial3D _team0Mat = null!;
	private StandardMaterial3D _team1Mat = null!;
	// AI drones (PIGs): keep the team hue for friend/foe, but darker + metallic with a
	// faint emissive rim so they read as menacing drones in-world (HUD highlights them too).
	private StandardMaterial3D _pigTeam0Mat = null!;
	private StandardMaterial3D _pigTeam1Mat = null!;
	private StandardMaterial3D _projectileMat = null!;
	// Shared dark backdrop for the base damage bars; each bar gets its own fill material so
	// its colour can ramp green->red independently.
	private StandardMaterial3D _hpBarBgMat = null!;

	private ShipController? _ship;   // sibling; lazily resolved for the live latency readout
	private Starscape? _starscape;   // sibling; repaints the backdrop as the local sector changes
	private DefRegistry _defs = null!;   // sibling; runtime ship/weapon/base defs the local ship predicts from

	// Enemy-shot masking lead (see ProjectileView). -1 = auto (derive from measured
	// one-way latency); >= 0 = a fixed override in ms, pinned via STDB_SHOT_MASK_MS for
	// playtest tuning. Parsed once in _Ready.
	private float _shotMaskMs = -1f;

	// The local player's predicted ship, or null when not flying. Read by
	// ShipController (drives prediction), CameraRig (chase target), and Hud.
	public PredictionController? LocalShip { get; private set; }

	// Death-cam: on local death the chase camera holds on the spot the ship died for a
	// beat (DeathCamSec) so the player watches their own blast up close, THEN the view
	// pulls back to the home overview. From the far overview the ~15u blast is an
	// invisible speck, so without this the player never sees their own explosion. The
	// home-overview reset is deferred to _Process so the death sector's scene — and the
	// blast — stay visible through the hold. CameraRig reads DeathCamActive/Transform.
	private const double DeathCamSec = 1.2;
	private double _deathCamUntil = -1.0;
	private bool _pendingHomeReset;
	public bool DeathCamActive => Time.GetTicksMsec() / 1000.0 < _deathCamUntil;
	public Transform3D DeathCamShipTransform { get; private set; }

	// Latest authoritative sim tick (Match.Tick). ShipController slaves its
	// prediction clock to this so client/server ticks index the same integration.
	public uint ServerTick { get; private set; }

	// Match phase + winning team (T9). Read by Hud to show the match-end banner.
	public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;
	public byte? Winner { get; private set; }

	// The local player's team, set when their ship spawns (null until then). Read by
	// TargetMarkers to tell friend from foe.
	public byte? LocalTeam => _localTeam;

	// Live enemy ship nodes (team != local team). Returns a shared scratch list — read
	// it immediately, don't retain it. Empty until the local team is known.
	public IReadOnlyList<RemoteShip> EnemyShips()
	{
		_enemyScratch.Clear();
		if (_localTeam is byte lt)
		{
			foreach (var node in _shipNodes.Values)
				// Exclude enemy pods: they're harmless and shouldn't draw a marker or be
				// Tab-targetable (let a downed opponent float home unmolested).
				if (node is RemoteShip rs && rs.Team != lt && !rs.IsPod && rs.Visible)
					_enemyScratch.Add(rs);
		}
		return _enemyScratch;
	}

	public override void _Ready()
	{
		_bases = new Node3D { Name = "Bases" };
		_asteroids = new Node3D { Name = "Asteroids" };
		_ships = new Node3D { Name = "Ships" };
		_projectiles = new Node3D { Name = "Projectiles" };
		_alephs = new Node3D { Name = "Alephs" };
		_effects = new Node3D { Name = "Effects" };
		AddChild(_bases);
		AddChild(_asteroids);
		AddChild(_ships);
		AddChild(_projectiles);
		AddChild(_alephs);
		AddChild(_effects);

		_asteroidMat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.42f, 0.38f) };
		_team0Mat = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.5f, 0.95f) };
		_team1Mat = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.3f, 0.25f) };
		_pigTeam0Mat = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.12f, 0.22f, 0.4f),
			Metallic = 0.8f, Roughness = 0.35f,
			EmissionEnabled = true, Emission = new Color(0.2f, 0.45f, 0.85f), EmissionEnergyMultiplier = 1.0f,
		};
		_pigTeam1Mat = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.4f, 0.14f, 0.12f),
			Metallic = 0.8f, Roughness = 0.35f,
			EmissionEnabled = true, Emission = new Color(0.85f, 0.25f, 0.2f), EmissionEnergyMultiplier = 1.0f,
		};
		// Bright unshaded tracers so shots read clearly against the dark sector.
		// HDR emission (energy > 1) pushes them past the glow threshold so they bloom.
		_projectileMat = new StandardMaterial3D
		{
			AlbedoColor = new Color(1f, 0.9f, 0.4f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			EmissionEnabled = true,
			Emission = new Color(1f, 0.85f, 0.35f),
			EmissionEnergyMultiplier = 2.5f,
		};

		_hpBarBgMat = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.03f, 0.03f, 0.04f, 0.75f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		};

		_defs = GetNode<DefRegistry>("../DefRegistry");
		_starscape = GetNodeOrNull<Starscape>("../Starscape");

		if (float.TryParse(OS.GetEnvironment("SHOT_MASK_MS"), out var ms) && ms >= 0f)
			_shotMaskMs = ms;
	}

	// ---- Native sim-server feed --------------------------------------------
	// The standalone sim server is the sole authority. The Net* entry points below are driven
	// by GameNetClient as it decodes the server's frames: the static world from Welcome, ship
	// state from snapshots, base health from MsgBases. There is no other source.

	// Per-snapshot match clock + phase from the sim server. The server hosts the lobby, so the
	// phase cycles Lobby -> Active -> Ended -> Lobby; winner 255 = none.
	public void NetSetMatch(uint tick, byte phase, byte winner)
	{
		ServerTick = tick;
		Phase = (MatchPhase)phase;
		Winner = winner == 255 ? (byte?)null : winner;
	}

	// Streamed base health (MsgBases). Bases are static nodes placed by the Welcome frame;
	// this just drives the floating damage bar the same way the STDB OnBaseUpdate path does.
	public void NetUpdateBaseHealth(ulong baseId, float health)
	{
		if (_baseHealthBars.TryGetValue(baseId, out var bar))
			UpdateBaseHealthBar(bar, health);
	}

	public void NetInsertShip(Ship row, bool local) => InsertShip(row, local);
	public void NetUpdateShip(Ship oldRow, Ship newRow) => UpdateShip(oldRow, newRow);
	public void NetDeleteShip(Ship row) => DeleteShip(row);

	// Static world from the Welcome frame, feeding the same bodies the STDB path uses.
	public void NetAddSector(Sector row) { _sectors[row.SectorId] = row; }
	public void NetAddBase(Base row) => InsertBase(row);
	public void NetAddAsteroid(Asteroid row) => InsertAsteroid(row);
	public void NetAddAleph(Aleph row) => InsertAleph(row);

	// ---- Sector visibility ---------------------------------------------
	// Each world node stashes its sector id in metadata; only nodes in the local
	// sector are shown. Stored as int (Godot Variant) and compared to _localSector.

	private void SetNodeSector(Node3D n, uint sector)
	{
		n.SetMeta("sector", (int)sector);
		n.Visible = sector == _localSector;
	}

	// Re-evaluate every world node's visibility against the current local sector —
	// called when the local ship warps to a new sector.
	private void RefreshSectorVisibility()
	{
		foreach (var group in new[] { _bases, _asteroids, _ships, _projectiles, _alephs, _effects })
			foreach (var child in group.GetChildren())
				if (child is Node3D n && n.HasMeta("sector"))
					n.Visible = (int)n.GetMeta("sector") == (int)_localSector;
	}

	// Drop a transient, self-freeing effect into the world at a sector-local position. Tagged
	// with its sector so it's hidden if the local view is elsewhere (effects are brief, so a
	// warp mid-effect simply hides it).
	private void SpawnEffect(Node3D fx, Vector3 pos, uint sector)
	{
		_effects.AddChild(fx);
		fx.Position = pos;
		SetNodeSector(fx, sector);
	}

	// ---- Aleph (warp funnel) -------------------------------------------

	private void InsertAleph(Aleph row)
	{
		if (_alephNodes.ContainsKey(row.AlephId))
			return;
		var pos = new Vector3(row.PosX, row.PosY, row.PosZ);
		var av = new AlephView
		{
			Name = $"Aleph_{row.AlephId}",
			Position = pos,
		};
		_alephs.AddChild(av);
		_alephNodes[row.AlephId] = av;
		_alephLinks.Add((row.SectorId, row.DestSectorId));

		// Orient the funnel so its mouth (+Y local axis) faces the sector center.
		var center = _sectors.TryGetValue(row.SectorId, out var sec)
			? new Vector3(sec.CenterX, sec.CenterY, sec.CenterZ)
			: Vector3.Zero;
		var toCenter = (center - pos).Normalized();
		if (toCenter.LengthSquared() > 0.001f)
		{
			// Quaternion rotating default up (+Y) to the desired direction.
			av.Quaternion = new Quaternion(Vector3.Up, toCenter);
		}

		SetNodeSector(av, row.SectorId);
	}

	// ---- Base -----------------------------------------------------------

	private void InsertBase(Base row)
	{
		if (_baseNodes.ContainsKey(row.BaseId))
			return;

		// Procedural sphere + hardpoint markers + blinking nav beacons, all sized/placed
		// from the subscribed BaseDef (M5). Every base is BaseTypeId 0 this phase.
		var node = BaseModelLoader.Build(_defs, DefaultBaseTypeId, row.Team, row.Team == 0 ? _team0Mat : _team1Mat);
		node.Name = $"Base_{row.BaseId}";
		node.Position = new Vector3(row.PosX, row.PosY, row.PosZ);
		_bases.AddChild(node);
		_baseNodes[row.BaseId] = node;
		_baseHealthBars[row.BaseId] = CreateBaseHealthBar(node, BaseModelLoader.Radius(_defs, DefaultBaseTypeId));
		UpdateBaseHealthBar(_baseHealthBars[row.BaseId], row.Health);
		_baseClip.Add((new Vector3(row.PosX, row.PosY, row.PosZ), row.SectorId));
		_baseTeams.Add((row.SectorId, row.Team));
		SetNodeSector(node, row.SectorId);
		GD.Print($"[WorldRenderer] Base {row.BaseId} (team {row.Team}) @ ({row.PosX}, {row.PosY}, {row.PosZ})");
	}

	// Build the floating damage bar (background + fill) as children of the base node, anchored
	// just above the sphere. Returns the handle stored per base; the fill is repositioned/recoloured
	// by UpdateBaseHealthBar and the root is screen-aligned each frame in _Process.
	private BaseHealthBar CreateBaseHealthBar(Node3D baseNode, float baseRadius)
	{
		var root = new Node3D { Name = "HealthBar", Position = new Vector3(0f, baseRadius + 22f, 0f) };

		var bg = new MeshInstance3D
		{
			Name = "Bg",
			Mesh = new QuadMesh { Size = new Vector2(BaseHealthBarWidth, BaseHealthBarHeight) },
			MaterialOverride = _hpBarBgMat,
		};

		var fillMat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = HealthColor(1f),
		};
		var fill = new MeshInstance3D
		{
			Name = "Fill",
			// Slightly smaller than the backdrop (a thin border) and nudged forward so it draws on top.
			Mesh = new QuadMesh { Size = new Vector2(BaseHealthBarWidth - 4f, BaseHealthBarHeight - 4f) },
			MaterialOverride = fillMat,
			Position = new Vector3(0f, 0f, 0.1f),
		};

		root.AddChild(bg);
		root.AddChild(fill);
		baseNode.AddChild(root);
		return new BaseHealthBar { Root = root, Fill = fill, FillMat = fillMat };
	}

	// Resize/recolour the fill to a 0..1 health fraction and hide the whole bar at full health.
	// The fill is left-anchored: scaling X by the fraction and shifting its centre left keeps the
	// left edge fixed so it depletes rightward.
	private static void UpdateBaseHealthBar(BaseHealthBar bar, float health)
	{
		float frac = Mathf.Clamp(health / BaseMaxHealth, 0f, 1f);
		float innerWidth = BaseHealthBarWidth - 4f;
		bar.Fill.Scale = new Vector3(frac, 1f, 1f);
		bar.Fill.Position = new Vector3(-innerWidth * 0.5f * (1f - frac), 0f, 0.1f);
		bar.FillMat.AlbedoColor = HealthColor(frac);
		bar.Root.Visible = frac < 0.999f;   // only show once the base has taken a hit
	}

	// Green at full health, through yellow at half, to red when nearly destroyed.
	private static Color HealthColor(float frac) =>
		frac > 0.5f
			? new Color(Mathf.Lerp(0.9f, 0.15f, (frac - 0.5f) * 2f), 0.85f, 0.15f)
			: new Color(0.9f, Mathf.Lerp(0.15f, 0.85f, frac * 2f), 0.15f);

	// ---- Asteroid -------------------------------------------------------

	// Loaded asteroid meshes keyed by variant name (GLB stem). The generated .glb carries
	// its PBR material on the mesh surface, so reusing one Mesh across instances keeps the
	// colour/normal/ORM maps. AuthoredRadius is the mesh's bounding radius at author scale,
	// used to scale each instance to its row's collision Radius. A null Mesh marks a variant
	// that failed to load (e.g. asset missing) so we don't retry and fall back to a sphere.
	private readonly Dictionary<string, (Mesh? Mesh, float AuthoredRadius)> _asteroidMeshes = new();

	// Load (and cache) the mesh + authored radius for a variant, or (null, 0) if unavailable.
	private (Mesh? Mesh, float AuthoredRadius) AsteroidMesh(string variant)
	{
		if (_asteroidMeshes.TryGetValue(variant, out var cached))
			return cached;

		(Mesh? Mesh, float AuthoredRadius) result = (null, 0f);
		var scene = GD.Load<PackedScene>($"res://assets/asteroids/{variant}.glb");
		if (scene?.Instantiate() is Node root)
		{
			if (FindMeshInstance(root) is MeshInstance3D mi && mi.Mesh is Mesh mesh)
			{
				// True bounding radius = farthest vertex from the mesh origin (meshes are
				// authored as radial star-fields centred on the origin). Scaling each instance
				// by row.Radius / authored then makes the collision sphere tightly circumscribe
				// the silhouette. Using the AABB's half-diagonal here instead would overestimate
				// the radius by up to sqrt(3), shrinking the rock well inside its hitbox.
				float authored = MeshBoundingRadius(mesh);
				if (authored > 0.001f)
					result = (mesh, authored);
			}
			root.QueueFree();
		}
		if (result.Mesh is null)
			GD.PushWarning($"[WorldRenderer] asteroid variant '{variant}' unavailable — using sphere fallback");
		_asteroidMeshes[variant] = result;
		return result;
	}

	private static MeshInstance3D? FindMeshInstance(Node node)
	{
		if (node is MeshInstance3D mi)
			return mi;
		foreach (var child in node.GetChildren())
			if (FindMeshInstance(child) is MeshInstance3D found)
				return found;
		return null;
	}

	// Farthest vertex distance from the mesh origin, across all surfaces. This is the tight
	// bounding-sphere radius for an origin-centred mesh; falls back to the AABB half-diagonal
	// if a surface exposes no vertex array.
	private static float MeshBoundingRadius(Mesh mesh)
	{
		float maxSq = 0f;
		for (int s = 0; s < mesh.GetSurfaceCount(); s++)
		{
			var arrays = mesh.SurfaceGetArrays(s);
			if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
				continue;
			foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
				maxSq = Mathf.Max(maxSq, v.LengthSquared());
		}
		return maxSq > 0f ? Mathf.Sqrt(maxSq) : mesh.GetAabb().Size.Length() * 0.5f;
	}

	private void InsertAsteroid(Asteroid row)
	{
		if (_asteroidNodes.ContainsKey(row.AsteroidId))
			return;

		MeshInstance3D node;
		var (mesh, authored) = string.IsNullOrEmpty(row.Variant) ? (null, 0f) : AsteroidMesh(row.Variant);
		if (mesh is not null)
		{
			node = new MeshInstance3D
			{
				Name = $"Asteroid_{row.AsteroidId}",
				Mesh = mesh,
				Position = new Vector3(row.PosX, row.PosY, row.PosZ),
				Rotation = new Vector3(row.RotX, row.RotY, row.RotZ),
				Scale = Vector3.One * (row.Radius / authored),
			};
		}
		else
		{
			// Fallback: missing/failed variant renders as the old grey sphere.
			node = new MeshInstance3D
			{
				Name = $"Asteroid_{row.AsteroidId}",
				Mesh = new SphereMesh { Radius = row.Radius, Height = row.Radius * 2f, RadialSegments = 12, Rings = 6 },
				MaterialOverride = _asteroidMat,
				Position = new Vector3(row.PosX, row.PosY, row.PosZ),
			};
		}
		_asteroids.AddChild(node);
		_asteroidNodes[row.AsteroidId] = node;
		var (axis, speed) = AsteroidSpin(row.AsteroidId);
		_asteroidSpins[row.AsteroidId] = (node, axis, speed);
		_asteroidClip.Add((new Vector3(row.PosX, row.PosY, row.PosZ), row.Radius * AsteroidCollisionScale, row.SectorId));
		SetNodeSector(node, row.SectorId);
	}

	// Stable pseudo-random tumble for one rock: hash the id into a uniform-ish unit axis and a
	// slow rate. Deterministic so the axis never changes frame-to-frame (no per-frame RNG), and
	// purely client-side — nothing here touches the sim. Rates are deliberately lazy (~0.03..0.15
	// rad/s) so rocks drift rather than visibly whirl.
	private static (Vector3 Axis, float Speed) AsteroidSpin(ulong id)
	{
		// splitmix64-style avalanche so neighbouring ids don't share an axis.
		ulong h = id * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
		h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
		h ^= h >> 27; h *= 0x94D049BB133111EBUL;
		h ^= h >> 31;
		float u1 = (h & 0x1FFFFF) / (float)0x200000;          // [0,1)  -> cos(polar)
		float u2 = ((h >> 21) & 0x1FFFFF) / (float)0x200000;  // [0,1)  -> azimuth
		float u3 = ((h >> 42) & 0xFFFF) / (float)0x10000;     // [0,1)  -> rate
		float z = u1 * 2f - 1f;
		float phi = u2 * Mathf.Tau;
		float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
		var axis = new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), z);
		if (axis.LengthSquared() < 1e-6f)
			axis = Vector3.Up;
		return (axis.Normalized(), 0.03f + u3 * 0.12f);
	}

	// ---- Ship -----------------------------------------------------------

	private void InsertShip(Ship row, bool local)
	{
		if (_shipNodes.ContainsKey(row.ShipId))
			return;

		Node3D node;
		if (local)
		{
			var pc = new PredictionController { Name = $"Ship_{row.ShipId}" };
			node = pc;
			_ships.AddChild(pc);
			pc.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, ShipMaterial(row.Team, row.IsPig)));
			ShipModelLoader.AttachEngineGlow(pc, _defs, row.Class, row.IsPod, row.Team);
			pc.Initialize(row, _defs);
			LocalShip = pc;
			_localTeam = row.Team;
			// Respawn cancels any in-flight death-cam: the camera follows the new ship at once.
			_deathCamUntil = -1.0;
			_pendingHomeReset = false;
			// Follow the local ship's sector and re-show that sector's world.
			_localSector = row.SectorId;
			_starscape?.SetSector(row.SectorId);
			_shipNodes[row.ShipId] = node;
			SetNodeSector(node, row.SectorId);
			RefreshSectorVisibility();
			GD.Print($"[WorldRenderer] local ship {row.ShipId} spawned (team {row.Team}, sector {row.SectorId})");
			return;
		}

		var rs = new RemoteShip { Name = $"Ship_{row.ShipId}" };
		node = rs;
		_ships.AddChild(rs);
		rs.AddChild(ShipModelLoader.Build(_defs, row.Class, row.IsPod, ShipMaterial(row.Team, row.IsPig)));
		ShipModelLoader.AttachEngineGlow(rs, _defs, row.Class, row.IsPod, row.Team);
		rs.Initialize(row, _defs);
		_shipNodes[row.ShipId] = node;
		SetNodeSector(node, row.SectorId);
	}

	private void UpdateShip(Ship oldRow, Ship newRow)
	{
		if (!_shipNodes.TryGetValue(newRow.ShipId, out var node))
			return;
		switch (node)
		{
			case PredictionController pc:
				// A sector change on the LOCAL ship is a warp: hard-snap prediction to the
				// new position (no spring easing across the discontinuity) and switch the
				// rendered world to the destination sector.
				bool warped = newRow.SectorId != _localSector;
				pc.OnAuthoritative(newRow, warped);
				pc.SetMeta("sector", (int)newRow.SectorId);
				if (warped)
				{
					_localSector = newRow.SectorId;
					_starscape?.SetSector(newRow.SectorId);
					RefreshSectorVisibility();
				}
				break;
			case RemoteShip rs:
				// LastFireTick advanced → this ship fired since the last update we saw.
				// Synthesize the bolt locally (no Projectile rows are replicated).
				if (newRow.LastFireTick != oldRow.LastFireTick && newRow.LastFireTick != 0 && !newRow.IsPod)
					SpawnBoltFor(newRow);
				rs.OnAuthoritative(newRow);
				SetNodeSector(rs, newRow.SectorId);   // a remote ship may have warped in/out
				break;
		}
	}

	private void DeleteShip(Ship row)
	{
		if (!_shipNodes.Remove(row.ShipId, out var node))
			return;

		bool local = LocalShip == node;

		// A rescued pod is removed cleanly (a friendly flew onto it), not destroyed — it just
		// vanishes, no blast. The row delete is identical to a kill's, so detect the rescue the
		// way the server does: a friendly non-pod ship in hull contact (see FriendlyRescuerNear).
		bool rescued = row.IsPod && FriendlyRescuerNear(node, row.SectorId, row.Team);

		if (!rescued)
		{
			// A fiery blast at the death point (Fighters bigger than Scouts). For the local ship
			// place it at the predicted node position the player was actually watching (not the
			// authoritative row coords, which lag prediction) so the blast — and the death-cam
			// framed on it below — line up exactly. Remote ships have no prediction; use row coords.
			Vector3 deathPos = local ? node.GlobalPosition : new Vector3(row.PosX, row.PosY, row.PosZ);
			var boom = ExplosionEffect.Create(row.Class, row.Team);
			SpawnEffect(boom, deathPos, row.SectorId);
		}

		if (local)
		{
			LocalShip = null;
			// Death-cam ONLY when the local POD is DESTROYED — that's the real death (spawn
			// menu reopens). A local COMBAT ship's death instead ejects an escape pod the
			// SAME tick: OnShipInsert for that pod re-points LocalShip, cutting the camera
			// straight to the pod (both row callbacks run before this frame renders, so there's
			// no overview flicker). So skip the death-cam there and only fire it for the pod.
			if (row.IsPod && !rescued)
			{
				// Hold the chase camera on the death point for a beat so the player sees their own
				// blast up close. The return to the home overview (respawn is at the team base) is
				// deferred until the hold expires (see _Process), keeping the death sector — where
				// the blast lives and stays visible — on screen until then.
				DeathCamShipTransform = node.GlobalTransform;
				_deathCamUntil = Time.GetTicksMsec() / 1000.0 + DeathCamSec;
				_pendingHomeReset = _localSector != HomeSector;
			}
			else if (row.IsPod)
			{
				// Local pod rescued: no blast to hold the camera on, but still return the view to
				// the home overview where the spawn menu reopens.
				_pendingHomeReset = _localSector != HomeSector;
			}
		}
		node.QueueFree();
	}

	// Client-side mirror of the server's rescue rule (Lib.cs rescue pass): is a friendly,
	// non-pod ship in roughly hull contact with this pod? If so the pod was picked up, not
	// killed, so its removal should be silent. Compares RENDERED positions (rescuer node vs
	// pod node) so the interpolation lag they share largely cancels, with a generous radius
	// (RescuePickupDist) for the residual predicted/interp gap. Restricted to the pod's own
	// sector — sectors share a coordinate origin, so a same-team ship in another sector can
	// overlap in raw coords. The pod node is already removed from _shipNodes by the caller.
	private bool FriendlyRescuerNear(Node3D podNode, uint sector, byte team)
	{
		Vector3 podPos = podNode.GlobalPosition;
		foreach (var node in _shipNodes.Values)
		{
			(byte t, bool isPod) = node switch
			{
				RemoteShip rs => (rs.Team, rs.IsPod),
				PredictionController pc => (pc.Team, pc.IsPod),
				_ => ((byte)255, true),
			};
			if (isPod || t != team)
				continue;
			if (node.HasMeta("sector") && (int)node.GetMeta("sector") != (int)sector)
				continue;
			if (podPos.DistanceSquaredTo(node.GlobalPosition) <= RescuePickupDist * RescuePickupDist)
				return true;
		}
		return false;
	}

	// ---- Bolts (client-synthesized projectile visuals) -------------------

	// A REMOTE ship's row showed a new LastFireTick: rebuild the shot the server fired —
	// the exact mirror of the module's TryFire muzzle math. The spread direction is
	// deterministic in (ShipId, fire tick) via the shared FlightModel.SpreadDirection, so
	// every client and the server derive the same bolt from the same replicated row.
	private void SpawnBoltFor(Ship row)
	{
		if (!_defs.TryGetWeapon((byte)row.Class, out var hp, out var weapon))
			return;

		var state = ShipMath.StateFromRow(row);

		// Under server catch-up, one row update can span several sim ticks; the row's
		// position is at LastInputTick while the shot left at LastFireTick. Rewind the
		// ship along its (constant-velocity approximation) path to the fire tick so the
		// muzzle sits where the ship was when it fired.
		uint ticksPast = row.LastInputTick > row.LastFireTick
			? System.Math.Min(row.LastInputTick - row.LastFireTick, 8u) : 0u;
		Vec3 firePos = state.Pos - state.Vel * (ticksPast * FlightModel.Dt);

		Vec3 fwd = state.Rot.Rotate(new Vec3(hp.DirX, hp.DirY, hp.DirZ));
		Vec3 shotDir = FlightModel.SpreadDirection(fwd, weapon.SpreadRad, row.ShipId, row.LastFireTick);
		Vec3 mp = firePos + state.Rot.Rotate(new Vec3(hp.OffX, hp.OffY, hp.OffZ));
		Vec3 mv = shotDir * weapon.ProjectileSpeed + state.Vel;

		AddBolt(ShipMath.ToGodot(mp), ShipMath.ToGodot(mv), row.SectorId,
			weapon.ProjectileLifeTicks * FlightModel.Dt, ShotMaskLeadSec());
	}

	// The LOCAL ship's fire prediction produced a shot this tick (ShipController). Same
	// rendering as a remote bolt, no masking lead (prediction is already now-correct).
	public void SpawnLocalBolt(Vector3 pos, Vector3 vel, float lifeSec)
		=> AddBolt(pos, vel, _localSector, lifeSec, 0f);

	private void AddBolt(Vector3 pos, Vector3 vel, uint sector, float lifeSec, float leadSec)
	{
		var pv = new ProjectileView { Name = "Bolt" };
		_projectiles.AddChild(pv);
		pv.AddChild(NewProjectileMesh());
		pv.Initialize(pos, vel, ClipBoltTtl(sector, pos, vel, lifeSec), leadSec);
		SetNodeSector(pv, sector);
		_bolts.Add(pv);
	}

	// How far ahead to render an enemy/remote shot to mask its ~1 RTT-late pop-in
	// (see ProjectileView._renderLeadSec). Auto mode uses the measured one-way latency
	// (≈ half RTT); STDB_SHOT_MASK_MS pins a fixed value. Clamped so a bad reading can't
	// fling shots downrange. Returns 0 on localhost (PingMs unmeasured) — no masking needed.
	private float ShotMaskLeadSec()
	{
		if (_shotMaskMs >= 0f)
			return Mathf.Min(_shotMaskMs, 400f) / 1000f;
		_ship ??= GetNodeOrNull<ShipController>("../ShipController");
		float oneWayMs = (_ship?.PingMs ?? 0f) * 0.5f;
		return Mathf.Clamp(oneWayMs, 0f, 250f) / 1000f;
	}

	// Clip a bolt's flight time at the first STATIC obstruction (asteroid / enemy-or-any
	// base) along its line, so the visual stops at a rock the way the server's analytic
	// solve does. Static geometry is fully replicated, so this is a spawn-time pass over
	// the local caches — ships stay dynamic and are handled by the per-frame spark sweep.
	private float ClipBoltTtl(uint sector, Vector3 pos, Vector3 vel, float ttl)
	{
		foreach (var a in _asteroidClip)
		{
			if (a.Sector != sector)
				continue;
			ClipSphere(pos, vel, a.Pos, a.Radius, ref ttl);
		}
		float baseR = _defs.GetBaseDef(DefaultBaseTypeId)?.Radius ?? 45f;
		foreach (var b in _baseClip)
		{
			if (b.Sector != sector)
				continue;
			ClipSphere(pos, vel, b.Pos, baseR, ref ttl);
		}
		return ttl;
	}

	// Smallest positive entry time of the line pos+vel·t into a static sphere, if it is
	// within the current ttl — the client-side mirror of the module's FirstEntryTime
	// specialized to a static target.
	private static void ClipSphere(Vector3 pos, Vector3 vel, Vector3 center, float radius, ref float ttl)
	{
		Vector3 d = center - pos;
		float a = vel.LengthSquared();
		if (a < 1e-6f)
			return;
		float b = -2f * d.Dot(vel);
		float c = d.LengthSquared() - radius * radius;
		if (c <= 0f) { ttl = 0f; return; }   // spawned inside (e.g. muzzle against the rock)
		float disc = b * b - 4f * a * c;
		if (disc < 0f)
			return;
		float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
		if (t > 0f && t < ttl)
			ttl = t;
	}

	private MeshInstance3D NewProjectileMesh() => new MeshInstance3D
	{
		// Slim tracer bolt. The cylinder's long axis is local +Y; rotate it to local +Z
		// so it runs along ProjectileView's forward, which is aimed down the bolt's velocity.
		Mesh = new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.22f, Height = 2.2f, RadialSegments = 8, Rings = 1 },
		MaterialOverride = _projectileMat,
		RotationDegrees = new Vector3(-90f, 0f, 0f),
		// Self-lit glowing tracers: casting shadows would be wasteful and wrong-looking.
		CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
	};

	// Per-frame upkeep: bolt impacts/expiry, deferred camera resets, cosmetic spins.
	public override void _Process(double delta)
	{
		// Death-cam expiry: once the brief hold on the death point is over, pull the world
		// back to the home-battlefield overview (deferred from OnShipDelete so the death
		// sector stayed visible through the hold). Skipped if the player already respawned.
		if (_pendingHomeReset && LocalShip == null && !DeathCamActive)
		{
			_localSector = HomeSector;
			_starscape?.SetSector(HomeSector);
			RefreshSectorVisibility();
			_pendingHomeReset = false;
		}

		CheckBoltImpacts(delta);
		BillboardBaseHealthBars();

		// Lazy cosmetic tumble: spin each rock slowly about its fixed pseudo-random axis.
		if (_asteroidSpins.Count > 0)
		{
			float fdelta = (float)delta;
			foreach (var (node, axis, speed) in _asteroidSpins.Values)
				node.Rotate(axis, speed * fdelta);
		}

		// Cull bolts whose (obstruction-clipped) flight life has elapsed.
		for (int i = _bolts.Count - 1; i >= 0; i--)
		{
			if (_bolts[i].Expired)
			{
				_bolts[i].QueueFree();
				_bolts.RemoveAt(i);
			}
		}
	}

	// Screen-align each visible base damage bar so it always faces the camera. Copying the
	// camera's basis (orthonormal) makes the bar's local axes match the screen — left-anchored
	// depletion in UpdateBaseHealthBar then reads correctly from any angle. Cheap: at most a
	// couple of bases, and only those currently damaged-and-visible are touched.
	private void BillboardBaseHealthBars()
	{
		if (_baseHealthBars.Count == 0)
			return;
		var cam = GetViewport()?.GetCamera3D();
		if (cam == null)
			return;
		var camBasis = cam.GlobalTransform.Basis;
		foreach (var bar in _baseHealthBars.Values)
		{
			if (!bar.Root.IsVisibleInTree())
				continue;
			bar.Root.GlobalBasis = camBasis;
		}
	}

	// Purely client-side hit sparks: flash where a rendered bolt visually meets a ship this frame,
	// then consume the bolt so it stops on impact. Cosmetic and team-agnostic (friendly fire sparks
	// like anything else); the server resolved the real damage analytically at fire time. The
	// muzzle-clearance gate keeps a bolt from sparking on the ship that fired it. Visibility gates
	// both bolt and ship to the local sector — sectors share world coordinates, so this also
	// avoids cross-sector hits.
	private void CheckBoltImpacts(double delta)
	{
		if (_bolts.Count == 0 || _shipNodes.Count == 0)
			return;

		for (int i = _bolts.Count - 1; i >= 0; i--)
		{
			var pv = _bolts[i];
			if (!pv.Visible)
				continue;
			Vector3 b = pv.GlobalPosition;
			// Don't let a shot spark on the ship that fired it: ignore until it has left the muzzle.
			if (b.DistanceSquaredTo(pv.SpawnPos) < MuzzleClearance * MuzzleClearance)
				continue;
			Vector3 a = b - pv.Velocity * (float)delta;   // swept path across this frame
			foreach (var ship in _shipNodes.Values)
			{
				if (!ship.Visible)
					continue;
				Vector3 c = ship.GlobalPosition;
				Vector3 hit = ClosestPointOnSegment(a, b, c);
				if (c.DistanceSquaredTo(hit) <= VisualHitRadius * VisualHitRadius)
				{
					SpawnEffect(new HitFlash(), hit, _localSector);
					pv.QueueFree();
					_bolts.RemoveAt(i);
					break;
				}
			}
		}
	}

	// Closest point to p on segment [a, b], clamped to the endpoints.
	private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
	{
		Vector3 ab = b - a;
		float len2 = ab.LengthSquared();
		if (len2 < 1e-6f)
			return a;
		return a + ab * Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
	}

	// Team/PIG hull material for a ship's placeholder mesh. The ShipModelLoader (M4)
	// owns the mesh + hardpoint geometry; the materials live here with the rest of the
	// renderer's shared resources, so it resolves one and hands it to the loader.
	private StandardMaterial3D ShipMaterial(byte team, bool isPig)
		=> isPig
			? (team == 0 ? _pigTeam0Mat : _pigTeam1Mat)
			: (team == 0 ? _team0Mat : _team1Mat);
}
