using System;
using Godot;
using SpacetimeDB;
using SpacetimeDB.Types;

// Owns the DB connection and the world subscription. Other scripts read the
// connection + local identity from here and listen for `Connected` to register
// their own table-row callbacks before the subscription is applied.
public partial class ConnectionManager : Node
{
	// Server + database are configurable so the same build can target the local
	// dev server or Maincloud (T10) without a code edit. Defaults are the local
	// dev server. Override with env vars before launch, e.g.:
	//   STDB_URI=wss://maincloud.spacetimedb.com STDB_DB=stellar-allegiance godot ...
	private const string DefaultServerUrl = "ws://localhost:3001";
	private const string DefaultDbName    = "stellar-allegiance";

	private string _serverUrl = DefaultServerUrl;
	private string _dbName     = DefaultDbName;

	// Connection lifecycle, surfaced so the UI can show a status overlay instead of a
	// misleading empty lobby. `Conn` is non-null the moment the builder runs — well
	// before the socket actually connects, and it stays non-null after a fault — so
	// `Conn != null` is NOT "connected". Read `State` for that.
	public enum ConnState { Connecting, Connected, Failed, Disconnected }
	public ConnState State { get; private set; } = ConnState.Connecting;

	public DbConnection? Conn { get; private set; }
	public Identity? LocalIdentity { get; private set; }

	// The server we're targeting, for the status overlay's message.
	public string ServerUrl => _serverUrl;

	// Fired once the websocket connects, BEFORE the subscription is registered,
	// so listeners can attach OnInsert/OnDelete handlers in time for the
	// initial row snapshot.
	public event Action<DbConnection>? Connected;

	private const string MaincloudUrl = "wss://maincloud.spacetimedb.com";

	public override void _Ready()
	{
		// Exported builds have no local dev server, so they default to Maincloud;
		// the editor keeps the localhost default for local iteration.
		if (!OS.HasFeature("editor")) _serverUrl = MaincloudUrl;

		// Precedence: --maincloud flag, then env vars, else the default above.
		// (The flag is the friendliest knob for a second machine / exported build.)
		foreach (var a in OS.GetCmdlineArgs())
			if (a == "--maincloud") _serverUrl = MaincloudUrl;

		var uri = OS.GetEnvironment("STDB_URI");
		var db  = OS.GetEnvironment("STDB_DB");
		if (!string.IsNullOrEmpty(uri)) _serverUrl = uri;
		if (!string.IsNullOrEmpty(db))  _dbName = db;
		Connect();
	}

	// Build (or rebuild) the connection. Split out so the status overlay's Retry button
	// can re-attempt after a failed/lost connection without restarting the client.
	public void Connect()
	{
		State = ConnState.Connecting;
		GD.Print($"[ConnectionManager] Connecting to {_serverUrl} / {_dbName}");

		Conn = DbConnection.Builder()
			.WithUri(_serverUrl)
			.WithDatabaseName(_dbName)
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
		State = ConnState.Connected;
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
		State = ConnState.Failed;
		GD.PrintErr($"[ConnectionManager] Connection error: {e.Message}");
	}

	private void OnDisconnect(DbConnection conn, Exception? e)
	{
		// Distinguish "never got in" from "lost a live link": if we'd connected, this is
		// a drop; otherwise it's part of a failed attempt and Failed already covers it.
		State = State == ConnState.Connected ? ConnState.Disconnected : ConnState.Failed;
		LocalIdentity = null;
		GD.Print($"[ConnectionManager] Disconnected ({(e is null ? "clean" : e.Message)})");
	}
}
