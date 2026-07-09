using System.Collections.Generic;
using Godot;

// =====================================================================
//  AsteroidAmbience.cs — CLIENT PROXIMITY-AUDIO DRIVER
//
//  Two proximity-driven sounds, both keyed off Node3D positions WorldRenderer
//  already tracks and fed once per frame from WorldRenderer._Process:
//
//   1. Asteroid ambient hum + near-miss "woosh". A small pool of LOOPING
//      AudioStreamPlayer3D emitters (SfxId.AsteroidAmbient) is LATCHED onto the
//      nearest in-range rocks and each stays glued to its one physical rock until
//      that rock leaves range. Latching (not "snap to nearest each frame") is what
//      lets Godot's built-in 3D attenuation + stereo panning (+ optional doppler)
//      produce a coherent swell-and-pan as the listener streaks past — a
//      frame-by-frame reassignment would teleport the emitter and destroy it. A
//      short fade-in on each latch and a boundary crossfade near the range edge
//      keep rocks from popping in/out.
//
//   2. Probe proximity ping. A periodic one-shot (SfxId.ProbePing) fired at any
//      deployed probe's position while the local ship sits inside its ping radius,
//      throttled per-probe at a steady rate regardless of distance.
//
//  Owned + built by WorldRenderer (like EngineGlow's per-ship loops); it holds no
//  world state of its own beyond the emitter pool and the per-probe ping clocks.
//  Follows the client's no-fallback discipline: if the streams never loaded the
//  driver is simply inert.
// =====================================================================
public partial class AsteroidAmbience : Node
{
    // ---- Asteroid hum ---------------------------------------------------
    private const int PoolSize = 4;              // simultaneous humming rocks (nearest N)
    private const float HumUnitSize = 40f;       // 3D attenuation scale (matches world's large units)
    private const float HumMaxDistance = 700f;   // hard cutoff for the emitter's own falloff
    private const float LatchRange = 500f;       // a rock within this (and in-sector) grabs a free emitter
    private const float UnlatchRange = 560f;     // ...and holds it until it drifts past this (hysteresis)
    private const float BoundaryBand = 120f;     // crossfade the last stretch to 0 so rocks don't pop at the edge
    private const float FadeInRate = 4f;         // per-latch volume ease (units/s → ~0.25s), masks the latch teleport

    // ---- Probe ping -----------------------------------------------------
    private const float ProbePingRadius = 400f;  // ship must be within this of a probe to hear it
    private const float PingInterval = 2.0f;     // steady seconds between pings anywhere in range

    private sealed class Emitter
    {
        public AudioStreamPlayer3D Player = null!;
        public ulong RockId;   // the asteroid this emitter is glued to (0 = free)
        public float FadeIn;   // 0..1 envelope ramped after each fresh latch
    }

    private readonly List<Emitter> _pool = new();
    private bool _poolBuilt;

    // Per-probe ping cooldown (seconds until the next ping). Reset to 0 on exit so re-entering range pings at once.
    private readonly Dictionary<ulong, double> _probeCooldown = new();
    private readonly List<ulong> _probePrune = new();

    // Lazily build the emitter pool once the looping stream is actually loaded (SfxManager may _Ready after us).
    private void EnsurePool()
    {
        if (_poolBuilt)
            return;
        var stream = SfxManager.Instance?.GetStream(SfxManager.SfxId.AsteroidAmbient);
        if (stream == null)
            return; // stream not loaded (yet / at all) — stay inert; retry next Tick
        for (int i = 0; i < PoolSize; i++)
        {
            var p = new AudioStreamPlayer3D
            {
                Bus = "Ambient",
                Stream = stream,
                UnitSize = HumUnitSize,
                MaxDistance = HumMaxDistance,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                // The listener side (the flight Camera3D) tracks its own velocity too — together
                // they bend the loop's pitch into a woosh as the ship streaks past a rock.
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.IdleStep,
                VolumeDb = -80f, // start silent; the per-frame envelope lifts it once latched
            };
            AddChild(p);
            p.Play(); // loops run continuously — we drive audibility with VolumeDb, never Play/Stop churn
            _pool.Add(new Emitter { Player = p, RockId = 0, FadeIn = 0f });
        }
        _poolBuilt = true;
    }

    // Driven once per frame by WorldRenderer. `asteroids`/`probes` are its live node maps; `sector` is the
    // local ship's sector (rocks/probes elsewhere share world coords but must stay silent).
    public void Tick(
        float delta,
        Vector3 listenerPos,
        uint sector,
        IReadOnlyDictionary<ulong, Node3D> asteroids,
        IReadOnlyDictionary<ulong, ProbeView> probes)
    {
        EnsurePool();
        if (_poolBuilt)
            UpdateAsteroidHum(delta, listenerPos, sector, asteroids);
        UpdateProbePings(delta, listenerPos, sector, probes);
    }

    private void UpdateAsteroidHum(float delta, Vector3 listenerPos, uint sector, IReadOnlyDictionary<ulong, Node3D> asteroids)
    {
        // Pass 1 — service latched emitters: drop any whose rock vanished, left the sector, or drifted past the
        // unlatch range; otherwise glue the emitter to the rock and shape its volume by proximity.
        foreach (var e in _pool)
        {
            if (e.RockId == 0)
                continue;
            if (!asteroids.TryGetValue(e.RockId, out var node) || !InSector(node, sector))
            {
                Release(e);
                continue;
            }
            float dist = node.GlobalPosition.DistanceTo(listenerPos);
            if (dist > UnlatchRange)
            {
                Release(e);
                continue;
            }
            e.Player.GlobalPosition = node.GlobalPosition;
            e.FadeIn = Mathf.Min(1f, e.FadeIn + delta * FadeInRate);
            ApplyHumVolume(e, dist);
        }

        // Pass 2 — fill free emitters with the nearest as-yet-unlatched rocks in range. Small pool, so a simple
        // "for each free slot, take the nearest unclaimed candidate" is plenty (no full sort needed).
        for (int slot = 0; slot < _pool.Count; slot++)
        {
            var e = _pool[slot];
            if (e.RockId != 0)
                continue;
            ulong bestId = 0;
            float bestDist = LatchRange;
            foreach (var (id, node) in asteroids)
            {
                if (!InSector(node, sector) || IsLatched(id))
                    continue;
                float d = node.GlobalPosition.DistanceTo(listenerPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestId = id;
                }
            }
            if (bestId == 0)
                continue; // nothing new in range — leave the slot idle (silent)
            e.RockId = bestId;
            e.FadeIn = 0f;
            e.Player.GlobalPosition = asteroids[bestId].GlobalPosition;
            ApplyHumVolume(e, bestDist);
        }
    }

    // Volume = a near-edge boundary crossfade (silent AT UnlatchRange, full once well inside) times the
    // per-latch fade-in envelope. The emitter's own 3D attenuation still does the realistic falloff on top;
    // this envelope only kills the edge pop and the latch-teleport transient.
    private static void ApplyHumVolume(Emitter e, float dist)
    {
        float boundary = Mathf.Clamp((UnlatchRange - dist) / BoundaryBand, 0f, 1f);
        float gain = boundary * e.FadeIn;
        e.Player.VolumeDb = gain > 0.001f ? Mathf.LinearToDb(gain) : -80f;
    }

    private void Release(Emitter e)
    {
        e.RockId = 0;
        e.FadeIn = 0f;
        e.Player.VolumeDb = -80f;
    }

    private bool IsLatched(ulong rockId)
    {
        foreach (var e in _pool)
            if (e.RockId == rockId)
                return true;
        return false;
    }

    private void UpdateProbePings(float delta, Vector3 listenerPos, uint sector, IReadOnlyDictionary<ulong, ProbeView> probes)
    {
        var sfx = SfxManager.Instance;
        if (sfx == null)
            return;

        foreach (var (id, probe) in probes)
        {
            if (!InSector(probe, sector))
            {
                if (_probeCooldown.ContainsKey(id))
                    _probeCooldown[id] = 0.0; // out of the active sector — arm an immediate ping on return
                continue;
            }
            float dist = probe.GlobalPosition.DistanceTo(listenerPos);
            if (dist > ProbePingRadius)
            {
                _probeCooldown[id] = 0.0; // left range — next entry pings at once
                continue;
            }
            double cd = _probeCooldown.GetValueOrDefault(id, 0.0) - delta;
            if (cd <= 0.0)
            {
                sfx.PlayAt(SfxManager.SfxId.ProbePing, probe.GlobalPosition);
                cd = PingInterval; // steady rate regardless of distance
            }
            _probeCooldown[id] = cd;
        }

        // Prune cooldown clocks for probes that have despawned so the map mirrors the live probe set.
        if (_probeCooldown.Count > probes.Count)
        {
            _probePrune.Clear();
            foreach (var id in _probeCooldown.Keys)
                if (!probes.ContainsKey(id))
                    _probePrune.Add(id);
            foreach (var id in _probePrune)
                _probeCooldown.Remove(id);
        }
    }

    // Mirrors WorldRenderer's node "sector" meta contract (SetNodeSector): a node with no tag is treated as
    // not-in-sector (silent), never as a match.
    private static bool InSector(Node3D n, uint sector) =>
        n.HasMeta("sector") && (int)n.GetMeta("sector") == (int)sector;
}
