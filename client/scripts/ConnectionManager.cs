using System;
using Godot;
using SpacetimeDB;
using SpacetimeDB.Types;

// Owns the DB connection and the world subscription. Other scripts read the
// connection + local identity from here and listen for `Connected` to register
// their own table-row callbacks before the subscription is applied.
public partial class ConnectionManager : Node
{
	private const string ServerUrl = "ws://localhost:3001";
	private const string DbName    = "stellar-allegiance";

	public DbConnection? Conn { get; private set; }
	public Identity? LocalIdentity { get; private set; }

	// Fired once the websocket connects, BEFORE the subscription is registered,
	// so listeners can attach OnInsert/OnDelete handlers in time for the
	// initial row snapshot.
	public event Action<DbConnection>? Connected;

	public override void _Ready()
	{
		Conn = DbConnection.Builder()
			.WithUri(ServerUrl)
			.WithDatabaseName(DbName)
			.OnConnect(OnConnect)
			.OnConnectError(OnConnectError)
			.OnDisconnect(OnDisconnect)
			.Build();
	}

	public override void _Process(double delta)
	{
		Conn?.FrameTick();
	}

	private void OnConnect(DbConnection conn, Identity identity, string token)
	{
		LocalIdentity = identity;
		GD.Print($"[ConnectionManager] Connected — identity: {identity}");

		// Let renderers register row callbacks first…
		Connected?.Invoke(conn);

		// …then subscribe so the initial snapshot fires those callbacks.
		conn.SubscriptionBuilder()
			.OnApplied(ctx => GD.Print($"[ConnectionManager] Subscription applied — bases: {ctx.Db.Base.Count}, asteroids: {ctx.Db.Asteroid.Count}, ships: {ctx.Db.Ship.Count}"))
			.OnError((_, err) => GD.PrintErr($"[ConnectionManager] Subscription error: {err.Message}"))
			.SubscribeToAllTables();
	}

	private void OnConnectError(Exception e)
	{
		GD.PrintErr($"[ConnectionManager] Connection error: {e.Message}");
	}

	private void OnDisconnect(DbConnection conn, Exception? e)
	{
		GD.Print("[ConnectionManager] Disconnected");
	}
}
