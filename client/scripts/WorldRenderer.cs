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

	private readonly Dictionary<ulong, Node3D> _baseNodes = new();
	private readonly Dictionary<ulong, Node3D> _asteroidNodes = new();
	private readonly Dictionary<ulong, Node3D> _shipNodes = new();

	private StandardMaterial3D _asteroidMat = null!;
	private StandardMaterial3D _team0Mat = null!;
	private StandardMaterial3D _team1Mat = null!;

	private ConnectionManager _cm = null!;

	// The local player's predicted ship, or null when not flying. Read by
	// ShipController (drives prediction), CameraRig (chase target), and Hud.
	public PredictionController? LocalShip { get; private set; }

	// Latest authoritative sim tick (Match.Tick). ShipController slaves its
	// prediction clock to this so client/server ticks index the same integration.
	public uint ServerTick { get; private set; }

	public override void _Ready()
	{
		_bases = new Node3D { Name = "Bases" };
		_asteroids = new Node3D { Name = "Asteroids" };
		_ships = new Node3D { Name = "Ships" };
		AddChild(_bases);
		AddChild(_asteroids);
		AddChild(_ships);

		_asteroidMat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.42f, 0.38f) };
		_team0Mat = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.5f, 0.95f) };
		_team1Mat = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.3f, 0.25f) };

		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_cm.Connected += OnConnected;
	}

	private void OnConnected(DbConnection conn)
	{
		conn.Db.Base.OnInsert += OnBaseInsert;
		conn.Db.Base.OnDelete += OnBaseDelete;
		conn.Db.Asteroid.OnInsert += OnAsteroidInsert;
		conn.Db.Asteroid.OnDelete += OnAsteroidDelete;
		conn.Db.Ship.OnInsert += OnShipInsert;
		conn.Db.Ship.OnUpdate += OnShipUpdate;
		conn.Db.Ship.OnDelete += OnShipDelete;
		conn.Db.Match.OnInsert += (_, row) => ServerTick = row.Tick;
		conn.Db.Match.OnUpdate += (_, _, row) => ServerTick = row.Tick;
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
		GD.Print($"[WorldRenderer] Base {row.BaseId} (team {row.Team}) @ ({row.PosX}, {row.PosY}, {row.PosZ})");
	}

	private void OnBaseDelete(EventContext ctx, Base row)
	{
		if (_baseNodes.Remove(row.BaseId, out var node))
			node.QueueFree();
	}

	// ---- Asteroid -------------------------------------------------------

	private void OnAsteroidInsert(EventContext ctx, Asteroid row)
	{
		if (_asteroidNodes.ContainsKey(row.AsteroidId))
			return;

		var node = new MeshInstance3D
		{
			Name = $"Asteroid_{row.AsteroidId}",
			Mesh = new SphereMesh { Radius = row.Radius, Height = row.Radius * 2f, RadialSegments = 12, Rings = 6 },
			MaterialOverride = _asteroidMat,
			Position = new Vector3(row.PosX, row.PosY, row.PosZ),
		};
		_asteroids.AddChild(node);
		_asteroidNodes[row.AsteroidId] = node;
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
		if (IsLocal(row))
		{
			var pc = new PredictionController { Name = $"Ship_{row.ShipId}" };
			node = pc;
			_ships.AddChild(pc);
			pc.AddChild(BuildShipMesh(row.Team, row.Class));
			pc.Initialize(row);
			LocalShip = pc;
			GD.Print($"[WorldRenderer] local ship {row.ShipId} spawned (team {row.Team})");
		}
		else
		{
			var rs = new RemoteShip { Name = $"Ship_{row.ShipId}" };
			node = rs;
			_ships.AddChild(rs);
			rs.AddChild(BuildShipMesh(row.Team, row.Class));
			rs.Initialize(row);
		}
		_shipNodes[row.ShipId] = node;
	}

	private void OnShipUpdate(EventContext ctx, Ship oldRow, Ship newRow)
	{
		if (!_shipNodes.TryGetValue(newRow.ShipId, out var node))
			return;
		switch (node)
		{
			case PredictionController pc: pc.OnAuthoritative(newRow); break;
			case RemoteShip rs: rs.OnAuthoritative(newRow); break;
		}
	}

	private void OnShipDelete(EventContext ctx, Ship row)
	{
		if (!_shipNodes.Remove(row.ShipId, out var node))
			return;
		if (LocalShip == node)
			LocalShip = null;
		node.QueueFree();
	}

	// Distinct silhouettes per class (T7), both built pointing local +Z to match
	// the flight model's forward axis: the Scout is a sleek cone, the Fighter a
	// chunkier, boxier hull that reads as the heavier ship.
	private MeshInstance3D BuildShipMesh(byte team, ShipClass cls)
	{
		var mat = team == 0 ? _team0Mat : _team1Mat;

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
}
