using System.Collections.Generic;
using Godot;
using SpacetimeDB.Types;

// Maps DB rows -> scene nodes. For T2 only the static world (bases + asteroids)
// is rendered; ships/projectiles arrive in later tasks. The client never
// mutates state here — it only mirrors whatever the subscription delivers.
public partial class WorldRenderer : Node3D
{
	private const float BaseRadius = 45f;

	private Node3D _bases = null!;
	private Node3D _asteroids = null!;
	private Node3D _ships = null!;
	private Node3D _projectiles = null!;
	private Node3D _alephs = null!;
	private Node3D _effects = null!;   // transient FX (explosions, hit flashes); self-freeing

	// How long a predicted ghost shot lives unmatched before it's culled. Covers
	// prediction lead + round-trip to the authoritative row (~0.2s); a never-matched
	// ghost (rare mispredicted fire) disappears after this.
	private const double GhostTtl = 0.6;

	private readonly Dictionary<ulong, Node3D> _baseNodes = new();
	private readonly Dictionary<ulong, Node3D> _asteroidNodes = new();
	private readonly Dictionary<ulong, Node3D> _shipNodes = new();
	private readonly Dictionary<ulong, ProjectileView> _projectileNodes = new();
	private readonly Dictionary<ulong, Node3D> _alephNodes = new();

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
	public float LocalSectorRadius => _sectors.TryGetValue(_localSector, out var s) ? s.Radius : 0f;
	public Vector3 LocalSectorCenter =>
		_sectors.TryGetValue(_localSector, out var s) ? new Vector3(s.CenterX, s.CenterY, s.CenterZ) : Vector3.Zero;

	// Client-side muzzle prediction (own shots): ghosts spawned on fire, in FIFO
	// fire order, each handed off to the matching authoritative row as it arrives.
	private readonly List<ProjectileView> _predictedShots = new();
	private byte? _localTeam;

	// Scratch reused by EnemyShips() so the per-frame marker pass allocates nothing.
	private readonly List<RemoteShip> _enemyScratch = new();

	// Scratch for the per-frame client-side hit-spark pass: ids of bolts that visually struck a
	// ship this frame, collected then consumed after iteration (no mutating the dict mid-loop).
	private readonly List<ulong> _projHitScratch = new();

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

	private ConnectionManager _cm = null!;
	private ShipController? _ship;   // sibling; lazily resolved for the live latency readout
	private Starscape? _starscape;   // sibling; repaints the backdrop as the local sector changes

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
				if (node is RemoteShip rs && rs.Team != lt && rs.Visible)
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

		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_cm.Connected += OnConnected;
		_starscape = GetNodeOrNull<Starscape>("../Starscape");

		if (float.TryParse(OS.GetEnvironment("STDB_SHOT_MASK_MS"), out var ms) && ms >= 0f)
			_shotMaskMs = ms;
	}

	private void OnConnected(DbConnection conn)
	{
		conn.Db.Base.OnInsert += OnBaseInsert;
		conn.Db.Base.OnUpdate += OnBaseUpdate;
		conn.Db.Base.OnDelete += OnBaseDelete;
		conn.Db.Asteroid.OnInsert += OnAsteroidInsert;
		conn.Db.Asteroid.OnDelete += OnAsteroidDelete;
		conn.Db.Ship.OnInsert += OnShipInsert;
		conn.Db.Ship.OnUpdate += OnShipUpdate;
		conn.Db.Ship.OnDelete += OnShipDelete;
		conn.Db.Projectile.OnInsert += OnProjectileInsert;
		// No OnUpdate: projectiles are constant-velocity, so the client fire-and-forgets
		// the spawn line (see ProjectileView). Per-tick position updates are ignored.
		conn.Db.Projectile.OnDelete += OnProjectileDelete;
		conn.Db.Match.OnInsert += (_, row) => OnMatch(row);
		conn.Db.Match.OnUpdate += (_, _, row) => OnMatch(row);
		// Sectors define the boundary radius; alephs are the warp funnels. Both are
		// seeded once at Init and effectively static, but we still listen for updates.
		conn.Db.Sector.OnInsert += (_, row) => _sectors[row.SectorId] = row;
		conn.Db.Sector.OnUpdate += (_, _, row) => _sectors[row.SectorId] = row;
		conn.Db.Aleph.OnInsert += OnAlephInsert;
		conn.Db.Aleph.OnDelete += OnAlephDelete;
	}

	private void OnMatch(Match row)
	{
		ServerTick = row.Tick;
		Phase = row.Phase;
		Winner = row.Winner;
	}

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

	private void OnAlephInsert(EventContext ctx, Aleph row)
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

	private void OnAlephDelete(EventContext ctx, Aleph row)
	{
		if (_alephNodes.Remove(row.AlephId, out var node))
			node.QueueFree();
	}

	// ---- Base -----------------------------------------------------------

	private void OnBaseInsert(EventContext ctx, Base row)
	{
		if (_baseNodes.ContainsKey(row.BaseId))
			return;

		var node = new MeshInstance3D
		{
			Name = $"Base_{row.BaseId}",
			Mesh = new SphereMesh { Radius = BaseRadius, Height = BaseRadius * 2f, RadialSegments = 32, Rings = 16 },
			MaterialOverride = row.Team == 0 ? _team0Mat : _team1Mat,
			Position = new Vector3(row.PosX, row.PosY, row.PosZ),
		};
		_bases.AddChild(node);
		_baseNodes[row.BaseId] = node;
		SetNodeSector(node, row.SectorId);
		GD.Print($"[WorldRenderer] Base {row.BaseId} (team {row.Team}) @ ({row.PosX}, {row.PosY}, {row.PosZ})");
	}

	// A destroyed base (Health <= 0, T9) is removed from the scene. A base that comes
	// back to full health (RestartMatch heals every base) is re-created if it had been
	// removed — so the battlefield is whole again for the next match.
	private void OnBaseUpdate(EventContext ctx, Base oldRow, Base newRow)
	{
		if (newRow.Health <= 0f)
		{
			if (_baseNodes.Remove(newRow.BaseId, out var node))
			{
				node.QueueFree();
				GD.Print($"[WorldRenderer] Base {newRow.BaseId} (team {newRow.Team}) destroyed");
			}
		}
		else if (!_baseNodes.ContainsKey(newRow.BaseId))
		{
			OnBaseInsert(ctx, newRow);   // restored after a restart
		}
	}

	private void OnBaseDelete(EventContext ctx, Base row)
	{
		if (_baseNodes.Remove(row.BaseId, out var node))
			node.QueueFree();
	}

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

	private void OnAsteroidInsert(EventContext ctx, Asteroid row)
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
		SetNodeSector(node, row.SectorId);
	}

	private void OnAsteroidDelete(EventContext ctx, Asteroid row)
	{
		if (_asteroidNodes.Remove(row.AsteroidId, out var node))
			node.QueueFree();
	}

	// ---- Ship -----------------------------------------------------------

	private bool IsLocal(Ship row) =>
		_cm.LocalIdentity is { } id && row.Owner == id;

	private void OnShipInsert(EventContext ctx, Ship row)
	{
		if (_shipNodes.ContainsKey(row.ShipId))
			return;

		Node3D node;
		// PIGs are never the local player (defensive: their Owner is the module identity,
		// so IsLocal is already false) — always render them as interpolated remote ships.
		if (IsLocal(row) && !row.IsPig)
		{
			var pc = new PredictionController { Name = $"Ship_{row.ShipId}" };
			node = pc;
			_ships.AddChild(pc);
			pc.AddChild(BuildShipMesh(row.Team, row.Class, row.IsPig));
			AttachEngineGlow(pc, row.Class, row.Team);
			pc.Initialize(row);
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
		rs.AddChild(BuildShipMesh(row.Team, row.Class, row.IsPig));
		AttachEngineGlow(rs, row.Class, row.Team);
		rs.Initialize(row);
		_shipNodes[row.ShipId] = node;
		SetNodeSector(node, row.SectorId);
	}

	private void OnShipUpdate(EventContext ctx, Ship oldRow, Ship newRow)
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
				rs.OnAuthoritative(newRow);
				SetNodeSector(rs, newRow.SectorId);   // a remote ship may have warped in/out
				break;
		}
	}

	private void OnShipDelete(EventContext ctx, Ship row)
	{
		if (!_shipNodes.Remove(row.ShipId, out var node))
			return;

		bool local = LocalShip == node;
		// A fiery blast at the death point (Fighters bigger than Scouts). For the local ship
		// place it at the predicted node position the player was actually watching (not the
		// authoritative row coords, which lag prediction) so the blast — and the death-cam
		// framed on it below — line up exactly. Remote ships have no prediction; use row coords.
		Vector3 deathPos = local ? node.GlobalPosition : new Vector3(row.PosX, row.PosY, row.PosZ);
		var boom = ExplosionEffect.Create(row.Class, row.Team);
		SpawnEffect(boom, deathPos, row.SectorId);

		if (local)
		{
			LocalShip = null;
			// Drop unmatched ghosts so a respawn's FIFO never adopts a stale one.
			foreach (var g in _predictedShots)
				g.QueueFree();
			_predictedShots.Clear();
			// Hold the chase camera on the death point for a beat so the player sees their own
			// blast up close. The return to the home overview (respawn is at the team base) is
			// deferred until the hold expires (see _Process), keeping the death sector — where
			// the blast lives and stays visible — on screen until then.
			DeathCamShipTransform = node.GlobalTransform;
			_deathCamUntil = Time.GetTicksMsec() / 1000.0 + DeathCamSec;
			_pendingHomeReset = _localSector != HomeSector;
		}
		node.QueueFree();
	}

	// ---- Projectile -----------------------------------------------------

	private void OnProjectileInsert(EventContext ctx, Projectile row)
	{
		if (_projectileNodes.ContainsKey(row.ProjectileId))
			return;

		// Own shots: hand the oldest predicted ghost off to this authoritative row
		// instead of spawning a second node (the ghost is already in flight).
		if (_localTeam is byte lt && row.Team == lt && _predictedShots.Count > 0)
		{
			var ghost = _predictedShots[0];
			_predictedShots.RemoveAt(0);
			ghost.AttachAuthoritative(row.ProjectileId);
			ghost.Name = $"Projectile_{row.ProjectileId}";
			SetNodeSector(ghost, row.SectorId);
			_projectileNodes[row.ProjectileId] = ghost;
			return;
		}

		var pv = new ProjectileView { Name = $"Projectile_{row.ProjectileId}" };
		_projectiles.AddChild(pv);
		pv.AddChild(NewProjectileMesh());
		pv.Initialize(row, ShotMaskLeadSec());
		SetNodeSector(pv, row.SectorId);
		_projectileNodes[row.ProjectileId] = pv;
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

	// Spawn an immediate ghost for the local player's own shot (muzzle prediction),
	// rendered identically to an authoritative projectile and queued in fire order.
	public void SpawnPredictedProjectile(byte team, Vector3 pos, Vector3 vel)
	{
		var pv = new ProjectileView { Name = "Projectile_predicted" };
		_projectiles.AddChild(pv);
		pv.AddChild(NewProjectileMesh());
		pv.InitializePredicted(pos, vel);
		SetNodeSector(pv, _localSector);   // own shot is in our sector; hidden if we warp away
		_predictedShots.Add(pv);
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

	// Cull predicted ghosts that were never matched to an authoritative row.
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

		if (_predictedShots.Count == 0)
			return;
		double now = Time.GetTicksMsec() / 1000.0;
		for (int i = _predictedShots.Count - 1; i >= 0; i--)
		{
			if (_predictedShots[i].GhostExpired(now, GhostTtl))
			{
				_predictedShots[i].QueueFree();
				_predictedShots.RemoveAt(i);
			}
		}
	}

	// Purely client-side hit sparks: flash where a rendered bolt visually meets a ship this frame,
	// then consume the bolt so it stops on impact. Cosmetic and team-agnostic (friendly fire sparks
	// like anything else); the server still authoritatively resolves damage and deletes the row.
	// Only authoritative bolts are tested (own predicted ghosts haven't cleared their muzzle yet
	// and become authoritative long before reaching a target). Visibility gates both bolt and ship
	// to the local sector — sectors share world coordinates, so this also avoids cross-sector hits.
	private void CheckBoltImpacts(double delta)
	{
		if (_projectileNodes.Count == 0 || _shipNodes.Count == 0)
			return;

		_projHitScratch.Clear();
		foreach (var (id, pv) in _projectileNodes)
		{
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
					_projHitScratch.Add(id);
					break;
				}
			}
		}
		foreach (var id in _projHitScratch)
			if (_projectileNodes.Remove(id, out var pv))
				pv.QueueFree();
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

	// A projectile leaving the table just frees its node. The hit SPARK is no longer driven from
	// this server delete (its timing/position couldn't match what the player sees); it's a purely
	// client-side effect spawned where the rendered bolt visually meets a ship (see _Process).
	private void OnProjectileDelete(EventContext ctx, Projectile row)
	{
		if (_projectileNodes.Remove(row.ProjectileId, out var pv))
			pv.QueueFree();
	}

	// Distinct silhouettes per class (T7), both built pointing local +Z to match
	// the flight model's forward axis: the Scout is a sleek cone, the Fighter a
	// chunkier, boxier hull that reads as the heavier ship.
	private MeshInstance3D BuildShipMesh(byte team, ShipClass cls, bool isPig)
	{
		var mat = isPig
			? (team == 0 ? _pigTeam0Mat : _pigTeam1Mat)
			: (team == 0 ? _team0Mat : _team1Mat);

		if (cls == ShipClass.Fighter)
		{
			return new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = new Vector3(3.6f, 1.6f, 5.5f) },
				MaterialOverride = mat,
			};
		}

		return new MeshInstance3D
		{
			Mesh = new CylinderMesh
			{
				TopRadius = 0f,
				BottomRadius = 1.4f,
				Height = 4.5f,
				RadialSegments = 12,
			},
			MaterialOverride = mat,
			RotationDegrees = new Vector3(90f, 0f, 0f), // +Y cone tip -> +Z
		};
	}

	// Build and attach the dynamic engine glow (see EngineGlow) to a freshly
	// spawned ship, then hand the node its reference so it can drive throttle each
	// frame. Nozzle layout matches the hull silhouette from BuildShipMesh: a
	// Scout's single central thruster vs a Fighter's heavier twin engines.
	private void AttachEngineGlow(Node3D shipNode, ShipClass cls, byte team)
	{
		// Hot exhaust tinted toward the team hue so friend/foe still reads in a dogfight.
		Color hot = team == 0 ? new Color(0.5f, 0.78f, 1f) : new Color(1f, 0.62f, 0.4f);

		EngineGlow glow = cls == ShipClass.Fighter
			? new EngineGlow
			{
				Name = "EngineGlow",
				Nozzles = new[] { new Vector3(-1.1f, 0f, -2.75f), new Vector3(1.1f, 0f, -2.75f) },
				NozzleRadius = 0.6f,
				PlumeLength = 3.8f,
				LightRange = 18f,
				CoreColor = hot,
			}
			: new EngineGlow
			{
				Name = "EngineGlow",
				Nozzles = new[] { new Vector3(0f, 0f, -2.25f) },
				NozzleRadius = 0.85f,
				PlumeLength = 3.5f,
				LightRange = 15f,
				CoreColor = hot,
			};

		shipNode.AddChild(glow);
		switch (shipNode)
		{
			case PredictionController pc: pc.AttachEngine(glow); break;
			case RemoteShip rs: rs.AttachEngine(glow); break;
		}

		// Ghostly team-coloured ribbon tracing the ship's path (same hue as the glow so
		// friend/foe still reads). It rides the ship node's transform, so no per-frame
		// driving is needed. Anchored at the rear of the hull (roughly where the engines
		// sit) so the ribbon streams off the ship's BACK, not its centre. Fighters get a
		// slightly wider streak to match their bulk.
		shipNode.AddChild(new TeamTrail
		{
			Name = "TeamTrail",
			Position = new Vector3(0f, 0f, cls == ShipClass.Fighter ? -2.75f : -2.25f),
			TeamColor = hot,
			Width = cls == ShipClass.Fighter ? 0.5f : 0.4f,
		});
	}
}
