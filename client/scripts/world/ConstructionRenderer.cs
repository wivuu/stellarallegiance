using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Base construction (v37): one BuildSphere per rock a constructor is actively raising a base on, driven by
// the MsgConstructorBuilds stream (NetUpdateConstructorBuilds). Keyed by rock id; created when the build
// appears, grown by phase/progress, freed when the build drops out (base completes). Owns its BuildSphere /
// ConstructorDebris nodes under the injected `_effects` container. Reaches the rocks (IAsteroidQuery-style,
// via AsteroidRenderer), the drones (IShipQuery), the collision world, the sector view, and the def
// registry through injected seams. Implements IBuildQuery (CollisionSystem's build-contact gate +
// TargetMarkers' rock-lock suppression) and IBuildRockSink (AsteroidRenderer.NetRemoveRock stashes a
// consumed rock's last radius here so the sphere keeps growing after the rock node is gone).
public sealed class ConstructionRenderer : IBuildQuery, IBuildRockSink
{
    private readonly Node3D _effects;
    private readonly AsteroidRenderer _rocks;
    private readonly CollisionWorld _collisionWorld;
    private readonly DefRegistry _defs;
    private readonly IShipQuery _ships;
    private readonly SectorView _sectors;

    public ConstructionRenderer(
        Node3D effects,
        AsteroidRenderer rocks,
        CollisionWorld collisionWorld,
        DefRegistry defs,
        IShipQuery ships,
        SectorView sectors
    )
    {
        _effects = effects;
        _rocks = rocks;
        _collisionWorld = collisionWorld;
        _defs = defs;
        _ships = ships;
        _sectors = sectors;
    }

    // Base construction (v37): one BuildSphere per rock a constructor is actively raising a base on, driven
    // by the MsgConstructorBuilds stream. Keyed by rock id; created when the build appears, grown by
    // phase/progress, freed when the build drops out (base completes).
    public struct ConstructorBuild
    {
        public ulong ShipId,
            RockId;
        public byte Phase;
        public float Progress;
    }

    private List<ConstructorBuild> _constructorBuilds = new();
    private readonly Dictionary<ulong, BuildSphere> _buildSpheres = new();
    private readonly List<ulong> _buildSpherePrune = new(); // scratch

    // Rock-spitting debris spray per active build, live only while the drone SINKS into the rock (phase 1).
    private readonly Dictionary<ulong, ConstructorDebris> _constructorDebris = new();
    private readonly List<ulong> _constructorDebrisPrune = new(); // scratch

    // Last-known radius per active build's rock, so the sphere keeps growing after the rock despawns
    // mid-build (a finished base consumes its asteroid — the rock node is gone but the sphere lives on).
    private readonly Dictionary<ulong, float> _buildRockRadius = new();

    // IBuildRockSink — AsteroidRenderer.NetRemoveRock stashes a consumed rock's last radius here (only when
    // a sphere exists to feed) so the sphere keeps growing after the rock node is gone.
    public bool HasBuildSphere(ulong id) => _buildSpheres.ContainsKey(id);

    public void StashRockRadius(ulong id, float radius) => _buildRockRadius[id] = radius;

    // Whether this ship has a row in the live build stream (MsgConstructorBuilds) — i.e. it is a constructor
    // in its Aligning/Approaching/Sinking/Building window at its target rock. The list is at most a few
    // drones, so a linear scan per visible ship is trivial.
    public bool HasBuildRow(ulong shipId) // IBuildQuery (CollisionSystem's build-contact gate)
    {
        foreach (var b in _constructorBuilds)
            if (b.ShipId == shipId)
                return true;
        return false;
    }

    // Whether a constructor has claimed this rock for a base (it has a row in the live build stream, any
    // phase from Aligning through Building). Such a rock is about to become a base, so it drops out of
    // Tab/lock targeting (TargetMarkers) — you can't nav-lock a rock that's being consumed.
    public bool IsRockUnderConstruction(ulong rockId)
    {
        foreach (var b in _constructorBuilds)
            if (b.RockId == rockId)
                return true;
        return false;
    }

    // MsgConstructorBuilds: replace the active-build list wholesale. Tick reconciles the BuildSphere nodes
    // against it each frame (a build that completed/cancelled drops out → its sphere FADES out and
    // self-frees; the finished base arrives via the normal reveal path). The server sends a brief 0-count
    // keepalive after builds end so this drop is reliably seen despite lossy delivery.
    public void NetUpdateConstructorBuilds(List<ConstructorBuild> builds) => _constructorBuilds = builds;

    // A world rebuild frees the BuildSphere/ConstructorDebris nodes via the coordinator's _effects sweep;
    // clear the id-keyed tracking so nothing dangles into the fresh world.
    public void Reset()
    {
        _buildSpheres.Clear();
        _buildSpherePrune.Clear();
        _constructorDebris.Clear();
        _constructorDebrisPrune.Clear();
        _buildRockRadius.Clear();
        _constructorBuilds.Clear();
    }

    // Grow/place a glowing sphere enveloping each rock a constructor is building on; free spheres whose
    // build dropped out. Envelop fraction ramps through the phases (align → sink → build) so the sphere
    // gradually swallows the asteroid, peaking just past the rock surface as the base completes.
    public void Tick()
    {
        var live = new HashSet<ulong>();
        foreach (var b in _constructorBuilds)
        {
            // No sphere during ALIGNING (phase 0) — it only appears once the drone starts sinking into the
            // rock (phase 1), when the meshes begin to intersect.
            if (b.Phase < 1)
                continue;
            // Resolve the rock's live position/radius. Once it despawns (a finished base consumes it, or it
            // goes fogged), fall back to the last-known radius + the sphere's held position so the sphere
            // keeps growing rather than blinking out. A build we never had a rock for is skipped.
            Node3D? node = _rocks.Nodes.TryGetValue(b.RockId, out var n) ? n : null;
            Asteroid? rock = node is not null ? _rocks.GetAsteroid(b.RockId) : null;
            if (rock is null && !_buildSpheres.ContainsKey(b.RockId))
                continue; // never saw the rock and have no sphere to anchor — nothing to draw
            live.Add(b.RockId);

            var (sphere, worldR) = UpdateBuildSphereGeometry(b, node, rock);
            RegisterBuildSphereCollision(b, rock, worldR);
            UpdateBuildSphereCover(sphere, b);
            UpdateConstructorDebris(b, rock, sphere);
            LatchDroneHideForBuild(b);
            DissolveBuildRock(b, node);
        }
        // A build that completed/cancelled drops out of the stream. Don't free its sphere instantly — FADE
        // it (the finished base has appeared underneath via the reveal path); it self-frees.
        _buildSpherePrune.Clear();
        foreach (var kv in _buildSpheres)
            if (!live.Contains(kv.Key))
                _buildSpherePrune.Add(kv.Key);
        foreach (var id in _buildSpherePrune)
        {
            _buildSpheres[id].BeginFade();
            _buildSpheres.Remove(id);
            _collisionWorld.RemoveBuildSphere(id); // build ended — stop predicting a bounce off its shell
            _buildRockRadius.Remove(id); // build's done — drop its cached rock radius
            // If the rock still exists, the build CANCELLED (a completion would have consumed it via
            // MsgRockGone) — un-dim the rock we were fading so it returns to its normal opaque look.
            if (_rocks.Nodes.TryGetValue(id, out var rockNode))
                NodeFx.DimNode(rockNode, FadeController.RestTransparencyFor(rockNode));
        }
        // A build that dropped out while still sinking (cancelled) leaves an orphaned debris spray — stop it.
        _constructorDebrisPrune.Clear();
        foreach (var kv in _constructorDebris)
            if (!live.Contains(kv.Key))
                _constructorDebrisPrune.Add(kv.Key);
        foreach (var id in _constructorDebrisPrune)
        {
            _constructorDebris[id].Stop();
            _constructorDebris.Remove(id);
        }
    }

    // Grow/position the glowing sphere enveloping the rock a constructor is building on, creating it on
    // first sight. Envelop fraction ramps through the phases (sink → build) so the sphere gradually swallows
    // the asteroid, peaking just past the rock surface as the base completes.
    private (BuildSphere sphere, float worldR) UpdateBuildSphereGeometry(ConstructorBuild b, Node3D? node, Asteroid? rock)
    {
        if (!_buildSpheres.TryGetValue(b.RockId, out var sphere))
        {
            sphere = new BuildSphere();
            _effects.AddChild(sphere);
            _buildSpheres[b.RockId] = sphere;
        }
        if (rock is not null)
        {
            sphere.GlobalPosition = node!.GlobalPosition;
            _sectors.SetNodeSector(sphere, rock.SectorId);
            _buildRockRadius[b.RockId] = MathF.Max(2f, rock.CurrentRadius);
        }
        // Envelop radius (world units). Phase 1 (sink) BEGINS at surface contact and its progress is the
        // drone's physical embed-depth fraction (v38), so the sphere emerges from the rock CENTER and grows
        // with the hull's actual descent out to the rock surface. Phase 2 (build, the station's
        // build-time-seconds) grows it from the surface out to finalR — the eventual base's footprint, so
        // the finished base is revealed from INSIDE a fully-enveloping shell rather than poking out of a
        // sphere that only reached the rock radius. NOT bigger, or the sphere dwarfs the base: the base GLB
        // is scaled so its LONGEST axis spans baseR·2 (BaseModelLoader.LoadHull → NormalizeLongestAxis), so
        // baseR IS the base's furthest tip — the sphere ends snug there. rockR·1.05 is a floor for the rare
        // rock wider than the base (still covered as it grows).
        float rockR = _buildRockRadius.TryGetValue(b.RockId, out var rr) ? rr : 2f;
        float baseR = _defs.GetBaseDef(BaseRenderer.DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius;
        float finalR = MathF.Max(rockR * 1.05f, baseR);
        float worldR = b.Phase == 1 ? rockR * (0.05f + 0.50f * b.Progress) : Mathf.Lerp(rockR * 0.55f, finalR, b.Progress);
        sphere.SetEnvelop(worldR);
        return (sphere, worldR);
    }

    // Solid barrier for local prediction: only in BUILDING (phase 2), when the shell grows PAST the (still-
    // solid) rock — matches the server's ResolveBuildSphereCollisions so the local ship bounces off the
    // shell instead of sinking into it and snapping back. Registered in SIM/sector coordinates (the rock's
    // raw row position), where the ship prediction runs — not the sphere node's render-space GlobalPosition.
    // Dropped when the rock is unavailable (fogged/gone: a predict-miss the server reconciles) or the build
    // leaves phase 2.
    private void RegisterBuildSphereCollision(ConstructorBuild b, Asteroid? rock, float worldR)
    {
        if (b.Phase >= 2 && rock is not null)
            _collisionWorld.SetBuildSphere(rock.SectorId, b.RockId, new Vec3(rock.PosX, rock.PosY, rock.PosZ), worldR);
        else
            _collisionWorld.RemoveBuildSphere(b.RockId);
    }

    // Core opacity: stay mostly TRANSLUCENT while the drone SINKS (so you watch the mesh slide down into the
    // rock), then ramp to opaque through the first half of BUILDING as the sphere swallows it. Continuous
    // across the phase seam (sink ends ≈0.35, build starts at 0.35).
    private void UpdateBuildSphereCover(BuildSphere sphere, ConstructorBuild b)
    {
        sphere.SetCover(b.Phase == 1 ? b.Progress * 0.35f : Mathf.Clamp(0.35f + b.Progress * 1.4f, 0f, 1f));
    }

    // Rock-spitting debris: while the drone grinds into the surface (phase 1) throw a continuous spray of
    // rock chunks from the contact point, anchored on the still-visible drone (falling back to the sphere
    // centre). The instant it embeds and hides (phase 2) stop the spray — the last chunks in flight settle
    // out, then the node self-frees.
    private void UpdateConstructorDebris(ConstructorBuild b, Asteroid? rock, BuildSphere sphere)
    {
        if (b.Phase == 1 && rock is not null)
        {
            if (!_constructorDebris.TryGetValue(b.RockId, out var debris))
            {
                debris = new ConstructorDebris();
                _effects.AddChild(debris);
                _constructorDebris[b.RockId] = debris;
            }
            debris.GlobalPosition =
                _ships.Nodes.TryGetValue(b.ShipId, out var dn) && dn.Visible ? dn.GlobalPosition : sphere.GlobalPosition;
            _sectors.SetNodeSector(debris, rock.SectorId);
        }
        else if (_constructorDebris.TryGetValue(b.RockId, out var debris))
        {
            debris.Stop(); // embedded/hidden — cut the spray
            _constructorDebris.Remove(b.RockId); // stop tracking; it self-frees so we never touch a freed node
        }
    }

    // Keep the mesh VISIBLE only while it SINKS (phase 1) so you watch it slide into the rock; the instant
    // BUILDING begins (phase 2) hard-hide it. By then it's fully embedded — the still-solid rock (its fade
    // doesn't start until build ~35%) plus the growing opaque core cover the spot, so it eases away rather
    // than popping — and the build sphere must completely occlude it, never leaving the drone floating
    // visibly inside. Latching HideForBuild stops the per-snapshot SetNodeSector re-showing it (else it
    // blinks); Building is terminal, so it stays hidden to despawn.
    private void LatchDroneHideForBuild(ConstructorBuild b)
    {
        if (b.Phase >= 2 && _ships.Nodes.TryGetValue(b.ShipId, out var shipNode) && shipNode is RemoteShip drone)
        {
            drone.HideForBuild = true;
            drone.Visible = false;
        }
    }

    // Dissolve the actual ROCK as the base rises so it's gone by the time the finished base is revealed —
    // the opaque core hides the drone, this fades the rock itself. Stays fully SOLID through the sink and
    // the first third of BUILDING (so the drone-hide above is covered), then dissolves gradually across the
    // back two-thirds, fully gone by ~build-95% (the server then sends MsgRockGone and the already-
    // transparent node slips away under the sphere). Only its own node, only while a build row is live;
    // RestTransparencyFor restores it if it cancels.
    private void DissolveBuildRock(ConstructorBuild b, Node3D? node)
    {
        if (node is not null)
            NodeFx.DimNode(node, b.Phase >= 2 ? Mathf.Clamp((b.Progress - 0.35f) / 0.60f, 0f, 1f) : 0f);
    }
}
