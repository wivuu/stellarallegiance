using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Narrow interfaces the world renderers use to reach each other (and the coordinator) without any of them
// holding a reference to the whole WorldRenderer god-object. Each is a small facet; the concrete
// implementers are noted. As later milestones extract the remaining concerns, the implementer of a seam
// moves from the coordinator to the owning subsystem (e.g. IBoltSource → BoltRenderer, IRadarVisibility →
// FogStore), but the seam — and its consumers — stay unchanged.

// The warp/sector orchestration the ship lifecycle drives (stays in the coordinator per the plan).
public interface IWarpDriver
{
    // Local ship warped to `destSector`: hide the old sector hard and arm the deferred cover→swap→reveal.
    void BeginWarp(uint destSector);

    // Abandon an in-flight warp (a spawn/respawn/reconnect/home-reset supersedes it).
    void AbandonWarp();

    // Enter/settle into a sector without a warp flash (spawn or home-reset): set local sector + repaint env
    // + refresh visibility.
    void EnterSector(uint sector);

    // The pilot's home garrison sector (their team's base, else the lowest sector id).
    uint HomeSector { get; }
}

// Client-synthesized bolt spawn for a remote ship's observed fire (coordinator → BoltRenderer in C2).
public interface IBoltSource
{
    void SpawnBoltFor(Ship row);
}

// Drop a transient self-freeing effect into the world at a sector-local position (coordinator-owned).
public interface IEffectSink
{
    void SpawnEffect(Node3D fx, Vector3 pos, uint sector);
}

// Open the brief "CONTACT LOST" toast window when a fogged enemy leaves the streamed set
// (coordinator → FogStore in C4).
public interface IContactLostSink
{
    void OpenContactLostWindow();
}

// Whether an enemy ship id is on our team's radar tier (vs eyeball-only) (coordinator → FogStore in C4).
public interface IRadarVisibility
{
    bool IsRadarVisible(ulong shipId);
}

// Read-only view of the live ship nodes, for the bolt/collision/mining/construction/fog/HUD consumers.
public interface IShipQuery
{
    IReadOnlyDictionary<ulong, Node3D> Nodes { get; }
    bool TryGetShield(ulong shipId, out float shield);
    PredictionController? LocalShip { get; }
    int Count { get; }
}

// The other ships the local predicted ship can bump into (for its collision provider).
public interface IShipObstacleSource
{
    IReadOnlyList<Collide.MovingShip> ShipObstacles();
}

// Read-only view of the live probe nodes, for the bolt-impact sweep (coordinator → ProbeRenderer in C3).
public interface IProbeQuery
{
    IReadOnlyDictionary<ulong, ProbeView> Nodes { get; }
}

// Read-only view of the live aleph-gate nodes, for the bolt-impact sweep (implemented by AlephRenderer).
public interface IAlephQuery
{
    IReadOnlyDictionary<ulong, Node3D> Nodes { get; }
}

// The live constructor build stream, for the collision thud gate + rock-lock suppression
// (coordinator → ConstructionRenderer in C4).
public interface IBuildQuery
{
    bool HasBuildRow(ulong shipId);
    bool IsRockUnderConstruction(ulong rockId);
}
