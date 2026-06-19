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

	// Public lobby (public lobby) base URL — where the server browser fetches its list and where
	// WebRTC joins are signaled. Resolved from PUBLIC_LOBBY / --lobby in _Ready.
	public string LobbyBase { get; private set; } = DefaultLobby;
	private const string DefaultLobby = "https://wivuu-public-lobby-production.up.railway.app";

	private GameNetClient _net = null!;
	private ServerInputOverlay? _input;

	public override void _Ready()
	{
		_net = GetNode<GameNetClient>("../GameNetClient");

		// --host ip-or-hostname:port connects immediately; otherwise show the input screen.
		// --lobby host:port overrides the public-lobby address (else PUBLIC_LOBBY env, else default).
		string host = "", lobby = "";
		var cmd = OS.GetCmdlineArgs();
		for (int i = 0; i < cmd.Length; i++)
		{
			if (cmd[i] == "--host" && i + 1 < cmd.Length) host = cmd[i + 1];
			else if (cmd[i].StartsWith("--host=")) host = cmd[i]["--host=".Length..];
			else if (cmd[i] == "--lobby" && i + 1 < cmd.Length) lobby = cmd[i + 1];
			else if (cmd[i].StartsWith("--lobby=")) lobby = cmd[i]["--lobby=".Length..];
		}
		if (string.IsNullOrEmpty(lobby)) lobby = OS.GetEnvironment("PUBLIC_LOBBY");
		if (string.IsNullOrEmpty(lobby)) lobby = DefaultLobby;
		LobbyBase = lobby.StartsWith("http") ? lobby.TrimEnd('/') : $"http://{lobby}";

		// SIM_URI keeps working as a dev override (full ws:// URL).
		var simUri = OS.GetEnvironment("SIM_URI");
		if (!string.IsNullOrEmpty(simUri)) ConnectTo(simUri);
		else if (!string.IsNullOrEmpty(host)) ConnectTo(host);
		else ShowInput();
	}

	// Submit handler for the address screen, and the entry point for --host. Direct WebSocket join.
	public void ConnectTo(string hostOrUrl)
	{
		ServerUrl = ToWsUrl(hostOrUrl);
		HideInput();
		State = ConnState.Connecting;
		GD.Print($"[ConnectionManager] connecting to {ServerUrl}");
		_net.Connect(ServerUrl);
	}

	// Join a server picked from the public-lobby browser: WebRTC, signaled through LobbyBase.
	public void ConnectToLobby(string sessionId, string displayName)
	{
		ServerUrl = $"webrtc://{displayName}";
		HideInput();
		State = ConnState.Connecting;
		GD.Print($"[ConnectionManager] joining lobby server {displayName} ({sessionId})");
		_net.ConnectWebRtc(LobbyBase, sessionId);
	}

	// Retry button (ConnectionOverlay): return to the address screen so the player can fix or
	// change the server they're pointing at.
	public void Connect() => ShowInput();

	// Leave button (Lobby): voluntarily drop the current server and return to the address screen
	// so the player can pick a different one.
	public void Leave()
	{
		GD.Print("[ConnectionManager] leaving server");
		_net.Disconnect();
		ServerUrl = "";
		ShowInput();
	}

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
		// An intentional Leave() already returned us to the address screen and tore the socket
		// down; the resulting (deferred) socket-closed callback must NOT flip us to a "Server
		// offline"/"Connection lost" error overlay. Only a drop we didn't ask for counts.
		if (State == ConnState.AwaitingAddress) return;
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
