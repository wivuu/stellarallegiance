using System;
using Godot;

// Owns "which server, and are we connected" — the front of the single native connection.
// SpacetimeDB is gone: there is exactly one link, the WebSocket to the standalone sim server
// (GameNetClient). If the client was launched without --host the FIRST thing the player sees
// is the address-input screen (ServerInputOverlay); with --host ip-or-hostname:port it connects
// straight away. GameNetClient calls the Notify* methods back as the socket's state changes.
public partial class ConnectionManager : Node
{
	// Lifecycle. AwaitingAddress = showing the address-input screen; Connecting = socket
	// opening / handshaking; Connected = Welcome received; Failed/Disconnected = link down.
	public enum ConnState { AwaitingAddress, Connecting, Connected, Failed, Disconnected }
	public ConnState State { get; private set; } = ConnState.AwaitingAddress;

	// The ws:// URL we're targeting, for the status overlay.
	public string ServerUrl { get; private set; } = "";

	private GameNetClient _net = null!;
	private ServerInputOverlay? _input;

	public override void _Ready()
	{
		_net = GetNode<GameNetClient>("../GameNetClient");

		// --host ip-or-hostname:port connects immediately; otherwise show the input screen.
		string host = "";
		var cmd = OS.GetCmdlineArgs();
		for (int i = 0; i < cmd.Length; i++)
		{
			if (cmd[i] == "--host" && i + 1 < cmd.Length) host = cmd[i + 1];
			else if (cmd[i].StartsWith("--host=")) host = cmd[i]["--host=".Length..];
		}
		// SIM_URI keeps working as a dev override (full ws:// URL).
		var simUri = OS.GetEnvironment("SIM_URI");
		if (!string.IsNullOrEmpty(simUri)) ConnectTo(simUri);
		else if (!string.IsNullOrEmpty(host)) ConnectTo(host);
		else ShowInput();
	}

	// Submit handler for the address screen, and the entry point for --host.
	public void ConnectTo(string hostOrUrl)
	{
		ServerUrl = ToWsUrl(hostOrUrl);
		HideInput();
		State = ConnState.Connecting;
		GD.Print($"[ConnectionManager] connecting to {ServerUrl}");
		_net.Connect(ServerUrl);
	}

	// Retry button (ConnectionOverlay): return to the address screen so the player can fix or
	// change the server they're pointing at.
	public void Connect() => ShowInput();

	// ---- Called by GameNetClient as the socket state changes -------------

	public void NotifyConnected()
	{
		State = ConnState.Connected;
		GD.Print("[ConnectionManager] connected");
	}

	public void NotifyFailed(string reason)
	{
		State = ConnState.Failed;
		GD.PrintErr($"[ConnectionManager] connect failed: {reason}");
	}

	public void NotifyDisconnected()
	{
		State = State == ConnState.Connected ? ConnState.Disconnected : ConnState.Failed;
	}

	// ---- Address-input screen -------------------------------------------

	private void ShowInput()
	{
		State = ConnState.AwaitingAddress;
		if (_input is not null) { _input.Visible = true; return; }
		var layer = new CanvasLayer { Name = "ServerInputLayer", Layer = 100 };
		AddChild(layer);
		_input = new ServerInputOverlay();
		layer.AddChild(_input);
		_input.Init(this);
	}

	private void HideInput()
	{
		if (_input is not null) _input.Visible = false;
	}

	// Normalize "ip-or-hostname:port" (or a full ws:// URL) into a ws:// game endpoint.
	private static string ToWsUrl(string input)
	{
		input = input.Trim();
		if (input.StartsWith("ws://") || input.StartsWith("wss://"))
			return input.Contains("/game") ? input : input.TrimEnd('/') + "/game";
		return $"ws://{input}/game";
	}
}
