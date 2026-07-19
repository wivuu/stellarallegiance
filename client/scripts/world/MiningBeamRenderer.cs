using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// Mining beams (client-only VFX). For every ship whose ShipFlagMining is set and whose mesh is visible,
// ensure a MiningBeam child exists and point it at the rock it's harvesting; tear a beam down on the flag's
// falling edge (or when the ship leaves / hides / has no rock to aim at). The server streams the flag plus
// (via MsgMinerTargets) the exact target rock.
public sealed class MiningBeamRenderer
{
    private readonly IShipQuery _ships;
    private readonly AsteroidRenderer _rocks;

    // One active mining beam per ship currently transferring ore (ShipFlagMining). Attached as a child of
    // the ship node and torn down on the flag's falling edge (Tick). Purely client-side VFX.
    private readonly Dictionary<ulong, MiningBeam> _miningBeams = new();
    private readonly List<ulong> _miningBeamPrune = new(); // scratch: beams to drop this frame

    // Streamed (MsgMinerTargets): shipId -> the rock that miner is actively harvesting, so the beam aims at
    // the real target instead of guessing the nearest He3. Replaced wholesale each frame it arrives; a
    // miner that stops mining drops out of the broadcast (and its beam clears on the flag).
    private Dictionary<ulong, ulong> _minerTargetRock = new();

    public MiningBeamRenderer(IShipQuery ships, AsteroidRenderer rocks)
    {
        _ships = ships;
        _rocks = rocks;
    }

    // MsgMinerTargets: replace the shipId -> target-rock map wholesale. A ship that stopped mining simply
    // isn't in the new map; its beam clears on the ShipFlagMining falling edge in Tick.
    public void NetUpdateMinerTargets(Dictionary<ulong, ulong> map) => _minerTargetRock = map;

    // True while MsgMinerTargets says this miner is actively harvesting that exact rock — the F3 map uses it
    // to dismiss a commander MINE waypoint once the order is fulfilled (the miner arrived and its beam is on
    // the ordered rock), the miner analog of IsRockUnderConstruction for constructors.
    public bool IsMinerHarvesting(ulong shipId, ulong rockId) =>
        _minerTargetRock.TryGetValue(shipId, out ulong target) && target == rockId;

    // Per-frame: drive/create a beam for each actively-mining visible ship, then prune the rest. `camPos`
    // is the shadow/camera reference the beam's debris chips ray from.
    public void Tick(Vector3 camPos)
    {
        // Drive / create a beam for each actively-mining visible ship.
        foreach (var (id, node) in _ships.Nodes)
        {
            if (node is not RemoteShip rs || !rs.IsMining || !rs.Visible)
                continue;
            if (MiningTargetRock(id, rs.GlobalPosition) is not (Vector3 rockCenter, float rockRadius, var rockMesh))
                continue; // no known/visible rock — hold off (drop any stale beam in the prune below)

            if (!_miningBeams.TryGetValue(id, out var beam))
            {
                beam = new MiningBeam { Name = "MiningBeam" };
                rs.AddChild(beam);
                _miningBeams[id] = beam;
            }
            // Fire from the ship's weapon-hardpoint muzzle (not the hull centre); debris chips off the
            // rock's real mesh surface via rockMesh.
            beam.UpdateBeam(rs.MiningMuzzleWorld(), rockCenter, rockRadius, rockMesh, camPos);
        }

        // Prune beams whose ship stopped mining, hid, left, or lost its target rock.
        if (_miningBeams.Count > 0)
        {
            _miningBeamPrune.Clear();
            foreach (var (id, _) in _miningBeams)
            {
                bool keep =
                    _ships.Nodes.TryGetValue(id, out var node)
                    && node is RemoteShip rs
                    && rs.IsMining
                    && rs.Visible
                    && MiningTargetRock(id, rs.GlobalPosition) is not null;
                if (!keep)
                    _miningBeamPrune.Add(id);
            }
            foreach (var id in _miningBeamPrune)
            {
                if (_miningBeams.Remove(id, out var beam) && GodotObject.IsInstanceValid(beam))
                    beam.QueueFree();
            }
        }
    }

    // The rock a mining ship is aiming at, as (center, current radius). Prefer the server-streamed exact
    // target (MsgMinerTargets) when that rock is known + in view; otherwise fall back to the nearest in-view
    // He3 rock so a pre-v33 server (or a not-yet-arrived frame) still shows a beam.
    private (Vector3 Center, float Radius, MeshInstance3D? Node)? MiningTargetRock(ulong shipId, Vector3 fromPos)
    {
        if (
            _minerTargetRock.TryGetValue(shipId, out ulong rockId)
            && _rocks.Nodes.TryGetValue(rockId, out var node)
            && _rocks.GetAsteroid(rockId) is { } rock
        )
            return (node.GlobalPosition, rock.CurrentRadius, node as MeshInstance3D);
        return NearestHe3Rock(fromPos);
    }

    // The nearest visible He3 rock (with ore remaining) to `from`, as (center, current radius, node), or
    // null if none is in view. The fallback aim when the streamed target rock isn't known/visible. The node
    // is handed to the beam so its chips ray off the rock's real mesh surface.
    private (Vector3 Center, float Radius, MeshInstance3D? Node)? NearestHe3Rock(Vector3 from)
    {
        (Vector3, float, MeshInstance3D?)? best = null;
        float bestSq = float.MaxValue;
        foreach (var (id, node) in _rocks.InView())
        {
            if (_rocks.GetAsteroid(id) is not { } rock)
                continue;
            if (rock.RockClass != (byte)RockClass.Helium3 || rock.OrePct <= 0)
                continue;
            float dSq = (node.GlobalPosition - from).LengthSquared();
            if (dSq < bestSq)
            {
                bestSq = dSq;
                best = (node.GlobalPosition, rock.CurrentRadius, node as MeshInstance3D);
            }
        }
        return best;
    }
}
