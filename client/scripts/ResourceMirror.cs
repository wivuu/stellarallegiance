using System;
using System.Collections.Generic;
using StellarAllegiance.Shared;

// =====================================================================
//  ResourceMirror.cs — OWN-SHIP RESOURCE PREDICTION (equipment PR)
//
//  The client half of THE shared resource rule (shared/ShipResources.cs): the local ship's energy
//  pool, magazine, ammo-pack charges and cloak level, predicted tick by tick in the SERVER's order
//  (Simulation Pass A), so the readouts, the gun gate and the cloak respond on the tick the pilot
//  acts instead of a round trip later. Nothing here re-implements a rule — every step is a call
//  into ShipResources / FireCadence; this class only sequences them and keeps the history a
//  reconcile needs.
//
//  Per predicted tick, driven by PredictionController.Step (the flight step sits between (b) and
//  (d) exactly as in Pass A, and reads no pool):
//    BeginTick (a) AmmoStep — land a finished pack load, then commit the next one (input-free)
//              (b) snapshot — the ring records the pools the fire phase starts with (what
//                  ShipRecord.Pools carries for that LastInputTick), the tick's inputs and the
//                  per-mount cadence stamps it began with
//    FireStep  (d) the pilot's barrels in declaration order: FireCadence AND TrySpendShot; a blocked
//                  mount does not stamp. Turret stations fire off their GUNNERS' input, which the
//                  pilot's client cannot know — their spend reaches it through Resync.
//    EndTick   (e) EnergyStep — recharge, then the cloak (the latched input LEVEL)
//
//  Reconcile (Resync, one per authoritative row): the ring entry at the row's LastInputTick is
//  compared EXACTLY with the row's Pools, and with whether a pilot mount fired that tick (the row's
//  LastFireTick == LastInputTick). A match costs nothing. A mismatch — a gunner's turret shot, an
//  input the server applied a tick late, a salvaged pack, a research change — REPLAYS every
//  recorded tick after it from the authoritative pools with the recorded inputs, re-deriving the
//  cadence stamps on the way. So the prediction converges within that one ack, and a trigger press
//  the server took a tick late cannot leave the cadence a tick out of phase (and the pools
//  mismatching every volley) for the rest of the burst. The pending ammo-pack load never crosses
//  the wire; a replay keeps the predicted one, corrected by what the authoritative pools imply
//  (InferLoad).
//
//  Godot-free on purpose: tests/ResourcePredictTest links this file and steps it tick for tick
//  against the real Simulation.
// =====================================================================

// GateBlock lives in CockpitCues.cs (below the type, above ResourceCues) — it's a plain byte enum with
// no dependencies, and keeping it there lets tests/CockpitCuesTest link that one file for ResourceCues
// coverage without pulling in this file's whole EquipmentSet/ShipPools dependency chain.

// The three parts one ship flies (indexed by EquipmentDef.Slot*), resolved from its effective ids.
public readonly record struct EquipmentSet(EquipmentDef? Shield, EquipmentDef? Afterburner, EquipmentDef? Cloak)
{
    public EquipmentDef? this[byte slot] =>
        slot switch
        {
            EquipmentDef.SlotShield => Shield,
            EquipmentDef.SlotAfterburner => Afterburner,
            EquipmentDef.SlotCloak => Cloak,
            _ => null,
        };

    // The client mirror of Simulation.ApplyEquipment's PartIn. `hull` is the EFFECTIVE hull (the Pod
    // def for a pod: no slots, nothing equipped); `ids` = the ship's effective ids by slot (its
    // MsgShipLoadout row, or the hangar's expectation), null = the hull's DefaultEquipment — the
    // wire's omission rule. A def that isn't that slot's kind resolves as empty, like the server's
    // belt and braces. False when an id names a part whose def hasn't streamed: the caller guards
    // rather than flying without it (no baked fallback).
    public static bool TryResolve(
        ShipClassDef hull,
        ushort[]? ids,
        Func<ushort, EquipmentDef?> getEquipment,
        out EquipmentSet set
    )
    {
        bool known = true;
        set = new EquipmentSet(
            Part(EquipmentDef.SlotShield),
            Part(EquipmentDef.SlotAfterburner),
            Part(EquipmentDef.SlotCloak)
        );
        return known;

        EquipmentDef? Part(byte slot)
        {
            ushort id =
                ids is null ? hull.DefaultEquipmentFor(slot)
                : slot < ids.Length ? ids[slot]
                : EquipmentDef.NoEquipment;
            if (id == EquipmentDef.NoEquipment)
                return null;
            if (getEquipment(id) is not { } e)
            {
                known = false;
                return null;
            }
            return e.Slot == slot ? e : null;
        }
    }
}

public sealed class ResourceMirror
{
    // History depth in ticks (3.2 s at 20 Hz) — deeper than the flight buffer, so any ack the flight
    // reconcile can still use has its resource entry too.
    public const int RingSize = 64;

    // One predicted tick. PreFire is what the server's record for this tick will carry; LoadEnd the
    // pending pack load after the ammo step (client-only); Firing/Cloak the inputs the tick ran on;
    // Fired whether a pilot mount fired (the row's LastFireTick == LastInputTick).
    private struct Entry
    {
        public uint Tick; // 0 = empty (tick 0 never runs a step)
        public ShipPools PreFire;
        public uint LoadEnd;
        public bool Firing;
        public bool Cloak;
        public bool Fired;
    }

    private readonly Entry[] _ring = new Entry[RingSize];

    // RingSize × _mounts: each recorded tick's per-mount cadence stamps as its fire phase BEGAN,
    // so a replay restarts the cadence from the right place. Sized to the mount count (the class's
    // weapon hardpoints), reallocated only if that count changes.
    private uint[] _stampRing = Array.Empty<uint>();
    private uint[] _scratch = Array.Empty<uint>();
    private int _mounts = -1;

    private uint _first; // first tick recorded since the last seed (older acks predate this run)
    private uint _newest; // newest recorded tick (0 = nothing since the last seed)
    private int _cur = -1; // ring slot of the tick between BeginTick and EndTick

    // The rule's inputs for this ship (DefRegistry.TryResourceStats), refreshed by the owner before
    // every tick and every reconcile — never stamped at spawn, so a research change applies live.
    public ShipResourceStats Stats;

    // The predicted pools ENTERING the next tick, and the pending pack load (0 = none).
    public ShipPools Pools;
    public uint LoadEndTick;

    public bool Seeded { get; private set; }

    // The most recent reason the trigger was held and a cadence-ready gun was refused; cleared when
    // a shot goes out or the trigger is released, kept while every gun is merely cycling.
    public GateBlock LastBlock { get; private set; }

    // The cloak input the last stepped tick ran on (the latch as the sim saw it).
    public bool CloakHeld { get; private set; }

    // Replays run since the last Reset — the [predict-stats] res_resync count.
    public int Resyncs { get; private set; }

    // A fresh ship: nothing known until the first seed.
    public void Reset()
    {
        Seeded = false;
        Pools = default;
        LoadEndTick = 0;
        LastBlock = GateBlock.None;
        CloakHeld = false;
        Resyncs = 0;
        ClearRing();
    }

    // Adopt authoritative pools outright (spawn, follow-authority, an ack the prediction can't use):
    // `auth` is a ShipRecord's Pools and `authTick` its LastInputTick. The history is discarded —
    // the next acks compare against ticks predicted from here.
    public void Seed(in ShipPools auth, uint authTick)
    {
        Pools = auth;
        LoadEndTick = InferLoad(in auth, LoadEndTick, authTick);
        Seeded = true;
        ClearRing();
    }

    private void ClearRing()
    {
        Array.Clear(_ring);
        _first = _newest = 0;
        _cur = -1;
    }

    // (a) + (b): the ammo step, then record this tick's pre-fire snapshot. `stamps` are the pilot
    // mounts' cadence stamps (one per weapon hardpoint), read here and written by FireStep.
    public void BeginTick(uint tick, bool firing, bool cloakHeld, uint[] stamps)
    {
        // A prediction clock that stepped BACKWARD (a re-anchor) makes every recorded tick ahead of
        // it meaningless — start the history over from here.
        if (_newest != 0 && tick <= _newest)
            ClearRing();
        if (stamps.Length != _mounts)
        {
            _mounts = stamps.Length;
            _stampRing = new uint[RingSize * _mounts];
            _scratch = new uint[_mounts];
            ClearRing(); // older entries were recorded against a different mount layout
        }

        ShipResources.AmmoStep(ref Pools, ref LoadEndTick, tick, in Stats, ammoEnabled: true);

        _cur = (int)(tick % RingSize);
        _ring[_cur] = new Entry
        {
            Tick = tick,
            PreFire = Pools,
            LoadEnd = LoadEndTick,
            Firing = firing,
            Cloak = cloakHeld,
        };
        Array.Copy(stamps, 0, _stampRing, _cur * _mounts, _mounts);
        if (_first == 0)
            _first = tick;
        _newest = tick;
    }

    // (d): the pilot's fire phase for the tick BeginTick opened. Appends every barrel that fired to
    // `fired` (the owner spawns their bolts) and returns how many did.
    public int FireStep(
        IReadOnlyList<(HardpointDef hp, WeaponDef? weapon)> slots,
        uint[] stamps,
        uint tick,
        List<byte>? fired
    )
    {
        if (_cur < 0)
            return 0;
        ref Entry e = ref _ring[_cur];
        if (!e.Firing)
        {
            LastBlock = GateBlock.None;
            return 0;
        }
        int n = SelectBarrels(
            slots,
            stamps,
            tick,
            ref Pools,
            fired,
            cadence: true,
            resources: true,
            loading: LoadEndTick != 0,
            out GateBlock block
        );
        e.Fired = n > 0;
        if (block != GateBlock.None)
            LastBlock = block;
        else if (n > 0)
            LastBlock = GateBlock.None;
        return n;
    }

    // (e): the energy step closing the tick BeginTick opened.
    public void EndTick()
    {
        if (_cur < 0)
            return;
        CloakHeld = _ring[_cur].Cloak;
        ShipResources.EnergyStep(ref Pools, in Stats, CloakHeld, energyEnabled: true);
        _cur = -1;
    }

    // The pre-fire pools predicted for `tick`, if it is still in the history (diagnostics).
    public bool TryGetPredicted(uint tick, out ShipPools pools)
    {
        ref Entry e = ref _ring[tick % RingSize];
        pools = e.PreFire;
        return tick != 0 && e.Tick == tick;
    }

    // Reconcile against one authoritative row: `auth` = its Pools, `ackTick` = its LastInputTick,
    // `authFired` = a pilot mount fired that tick (LastFireTick == LastInputTick). `slots` are the
    // ship's effective barrels and `stamps` the live cadence stamps a replay rewrites. Returns true
    // when the prediction had diverged and was corrected.
    public bool Resync(
        uint ackTick,
        in ShipPools auth,
        bool authFired,
        IReadOnlyList<(HardpointDef hp, WeaponDef? weapon)> slots,
        uint[] stamps
    )
    {
        if (!Seeded || _newest == 0)
        {
            // Nothing predicted since the last seed: follow authority.
            Seed(in auth, ackTick);
            return false;
        }
        if (ackTick < _first)
            return false; // predates this prediction run (spawn / handback) — the acks after it compare
        if (ackTick > _newest || _newest - ackTick >= RingSize)
        {
            // Authority is AHEAD of the prediction (a stalled client), or the tick fell off the
            // history: there is nothing to replay from, so adopt it.
            Seed(in auth, ackTick);
            Resyncs++;
            return true;
        }
        int slot = (int)(ackTick % RingSize);
        ref Entry e = ref _ring[slot];
        if (e.Tick != ackTick)
            return false; // a gap in the predicted ticks — nothing to compare this ack against
        if (e.PreFire == auth && e.Fired == authFired)
            return false;
        Replay(slot, in auth, authFired, slots, stamps);
        Resyncs++;
        return true;
    }

    // Re-run the history from the acked tick: its pre-fire pools become the authority's, its fire
    // phase is forced to the server's outcome, and every later recorded tick re-steps from its own
    // recorded inputs. Leaves Pools / LoadEndTick / the live stamps where the replay ends.
    private void Replay(
        int slot,
        in ShipPools auth,
        bool authFired,
        IReadOnlyList<(HardpointDef hp, WeaponDef? weapon)> slots,
        uint[] stamps
    )
    {
        ref Entry e = ref _ring[slot];
        uint tick = e.Tick;
        var pools = auth;
        uint loadEnd = InferLoad(in auth, e.LoadEnd, tick);
        Array.Copy(_stampRing, slot * _mounts, _scratch, 0, _mounts);
        e.PreFire = auth;
        e.LoadEnd = loadEnd;
        e.Fired = authFired;

        // The acked tick's fire phase. WHETHER a pilot mount fired is known (the row stamps
        // LastFireTick), so it is forced to match — a trigger the server took a tick late fires
        // nothing here. WHICH mounts, the shared gate derives from the row's pools the way a remote
        // replay does; if the cadence stamps say none were ready (exactly the phase slip this replay
        // may be repairing), every mount the pools allow.
        if (authFired && SelectBarrels(slots, _scratch, tick, ref pools, null, true, true, loadEnd != 0, out _) == 0)
            SelectBarrels(slots, _scratch, tick, ref pools, null, false, true, loadEnd != 0, out _);
        ShipResources.EnergyStep(ref pools, in Stats, e.Cloak, energyEnabled: true);

        // Every later predicted tick, from its own recorded inputs (the history is contiguous but a
        // missing tick is simply skipped, like the live step skipped it).
        for (uint t = tick + 1; t != 0 && t <= _newest; t++)
        {
            int s = (int)(t % RingSize);
            ref Entry ej = ref _ring[s];
            if (ej.Tick != t)
                continue;
            ShipResources.AmmoStep(ref pools, ref loadEnd, t, in Stats, ammoEnabled: true);
            ej.PreFire = pools;
            ej.LoadEnd = loadEnd;
            Array.Copy(_scratch, 0, _stampRing, s * _mounts, _mounts);
            ej.Fired = ej.Firing && SelectBarrels(slots, _scratch, t, ref pools, null, true, true, loadEnd != 0, out _) > 0;
            ShipResources.EnergyStep(ref pools, in Stats, ej.Cloak, energyEnabled: true);
        }

        Pools = pools;
        LoadEndTick = loadEnd;
        Array.Copy(_scratch, stamps, Math.Min(_mounts, stamps.Length));
    }

    // The pending pack load implied by authoritative pools taken after that tick's ammo step. The load
    // never crosses the wire, but the rule pins it down: ammo only FALLS while a load is pending, so a
    // magazine that covers the cheapest ammo gun has none; one below it with a charge aboard must have
    // one (the ammo step would have committed it) — keep the predicted end if it is a consistent one,
    // else assume the latest the server's can be (the refill lands on the HUD a little late and the
    // next ack corrects it). Below the cheapest shot with NO charge aboard is ambiguous (the last
    // charge may be loading), so the prediction stands.
    private uint InferLoad(in ShipPools p, uint predicted, uint atTick)
    {
        if (Stats.MinAmmoPerShot == 0 || Stats.AmmoPerCharge == 0 || Stats.AmmoReloadTicks == 0)
            return 0; // no ammo gun or no pack line: nothing loads (a 0-tick load lands within its own step)
        if (p.Ammo >= Stats.MinAmmoPerShot)
            return 0;
        if (p.AmmoPacks > 0)
            return predicted > atTick && predicted - atTick <= Stats.AmmoReloadTicks
                ? predicted
                : atTick + Stats.AmmoReloadTicks;
        return predicted;
    }

    // THE fire gate over one ship's pilot barrels — shared by this mirror (own ship), its replay and
    // BoltRenderer's remote replay, so every client derives the same fired set from the same row.
    // Visits every weapon hardpoint in declaration order (skipped slots still consume their index,
    // the per-barrel spread seed); `cadence` gates on FireCadence.MountFires against `stamps`,
    // `resources` on ShipResources.TrySpendShot against `pools` (deducting what fires). A fired mount
    // is stamped with `tick` and appended to `fired`; a resource-blocked mount is NOT stamped and
    // reports why through `block` (the first one, `loading` = an ammo pack is loading).
    public static int SelectBarrels(
        IReadOnlyList<(HardpointDef hp, WeaponDef? weapon)> slots,
        uint[] stamps,
        uint tick,
        ref ShipPools pools,
        List<byte>? fired,
        bool cadence,
        bool resources,
        bool loading,
        out GateBlock block
    )
    {
        block = GateBlock.None;
        int n = 0;
        int count = Math.Min(slots.Count, stamps.Length);
        for (int b = 0; b < count; b++)
        {
            if (slots[b].weapon is not { Kind: WeaponKind.Bolt } w)
                continue; // empty/unbound mount, or a rack (fired by Firing2, not primary fire)
            if (cadence && !FireCadence.MountFires(tick, stamps[b], w.FireIntervalTicks))
                continue;
            if (resources && !ShipResources.TrySpendShot(ref pools, w.EnergyPerShot, w.AmmoPerShot))
            {
                if (block == GateBlock.None)
                    block = BlockFor(in pools, loading, w);
                continue; // out of energy / ammo: no shot, no stamp
            }
            stamps[b] = tick;
            fired?.Add((byte)b);
            n++;
        }
        return n;
    }

    // Why a shot of `w` would be refused by these pools right now (None = affordable) — the per-gun
    // readout seam. Energy first, like TrySpendShot.
    public static GateBlock BlockFor(in ShipPools pools, bool loading, WeaponDef w)
    {
        if (w.EnergyPerShot > 0f && pools.Energy < w.EnergyPerShot)
            return GateBlock.NoEnergy;
        if (w.AmmoPerShot > 0 && pools.Ammo < w.AmmoPerShot)
            return loading ? GateBlock.Loading : GateBlock.NoAmmo;
        return GateBlock.None;
    }

    // The cheapest AmmoPerShot over a ship's EFFECTIVE mounts — the client's derivation of
    // Simulation.RefreshMinAmmoPerShot, through THE shared ShipResources.MinAmmoPerShot: the pilot's
    // barrels (every Weapon-kind hardpoint in declaration order, the loadout row's id where it has
    // one, else the authored gun — DefRegistry.SlotsForShip's overlay) and the crew stations (the
    // row's turret guns, else the authored BOUND stations: Turret hardpoints on a Gun mount).
    // `hardpoints` is the COMBAT class's list; a pod flies no guns, so its caller passes 0.
    public static ushort MinAmmoPerShot(
        IReadOnlyList<HardpointDef> hardpoints,
        uint[]? barrelIds,
        uint[]? turretIds,
        Func<uint, WeaponDef?> getWeapon
    )
    {
        int nb = 0,
            nt = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            var h = hardpoints[i];
            if (h.Kind == HardpointKind.Weapon)
                nb++;
            else if (h.Kind == HardpointKind.Turret && h.Mount == WeaponMountKind.Gun)
                nt++;
        }
        Span<uint> barrels = nb <= 32 ? stackalloc uint[nb] : new uint[nb];
        Span<uint> authoredTurrets = nt <= 32 ? stackalloc uint[nt] : new uint[nt];
        int b = 0,
            t = 0;
        for (int i = 0; i < hardpoints.Count; i++)
        {
            var h = hardpoints[i];
            if (h.Kind == HardpointKind.Weapon)
            {
                barrels[b] = barrelIds is not null && b < barrelIds.Length ? barrelIds[b] : h.WeaponId;
                b++;
            }
            else if (h.Kind == HardpointKind.Turret && h.Mount == WeaponMountKind.Gun)
                authoredTurrets[t++] = h.WeaponId;
        }
        ReadOnlySpan<uint> turrets = turretIds is not null ? turretIds : authoredTurrets;
        return ShipResources.MinAmmoPerShot(barrels, turrets, getWeapon);
    }
}
