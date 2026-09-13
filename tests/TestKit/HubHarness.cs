using System.Collections.Concurrent;
using SimServer.Net;
using StellarAllegiance.Shared.Net;

namespace TestKit;

// In-memory IClientTransport for the hub-level tests: feed client->server frames, capture the
// server->client frames the hub sends. Receive blocks until a frame is fed (or the token cancels,
// which reads as "transport closed").
public sealed class FakeHubTransport : IClientTransport
{
    private readonly BlockingCollection<byte[]> _in = new();
    public readonly ConcurrentQueue<byte[]> Sent = new();

    public void Feed(byte[] frame) => _in.Add(frame);

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            byte[] f = await Task.Run(() => _in.Take(ct), ct);
            Array.Copy(f, buffer, f.Length);
            return f.Length;
        }
        catch (OperationCanceledException)
        {
            return -1; // transport closed
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Sent.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct) => ValueTask.CompletedTask;

    // The frames of one message type the hub has sent so far, oldest first.
    public List<byte[]> SentOf(byte msgId)
    {
        var list = new List<byte[]>();
        foreach (var f in Sent)
            if (f.Length > 0 && f[0] == msgId)
                list.Add(f);
        return list;
    }

    // Poll until the hub has sent at least `count` frames of a type (the receive loop runs on a task).
    public byte[]? WaitFor(byte msgId, int count = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var have = SentOf(msgId);
            if (have.Count >= count)
                return have[count - 1];
            Thread.Sleep(10);
        }
        return null;
    }
}

// The inbound frames a test feeds the hub, built from the shared wire definitions (never hand-packed).
public static class HubFrames
{
    public static byte[] Hello(string name, string secret = "", string reconnectToken = "", string joinToken = "") =>
        new HelloMessage
        {
            Secret = secret,
            Name = name,
            ReconnectToken = reconnectToken,
            JoinToken = joinToken,
        }.ToBytes();

    public static byte[] SetTeam(byte team) => new SetTeamMessage { Team = team }.ToBytes();

    public static byte[] SetReady(bool ready) => new SetReadyMessage { Ready = ready }.ToBytes();

    // A bare spawn: hull default cargo, authored loadout, server default launch base.
    public static byte[] Spawn(byte shipClass, ulong launchBaseId = 0) =>
        new SpawnMessage
        {
            ShipClass = shipClass,
            LaunchBaseId = launchBaseId,
            Cargo = Array.Empty<StellarAllegiance.Shared.CargoLoadDef>(),
            Mounts = Array.Empty<MountOverrideRecord>(),
        }.ToBytes();

    // A docked captain's crew advertisement: the hull teammates may crew plus the FULL per-station
    // gun pick list (empty = every station keeps its authored gun). ClassId 0xFF retracts.
    public static byte[] HangarIntent(byte classId, params (byte hpIndex, uint weaponId)[] turrets)
    {
        var picks = new MountOverrideRecord[turrets.Length];
        for (int i = 0; i < picks.Length; i++)
            picks[i] = new MountOverrideRecord { HpIndex = turrets[i].hpIndex, WeaponId = turrets[i].weaponId };
        return new HangarIntentMessage { ClassId = classId, Turrets = picks }.ToBytes();
    }

    // Claim (mode 1) or give up (mode 0) a turret station on a teammate's docked ship.
    public static byte[] CrewSeat(byte mode, int captainId, byte seatIndex) =>
        new CrewSeatMessage
        {
            Mode = mode,
            CaptainId = captainId,
            SeatIndex = seatIndex,
        }.ToBytes();

    // A riding gunner's turret input: the SHIP-LOCAL aim direction plus the flag bits
    // (TurretAim.FlagFiring). HELD semantics — the server keeps the latest frame per gunner and
    // fires on its own cadence for as long as Firing is set, so a test sends it once and steps.
    public static byte[] TurretInput(uint tick, StellarAllegiance.Shared.Vec3 aim, byte flags) =>
        new TurretInputMessage
        {
            Tick = tick,
            AimX = aim.X,
            AimY = aim.Y,
            AimZ = aim.Z,
            Flags = flags,
        }.ToBytes();
}
