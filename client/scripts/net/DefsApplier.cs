using StellarAllegiance.Shared;
using StellarAllegiance.Shared.Net;

// DefsApplier — the MsgDefs (frame 7) apply, lifted whole out of GameNetClient (T6).
//
// This is the client half of the content pipeline: the server streams its content bundle as a
// DefsMessage (shared/Net/Messages.cs) whose payload IS the shared def types (shared/Defs.cs —
// ShipClassDef/WeaponDef/... are the wire records, codec generated), decoded by the same generated
// reader the server's writer was generated from, and loaded into DefRegistry — the ONLY place the
// client learns hull/weapon/cargo/base stats, the world config, the tech-path catalog, the station
// catalog and the faction identity. Nothing here may fall back to a compile-time constant: every
// value comes from the server's streamed YAML content.
//
// A new authored field is ONE edit: the field on the def class in shared/Defs.cs. There is no mirror
// to keep in step. Runs on the main thread, driven by FrameApplier's dispatch.
public sealed class DefsApplier
{
    private readonly DefRegistry _defs;
    private readonly INetClientHost _host;

    public DefsApplier(DefRegistry defs, INetClientHost host)
    {
        _defs = defs;
        _host = host;
    }

    public void Apply(in DefsMessage d)
    {
        // The wire carries the streamed subset of WorldConfig; everything else on the client's copy
        // stays at its default (server-only tuning is never sent — EyeballMultiplier precedent).
        var cfg = new WorldConfig
        {
            Id = d.World.Id,
            SectorScale = d.World.SectorScale,
            AsteroidDensity = d.World.AsteroidDensity,
            DebugFreezeBrain = d.World.DebugFreezeBrain,
            DebugNoFire = d.World.DebugNoFire,
            FogOfWar = d.World.FogOfWar,
        };
        _defs.Load(
            d.Ships,
            d.Weapons,
            d.Bases,
            d.CargoItems,
            cfg,
            d.Techs,
            d.Developments,
            d.Stations,
            d.FactionName,
            d.FactionAttributes
        );
        Log.Print(
            $"[GameNet] defs received — {d.Ships.Count} ship classes, {d.Weapons.Count} weapons, {d.CargoItems.Count} cargo items, {d.Bases.Count} bases, {d.Techs.Count} techs, {d.Developments.Count} developments, {d.Stations.Count} stations"
        );
        _host.RaiseDefsReceived();
    }
}
