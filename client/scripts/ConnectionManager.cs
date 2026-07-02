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

	// Connect sub-stages, surfaced to the connecting modal's stage log. WS attempts skip
	// Negotiate (the address is dialled directly); WebRTC uses all five. GameNetClient
	// reports progress via NotifyStage as each boundary is crossed.
	public enum ConnectStage
	{
		Locate, // resolve the target (WebRTC: fetch the server's lobby entry)
		Negotiate, // WebRTC only: ICE gathering + offer/answer exchange
		Channel, // socket / datachannel opening
		Auth, // Hello sent, waiting for Welcome
		Sync, // Welcome received, applying the world snapshot
	}

	public enum StageState
	{
		Pending,
		Active,
		Done,
		Failed,
	}

	public sealed class StageRecord
	{
		public ConnectStage Id;
		public string Label = "";
		public StageState State = StageState.Pending;
		public int DurationMs = -1; // filled when the stage settles
		internal ulong StartMs; // Time.GetTicksMsec at activation
	}

	private readonly System.Collections.Generic.List<StageRecord> _stages = new();
	public System.Collections.Generic.IReadOnlyList<StageRecord> Stages => _stages;

	// Bumped every time the stage list is rebuilt (new attempt / redial) — the modal
	// rebuilds its log rows when this changes rather than diffing records.
	public int StageGeneration { get; private set; }
	public ConnectStage CurrentStage { get; private set; }

	// Human-readable failure detail from the last NotifyFailed (empty for silent drops).
	public string FailReason { get; private set; } = "";

	// What the connecting modal shows in the server well: a friendly name (lobby entry
	// name, else the bare host) plus the technical address line.
	public string ServerDisplayName { get; private set; } = "";
	public string ServerAddress { get; private set; } = "";

	public string TransportLabel => _mode == Transport.WebRtc ? "LOBBY · WEBRTC" : "DIRECT · WS";

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

	// Set once QuitGracefully has started — a second close request (impatient re-click of the
	// window ✕, Cmd+Q spam) must not restart the Bye drain or double-Quit.
	private bool _quitting;

	public override void _Ready()
	{
		_net = GetNode<GameNetClient>("../GameNetClient");

		// Window close (and macOS Cmd+Q) no longer kills the process outright — it raises
		// NotificationWMCloseRequest instead, which routes through QuitGracefully so the MsgBye
		// gets flushed to the server before we exit. Explicit GetTree().Quit() calls (--ui-shot,
		// showcase) are unaffected.
		GetTree().AutoAcceptQuit = false;

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

	// Submit handler for the address screen, and the entry point for --host. Direct WebSocket
	// join. The server browser stays visible underneath — the connecting modal draws over it.
	public void ConnectTo(string hostOrUrl, string? displayName = null)
	{
		ServerUrl = ToWsUrl(hostOrUrl);
		ServerAddress = ServerUrl;
		ServerDisplayName = string.IsNullOrEmpty(displayName) ? HostOf(ServerUrl) : displayName!;
		_mode = Transport.Ws;
		State = ConnState.Connecting;
		BeginStages();
		NotifyStage(ConnectStage.Channel); // address is literal — Locate completes instantly
		GD.Print($"[ConnectionManager] connecting to {ServerUrl}");
		_net.Connect(ServerUrl);
	}

	// Join a server picked from the public-lobby browser: WebRTC, signaled through LobbyBase.
	public void ConnectToLobby(string sessionId, string displayName)
	{
		ServerUrl = $"webrtc://{displayName}";
		ServerDisplayName = displayName;
		ServerAddress = $"{HostOf(LobbyBase)} · {(sessionId.Length > 12 ? sessionId[..12] : sessionId)}";
		_mode = Transport.WebRtc;
		_sessionId = sessionId;
		State = ConnState.Connecting;
		BeginStages();
		GD.Print($"[ConnectionManager] joining lobby server {displayName} ({sessionId})");
		_net.ConnectWebRtc(LobbyBase, sessionId);
	}

	// Modal calls this once the post-connect success flash has played: the player is in,
	// so the server browser underneath can finally go away.
	public void ConcludeConnect() => HideInput();

	// Cancel an in-flight connect: abort the socket (no Bye — nothing was established)
	// and fall back to the still-visible server browser.
	public void CancelConnect()
	{
		GD.Print("[ConnectionManager] connect cancelled");
		_net.Abort();
		ShowInput();
	}

	// BACK from the failed modal / LEAVE SERVER during a reconnect: drop the attempt
	// entirely and return to the server browser.
	public void AbortToBrowser()
	{
		GD.Print("[ConnectionManager] returning to server browser");
		_net.Abort();
		ServerUrl = "";
		ShowInput();
	}

	// RETRY LINK on the failed modal: re-dial the last target over the same transport.
	public void RetryLast()
	{
		if (string.IsNullOrEmpty(ServerUrl))
		{
			ShowInput();
			return;
		}
		State = ConnState.Connecting;
		BeginStages();
		GD.Print($"[ConnectionManager] retrying {ServerUrl}");
		if (_mode == Transport.WebRtc)
		{
			_net.ConnectWebRtc(LobbyBase, _sessionId);
		}
		else
		{
			NotifyStage(ConnectStage.Channel);
			_net.Connect(ServerUrl);
		}
	}

	// Leave button (Lobby): voluntarily drop the current server and return to the address screen
	// so the player can pick a different one.
	public void Leave()
	{
		GD.Print("[ConnectionManager] leaving server");
		_net.Disconnect();
		ServerUrl = "";
		ShowInput();
	}

	// AutoAcceptQuit is off (see _Ready): the window ✕ / Cmd+Q arrives here instead of
	// terminating the process, so the quit can say goodbye first.
	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
			QuitGracefully();
	}

	// Quit to desktop, but cleanly: send MsgBye so the server frees our ship immediately instead
	// of parking a 5s reconnect-grace orphan. Only call GameNetClient.Disconnect() when a link is
	// (or may be) up — Disconnect unconditionally queues the Bye byte into the lifetime _tx
	// channel, so queuing it while idle would leave a stale Bye poisoning the NEXT connection.
	// Used by the escape menu's QUIT TO DESKTOP and the window-close path above.
	public async void QuitGracefully()
	{
		if (_quitting)
			return;
		_quitting = true;
		GD.Print("[ConnectionManager] quitting to desktop");
		if (State is ConnState.Connected or ConnState.Connecting or ConnState.Reconnecting)
			_net.Disconnect();
		// 0.3s covers Disconnect's internal 200ms delay-then-cancel, letting the send loop
		// drain the Bye frame before the process goes away.
		await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
		GetTree().Quit();
	}

	// ---- Connect-stage tracking ------------------------------------------

	// Rebuild the stage log for a fresh attempt (initial dial, retry, or each auto-redial).
	private void BeginStages()
	{
		_stages.Clear();
		void Add(ConnectStage id, string label) => _stages.Add(new StageRecord { Id = id, Label = label });
		Add(ConnectStage.Locate, "LOCATE HOST");
		if (_mode == Transport.WebRtc)
			Add(ConnectStage.Negotiate, "NEGOTIATE PATH");
		Add(ConnectStage.Channel, "OPEN CHANNEL");
		Add(ConnectStage.Auth, "AUTHENTICATE");
		Add(ConnectStage.Sync, "SYNC WORLD");
		FailReason = "";
		_stages[0].State = StageState.Active;
		_stages[0].StartMs = Time.GetTicksMsec();
		CurrentStage = ConnectStage.Locate;
		StageGeneration++;
	}

	// GameNetClient reports crossing a stage boundary (already marshalled to the main
	// thread). Everything before `stage` settles as Done with its measured duration.
	public void NotifyStage(ConnectStage stage)
	{
		if (State is not (ConnState.Connecting or ConnState.Reconnecting))
			return;
		ulong now = Time.GetTicksMsec();
		foreach (var rec in _stages)
		{
			if (rec.Id < stage && rec.State != StageState.Done)
			{
				rec.DurationMs = rec.State == StageState.Active ? (int)(now - rec.StartMs) : 0;
				rec.State = StageState.Done;
			}
			else if (rec.Id == stage && rec.State != StageState.Active)
			{
				rec.State = StageState.Active;
				rec.StartMs = now;
			}
		}
		CurrentStage = stage;
	}

	// The active stage is where the link died — freeze it as Failed for the error modal.
	private void FailCurrentStage()
	{
		ulong now = Time.GetTicksMsec();
		foreach (var rec in _stages)
			if (rec.State == StageState.Active)
			{
				rec.DurationMs = (int)(now - rec.StartMs);
				rec.State = StageState.Failed;
			}
	}

	// The label of the stage that failed, lowercased for the error note's prose.
	public string FailedStageLabel()
	{
		foreach (var rec in _stages)
			if (rec.State == StageState.Failed)
				return rec.Label.ToLowerInvariant();
		return "link";
	}

	// ---- Called by GameNetClient as the socket state changes -------------

	public void NotifyConnected()
	{
		State = ConnState.Connected;
		_reconnectElapsed = 0;
		_reconnectAttempts = 0;
		_attemptInFlight = false;
		// Settle the whole stage log — the modal's success flash shows every row ✓.
		ulong now = Time.GetTicksMsec();
		foreach (var rec in _stages)
		{
			if (rec.State == StageState.Done)
				continue;
			rec.DurationMs = rec.State == StageState.Active ? (int)(now - rec.StartMs) : 0;
			rec.State = StageState.Done;
		}
		GD.Print("[ConnectionManager] connected");
	}

	public void NotifyFailed(string reason)
	{
		FailReason = reason;
		GD.PrintErr($"[ConnectionManager] connect failed: {reason}");
		// A failed redial while auto-reconnecting settles the attempt and lets the _Process
		// driver pace the next one — same as NotifyDisconnected's Reconnecting branch.
		if (State == ConnState.Reconnecting)
		{
			_attemptInFlight = false;
			_sinceAttempt = 0;
			return;
		}
		State = ConnState.Failed;
		FailCurrentStage();
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
		FailCurrentStage();
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
			State = ConnState.Disconnected; // manual Retry modal takes over
			_attemptInFlight = false;
			FailCurrentStage();
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
		BeginStages(); // fresh stage log per redial
		if (_mode == Transport.WebRtc)
		{
			_net.ConnectWebRtc(LobbyBase, _sessionId);
		}
		else
		{
			NotifyStage(ConnectStage.Channel);
			_net.Connect(ServerUrl);
		}
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

	// "host:port" of a URL, for a friendly display name when no lobby name is known.
	private static string HostOf(string url)
	{
		try
		{
			var u = new Uri(url);
			return u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}";
		}
		catch (UriFormatException)
		{
			return url;
		}
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
