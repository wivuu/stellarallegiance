using System;
using Godot;

// Owns "which server, and are we connected" — the front of the single native connection.
// SpacetimeDB is gone: there is exactly one link, the WebSocket to the standalone sim server
// (GameNetClient). If the client was launched without --host the FIRST thing the player sees
// is the address-input screen (ServerLobbyOverlay); with --host ip-or-hostname:port it connects
// straight away. GameNetClient calls the Notify* methods back as the socket's state changes.
public partial class ConnectionManager : Node
{
	// Lifecycle. AwaitingAddress = showing the address-input screen; Connecting = socket
	// opening / handshaking; Connected = Welcome received; Failed/Disconnected = link down.
	public enum ConnState
	{
		AwaitingAddress,
		Connecting,
		Connected,
		Failed,
		Disconnected,
		Reconnecting, // unexpected drop — auto-redialing the same server, world still rendered
	}

	public ConnState State { get; private set; } = ConnState.AwaitingAddress;

	// How we last reached the current server, so a reconnect redials the same way.
	private enum Transport
	{
		Ws,
		WebRtc,
	}

	private Transport _mode = Transport.Ws;
	private string _sessionId = ""; // WebRtc only: the public-lobby session to rejoin

	// Auto-reconnect pacing. After an attempt settles (fails), the next one waits ReconnectInterval;
	// the whole loop gives up after ReconnectMax, falling through to the manual overlay. A new
	// attempt never starts while one is still in flight, so a slow WebRTC re-negotiation isn't
	// cancelled out from under itself.
	private const double ReconnectInterval = 1.5;
	private const double ReconnectMax = 20.0;
	private double _sinceAttempt;
	private double _reconnectElapsed;
	private int _reconnectAttempts;
	private bool _attemptInFlight;

	// Live attempt counter, surfaced to the overlay countdown.
	public int ReconnectAttempt => _reconnectAttempts;

	// The ws:// URL we're targeting, for the status overlay.
	public string ServerUrl { get; private set; } = "";

	// Public lobby (public lobby) base URL — where the server browser fetches its list and where
	// WebRTC joins are signaled. Resolved from PUBLIC_LOBBY / --lobby in _Ready.
	public string LobbyBase { get; private set; } = DefaultLobby;
	private const string DefaultLobby = "https://wivuu-public-lobby-production.up.railway.app";

	private GameNetClient _net = null!;
	private ServerLobbyOverlay? _input;

	public override void _Ready()
	{
		_net = GetNode<GameNetClient>("../GameNetClient");

		// --host ip-or-hostname:port connects immediately; otherwise show the input screen.
		// --lobby host:port overrides the public-lobby address (else PUBLIC_LOBBY env, else default).
		string host = "",
			lobby = "";
		var cmd = OS.GetCmdlineArgs();
		for (int i = 0; i < cmd.Length; i++)
		{
			if (cmd[i] == "--host" && i + 1 < cmd.Length)
				host = cmd[i + 1];
			else if (cmd[i].StartsWith("--host="))
				host = cmd[i]["--host=".Length..];
			else if (cmd[i] == "--lobby" && i + 1 < cmd.Length)
				lobby = cmd[i + 1];
			else if (cmd[i].StartsWith("--lobby="))
				lobby = cmd[i]["--lobby=".Length..];
		}
		if (string.IsNullOrEmpty(lobby))
			lobby = OS.GetEnvironment("PUBLIC_LOBBY");
		if (string.IsNullOrEmpty(lobby))
			lobby = DefaultLobby;
		LobbyBase = lobby.StartsWith("http") ? lobby.TrimEnd('/') : $"http://{lobby}";

		// SIM_URI keeps working as a dev override (full ws:// URL).
		var simUri = OS.GetEnvironment("SIM_URI");
		if (!string.IsNullOrEmpty(simUri))
			ConnectTo(simUri);
		else if (!string.IsNullOrEmpty(host))
			ConnectTo(host);
		else
			ShowInput();
	}

	// Hand the pilot name the player typed on the start screen to the net client so the next
	// connect's Hello carries it. The overlay calls this before either ConnectTo/ConnectToLobby.
	public void SetPilotName(string name) => _net.SetPilotName(name);

	// Shared-secret password from the direct-connect modal; rides the next Hello frame.
	public void SetJoinSecret(string secret) => _net.SetJoinSecret(secret);

	// Submit handler for the address screen, and the entry point for --host. Direct WebSocket join.
	public void ConnectTo(string hostOrUrl)
	{
		ServerUrl = ToWsUrl(hostOrUrl);
		_mode = Transport.Ws;
		HideInput();
		State = ConnState.Connecting;
		GD.Print($"[ConnectionManager] connecting to {ServerUrl}");
		_net.Connect(ServerUrl);
	}

	// Join a server picked from the public-lobby browser: WebRTC, signaled through LobbyBase.
	public void ConnectToLobby(string sessionId, string displayName)
	{
		ServerUrl = $"webrtc://{displayName}";
		_mode = Transport.WebRtc;
		_sessionId = sessionId;
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
		_reconnectElapsed = 0;
		_reconnectAttempts = 0;
		_attemptInFlight = false;
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
		if (State == ConnState.AwaitingAddress)
			return;
		// A failed redial while already reconnecting: mark the attempt settled so the _Process
		// driver paces and fires the next one (until ReconnectMax). Don't flip to an error overlay.
		if (State == ConnState.Reconnecting)
		{
			_attemptInFlight = false;
			_sinceAttempt = 0;
			return;
		}
		// A live link that dropped out from under us: keep the stale world rendered and start
		// auto-reconnecting to the same server (the driver in _Process redials). If the connect
		// was never up (Connecting/Failed), there's nothing to reclaim — show the error overlay.
		if (State == ConnState.Connected)
		{
			State = ConnState.Reconnecting;
			_reconnectElapsed = 0;
			_reconnectAttempts = 0;
			_attemptInFlight = false;
			_sinceAttempt = ReconnectInterval; // attempt on the next frame
			GD.Print("[ConnectionManager] connection lost — auto-reconnecting");
			return;
		}
		State = ConnState.Failed;
	}

	// Drives the auto-reconnect: while Reconnecting, redial the same server every ReconnectInterval
	// until NotifyConnected flips us to Connected, or ReconnectMax elapses and we surface the
	// manual "Connection lost" overlay.
	public override void _Process(double delta)
	{
		if (State != ConnState.Reconnecting)
			return;

		_reconnectElapsed += delta;
		if (_reconnectElapsed >= ReconnectMax)
		{
			GD.Print("[ConnectionManager] auto-reconnect timed out");
			State = ConnState.Disconnected; // manual Retry overlay takes over
			_attemptInFlight = false;
			return;
		}

		if (_attemptInFlight)
			return; // let the current dial settle (esp. a slow WebRTC negotiation) before retrying

		_sinceAttempt += delta;
		if (_sinceAttempt < ReconnectInterval)
			return;
		_sinceAttempt = 0;
		_reconnectAttempts++;
		_attemptInFlight = true;
		GD.Print($"[ConnectionManager] reconnect attempt {_reconnectAttempts} to {ServerUrl}");
		if (_mode == Transport.WebRtc)
			_net.ConnectWebRtc(LobbyBase, _sessionId);
		else
			_net.Connect(ServerUrl);
	}

	// "Leave & Return to Lobby" during a reconnect: give up the ship the server may still be
	// holding (clear the reconnect token + drop the stale world) but stay on the server — the
	// auto-reconnect keeps running and we rejoin fresh into the team lobby instead of the ship.
	public void AbandonReconnect()
	{
		GD.Print("[ConnectionManager] abandoning ship — rejoining lobby");
		_net.GiveUpShip();
		_sinceAttempt = ReconnectInterval; // redial promptly into the lobby
	}

	// ---- Address-input screen -------------------------------------------

	private void ShowInput()
	{
		State = ConnState.AwaitingAddress;
		if (_input is not null)
		{
			_input.Visible = true;
			return;
		}
		var layer = new CanvasLayer { Name = "ServerInputLayer", Layer = 100 };
		AddChild(layer);
		_input = new ServerLobbyOverlay();
		layer.AddChild(_input);
		_input.Init(this);
	}

	private void HideInput()
	{
		if (_input is not null)
			_input.Visible = false;
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
