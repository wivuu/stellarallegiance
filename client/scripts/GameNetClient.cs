using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

// Phase-1b: the client side of the native sim server's wire protocol
// (server/Net/Protocol.cs). Active only when SIM_URI is set (e.g.
// SIM_URI=ws://localhost:8090/game) — without it the node is inert and the client runs
// the pure-STDB path unchanged.
//
// Architecture: this node owns the game socket and translates frames into the SAME row
// types the renderer already consumes — it synthesizes SpacetimeDB.Types.Ship/Base/
// Asteroid/Aleph/Sector objects and feeds WorldRenderer's Net* entry points, so the
// entire rendering/prediction/interpolation stack is untouched by the transport swap.
// STDB stays connected for defs/chat (DefRegistry is unaffected); WorldRenderer ignores
// STDB world tables in this mode (EnableNativeMode).
//
// Socket I/O runs on background tasks; received frames are queued and applied in
// _Process on the main thread (Godot scene-tree access is not thread-safe).
public partial class GameNetClient : Node
{
	public bool Active { get; private set; }
	public ulong LocalShipId { get; private set; }

	// Raised on the main thread when the server echoes a ping nonce. ShipController subscribes
	// to turn the round trip into an RTT sample (its native path has no reducer-ack to measure).
	public event Action<uint>? Pong;

	private WorldRenderer _world = null!;
	private ClientWebSocket? _ws;
	private readonly CancellationTokenSource _cts = new();
	// Per-connection cancellation, recreated on each Activate. Cancelled on lobby return
	// (match end) to drop the game socket WITHOUT tearing down the node, so the next match's
	// fresh JoinToken can re-activate a brand-new socket.
	private CancellationTokenSource? _socketCts;
	// Latched while handing authority back to STDB after a match ends — ignore any further
	// frames the socket flushes before it closes. Cleared when a new match re-activates.
	private bool _returning;
	private readonly ConcurrentQueue<byte[]> _rx = new();
	// Outbound frames are buffered here even before the socket opens (a spawn request
	// can arrive first); the send loop drains it once connected.
	private readonly Channel<byte[]> _tx = Channel.CreateUnbounded<byte[]>();

	// Last-applied row per ship so updates can hand the renderer (oldRow, newRow) — the
	// shape its LastFireTick bolt-synthesis and warp detection already key off.
	private readonly Dictionary<ulong, Ship> _rows = new();
	private readonly HashSet<ulong> _seenThisSnapshot = new();

	// Match credentials (Phase 1c). The lobby flow stays in STDB: when the match goes
	// Active the module mints this player's JoinToken (RLS: own row only) and the
	// SimEndpoint row says where to connect — both arriving here via the ordinary STDB
	// subscription. SIM_URI remains a dev override that connects immediately with no
	// credentials (the sim server accepts that only when run without --secret).
	private ConnectionManager _cm = null!;
	private string _identityHex = "";
	private byte _team;
	private string _token = "";
	private ulong _matchId;   // match epoch the token is bound to (from the JoinToken row)
	private long _expiry;     // token expiry, unix seconds (from the JoinToken row)
	private string _endpointUrl = "";

	public override void _Ready()
	{
		_world = GetNode<WorldRenderer>("../WorldRenderer");
		_cm = GetNode<ConnectionManager>("../ConnectionManager");

		var uri = OS.GetEnvironment("SIM_URI");
		if (!string.IsNullOrEmpty(uri))
		{
			Activate(uri);
			return;
		}

		_cm.Connected += conn =>
		{
			conn.Db.SimEndpoint.OnInsert += (_, row) => { _endpointUrl = row.Url; TryActivate(); };
			conn.Db.SimEndpoint.OnUpdate += (_, _, row) => { _endpointUrl = row.Url; TryActivate(); };
			conn.Db.JoinToken.OnInsert += (_, row) => OnToken(row);
			conn.Db.JoinToken.OnUpdate += (_, _, row) => OnToken(row);
		};
	}

	private void OnToken(JoinToken row)
	{
		// RLS already limits us to our own row, but verify against the live identity.
		if (_cm.LocalIdentity is { } id && row.Identity == id)
		{
			_identityHex = id.ToString();
			_team = row.Team;
			_token = row.Token;
			_matchId = row.MatchId;
			_expiry = row.Expiry;
			TryActivate();
		}
	}

	private void TryActivate()
	{
		if (!Active && _endpointUrl.Length > 0 && _token.Length > 0)
			Activate(_endpointUrl);
	}

	private void Activate(string uri)
	{
		Active = true;
		_returning = false;
		_socketCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
		_world.EnableNativeMode();
		GD.Print($"[GameNet] native sim mode — connecting to {uri}" +
			(_token.Length > 0 ? $" (team {_team}, token ready)" : " (dev, no credentials)"));
		_ = Task.Run(() => RunSocket(uri, _socketCts.Token));
	}

	// Match ended: drop the game socket and hand the world back to STDB so the durable
	// post-match/lobby flow resumes. The next match mints a fresh JoinToken (RestartMatch
	// cleared the old one) which re-fires TryActivate → a new socket.
	private void BeginLobbyReturn()
	{
		if (_returning)
			return;
		_returning = true;
		Active = false;
		GD.Print("[GameNet] match ended — returning authority to STDB lobby");
		_socketCts?.Cancel();
		while (_tx.Reader.TryRead(out _)) { }   // discard any unsent input frames
		_world.DisableNativeMode();
		_rows.Clear();
		_token = "";   // require a fresh token before re-activating
	}

	public override void _ExitTree()
	{
		_cts.Cancel();
		_tx.Writer.TryComplete();
	}

	// ---- API used by ShipController --------------------------------------

	// Hello v6: class, team, identity, matchId, expiry, token. The sim server recomputes the
	// HMAC over (identity, team, matchId, expiry) with the shared secret and constant-time
	// compares — a forged team/identity, a replayed previous-match token (different matchId),
	// or an expired one all fail. A credential-less Hello (dev SIM_URI mode) sends zero-length
	// id/token (matchId/expiry are ignored when no secret is configured server-side).
	//   layout: u8 Hello, u8 cls, u8 team, u8 idLen, id…, u64 matchId, i64 expiry, u8 tokLen, tok…
	public void RequestSpawn(byte shipClass)
	{
		var id = System.Text.Encoding.UTF8.GetBytes(_identityHex);
		var tok = System.Text.Encoding.UTF8.GetBytes(_token);
		var f = new byte[4 + id.Length + 8 + 8 + 1 + tok.Length];
		int o = 0;
		f[o++] = 1;   // Hello
		f[o++] = shipClass;
		f[o++] = _team;
		f[o++] = (byte)id.Length;
		id.CopyTo(f, o); o += id.Length;
		BitConverter.TryWriteBytes(f.AsSpan(o), _matchId); o += 8;
		BitConverter.TryWriteBytes(f.AsSpan(o), _expiry); o += 8;
		f[o++] = (byte)tok.Length;
		tok.CopyTo(f, o);
		_tx.Writer.TryWrite(f);
	}

	public void SendInput(uint tick, in ShipInputState input)
	{
		var f = new byte[30];
		f[0] = 2;   // Input
		BitConverter.TryWriteBytes(f.AsSpan(1), tick);
		BitConverter.TryWriteBytes(f.AsSpan(5), input.Thrust);
		BitConverter.TryWriteBytes(f.AsSpan(9), input.StrafeX);
		BitConverter.TryWriteBytes(f.AsSpan(13), input.StrafeY);
		BitConverter.TryWriteBytes(f.AsSpan(17), input.Yaw);
		BitConverter.TryWriteBytes(f.AsSpan(21), input.Pitch);
		BitConverter.TryWriteBytes(f.AsSpan(25), input.Roll);
		f[29] = (byte)((input.Firing ? 1 : 0) | (input.Boost ? 2 : 0) | (input.Coast ? 4 : 0));
		_tx.Writer.TryWrite(f);
	}

	// Send a ping nonce; the server bounces it back as a Pong (see Apply case 6). ShipController
	// owns the cadence + send-time bookkeeping so the RTT feeds the same adaptive-lead path the
	// STDB build drives off reducer acks.
	public void SendPing(uint nonce)
	{
		var f = new byte[5];
		f[0] = 3;   // Ping
		BitConverter.TryWriteBytes(f.AsSpan(1), nonce);
		_tx.Writer.TryWrite(f);
	}

	// ---- Socket I/O (background) ------------------------------------------

	private async Task RunSocket(string uri, CancellationToken ct)
	{
		try
		{
			_ws = new ClientWebSocket();
			await _ws.ConnectAsync(new Uri(uri), ct);

			var send = Task.Run(async () =>
			{
				await foreach (var frame in _tx.Reader.ReadAllAsync(ct))
					await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
			}, ct);

			var buf = new byte[512 * 1024];
			while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
			{
				int len = 0;
				WebSocketReceiveResult r;
				do
				{
					r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), ct);
					len += r.Count;
				} while (!r.EndOfMessage && len < buf.Length);
				if (r.MessageType == WebSocketMessageType.Close)
					break;
				_rx.Enqueue(buf.AsSpan(0, len).ToArray());
			}
			await send.ContinueWith(_ => { });
		}
		catch (OperationCanceledException) { }
		catch (Exception e)
		{
			GD.PrintErr($"[GameNet] socket error: {e.Message}");
		}
	}

	// ---- Frame application (main thread) -----------------------------------

	public override void _Process(double delta)
	{
		while (_rx.TryDequeue(out var frame))
			Apply(frame);
	}

	private void Apply(byte[] f)
	{
		if (_returning)
			return;   // socket is closing; ignore whatever it flushes on the way out
		using var r = new BinaryReader(new MemoryStream(f));
		switch (r.ReadByte())
		{
			case 1: ApplyWelcome(r); break;
			case 2:
				LocalShipId = r.ReadUInt64();
				GD.Print($"[GameNet] assigned ship {LocalShipId}");
				break;
			case 3: ApplySnapshot(r); break;
			case 4: ApplyShipGone(r.ReadUInt64()); break;
			case 5: ApplyBases(r); break;
			case 6: Pong?.Invoke(r.ReadUInt32()); break;
		}
	}

	private void ApplyBases(BinaryReader r)
	{
		byte count = r.ReadByte();
		for (int i = 0; i < count; i++)
			_world.NetUpdateBaseHealth(r.ReadUInt64(), r.ReadSingle());
	}

	// Must match server/Net/Protocol.cs Version. Bump together when a frame layout changes.
	private const byte ProtocolVersion = 6;

	private void ApplyWelcome(BinaryReader r)
	{
		byte version = r.ReadByte();
		if (version != ProtocolVersion)
		{
			GD.PrintErr($"[GameNet] protocol mismatch: server v{version}, client v{ProtocolVersion}. " +
				"Restart the sim server with the current build (e.g. kill the stale process on :8090).");
			_socketCts?.Cancel();   // refuse to misread frames; drop the connection
			return;
		}
		r.ReadInt32();   // clientId
		r.ReadByte();    // team (the ship row carries it too)
		r.ReadUInt32();  // tick
		r.ReadSingle();  // dt

		ushort sectors = r.ReadUInt16();
		for (int i = 0; i < sectors; i++)
		{
			var row = new Sector { SectorId = r.ReadUInt32(), Name = "", Radius = r.ReadSingle() };
			_world.NetAddSector(row);
		}
		ushort bases = r.ReadUInt16();
		for (int i = 0; i < bases; i++)
		{
			var row = new Base
			{
				BaseId = r.ReadUInt64(), Team = r.ReadByte(), SectorId = r.ReadUInt32(),
				PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(),
			};
			r.ReadSingle();   // radius (client renders from BaseDef, like STDB mode)
			row.Health = r.ReadSingle();
			_world.NetAddBase(row);
		}
		uint asteroids = r.ReadUInt32();
		for (int i = 0; i < asteroids; i++)
		{
			var row = new Asteroid
			{
				AsteroidId = r.ReadUInt64(), SectorId = r.ReadUInt32(),
				PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(),
				Radius = r.ReadSingle(),
			};
			// Cosmetic shape (v5): variant index + fixed orientation, drawn server-side from
			// the same DetRng sequence as the module — so rocks render as their real GLB meshes,
			// not grey spheres. Out-of-range index -> "" -> WorldRenderer sphere fallback.
			byte variant = r.ReadByte();
			row.RotX = r.ReadSingle(); row.RotY = r.ReadSingle(); row.RotZ = r.ReadSingle();
			row.Variant = AsteroidShapes.NameForIndex(variant);
			_world.NetAddAsteroid(row);
		}
		ushort alephs = r.ReadUInt16();
		for (int i = 0; i < alephs; i++)
		{
			var row = new Aleph
			{
				AlephId = r.ReadUInt64(), SectorId = r.ReadUInt32(), DestSectorId = r.ReadUInt32(),
				PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(),
			};
			_world.NetAddAleph(row);
		}
		GD.Print($"[GameNet] world received — {sectors} sectors, {bases} bases, {asteroids} asteroids");
	}

	private void ApplySnapshot(BinaryReader r)
	{
		uint tick = r.ReadUInt32();
		byte phase = r.ReadByte();
		byte winner = r.ReadByte();
		_world.NetSetMatch(tick, phase, winner);
		if (phase == 2)              // Ended: hand authority back to STDB for the post-match flow
			BeginLobbyReturn();

		ushort count = r.ReadUInt16();
		_seenThisSnapshot.Clear();
		for (int i = 0; i < count; i++)
		{
			// Quantized record (v4) — must mirror server/Net/Protocol.cs WriteShip exactly;
			// pos/rot/vel/etc. round-trip through the shared WireQuant codec.
			ulong id = r.ReadUInt64();
			byte team = r.ReadByte();
			byte cls = r.ReadByte();
			byte flags = r.ReadByte();
			ushort sector = r.ReadUInt16();
			short px = r.ReadInt16(), py = r.ReadInt16(), pz = r.ReadInt16();
			uint rot = r.ReadUInt32();
			ushort vx = r.ReadUInt16(), vy = r.ReadUInt16(), vz = r.ReadUInt16();
			ushort ax = r.ReadUInt16(), ay = r.ReadUInt16(), az = r.ReadUInt16();
			ushort ab = r.ReadUInt16();
			ushort hp = r.ReadUInt16();
			uint lastInput = r.ReadUInt32();
			uint lastFire = r.ReadUInt32();

			_rows.TryGetValue(id, out var prev);
			var row = new Ship();
			row.ShipId = id;
			row.Team = team;
			row.Class = (ShipClass)cls;
			row.IsPig = (flags & 1) != 0;   // ShipFlagPig — render as a drone (HUD highlight)
			row.IsPod = (flags & 2) != 0;   // ShipFlagPod — escape pod mesh, pod flight stats
			row.SectorId = sector;
			row.PosX = WireQuant.UnpackPos(px); row.PosY = WireQuant.UnpackPos(py); row.PosZ = WireQuant.UnpackPos(pz);
			WireQuant.UnpackQuat(rot, out float rx, out float ry, out float rz, out float rw);
			row.RotX = rx; row.RotY = ry; row.RotZ = rz; row.RotW = rw;
			row.VelX = WireQuant.UnpackHalf(vx); row.VelY = WireQuant.UnpackHalf(vy); row.VelZ = WireQuant.UnpackHalf(vz);
			row.AngVelX = WireQuant.UnpackHalf(ax); row.AngVelY = WireQuant.UnpackHalf(ay); row.AngVelZ = WireQuant.UnpackHalf(az);
			row.AbPower = WireQuant.UnpackHalf(ab);
			row.Health = WireQuant.UnpackHalf(hp);
			row.LastInputTick = lastInput;
			row.LastFireTick = lastFire;
			// Mass isn't on the wire: the server seeds it from the same shared class stats,
			// so re-derive it identically (a pod uses the slow Pod profile so prediction
			// integrates it the same — StateFromRow reads it back).
			row.Mass = FlightModel.StatsFor(cls, row.IsPod).Mass;

			_seenThisSnapshot.Add(id);
			if (prev is null)
				_world.NetInsertShip(row, id == LocalShipId);
			else
				_world.NetUpdateShip(prev, row);
			_rows[id] = row;
		}
	}

	private void ApplyShipGone(ulong shipId)
	{
		if (_rows.Remove(shipId, out var row))
			_world.NetDeleteShip(row);
	}
}
