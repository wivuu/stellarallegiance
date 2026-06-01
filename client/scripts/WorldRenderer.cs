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

	private readonly Dictionary<ulong, Node3D> _baseNodes = new();
	private readonly Dictionary<ulong, Node3D> _asteroidNodes = new();

	private StandardMaterial3D _asteroidMat = null!;
	private StandardMaterial3D _team0Mat = null!;
	private StandardMaterial3D _team1Mat = null!;

	public override void _Ready()
	{
		_bases = new Node3D { Name = "Bases" };
		_asteroids = new Node3D { Name = "Asteroids" };
		AddChild(_bases);
		AddChild(_asteroids);

		_asteroidMat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.42f, 0.38f) };
		_team0Mat = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.5f, 0.95f) };
		_team1Mat = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.3f, 0.25f) };

		var cam = GetNode<Camera3D>("../Camera3D");
		cam.Position = new Vector3(600f, 750f, 1600f);
		cam.LookAt(Vector3.Zero, Vector3.Up);

		var cm = GetNode<ConnectionManager>("../ConnectionManager");
		cm.Connected += OnConnected;
	}

	private void OnConnected(DbConnection conn)
	{
		conn.Db.Base.OnInsert += OnBaseInsert;
		conn.Db.Base.OnDelete += OnBaseDelete;
		conn.Db.Asteroid.OnInsert += OnAsteroidInsert;
		conn.Db.Asteroid.OnDelete += OnAsteroidDelete;
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
}
