using System;
using Godot;
using SpacetimeDB;
using SpacetimeDB.Types;

public partial class ConnectionManager : Node
{
	private const string ServerUrl = "ws://localhost:3001";
	private const string DbName    = "stellar-allegiance";

	private DbConnection? _conn;

	public override void _Ready()
	{
		_conn = DbConnection.Builder()
			.WithUri(ServerUrl)
			.WithDatabaseName(DbName)
			.OnConnect(OnConnect)
			.OnConnectError(OnConnectError)
			.OnDisconnect(OnDisconnect)
			.Build();
	}

	public override void _Process(double delta)
	{
		_conn?.FrameTick();
	}

	private void OnConnect(DbConnection conn, Identity identity, string token)
	{
		GD.Print($"[ConnectionManager] Connected — identity: {identity}");
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
