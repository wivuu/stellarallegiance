using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// The authoritative 20 Hz match simulation, ported from the module's SimulateTick
// (module/spacetimedb/Lib.cs) + TryFire/FirstEntryTime (Weapons.cs) onto plain process
// memory: no datastore, no transactions, no per-row serialization — Step() touches only
// arrays and dictionaries. Single-threaded by design (the sim thread owns all state);
// inputs/joins arrive through thread-safe queues drained at the top of each step.
//
// Scaffold scope (deliberately deferred, see plan): escape pods, rescue, docking,
// PIG drones, match phases/lobby. Death here is a simple timed respawn at the team base.
public sealed partial class Simulation
{
    public const uint TickHz = 20;
    private const int ShotRingSize = 64; // > max ProjectileLifeTicks

    public const byte PodClass = 255; // reserved "class" selecting the Pod flight profile

    // ---- Mechanics tuning — authored in world.yaml (`mechanics:` / `combat:`), resolved
    // once in the ctor from Content.World. Second-authored durations become ticks here. ----
    // Stage-2 economy: every team gets a flat credit paycheck this often; the amount per paycheck
    // is the faction's authored income (Content.Start.IncomePerPaycheck). Public for tests.
    public readonly uint PaycheckTicks;
    private readonly float DockRadiusFrac; // dock when within this fraction of your OWN base radius
    private readonly float LaunchSpeed; // u/s catapult out of the docking-exit hardpoint on spawn
    private readonly float RescueRadius; // pod pickup distance (no need to directly intersect)
    private readonly float PodEjectSpeed; // u/s initial fling (decays to Pod.MaxSpeed)
    private readonly float PodEjectSpin; // rad/s initial tumble (decays via angular drag)
    private readonly WorldMechanicsTuning _mech; // gate/warp knobs read at their use sites
    private readonly WorldCombatTuning _combat; // collision damage + boundary hazard

    // Mining tuning reads through the LIVE World (not a ctor-cached copy): the world owns the knob
    // set it seeded ore with, and StartMatch may swap in a fresh World — harvest rate/standoff must
    // follow it (and a test world's custom knobs must actually drive the sim).
    private WorldMiningTuning _mining => World.Mining;

    // The resolved content this match runs on (GameContent defaults, optionally YAML-overlaid at
    // boot). ONE source of truth with the defs streamed to the client (Protocol.BuildDefs(Content)),
    // so server authority and client prediction can never drift. Exposed for the hub's def frame.
    public ContentSet Content { get; }

    // Instance lookups built from Content in the ctor (were static-from-GameContent before the
    // Stage-1 content pipeline). WeaponDefs is keyed by WeaponId (a muzzle's hardpoint names the
    // weapon it fires); ShipDefs by ClassId; _stats is the per-class flight profile derived from the
    // loaded def, so a YAML-overridden ship flies the authored numbers on BOTH sides.
    private readonly Dictionary<uint, WeaponDef> WeaponDefs;
    private readonly Dictionary<byte, ShipClassDef> ShipDefs;
    private readonly Dictionary<byte, ShipStats> _stats;

    // A weapon muzzle in LOCAL ship space — the offset the bolt spawns at and the forward it
    // fires along. Single-sourced from the authored ShipClassDef hardpoints so the server's
    // hit-detection muzzles match the bolts the client renders from the same defs.
    private readonly record struct Muzzle(Vec3 Off, Vec3 Dir, uint WeaponId);

    // Per-class weapon muzzles, indexed by ClassId. A class with several Weapon hardpoints
    // (the Fighter's twin cannons) fires one bolt from EACH muzzle every fire tick; the array
    // for a class is in hardpoint declaration order, which fixes each muzzle's barrel index so
    // the per-barrel spread seed (FlightModel.SpreadDirection) matches the client.
    private readonly Muzzle[][] ClassMuzzles;

    // Per-class MISSILE-kind mounts (a subset of ClassMuzzles), indexed by ClassId, in hardpoint
    // order. Built from the loaded defs; the first entry is the rack a hull launches from + inits
    // its ammo. A hull with no missile hardpoint has an empty array.
    private readonly Muzzle[][] ClassMissileMounts;

    private static Muzzle[][] BuildMuzzles(IReadOnlyList<ShipClassDef> defs)
    {
        int max = 0;
        foreach (var d in defs)
            if (d.ClassId != GameContent.PodClassId && d.ClassId > max)
                max = d.ClassId;
        var table = new Muzzle[max + 1][];
        for (int i = 0; i < table.Length; i++)
            table[i] = System.Array.Empty<Muzzle>();
        foreach (var d in defs)
        {
            if (d.ClassId == GameContent.PodClassId)
                continue;
            var list = new List<Muzzle>();
            foreach (var h in d.Hardpoints)
                if (h.Kind == HardpointKind.Weapon)
                    list.Add(new Muzzle(new Vec3(h.OffX, h.OffY, h.OffZ), new Vec3(h.DirX, h.DirY, h.DirZ), h.WeaponId));
            table[d.ClassId] = list.ToArray();
        }
        return table;
    }

    // Spawn hull for a class, read straight from its def (was a duplicate switch). An unknown class
    // falls back to the Scout hull, matching the old default.
    private float HullFor(byte cls) =>
        ShipDefs.TryGetValue(cls, out var d) ? d.MaxHull : ShipDefs[FlightModel.ClassScout].MaxHull;

    // Shield knobs for a class, read straight from its def (0 capacity = the hull has no shield).
    // Unknown class falls back to the Scout def, mirroring HullFor. A pod flies the Pod def (which
    // authors no shield), so the shield rule uses the SAME effective-class resolution as StatsFor.
    private ShipClassDef ShieldDefFor(byte cls) =>
        ShipDefs.TryGetValue(cls, out var d) ? d : ShipDefs[FlightModel.ClassScout];

    private ShipClassDef ShieldDefFor(ShipSim s) => ShieldDefFor(s.IsPod ? GameContent.PodClassId : s.Class);

    private float ShieldCapacityFor(ShipSim s) => ShieldDefFor(s).ShieldCapacity;

    private float ShieldRechargeFor(ShipSim s) => ShieldDefFor(s).ShieldRecharge;

    private uint ShieldDelayTicksFor(ShipSim s) => (uint)MathF.Round(ShieldDefFor(s).ShieldDelaySec * TickHz);

    // The single damage seam: every ship-damage site routes through here so the shield rule is
    // applied ONCE and consistently. The energy shield absorbs first — a hit scaled by the weapon's
    // shieldMult (1.0 for collisions/boundary that carry no weapon). While the shield holds, the
    // hull is untouched; when a hit pops it, the raw damage it couldn't absorb spills into the hull
    // the same tick. ShieldDamageTick stamps this tick so the recharge sweep waits out the delay.
    // Death is still resolved by the end-of-step Health<=0 pass, not here.
    private void ApplyDamage(ShipSim s, float dmg, uint tick, float shieldMult = 1f)
    {
        if (dmg <= 0f)
            return;
        float cap = ShieldsEnabled ? ShieldCapacityFor(s) : 0f;
        if (cap > 0f && s.Shield > 0f && shieldMult > 0f)
        {
            s.ShieldDamageTick = tick;
            float shieldDmg = dmg * shieldMult; // raw damage as felt by the shield pool
            if (shieldDmg <= s.Shield)
            {
                s.Shield -= shieldDmg;
                return; // shield held; hull untouched
            }
            // Shield pops: it absorbed s.Shield of shield-damage = s.Shield/shieldMult raw; the rest spills.
            dmg -= s.Shield / shieldMult;
            s.Shield = 0f;
            if (dmg <= 0f)
                return;
        }
        s.Health -= dmg;
    }

    // Nanite heal: a healing bolt hitting a same-team ship restores hull only. Shields are never
    // touched (no ShieldDamageTick stamp, no shield pool change) and the hull is clamped to the
    // hull's max (HullFor). The health stream carries the new value to clients like any other change,
    // so the HUD shows the bar rise naturally — no separate wire event. Enemy hits never reach here
    // (FireBolt only targets same-team ships for a healing weapon); self-hits are excluded there too.
    private void ApplyHeal(ShipSim s, float power)
    {
        if (power <= 0f)
            return;
        float max = HullFor(s.IsPod ? GameContent.PodClassId : s.Class);
        s.Health = MathF.Min(s.Health + power, max);
    }

    // The EFFECTIVE weapon id at a ship's weapon-hardpoint barrel: the spawn-validated override
    // when one exists, else the authored class default. THE read seam for every armed-mount
    // consumer (TryFire, MissileMountFor(ship), the loadout wire table).
    private uint WeaponIdAt(ShipSim s, int barrel) =>
        s.MountWeaponIds is { } m && barrel < m.Length ? m[barrel] : ClassMuzzles[s.Class][barrel].WeaponId;

    // A class's AUTHORED primary gun — the first Bolt-kind muzzle, or the Scout gun if the hull
    // carries no bolt weapon (missile racks are ignored). Class-based by design: it only drives
    // the PIG threat heuristic (bots always fly authored loadouts, and a rough per-class damage
    // guess is fine for an enemy that may have swapped guns).
    private WeaponDef PrimaryWeapon(byte cls)
    {
        var m = cls < ClassMuzzles.Length ? ClassMuzzles[cls] : System.Array.Empty<Muzzle>();
        foreach (var mz in m)
            if (WeaponDefs.TryGetValue(mz.WeaponId, out var wd) && wd.Kind == WeaponKind.Bolt && !wd.IsHealing)
                return wd; // a healing gun (ER Nanite) is never a threat weapon — skip it
        return WeaponDefs[GameContent.ScoutWeaponId];
    }

    // The first AUTHORED missile mount for a class + its projected WeaponDef, or null if the hull
    // has none. Class-based: the PIG spawn/heuristic path only (bots fly authored loadouts).
    // Player paths (ammo seeding, TryFireMissile, UpdateLock, siege orders) use the ship-aware
    // overload below so an emptied/swapped rack is honored.
    private (Muzzle mount, WeaponDef w)? MissileMountFor(byte cls)
    {
        var mounts = cls < ClassMissileMounts.Length ? ClassMissileMounts[cls] : System.Array.Empty<Muzzle>();
        if (mounts.Length == 0)
            return null;
        return (mounts[0], WeaponDefs[mounts[0].WeaponId]);
    }

    // The first EFFECTIVE Missile-kind mount for this ship (per-ship loadout aware) + its def, or
    // null when the hull authors no rack / every rack slot was emptied. The returned Muzzle carries
    // the effective weapon id; its geometry is the class table's (overrides never move a mount).
    private (Muzzle mount, WeaponDef w)? MissileMountFor(ShipSim s)
    {
        var muzzles = s.Class < ClassMuzzles.Length ? ClassMuzzles[s.Class] : System.Array.Empty<Muzzle>();
        for (int barrel = 0; barrel < muzzles.Length; barrel++)
            if (WeaponDefs.TryGetValue(WeaponIdAt(s, barrel), out var wd) && wd.Kind == WeaponKind.Missile)
                return (new Muzzle(muzzles[barrel].Off, muzzles[barrel].Dir, wd.WeaponId), wd);
        return null;
    }

    // Flight stats for a class, derived from the LOADED def (authored in YAML) via the SAME path the
    // client takes (ShipStats.FromDef) — so server authority and client prediction integrate
    // bit-identically. A pod ignores its class and flies the Pod profile; an unknown class falls
    // back to the Scout def. Precomputed in the ctor from the content set.
    private ShipStats StatsFor(byte cls, bool isPod)
    {
        byte defId = isPod ? GameContent.PodClassId : cls;
        return _stats.TryGetValue(defId, out var s) ? s : _stats[FlightModel.ClassScout];
    }

    public sealed class ShipSim
    {
        public ulong ShipId;
        public int OwnerClientId; // a connected client's id, or -1 for server-owned (PIGs / PIG pods)
        public byte Team;
        public byte Class;
        public uint SectorId;
        public ShipState State; // shared FlightModel state (pos/vel/rot/angvel/mass/ab)
        public float Health;

        // Regenerating energy shield layered over Health. Shield absorbs damage first (overflow spills
        // to hull); ShieldDamageTick stamps the last tick it took damage, gating the recharge delay.
        // 0 capacity (per the class def) = no shield. Set full at spawn; recharged in the Step sweep.
        public float Shield;
        public uint ShieldDamageTick;

        // Additive radar-signature bias (SignatureModel.Bias) — the per-ship equipment/loadout/
        // ability seam a future fitting or cloak system mutates live. Seeded at spawn from the
        // class def's projected SignatureBias (hull + default-loadout sum); 0 = neutral.
        public float SigBias;
        public uint LastInputTick;
        public uint LastFireTick;

        // ---- Per-ship weapon loadout (hangar mount overrides, validated at spawn) ----
        // MountWeaponIds[barrel] = the EFFECTIVE weapon id at that weapon-hardpoint barrel
        // (HardpointDef.NoWeapon = deliberately-empty slot); null = the authored class default
        // (PIGs/pods/miners always null). Geometry always comes from ClassMuzzles — an override
        // swaps WHAT a mount fires, never WHERE it sits. Read through WeaponIdAt.
        public uint[]? MountWeaponIds;

        // Per-mount gun cadence gates (FireCadence.MountFires), lazily sized to the class muzzle
        // array in TryFire. LastFireTick stays the wire stamp "some gun fired this tick"; clients
        // derive WHICH mounts from the same shared rule, so these never go on the wire.
        public uint[]? MountLastFire;

        public ShipInputState HeldInput; // replayed on ticks with no exact-stamped input
        public bool Alive;
        public uint RespawnAtTick; // when !Alive

        // AI combat drone — server-driven via the PIG brain (Simulation.Pig.cs), not client input.
        // Orthogonal to Kind: a PIG escape pod is BOTH IsPig and Kind.Pod (IsPig = AI-controlled, Kind
        // = role/form). A player pod carries IsPig=false; MakePod carries the dead ship's IsPig over.
        public bool IsPig;

        // The ship's mutually-exclusive ROLE (combat hull / ejected pod / ore miner / constructor).
        // Replaces the old IsPod/IsMiner bools — Kind == ShipKind.Pod is an ejected pod (slow, unarmed,
        // flown by its owner or auto-flown home by PodThink), Kind == ShipKind.Miner is an AI ore
        // harvester. The PIG brain skips miner ships. See shared/ShipKind.cs for the full axis notes.
        public ShipKind Kind;

        // Derived role accessors — Kind is the single source of truth, so these can never drift from
        // it. Reads only; to change a ship's role, assign Kind. Keeps the many pod/miner read sites
        // (incl. negations like !s.IsPod) terse without a second stored flag.
        public bool IsPod => Kind == ShipKind.Pod;
        public bool IsMiner => Kind == ShipKind.Miner;

        // AI ore miner (Kind == ShipKind.Miner) live hold: He3 units, 0..the class def's OreCapacity.
        // HarvestStep fills it, an offload drains it.
        public float Ore;

        // True on the ticks a miner actually moved ore this step (drives ShipFlagMining on the wire so
        // clients can tag the drone as actively harvesting). Set/cleared per tick in MinerExecute.
        public bool IsHarvesting;

        // Tick of the most recent resolved physical contact (ship, asteroid, or base bounce) —
        // stamped by every bounce seam, damaging or not. Consumed by DisruptCollidedMiners: a
        // Harvesting miner bumped this tick drops its beam and re-approaches the rock.
        public uint LastCollisionTick;

        // Server-side autopilot engaged on this ship (player-requested navigation, WP1). While set,
        // InputFor synthesizes steering instead of using the client's held input, and WriteShip raises
        // ShipFlagAutopilot so the owning client suspends its own-ship prediction. The target is one of
        // {ship, base, rock, waypoint} — resolved each tick in AutopilotStep by ApKind.
        public bool ApEngaged;
        public byte ApKind; // 0 ship, 1 base, 2 rock, 3 waypoint (matches MsgSetAutopilot kind)
        public ulong ApTargetId; // target entity id (ship / base / rock); 0 for waypoint
        public uint ApWaypointSector; // waypoint's sector (ApKind==3); 0 / ignored for entity kinds
        public Vec3 ApWaypointPos; // waypoint position (ApKind==3); ignored for entity kinds

        // ---- Friendly-base docking maneuver (server-only, NEVER serialized) — the 3-phase state
        // machine in DockApproach. Reset on every autopilot engage; recomputed live each tick with
        // per-tick demotion guards (a hull bounce / drift / bad alignment self-heals to Transit). ----
        public byte ApDockPhase; // 0 Transit, 1 Align, 2 Creep — friendly-base dock leg only
        public int ApDockDoor = -1; // sticky BaseDockFaces index for this engagement (-1 = unchosen)
        public uint ApDockPhaseTick; // tick of the last phase change (align/creep timeouts)

        // ---- Guided-missile launcher state (0 / false on hulls with no missile mount) ----
        public byte MissileAmmo; // rounds left in the rack (init = mount MagazineSize at spawn)
        public uint LastMissileTick; // last tick a missile launched (fire-cadence gate)
        public ulong LockTargetId; // the ship this ship is trying to lock (from input)
        public uint LockProgress; // consecutive ticks the target held a valid lock (vs LockTicks)
        public bool Locked; // LockProgress reached LockTicks — a missile may launch
        public byte LockState; // wire byte: bit7 = Locked, bits0-6 = progress 0..100 (computed in UpdateLock)

        // ---- Chaff / mine dispenser state (0 on hulls carrying no matching cargo) ----
        // Ammo comes from the validated spawn cargo (D6), NOT a rack MagazineSize; the weapon ids are
        // the class's chaff/mine dispenser WeaponDefs (resolved at spawn). Last*Tick are the
        // authoritative cadence gates for held-input replay (mirror LastMissileTick).
        public byte ChaffAmmo;
        public byte MineAmmo;
        public uint LastChaffTick;
        public uint LastMineTick;
        public uint ChaffWeaponId;
        public uint MineWeaponId;

        // ---- Recon-probe dispenser state (0 on hulls carrying no probe cargo) — same D6/D9 seam
        // as the chaff/mine ammo above (Simulation.Probes.cs). ----
        public byte ProbeAmmo;
        public uint LastProbeTick;
        public uint ProbeWeaponId;

        // ---- Fuel-pod reserve (0 on hulls carrying no fuel cargo). No dispenser/weapon and no
        // input: a charge auto-consumes in Pass A when the tank empties while boost is held. ----
        public byte FuelPodAmmo; // charges left (packs × ChargesPerPack, capped 255)
        public float FuelPodFuelPerCharge; // tank refill per consumed charge (clamped to MaxFuel)

        // Last tick this ship spawned a minefield hit-FX ping (rate-limits the client's small
        // explosion + pop while it sits inside a lethal field — StepMines throttles off this).
        public uint LastMineFxTick;

        // Being-locked warning: 0 = no enemy locking me, 1 = a lock is progressing, 2 = a lock
        // completed. Reset to 0 every tick before Pass A, raised by UpdateLock on the TARGET; the
        // wire flags byte carries it to the client (ShipFlagLockingMe/LockedMe).
        public byte ThreatLockState;

        // Credits this ship's spawn deducted from the team (D7 dock refund). Set in SpawnCombatShip
        // from the class Cost that TryReserveSpawn charged; 0 for PIGs/pods (they pay nothing).
        public int PaidCost;

        // Why this ship left the world, carried on its ShipGone so the client renders the right FX
        // instead of guessing (GoneDestroyed = fiery blast, GoneClean = silent despawn for a
        // voluntary dock / pod rescue). Default 0 = destroyed; DockShip flips it to clean.
        public byte GoneReason;

        // Tick-stamped input ring (module ShipInput buffer equivalent): an input stamped
        // for tick T is applied exactly AT tick T, so the server replays the same input
        // sequence the client predicted with — the contract client prediction relies on.
        public readonly ShipInputState[] InputRing = new ShipInputState[InputRingSize];
        public readonly uint[] InputRingTick = new uint[InputRingSize]; // 0 = empty slot
    }

    public const int InputRingSize = 64;

    // An in-flight guided missile. A separate entity from ShipSim (own list, no flight-model
    // integration): turn-rate-limited pure pursuit in StepMissiles, direct-Power damage on impact.
    // Ids come from the shared _nextShipId counter (unique across ships + missiles).
    public sealed class MissileSim
    {
        public ulong MissileId;
        public ulong OwnerShipId; // launching ship (never a valid sweep target)
        public byte Team; // owner team (friendlies are not swept)
        public uint WeaponId; // missile-kind WeaponDef (ballistics + model/trail)
        public uint SectorId;
        public Vec3 Pos;
        public Vec3 Vel;
        public ulong TargetShipId; // the ship the seeker homes on (0 / invalid → coast)
        public uint ExpireAtTick; // launch tick + ProjectileLifeTicks

        // Chaff substitution latch (D5): once a chaff puff wins the decoy roll, the seeker breaks its
        // ship lock (TargetShipId=0) and homes on this puff instead. 0 = not decoyed. Track A fills.
        public ulong DecoyChaffId;
    }

    // Live missiles (appended by TryFireMissile in Pass A, stepped in StepMissiles). Exposed to the
    // hub for AOI-filtered MsgMissiles fan-out.
    private readonly List<MissileSim> _missiles = new();
    public IReadOnlyList<MissileSim> Missiles => _missiles;

    // Missiles that detonated / expired this step, drained by the hub into MsgMissileGone frames.
    // Reason 0 = expired/coasted out, 1 = impact. Cleared at the top of Step (like DeathsThisStep).
    public readonly List<(ulong id, byte reason, uint sector, Vec3 pos)> MissileGoneThisStep = new();

    // Chaff puffs ejected this step, drained by the hub into one-shot MsgChaff broadcasts (D2 — the
    // client animates + expires them locally, so there's no gone-message). Cleared at top of Step.
    public readonly List<ChaffSim> ChaffSpawnedThisStep = new();

    // Individual mines that popped this step (triggered/expired), drained by the hub into MsgMineGone
    // frames for per-mine pop FX + aliveMask reconcile. Reason 0 = field expired, 1 = triggered.
    // Cleared at top of Step.
    public readonly List<(ulong fieldId, byte mineIndex, byte reason, uint sector, Vec3 pos)> MineGoneThisStep = new();

    // Live minefields (deployed by TryDeployMine, stepped in StepMines). The hub streams the client's
    // anchor-sector fields on change + coarse keepalive (a lethal static hazard must not AOI-pop).
    public IReadOnlyList<MineFieldSim> Minefields => _minefields;

    // Set whenever a field was added/removed or its aliveMask changed this step, so the hub sends a
    // fresh (possibly empty) frame promptly instead of only on the coarse cadence. Cleared at top of Step.
    public bool MinefieldsChangedThisStep { get; private set; }

    private readonly record struct PendingShot(
        ulong TargetShipId,
        int BaseIndex,
        float Damage,
        float ShieldMult,
        ulong TargetProbeId = 0,
        bool Heal = false
    );

    // Settable (not readonly) so a map switch can swap in a fresh arena at match start (StartMatch,
    // sim thread). Reads across the sim keep working — it's a reference field, reassignment is atomic.
    public World World { get; private set; }
    private readonly ILogger _log;
    private readonly Dictionary<ulong, ShipSim> _ships = new();
    private readonly List<ShipSim> _order = new(); // stable iteration order
    private ulong _nextShipId = 1;
    private uint _tick;

    // Server-only RNG for non-deterministic gameplay effects whose result is baked into
    // ship state (warp exit jitter, pod eject impulse/tumble) — clients read the result
    // from snapshots, they never reproduce the draw, so plain Random is fine.
    private readonly Random _rng = new();

    // Inputs/joins from socket threads, drained by the sim thread each step.
    private readonly Queue<(int clientId, uint tick, ShipInputState input)> _inputQueue = new();

    // Player autopilot engage/disengage requests (MsgSetAutopilot), drained alongside the input queue.
    private readonly Queue<(int clientId, byte mode, byte kind, ulong id, uint sector, Vec3 pos)> _autopilotQueue = new();
    private readonly Queue<(
        int clientId,
        byte team,
        byte cls,
        (uint cargoId, byte count)[] cargo,
        ulong launchBaseId,
        (byte hpIndex, uint weaponId)[] mounts
    )> _joinQueue = new();
    private readonly Queue<int> _leaveQueue = new();

    // Unexpected-drop detach (park the ship for the grace window) and reconnect reclaim (hand it
    // back to the returning connection), both keyed by the connection's reconnect token.
    private readonly Queue<(int clientId, string token)> _detachQueue = new();
    private readonly Queue<(int clientId, string token)> _reclaimQueue = new();
    private readonly object _qLock = new();

    // How long a disconnected player's ship is held in the sim before it's reaped — the window a
    // reconnecting client has to reclaim it (world.yaml `reconnect-grace-seconds`, stock 5s).
    // The ship stays simulated and vulnerable.
    private readonly uint GraceTicks;

    // Ships held alive after an unexpected drop, keyed by the connection's reconnect token (hex).
    // Value is the still-bound OLD client id (the ship is still in _byClient[oldClientId]) plus
    // the tick at which the hold expires. Keyed by old client id rather than ShipSim so a
    // death->escape-pod swap during the grace window is followed transparently. Sim-thread only.
    private readonly Dictionary<string, (int oldClientId, uint expiryTick)> _heldOrphans = new();

    // The ship a client currently controls — a combat ship, OR (after death) the escape pod
    // it's flying. Absent while the client is dead and waiting on a respawn. ShipId changes
    // across combat->pod->respawn, so the hub re-sends YouAre whenever this ship flips.
    private readonly Dictionary<int, ShipSim> _byClient = new();

    // Remembered join class/team/cargo/mounts per connected client, so a respawn re-creates the
    // same ship with the same validated consumable hold AND weapon-slot overrides.
    private readonly Dictionary<
        int,
        (byte team, byte cls, (uint cargoId, byte count)[] cargo, ulong launchBaseId, (byte hpIndex, uint weaponId)[] mounts)
    > _clientInfo = new();

    // Chaff/mine dispenser WeaponDefs keyed by the cargo id they consume (D8 — dispensers are not
    // hardpoint-mounted; a spawn's cargo id names which dispenser its ammo feeds). Cargo item mass
    // by id, for the spawn-time payload validation. Built once from Content in the ctor.
    private readonly Dictionary<uint, WeaponDef> _dispenserByCargo = new();
    private readonly Dictionary<uint, float> _cargoMass = new();
    private readonly Dictionary<uint, byte> _chargesPerPack = new(); // dispenser ammo = packs × this
    private readonly Dictionary<uint, float> _fuelPerCharge = new(); // fuel cargo: tank refill per charge

    // Clients with no live ship and a scheduled respawn tick (set when a player pod resolves).
    private readonly Dictionary<int, uint> _clientRespawn = new();

    // Deferred structural mutations within a Step (you can't add/remove ships mid-pass while
    // iterating _order): collected during the collision/death/dock pass, applied after it.
    private readonly List<ShipSim> _toRemove = new();
    private readonly List<ShipSim> _toAdd = new();

    // Shots whose analytic outcome lands on a future tick (ring keyed by tick % size) —
    // the in-memory equivalent of the module's ShotResolution table.
    private readonly List<PendingShot>[] _shotRing;

    // Reused across CellsAlongRay calls (hot path per bolt) to avoid per-call allocation.
    private readonly HashSet<(int, int, int)> _rayCells = new();

    // Per-tick ship spatial grid for shot broad-phase (module ShipGridForSector).
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<ShipSim>>> _shipGrid = new();

    // Removals this step, drained by the hub to emit ShipGone events. Each carries a reason so the
    // client renders a blast (GoneDestroyed) or a silent despawn (GoneClean = dock / pod rescue).
    public readonly List<(ulong id, byte reason)> DeathsThisStep = new();

    // ShipGone reason codes (mirrored on the client in GameNetClient). Default 0 = a real death.
    public const byte GoneDestroyed = 0;
    public const byte GoneClean = 1;

    // Match lifecycle. The server is now the lobby host: it starts in Lobby, the matchmaker
    // (ShouldStartMatch hook, polled each step) flips it to Active, a destroyed base flips it
    // to Ended, and a few seconds later it returns to Lobby for the next match. Phase values
    // match the snapshot wire byte (0 Lobby, 1 Active, 2 Ended).
    public const byte PhaseLobby = 0;
    public const byte PhaseActive = 1;
    public const byte PhaseEnded = 2;
    public const byte NoWinner = 255;
    public byte Phase { get; private set; } = PhaseLobby;
    public byte Winner { get; private set; } = NoWinner;

    // AI drones spawn only while this is true. Default OFF; SIM_PIGS=1|true flips the
    // server default to ON. Toggled live by the /pigs chat command (set on a network
    // thread, read on the sim thread) — volatile for cross-thread visibility.
    public volatile bool PigsEnabled;

    // Regenerating shields are active when true (the default). Turned off, ships spawn with no shield
    // and every hit lands on the hull — used by the damage-mechanic tests (missile/mine/collision) to
    // isolate raw damage from shield absorption. The shield mechanic itself is covered by ShieldTest.
    public bool ShieldsEnabled = true;

    // How long the Ended result lingers before the server returns to the lobby for the next match
    // (world.yaml `ended-to-lobby-seconds`, stock 6s).
    private readonly uint EndedToLobbyTicks;
    private uint _returnToLobbyAtTick;

    // Lobby integration hooks (set by Program/ClientHub; null in unit tests). ShouldStartMatch
    // is polled every step while in Lobby — it consults the live lobby roster + the matchmaker
    // on the calling (sim) thread, reading thread-safe lobby state, so all sim mutation stays
    // on the sim thread. OnReturnToLobby lets the hub clear ready flags for the next match.
    public Func<bool>? ShouldStartMatch;
    public Action? OnReturnToLobby;

    // Map switch: BuildMatchWorld yields a fresh World for the lobby-selected map (null = keep the
    // current arena); StartMatch swaps it in so the arena the players play is built from the picked
    // map, not the boot default. OnMatchStart lets the hub re-Welcome every client onto the new world
    // and drop its world-derived caches. Both fire on the sim thread inside StartMatch.
    public Func<World?>? BuildMatchWorld;
    public Action? OnMatchStart;

    // True only on the single step the match ends — Program.cs reads it to fire the
    // one-shot result writeback (IMatchResultSink).
    public bool JustEnded { get; private set; }

    // Set whenever a base took damage this step (or the match ended), so the hub streams
    // a fresh Bases frame instead of leaving clients on the Welcome-time values.
    public bool BasesChangedThisStep { get; private set; }

    // Set whenever per-team economy changed this step (paycheck accrued, economy (re)seeded), so
    // the hub streams a fresh TeamState frame promptly instead of waiting on the coarse cadence.
    public bool TeamStateChangedThisStep { get; private set; }

    // Set whenever the MsgShipLoadout table changed this step (a ship with mount overrides spawned
    // or left), so the hub streams a fresh frame promptly; the coarse keepalive heals late joiners.
    // Ships flying the authored class loadout are OMITTED from the table (clients fall back to the
    // class default), so authored-only spawns don't touch this.
    public bool LoadoutsChangedThisStep { get; private set; }

    // Latches once a match has been touched (base damaged / ended); cleared when a match
    // (re)starts or returns to the lobby. IsIdle reads it so the empty-server reset knows
    // whether the sim still has a live/finished match to tear down.
    private bool _matchDirty;

    // True when the sim is already a clean idle lobby — no match running and no ships left.
    // The sim loop resets an emptied-out server to this state after a grace window, then the
    // server sits idle here (matchmaker won't start a match until players rejoin and ready up).
    // Reading it keeps that reset idempotent: it fires once per empty spell, not every tick.
    public bool IsIdle => Phase == PhaseLobby && _order.Count == 0 && !_matchDirty;

    public uint Tick => _tick;
    public int ShipCount => _order.Count;
    public IReadOnlyList<ShipSim> Ships => _order;

    // The hub gates spawn requests (MsgSpawn) on this — ships only spawn during a live match.
    public bool IsActive => Phase == PhaseActive;

    public Simulation(World world, ContentSet content, ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        World = world;
        Content = content;

        // Resolve the authored server-side tuning blocks (world.yaml) once. Stock values
        // come from the shared classes' initializers when a block/knob is unauthored.
        _mech = content.World.Mechanics;
        _combat = content.World.Combat;
        PaycheckTicks = System.Math.Max(1u, (uint)MathF.Round(_mech.PaycheckSeconds * TickHz));
        DockRadiusFrac = _mech.DockRadiusFrac;
        LaunchSpeed = _mech.LaunchSpeed;
        RescueRadius = World.ShipRadius * _mech.RescueRadiusMult;
        PodEjectSpeed = _mech.PodEjectSpeed;
        PodEjectSpin = _mech.PodEjectSpin;
        GraceTicks = (uint)MathF.Round(_mech.ReconnectGraceSeconds * TickHz);
        EndedToLobbyTicks = (uint)MathF.Round(_mech.EndedToLobbySeconds * TickHz);
        InitPigTuning(content.World.Ai);

        // Resolve the per-match def lookups ONCE from the loaded content (defaults, or YAML-overlaid).
        WeaponDefs = content.Weapons.ToDictionary(w => w.WeaponId);
        ShipDefs = content.Ships.ToDictionary(d => d.ClassId);
        ClassMuzzles = BuildMuzzles(content.Ships);
        // Missile-kind subset of the muzzles, filtered once against the resolved WeaponDefs.
        ClassMissileMounts = new Muzzle[ClassMuzzles.Length][];
        for (int c = 0; c < ClassMuzzles.Length; c++)
        {
            var mis = new List<Muzzle>();
            foreach (var m in ClassMuzzles[c])
                if (WeaponDefs.TryGetValue(m.WeaponId, out var wd) && wd.Kind == WeaponKind.Missile)
                    mis.Add(m);
            ClassMissileMounts[c] = mis.ToArray();
        }
        _stats = new Dictionary<byte, ShipStats>(content.Ships.Count);
        foreach (var d in content.Ships)
            _stats[d.ClassId] = ShipStats.FromDef(d); // same path the client takes → identical flight

        // Dispenser (chaff/mine/probe) WeaponDefs indexed by the cargo item they consume, + cargo
        // masses for the spawn-time payload check. Dispensers aren't mounted on hardpoints (D8).
        foreach (var w in content.Weapons)
            if ((w.Kind == WeaponKind.Chaff || w.Kind == WeaponKind.Mine || w.Kind == WeaponKind.Probe) && w.CargoId != 0)
                _dispenserByCargo[w.CargoId] = w;
        foreach (var c in content.CargoItems)
        {
            _cargoMass[c.CargoId] = c.Mass;
            _chargesPerPack[c.CargoId] = System.Math.Max((byte)1, c.ChargesPerPack);
            if (c.FuelPerCharge > 0f)
                _fuelPerCharge[c.CargoId] = c.FuelPerCharge; // fuel cargo — no dispenser WeaponDef
        }

        // PIG lead-prediction constants off the scout gun (all server weapons share these today).
        var pigShot = WeaponDefs[GameContent.ScoutWeaponId];
        PigShotSpeed = pigShot.ProjectileSpeed;
        PigShotLifeTicks = pigShot.ProjectileLifeTicks;
        PigShotSpeedSq = PigShotSpeed * PigShotSpeed;
        PigMaxLead = PigShotLifeTicks * FlightModel.Dt;

        PigsEnabled = (System.Environment.GetEnvironmentVariable("SIM_PIGS") ?? "") is "1" or "true";
        _shotRing = new List<PendingShot>[ShotRingSize];
        for (int i = 0; i < ShotRingSize; i++)
            _shotRing[i] = new List<PendingShot>();

        InitVision(); // fog-of-war switch + per-team vision state (Simulation.Vision.cs)
    }

    // ---- Thread-safe intake (called from socket tasks) -------------------

    // Join with an explicit consumable hold (chaff/mine counts from MsgSpawn). Empty cargo ⇒ the
    // hull's authored DefaultCargo is seeded at spawn. launchBaseId picks the friendly base to
    // launch from (v36 hangar sidebar); 0/invalid ⇒ the first team base (legacy behavior).
    // `mounts` = the hangar's weapon-slot overrides ((hardpoint Index, weaponId) pairs; weaponId
    // HardpointDef.NoWeapon = deliberately empty); empty ⇒ the authored class loadout.
    public void EnqueueJoin(
        int clientId,
        byte team,
        byte cls,
        (uint cargoId, byte count)[] cargo,
        ulong launchBaseId = 0,
        (byte hpIndex, uint weaponId)[]? mounts = null
    )
    {
        lock (_qLock)
            _joinQueue.Enqueue(
                (
                    clientId,
                    team,
                    cls,
                    cargo ?? System.Array.Empty<(uint, byte)>(),
                    launchBaseId,
                    mounts ?? System.Array.Empty<(byte, uint)>()
                )
            );
    }

    // Compat overload (no cargo): the hull's DefaultCargo is used. Kept for tests / callers that
    // don't carry a hold.
    public void EnqueueJoin(int clientId, byte team, byte cls) =>
        EnqueueJoin(clientId, team, cls, System.Array.Empty<(uint, byte)>());

    public void EnqueueLeave(int clientId)
    {
        lock (_qLock)
            _leaveQueue.Enqueue(clientId);
    }

    // Unexpected drop of a flying client: park its ship for the grace window instead of removing
    // it, so the player can reconnect and reclaim it. Token is the connection's reconnect token.
    public void EnqueueDetach(int clientId, string token)
    {
        lock (_qLock)
            _detachQueue.Enqueue((clientId, token));
    }

    // A reconnecting client re-presented a token: hand it back the ship still held under that
    // token, rebinding it to this new connection's id.
    public void EnqueueReclaim(int newClientId, string token)
    {
        lock (_qLock)
            _reclaimQueue.Enqueue((newClientId, token));
    }

    public void EnqueueInput(int clientId, uint tick, ShipInputState input)
    {
        lock (_qLock)
            _inputQueue.Enqueue((clientId, tick, input));
    }

    // Player autopilot engage (mode=1) / disengage (mode=0) request. Mirrors EnqueueInput: the socket
    // thread queues it, the sim thread applies it in DrainQueues (owner-validated there). kind selects
    // the target flavour (0 ship, 1 base, 2 rock, 3 waypoint); id/sector/pos carry the target.
    public void EnqueueSetAutopilot(int clientId, byte mode, byte kind, ulong id, uint sector, Vec3 pos)
    {
        lock (_qLock)
            _autopilotQueue.Enqueue((clientId, mode, kind, id, sector, pos));
    }

    public ulong ShipIdOf(int clientId)
    {
        lock (_qLock)
            return _byClient.TryGetValue(clientId, out var s) ? s.ShipId : 0;
    }

    // ---- One fixed-dt authoritative step ---------------------------------

    public void Step()
    {
        uint tick = ++_tick;
        float dt = FlightModel.Dt;
        // Accumulate the prior step's completed deaths for the fog witnessed-death rule BEFORE the
        // drain list is cleared (consumed + reset at the next vision apply, Simulation.Vision.cs).
        if (FogEnabled)
            foreach (var d in DeathsThisStep)
                _visionDeaths.Add(d.id);
        DeathsThisStep.Clear();
        LostContactsThisStep.Clear();
        MissileGoneThisStep.Clear();
        ChaffSpawnedThisStep.Clear();
        MineGoneThisStep.Clear();
        MinefieldsChangedThisStep = false;
        ProbeGoneThisStep.Clear();
        ProbesChangedThisStep = false;
        JustEnded = false;
        BasesChangedThisStep = false;
        TeamStateChangedThisStep = false;
        LoadoutsChangedThisStep = false;
        // Live rock shrink deltas accumulate on World across a step; clear alongside the other
        // change flags so a later wire stream drains only this step's changed rocks (nothing yet).
        World.RocksChangedThisStep.Clear();
        World.RocksRemovedThisStep.Clear(); // rock despawns (constructor completions) drained by the hub
        MinerNoticesThisStep.Clear();
        ConstructorNoticesThisStep.Clear();
        BasesCreatedThisStep.Clear();
        OrderNoticesThisStep.Clear();
        OrderDirectivesThisStep.Clear();
        ResearchChangedThisStep = false;
        ResearchNoticesThisStep.Clear();
        ResearchTeamNoticesThisStep.Clear();
        ConstructorChangedThisStep = false;

        DrainQueues(tick);
        ExpireHeldOrphans(tick);

        // Lobby host: poll the matchmaker while waiting in the lobby, and return to the lobby
        // a few seconds after a match ends so the next one can be readied up.
        if (Phase == PhaseLobby)
        {
            if (ShouldStartMatch?.Invoke() == true)
                StartMatch();
        }
        else if (Phase == PhaseEnded && tick >= _returnToLobbyAtTick)
        {
            ReturnToLobby();
        }

        ProcessRespawns(tick);
        if (Phase == PhaseActive)
        {
            PigBrainStep(tick); // 5 Hz AI decisions + squad lifecycle (Simulation.Pig.cs)
            MinerBrainStep(tick); // 5 Hz miner lifecycle + rock/base targeting (Simulation.Mining.cs)
            ConstructorBrainStep(tick); // 5 Hz constructor build lifecycle (Simulation.Constructors.cs)
            AccrueTeamCredits(tick); // Stage-2: flat per-team credit paycheck every PaycheckTicks
            ResearchStep(tick); // Stage-4: per-base research progress + on-deck promotion (Simulation.Research.cs)
            // Fog of war: 2 Hz per-team vision (apply previous kick + kick next, Simulation.Vision.cs).
            // Off the sim tick's critical path — only ever delays the NEXT vision result, never a tick.
            if (FogEnabled && tick % VisionEvery == 0)
                VisionStep(tick); // commits deferred rock removals at its worker join
            else if (!FogEnabled)
                CommitPendingRockRemovals(); // no vision worker → safe to mutate the rock grid now
        }
        ResolveDueShots(tick);
        RebuildShipGrid();

        // Being-locked warning is recomputed from scratch each tick: clear every ship's threat
        // state, then Pass A's UpdateLock raises it on whatever ships are currently being locked.
        foreach (var s in _order)
            s.ThreatLockState = 0;

        // Pass A: integrate + fire + warp (mirrors module Pass A). Every ship in _order is
        // live (dead ships were removed at the end of the step that killed them); PIGs are
        // server-driven, players (incl. their pods) replay their held/exact-tick input.
        foreach (var s in _order)
        {
            var input = InputFor(s, tick);
            var stats = StatsFor(s.Class, s.IsPod);
            // Fuel-pod auto-load: Integrate's afterburner gate reads PRE-tick fuel (the tick that
            // empties the tank still burns), so refilling here — on the first tick the tank sits
            // empty while boost is held — keeps the afterburner lit with no gap and no AbPower
            // decay. Server-side only; FlightModel.Integrate stays untouched (PIG determinism).
            // The client mirrors this rule in PredictionController.
            if (
                !s.IsPod
                && s.FuelPodAmmo > 0
                && input.Boost
                && stats.MaxFuel > 0f
                && stats.AbThrust > 0f
                && s.State.Fuel <= 0f
            )
            {
                s.FuelPodAmmo--;
                s.State.Fuel = MathF.Min(stats.MaxFuel, s.FuelPodFuelPerCharge);
            }
            s.State = FlightModel.Integrate(s.State, input, stats);
            s.LastInputTick = tick;
            // Pods are unarmed — only an armed combat ship fires / locks / dispenses.
            if (!s.IsPod)
            {
                UpdateLock(s, input, tick); // missile lock timer (no-op on hulls with no rack)
                if (input.Firing)
                    TryFire(s, tick);
                if (input.Firing2)
                    TryFireMissile(s, tick);
                if (input.DropChaff)
                    TryDropChaff(s, tick); // dispenser cadence-gated (Simulation.Chaff.cs)
                if (input.DropMine)
                    TryDeployMine(s, tick); // dispenser cadence-gated (Simulation.Mines.cs)
                if (input.DropProbe)
                    TryDeployProbe(s, tick); // dispenser cadence-gated (Simulation.Probes.cs)
            }
            TryWarp(s);
        }

        // Chaff drifts (drag + expiry) before missiles resolve their aim, so a puff ejected this
        // tick is a candidate decoy this tick. StepMines runs after missiles (mines are static).
        StepChaff(tick); // Simulation.Chaff.cs

        // Guided missiles: steer + sweep + damage, between Pass A (which may have launched some this
        // tick) and Pass C. A missile launched this tick coasts its first partial segment next tick.
        StepMissiles(tick, dt);

        // Mines: expire/deplete fields + proximity-trigger armed mines against the ship grid.
        StepMines(tick); // Simulation.Mines.cs

        // Recon probes: expire past their lifespan (passive — no per-tick effect otherwise).
        StepProbes(tick); // Simulation.Probes.cs

        // Pass C: ship-vs-ship collisions between ALL ships regardless of team (mass-weighted
        // impulse, module-identical), O(n²) over live ships — 200 ships = 20k pairs, trivial natively.
        for (int i = 0; i < _order.Count; i++)
        {
            var a = _order[i];
            for (int j = i + 1; j < _order.Count; j++)
            {
                var b = _order[j];
                if (a.SectorId != b.SectorId)
                    continue;
                CollideShips(a, b);
            }
        }

        // Boundary erosion, asteroid/base bounces, docking, death resolution. Structural
        // changes (pod ejection, despawn, dock) are deferred via _toRemove/_toAdd so we
        // don't mutate _order while iterating it.
        foreach (var s in _order)
            ResolveBoundaryCollisionsAndDocking(s, tick, dt);
        ApplyStructural();

        // Miner beam disruption: runs after Pass C and the structural loop so every bounce seam
        // has stamped LastCollisionTick, and after ApplyStructural so killed miners' slots are gone.
        DisruptCollidedMiners(tick);

        // Rescue pass: a pod in DIRECT hull contact with a friendly non-pod ship (same
        // sector) is rescued — same resolution as docking. Runs over the post-death set.
        foreach (var pod in _order)
        {
            if (!pod.IsPod)
                continue;
            foreach (var friend in _order)
            {
                if (friend.IsPod || friend.Team != pod.Team || friend.SectorId != pod.SectorId)
                    continue;
                if ((pod.State.Pos - friend.State.Pos).LengthSquared() <= RescueRadius * RescueRadius)
                {
                    DockShip(pod, tick);
                    break;
                }
            }
        }
        ApplyStructural();

        // Shield recharge sweep (end-of-tick so every damage phase this tick has already stamped
        // ShieldDamageTick — a ship hit this tick won't regen this tick). A shielded ship refills at
        // its authored rate once the quiet delay since the last shield hit has elapsed.
        foreach (var s in _order)
        {
            if (!ShieldsEnabled || !s.Alive)
                continue;
            float cap = ShieldCapacityFor(s);
            if (cap <= 0f || s.Shield >= cap)
                continue;
            if (tick - s.ShieldDamageTick < ShieldDelayTicksFor(s))
                continue;
            s.Shield = MathF.Min(cap, s.Shield + ShieldRechargeFor(s) * dt);
        }
    }

    // Per-ship boundary erosion, asteroid/base-sphere/deployable collisions, and enemy-bounce /
    // own-base dock-face-or-solid-shell resolution — the Pass-C-adjacent structural pass in Step().
    // Structural changes (pod ejection, despawn, dock) are deferred via _toRemove/_toAdd so the
    // caller's foreach over _order is safe; ApplyStructural() runs once after the whole pass.
    private void ResolveBoundaryCollisionsAndDocking(ShipSim s, uint tick, float dt)
    {
        float over = s.State.Pos.Length() - World.SectorRadius(s.SectorId);
        if (over > 0f)
            ApplyDamage(
                s,
                MathF.Min(_combat.BoundaryBaseDps + over * _combat.BoundaryRampDps, _combat.BoundaryMaxDps) * dt,
                tick
            );

        ResolveAsteroidCollisions(s);
        ResolveBuildSphereCollisions(s); // solid, growing base-construction shells
        ResolveDeployableCollisions(s, tick); // solid deployables (recon probes today)

        // Bases in this ship's sector: an ENEMY base is solid (bounce); your OWN base is
        // your dock — fly into its core and the ship/pod resolves (player ship/pod ->
        // scheduled respawn; PIG pod -> slot freed). Docking ends this ship's tick.
        bool docked = false;
        foreach (var b in World.Bases)
        {
            if (b.SectorId != s.SectorId)
                continue;
            if (b.Team != s.Team)
            {
                ResolveBaseCollision(s, b.Pos, b.BaseTypeId); // enemy base: fully solid hull
                continue;
            }
            if (ResolveOwnBaseDock(s, b, tick))
            {
                docked = true;
                break;
            }
        }
        if (docked)
            return;

        if (s.Health <= 0f)
            ResolveDeath(s, tick);
    }

    // Own-base branch of ResolveBoundaryCollisionsAndDocking: with a loaded hull you dock ONLY by
    // flying your ship into a rectangular docking door (a bounded face authored as a group of 5
    // HP_DockingEntrance markers) — the rest of the base is a solid hull that bounces you. Without
    // a model, fall back to the legacy core-sphere dock so docking can't break. Returns true when
    // the ship docked (or a miner offloaded) this tick.
    private bool ResolveOwnBaseDock(ShipSim s, World.BaseSite b, uint tick)
    {
        Vec3 d = s.State.Pos - b.Pos;
        if (World.BaseHullOf(b.BaseTypeId) is not null)
        {
            if (
                Collide.IntersectsDockFace(d, World.BaseDockFacesOf(b.BaseTypeId), World.DockFaceDepth, World.ShipRadius)
            )
            {
                // Intersected a rectangular docking door. Miners never enter DockShip
                // (it refunds PaidCost + rebinds the client) — they offload instead.
                if (s.IsMiner)
                    OffloadMiner(s, b, tick);
                else
                    DockShip(s, tick);
                return true;
            }
            // Solid shell everywhere else: the DEEPEST contact across the authored compound
            // sub-hulls, resolved through the shared Collide.SphereVsBody kernel so the client
            // predicts the identical bounce (same contact-selection rule — deepest wins). A
            // partless base collapses to the single merged hull, matching the old behaviour.
            if (
                Collide.SphereVsBody(s.State.Pos, World.ShipRadius, BaseBody(b.Pos, b.BaseTypeId), out Vec3 bn, out float bpen)
            )
                BounceShip(s, bn, bpen);
            return false;
        }
        else
        {
            float dockR = World.BaseRadiusOf(b.BaseTypeId) * DockRadiusFrac;
            if (d.LengthSquared() <= dockR * dockR)
            {
                if (s.IsMiner)
                    OffloadMiner(s, b, tick);
                else
                    DockShip(s, tick);
                return true;
            }
            return false;
        }
    }

    private void DrainQueues(uint tick)
    {
        lock (_qLock)
        {
            while (_joinQueue.Count > 0)
            {
                var (cid, team, cls, cargo, launchBase, mounts) = _joinQueue.Dequeue();
                // Remember the slot (team/cls/hold/launch base/mount overrides) and spawn this
                // very step (ProcessRespawns, tick now).
                _clientInfo[cid] = (team, cls, cargo, launchBase, mounts);
                _clientRespawn[cid] = tick;
            }
            while (_leaveQueue.Count > 0)
            {
                int cid = _leaveQueue.Dequeue();
                if (_byClient.Remove(cid, out var ship))
                    RemoveShipNow(ship);
                _clientInfo.Remove(cid);
                _clientRespawn.Remove(cid);
            }
            // Detach: keep the dropped client's ship in _byClient/_ships/_order (still simulated
            // and vulnerable) but zero its input so it coasts — no thrust, no fire — and record
            // the held orphan with its expiry. _clientInfo/_clientRespawn are deliberately kept so
            // a death->pod->respawn during the window still resolves and stays reclaimable.
            while (_detachQueue.Count > 0)
            {
                var (cid, token) = _detachQueue.Dequeue();
                if (_byClient.TryGetValue(cid, out var ship))
                {
                    ship.HeldInput = default;
                    Array.Clear(ship.InputRingTick, 0, ship.InputRingTick.Length);
                    _heldOrphans[token] = (cid, tick + GraceTicks);
                }
            }
            // Reclaim: a returning client re-presented a held token — rebind that ship (or its
            // current pod) from the old client id to the new connection. ShipIdOf(newCid) then
            // returns it and AfterStep re-issues MsgYouAre next tick, so the client resumes.
            while (_reclaimQueue.Count > 0)
            {
                var (newCid, token) = _reclaimQueue.Dequeue();
                if (_heldOrphans.Remove(token, out var orphan) && _byClient.Remove(orphan.oldClientId, out var ship))
                {
                    ship.OwnerClientId = newCid;
                    _byClient[newCid] = ship;
                    if (_clientInfo.Remove(orphan.oldClientId, out var info))
                        _clientInfo[newCid] = info;
                    if (_clientRespawn.Remove(orphan.oldClientId, out var rt))
                        _clientRespawn[newCid] = rt;
                }
            }
            while (_inputQueue.Count > 0)
            {
                var (cid, stamp, input) = _inputQueue.Dequeue();
                if (!_byClient.TryGetValue(cid, out var ship))
                    continue;
                if (stamp == 0 || stamp <= tick)
                {
                    // Unstamped (bots) or LATE (its tick was already simulated): hold it
                    // from now on — the module's dirty/re-derive fallback semantics.
                    ship.HeldInput = input;
                }
                else
                {
                    // Future-stamped: park it in the ring; Pass A applies it exactly at
                    // its tick (and promotes it to the held input from then on).
                    ship.InputRing[stamp % InputRingSize] = input;
                    ship.InputRingTick[stamp % InputRingSize] = stamp;
                }
            }
            // Autopilot engage/disengage (player navigation). Only a client that owns a live,
            // non-pod, non-PIG ship may drive it — everything else is ignored. mode=0 clears; mode=1
            // arms + records the target. kind>3 is malformed → ignored.
            while (_autopilotQueue.Count > 0)
            {
                var (cid, mode, kind, id, sector, pos) = _autopilotQueue.Dequeue();
                if (!_byClient.TryGetValue(cid, out var ship))
                    continue;
                if (ship.IsPod || ship.IsPig || !ship.Alive)
                    continue;
                if (mode == 0)
                {
                    ship.ApEngaged = false;
                    continue;
                }
                if (kind > 3)
                    continue;
                ship.ApEngaged = true;
                ship.ApKind = kind;
                ship.ApTargetId = id;
                ship.ApWaypointSector = sector;
                ship.ApWaypointPos = pos;
                // Fresh dock state per engagement: unchosen door, start in Transit (see DockApproach).
                ship.ApDockPhase = 0;
                ship.ApDockDoor = -1;
                ship.ApDockPhaseTick = tick;
            }
            DrainMinerQueues(tick); // miner buys (Simulation.Mining.cs)
            DrainConstructorQueues(tick); // constructor buys (Simulation.Constructors.cs)
            DrainCommandOrders(); // commander orders for AI vessels (Simulation.Orders.cs)
            DrainResearchOps(tick); // commander research start/cancel/queue (Simulation.Research.cs)
        }
    }

    // Restore a fresh lobby on an empty server (called from the sim loop a grace period after
    // the last client leaves). Tears the match down to a clean idle Lobby so the next handoff
    // readies up afresh, and the server sits idle until then. A live match cut short this way
    // gets its "match ended" log here — the normal win path logs via the result sink instead.
    public void ResetMatch()
    {
        if (Phase == PhaseActive)
            Log.MatchEnded(_log);
        ReturnToLobby();
    }

    // Lobby -> Active. Refills bases, clears the win state and any in-flight shot so a stale
    // resolution can't bleed into the new match. Players spawn on demand (MsgSpawn) once Active.
    // Stage-2 strategy spine: pay every team a flat credit paycheck on the paycheck cadence. Called
    // only while the match is active (gated by the caller). The amount is the faction's authored
    // income; a team with 0 income simply never gains credits. Server-only — no wire change yet.
    private void AccrueTeamCredits(uint tick)
    {
        if (tick % PaycheckTicks != 0)
            return;
        int income = Content.Start.IncomePerPaycheck;
        if (income == 0)
            return;
        foreach (var team in World.TeamStates.Values)
            team.Credits += income;
        TeamStateChangedThisStep = true;
    }

    public void StartMatch()
    {
        if (Phase == PhaseActive)
            return;
        // Build the arena from the lobby-selected map. A fresh World brings its own sectors/bases/
        // asteroids/rock grid and full BaseHealth, so the resets below all operate on the new world.
        var nextWorld = BuildMatchWorld?.Invoke();
        if (nextWorld != null)
            World = nextWorld;
        Phase = PhaseActive;
        Winner = NoWinner;
        _matchDirty = false;
        foreach (var ring in _shotRing)
            ring.Clear();
        // Fresh economy each match: reset every team to its starting credits + base unlocks. SEEDED
        // BEFORE ResetMatchBases (v41): the team attr cache resolves off the freshly-seeded OwnedTechs,
        // so ResetMatchBases stamps base health with the correct Iron ×1.15 station-armor factor.
        World.SeedEconomy(Content.Start);
        RecomputeTeamAttributes();
        World.ResetMatchBases();
        BasesChangedThisStep = true;
        // Fresh research slate too. A map swap already brought a fresh World (fresh ResearchByBase),
        // but StartMatch may REUSE the world (BuildMatchWorld null / same map in tests) — clear
        // explicitly so a previous match's in-flight research never bleeds into the new one.
        foreach (var rs in World.ResearchByBase)
        {
            rs.Active.Clear();
            rs.OnDeck = null;
        }
        ResearchChangedThisStep = true;
        ResolveTeamUnlocks();
        SeedMinerSlots(Tick); // one free miner slot per team, on the fresh economy + world
        DespawnAllConstructors(); // constructors are bought, never seeded — clear any from a prior match
        ResetVision(); // clear/reseed per-team fog vision, drain any in-flight compute (Simulation.Vision.cs)
        TeamStateChangedThisStep = true;
        Log.MatchStarted(_log);
        // World may have just been swapped to a new map — let the hub re-Welcome every client onto it
        // and invalidate its world-derived caches. Runs on the sim thread; Welcome frames are queued
        // before AfterStep streams the first Active snapshot, so clients rebuild geometry in order.
        OnMatchStart?.Invoke();
    }

    // Resolve each team's buildable hulls from its owned techs/capabilities (the Stage-2 unlock hook,
    // riding the library's forward-closure). Runs at match start AND whenever research completes
    // (Simulation.Research.CompleteResearch) — teams DO gain techs mid-match now (Stage 4).
    // The spawn gate (TryReserveSpawn) and the wire snapshot both read the resulting set.
    private void ResolveTeamUnlocks()
    {
        foreach (var ts in World.TeamStates.Values)
            ts.UnlockedClasses = Allegiance
                .Factions.Resolution.BuildableResolver.GetBuildables(Content.Catalog, ts.OwnedTechs, ts.OwnedCapabilities)
                .OfType<Allegiance.Factions.Model.Hull>()
                .Where(h => h.ClassId is not null)
                .Select(h => h.ClassId!.Value)
                .ToHashSet();
    }

    // ---- Faction-level team-wide stat multipliers (v41) --------------------------------------------
    // Test kill-switch (mirrors ShieldsEnabled/MinersEnabled): default on so real matches carry the Iron
    // Coalition GAS. Suites that assert absolute pre-multiplier damage/health/mining numbers flip it off
    // before StartMatch to run through the neutral ×1.0 path (RecomputeTeamAttributes clears the World
    // cache). AttributesEnabledDefault lets a suite that builds many sims neutralize them all with one
    // top-of-file line instead of per-sim; production never touches either (stays true).
    public static bool AttributesEnabledDefault = true;
    public bool AttributesEnabled = AttributesEnabledDefault;

    private static readonly int AttrCount = System.Enum.GetValues<Allegiance.Factions.Model.GameAttribute>().Length;

    // The team's resolved multiplier for one attribute (1.0 when unseeded/disabled). ALL consumption
    // (gun/missile damage, station armor, signature, mining) goes through this single accessor.
    private float TeamAttr(byte team, Allegiance.Factions.Model.GameAttribute a) => World.TeamAttr(team, (int)a);

    // Rebuild every team's resolved multiplier cache = faction base-attributes × each COMPLETED
    // development (a dev counts as completed when the team owns all its granted techs — the same proxy
    // MaybePreUpgradeSpawnedBase uses). Uses the library AttributeResolver so the multiplicative combine
    // has ONE implementation. Called at match start (fresh economy ⇒ faction-base only) and on research
    // completion. INTENTIONALLY UNCONSUMED attrs resolved into the cache but with no sim consumer:
    // MaxShieldStation + MaxEnergy (no station-shield / ship-energy model). A mid-match dev that changed
    // MaxArmorStation would NOT retro-rescale live base health (slice devs carry no attributes — noted).
    private void RecomputeTeamAttributes()
    {
        if (!AttributesEnabled)
        {
            World.ClearTeamAttributes();
            return;
        }
        var faction = Content.Catalog.Factions.Single();
        var devs = Content.Catalog.Developments;
        foreach (var (team, ts) in World.TeamStates)
        {
            bool Completed(Allegiance.Factions.Model.Development d) =>
                d.GrantedTechs.Count > 0 && d.GrantedTechs.All(t => ts.OwnedTechs.Contains(t));
            var resolved = Allegiance.Factions.Resolution.AttributeResolver.Resolve(faction, devs.Where(Completed));
            var arr = new float[AttrCount];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = (float)resolved.Get((Allegiance.Factions.Model.GameAttribute)i);
            World.SetTeamAttributes(team, arr);
        }
    }

    // -> Lobby. Tears down every ship (players + drones), refills bases, clears the win state
    // and shot ring, and lets the hub clear ready flags. Called a few seconds after a match
    // ends and whenever the server empties out.
    public void ReturnToLobby()
    {
        DespawnAllPigs();
        DespawnAllMiners();
        // Tear down any in-flight missiles too (emit gone so live clients don't keep ghosts).
        foreach (var mis in _missiles)
            MissileGoneThisStep.Add((mis.MissileId, 0, mis.SectorId, mis.Pos));
        _missiles.Clear();
        // Tear down chaff + minefields too (a fresh match starts with none). Flag the change so the
        // hub streams an empty minefield frame and live clients drop any lingering field.
        _chaff.Clear();
        _minefields.Clear();
        MinefieldsChangedThisStep = true;
        // Tear down probes too (match reseed clears them). MsgProbes has NO reconcile-by-omission (a
        // probe is only ever removed by an explicit MsgProbeGone — client ApplyProbes never drops on
        // absence), so emit a ProbeGone (reason 1 = cleanup/despawn, rendered as a silent removal — no
        // FX) for every live probe BEFORE clearing, or the client would keep phantom probes (F7).
        foreach (var p in _probes)
            ProbeGoneThisStep.Add((p.ProbeId, 1, p.Team, p.SectorId, p.Pos));
        _probes.Clear();
        ProbesChangedThisStep = true;
        foreach (var s in _order)
        {
            _ships.Remove(s.ShipId);
            DeathsThisStep.Add((s.ShipId, GoneDestroyed));
        }
        _order.Clear();
        _byClient.Clear();
        _clientRespawn.Clear();
        // Held orphans' ships were just torn down by the _order loop above; drop the stale tokens
        // so a reconnect mid-grace can't try to reclaim a ship that no longer exists.
        _heldOrphans.Clear();
        World.ResetMatchBases();
        BasesChangedThisStep = true;
        Phase = PhaseLobby;
        Winner = NoWinner;
        _matchDirty = false;
        foreach (var ring in _shotRing)
            ring.Clear();
        ResetVision(); // drain any in-flight vision compute + clear fog state (Simulation.Vision.cs)
        OnReturnToLobby?.Invoke();
    }

    // ---- Player ship lifecycle (spawn / respawn / death -> pod -> dock/rescue) ----

    // Spawn a combat ship for a connected client at its team base, facing the sector center
    // and launched clear of the base sphere (mirrors the module's SpawnShip).
    private ShipSim SpawnCombatShip(
        int clientId,
        byte team,
        byte cls,
        uint tick,
        (uint cargoId, byte count)[] cargo,
        ulong launchBaseId = 0,
        (byte hpIndex, uint weaponId)[]? mounts = null
    )
    {
        var s = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = clientId,
            Team = team,
            Class = cls,
            Alive = true,
        };
        PlaceAtBase(s, World.ShipRadius, tick, ResolveLaunchBase(team, launchBaseId));
        s.State.Mass = StatsFor(cls, false).Mass;
        s.State.Fuel = StatsFor(cls, false).MaxFuel; // dock-refill: dock despawns, relaunch = full tank
        s.Health = HullFor(cls);
        s.Shield = ShieldsEnabled ? ShieldCapacityFor(s) : 0f; // full shield at spawn; relaunch = full recharge
        s.ShieldDamageTick = 0;
        s.SigBias = ShieldDefFor(s).SignatureBias; // projected default-loadout signature bias
        // Validate the hangar's weapon-slot overrides + consumable hold as ONE loadout (they share
        // PayloadCapacity); any invalid piece reverts BOTH to the authored defaults (logged).
        var (mountIds, hold) = ResolveLoadout(team, cls, mounts, cargo);
        s.MountWeaponIds = mountIds;
        if (mountIds is not null)
            LoadoutsChangedThisStep = true; // MsgShipLoadout table gains a row this step

        if (MissileMountFor(s) is (_, WeaponDef mw)) // full magazine at spawn (no rearm yet); an emptied rack seeds 0
            s.MissileAmmo = mw.MagazineSize;
        // D7: remember what the team paid for this hull (TryReserveSpawn just deducted it) so a
        // voluntary dock can refund it. PIGs/pods never go through here, so they keep PaidCost 0.
        s.PaidCost = ShipDefs.TryGetValue(cls, out var cd) ? cd.Cost : 0;
        // D6/D9: seed the chaff/mine dispenser ammo from the validated spawn cargo (empty ⇒ hull default).
        SeedDispenserAmmo(s, hold);
        _ships[s.ShipId] = s;
        _order.Add(s);
        if (clientId >= 0)
            _byClient[clientId] = s;
        return s;
    }

    // Seed the ship's dispenser ammo/weapon-ids from an ALREADY-VALIDATED hold (ResolveLoadout).
    private void SeedDispenserAmmo(ShipSim s, (uint cargoId, byte count)[] chosen)
    {
        World.TeamStates.TryGetValue(s.Team, out var teamState);
        foreach (var (cargoId, count) in chosen)
        {
            // Fuel cargo has no dispenser WeaponDef and no tier chain — it seeds the auto-load
            // reserve directly (consumed in Pass A when the tank empties mid-boost).
            if (_fuelPerCharge.TryGetValue(cargoId, out float perCharge))
            {
                byte fuelPackSize = _chargesPerPack.TryGetValue(cargoId, out var fpk) ? fpk : (byte)1;
                s.FuelPodAmmo = (byte)System.Math.Min(255, s.FuelPodAmmo + count * fuelPackSize);
                s.FuelPodFuelPerCharge = perCharge;
                continue;
            }
            if (!_dispenserByCargo.TryGetValue(cargoId, out var w))
                continue;
            // Cargo stays one tier-neutral item per line (ids never change); the FIRED tier is the
            // team's researched successor — walk the same chain mounted racks migrate through.
            uint upgraded = MigrateWeaponTier(teamState, w.WeaponId);
            if (upgraded != w.WeaponId && WeaponDefs.TryGetValue(upgraded, out var uw))
                w = uw;
            // `count` is the loaded PACK count; each pack holds ChargesPerPack charges and one charge
            // is spent per gated press. Total charges = packs × pack-size, clamped to the wire byte.
            byte packSize = _chargesPerPack.TryGetValue(cargoId, out var pk) ? pk : (byte)1;
            byte charges = (byte)System.Math.Min(255, count * packSize);
            if (w.Kind == WeaponKind.Chaff)
            {
                s.ChaffAmmo = charges;
                s.ChaffWeaponId = w.WeaponId;
            }
            else if (w.Kind == WeaponKind.Mine)
            {
                s.MineAmmo = charges;
                s.MineWeaponId = w.WeaponId;
            }
            else if (w.Kind == WeaponKind.Probe)
            {
                s.ProbeAmmo = charges;
                s.ProbeWeaponId = w.WeaponId;
            }
        }
    }

    // Resolve a spawn request's weapon-slot overrides + consumable hold into the loadout to seed.
    // The two halves share PayloadCapacity, so they validate as ONE request: every override must
    // name a real Weapon-kind hardpoint and either empty it (NoWeapon) or mount a team-tech-owned
    // weapon the mount's TYPE accepts (HardpointDef.MountAccepts — a gun mount takes guns, a
    // missile mount takes racks, an untyped mount takes either; dispensers never mount); every
    // cargo id must be dispenser cargo; and the EFFECTIVE mount mass + hold mass must fit
    // PayloadCapacity. Any failure rejects the whole request (logged) back to the authored loadout
    // (null mounts = class default) — only a hacked/buggy client hits this, the hangar UI gates
    // mount type, capacity and tech before sending.
    private (uint[]? mountIds, (uint cargoId, byte count)[] cargo) ResolveLoadout(
        byte team,
        byte cls,
        (byte hpIndex, uint weaponId)[]? mounts,
        (uint cargoId, byte count)[] requested
    )
    {
        ShipDefs.TryGetValue(cls, out var def);
        (uint, byte)[] fallbackCargo = def is null
            ? System.Array.Empty<(uint, byte)>()
            : def.DefaultCargo.Select(c => (c.CargoId, c.Count)).ToArray();
        bool wantMounts = mounts is { Length: > 0 } && def is not null;
        bool wantCargo = requested is { Length: > 0 };

        World.TeamStates.TryGetValue(team, out var ts);

        // Effective per-barrel weapon ids: authored muzzles, then overrides applied by hardpoint Index
        // (mapped to barrel = position among the class's Weapon-kind hardpoints — the same declaration
        // order ClassMuzzles/the client's slot list use; Index is NOT assumed to equal position), then
        // a team-wide TIER MIGRATION (a researched gun tier auto-replaces the ones it obsoletes). The
        // migration runs even on a pure authored spawn, so a quick-launch also flies the current tier.
        var muzzles = cls < ClassMuzzles.Length ? ClassMuzzles[cls] : System.Array.Empty<Muzzle>();
        uint[] effective = new uint[muzzles.Length];
        for (int i = 0; i < muzzles.Length; i++)
            effective[i] = muzzles[i].WeaponId;

        if (wantMounts)
        {
            var barrelByIndex = new Dictionary<byte, int>();
            var mountByIndex = new Dictionary<byte, WeaponMountKind>();
            int barrel = 0;
            foreach (var h in def!.Hardpoints)
                if (h.Kind == HardpointKind.Weapon)
                {
                    barrelByIndex[h.Index] = barrel++;
                    mountByIndex[h.Index] = h.Mount;
                }
            foreach (var (hpIndex, weaponId) in mounts!)
            {
                if (!barrelByIndex.TryGetValue(hpIndex, out int b))
                {
                    Log.SpawnMountInvalid(_log, hpIndex, weaponId, cls);
                    return (null, fallbackCargo);
                }
                if (weaponId == HardpointDef.NoWeapon)
                {
                    effective[b] = HardpointDef.NoWeapon; // deliberately-empty slot
                    continue;
                }
                if (
                    !WeaponDefs.TryGetValue(weaponId, out var w) || !HardpointDef.MountAccepts(mountByIndex[hpIndex], w.Kind)
                )
                {
                    // Unknown, a dispenser (D8: not mountable), or the wrong category for this
                    // mount's type (missile on a gun mount / gun on a missile mount).
                    Log.SpawnMountInvalid(_log, hpIndex, weaponId, cls);
                    return (null, fallbackCargo);
                }
                foreach (ushort t in w.RequiredTechIdx)
                    if (ts is null || t >= Content.Techs.Count || !ts.OwnedTechs.Contains(Content.Techs[t].Id))
                    {
                        Log.SpawnMountTechLocked(_log, weaponId, team);
                        return (null, fallbackCargo);
                    }
                effective[b] = weaponId;
            }
        }

        // Weapon-tier migration (server-authoritative twin of ShipLoadout.MigrateTier): advance each
        // barrel up its successor chain while an owned tech obsoletes it. A researched gat-2 upgrades a
        // Gat Gun 1 mount — authored default OR saved override — to Gat Gun 2 at spawn. `migrated` flags
        // a pure authored spawn that changed, so it still gets a MsgShipLoadout row (others see tier-2).
        bool migrated = false;
        for (int i = 0; i < effective.Length; i++)
        {
            if (effective[i] == HardpointDef.NoWeapon)
                continue;
            uint up = MigrateWeaponTier(ts, effective[i]);
            if (up != effective[i])
            {
                effective[i] = up;
                migrated = true;
            }
        }

        // Cargo: an empty request normally means "hull default" (legacy quick-launch, no hangar
        // visit) — but WITH mount overrides it means a deliberately EMPTY hold: overrides only
        // come from the hangar, which always ships its real (possibly zero) hold counts, and
        // silently re-adding the default cargo could push a legal gun swap over PayloadCapacity.
        var cargo =
            wantCargo ? requested
            : wantMounts ? System.Array.Empty<(uint, byte)>()
            : fallbackCargo;

        // Nothing customized AND no tier migrated AND default cargo ⇒ the pure authored spawn (already
        // boot-validated to fit capacity): keep the null fast path so it flies ClassMuzzles with no row.
        if (!wantMounts && !wantCargo && !migrated)
            return (null, fallbackCargo);

        float used = 0f;
        for (int i = 0; i < effective.Length; i++)
            if (WeaponDefs.TryGetValue(effective[i], out var wm))
                used += wm.Mass;
        foreach (var (cargoId, count) in cargo)
        {
            bool isFuel = _fuelPerCharge.ContainsKey(cargoId);
            if (wantCargo && !isFuel && !_dispenserByCargo.ContainsKey(cargoId))
            {
                Log.SpawnCargoNotDispenser(_log, cargoId);
                return (null, fallbackCargo);
            }
            // Fuel pods on a hull with no fuel model would be dead cargo — reject like any other
            // invalid request (the hangar hides the row, so only a hacked/buggy client sends this).
            if (isFuel && count > 0 && (def is null || def.MaxFuel <= 0f))
            {
                Log.SpawnFuelCargoOnFuellessHull(_log, cargoId, cls);
                return (null, fallbackCargo);
            }
            used += count * (_cargoMass.TryGetValue(cargoId, out var m) ? m : 0f);
        }
        float cap = def?.PayloadCapacity ?? 0f;
        if (used > cap)
        {
            Log.SpawnLoadoutPayloadExceeds(_log, used, cap);
            return (null, fallbackCargo);
        }
        return (effective, cargo);
    }

    // Walk the weapon-tier successor chain for a team: while the current weapon is obsoleted by a tech
    // the team owns and names a successor NO HEAVIER than itself, advance to that successor. A
    // researched tier auto-replaces the guns it obsoletes at spawn. The mass guard keeps a boot-valid
    // loadout within PayloadCapacity (a heavier successor is left for the player to mount explicitly).
    // Bounded by the chain length (guard caps a malformed cycle). This is the authoritative twin of
    // the client's ShipLoadout.MigrateTier display helper.
    private uint MigrateWeaponTier(World.TeamState? ts, uint weaponId)
    {
        if (ts is null)
            return weaponId;
        for (int guard = 0; guard < 8; guard++)
        {
            if (
                !WeaponDefs.TryGetValue(weaponId, out var w)
                || w.SucceededByWeaponId == uint.MaxValue
                || w.ObsoletedByTechIdx.Length == 0
                || !WeaponDefs.TryGetValue(w.SucceededByWeaponId, out var next)
                || next.Mass > w.Mass
            )
                return weaponId;
            bool owns = false;
            foreach (ushort t in w.ObsoletedByTechIdx)
                if (t < Content.Techs.Count && ts.OwnedTechs.Contains(Content.Techs[t].Id))
                {
                    owns = true;
                    break;
                }
            if (!owns)
                return weaponId;
            weaponId = w.SucceededByWeaponId;
        }
        return weaponId;
    }

    // Position a ship just outside its team base, launched out of one of the base's DOCKING-EXIT
    // hardpoints (World.BaseExits, from the GLB; random pick when a base authors several).
    // Without a loaded model it falls back to the pre-hull behavior: outward toward the sector
    // center. `clearance` is added past the base radius so the spawn sits clear of the solid
    // shell (won't instantly re-dock). `at` pins the launch to a specific base (miners relaunch
    // from the base they offloaded at); null keeps the first-team-base pick.
    private void PlaceAtBase(ShipSim s, float clearance, uint tick, World.BaseSite? at = null)
    {
        Vec3 basePos = default;
        uint sector = World.DefaultSector;
        byte baseType = 0;
        if (at is World.BaseSite site)
        {
            basePos = site.Pos;
            sector = site.SectorId;
            baseType = site.BaseTypeId;
        }
        else
            foreach (var b in World.Bases)
                if (b.Team == s.Team)
                {
                    basePos = b.Pos;
                    sector = b.SectorId;
                    baseType = b.BaseTypeId;
                    break;
                }

        Vec3 outward;
        Quat rot;
        Vec3 spawnPos;
        var exits = World.BaseExitsOf(baseType);
        if (World.BaseHullOf(baseType) is not null && exits.Length > 0)
        {
            // Catapult out of a docking bay picked at random among the base's DockingExit
            // hardpoints: the ship first appears at the hardpoint pushed warp-exit-offset (plus
            // `clearance`) along the launch axis — the opposite of the node's inward-pointing
            // forward. (Any residual overlap with the bay is a benign outward pop — ApplyBounce
            // never damages a ship already moving outward.)
            var exit = exits[_rng.Next(exits.Length)];
            outward = exit.Dir;
            rot = LookRotationZ(outward);
            spawnPos = basePos + exit.Pos + outward * (clearance + _mech.WarpExitOffset);
        }
        else
        {
            float dirLen = basePos.Length();
            outward = dirLen > 1e-3f ? basePos * (-1f / dirLen) : new Vec3(0f, 0f, 1f);
            float yaw = MathF.Atan2(-basePos.X, -basePos.Z);
            rot = new Quat(0f, MathF.Sin(yaw * 0.5f), 0f, MathF.Cos(yaw * 0.5f));
            spawnPos = basePos + outward * (World.BaseRadiusOf(baseType) + clearance);
        }

        s.SectorId = sector;
        s.State = new ShipState
        {
            Pos = spawnPos,
            Vel = outward * LaunchSpeed, // catapult out of the bay instead of drifting
            Rot = rot,
            AngVel = default,
            Mass = s.State.Mass,
            AbPower = 0f,
        };
        s.LastFireTick = 0;
        s.MountLastFire = null; // per-mount cadence gates restart with the fresh LastFireTick
        s.LastInputTick = tick;
        s.Alive = true;
    }

    // Spawn fresh combat ships for clients whose scheduled respawn tick has arrived (and who
    // are still connected with no live ship). The first spawn at join goes through here too.
    private void ProcessRespawns(uint tick)
    {
        if (_clientRespawn.Count == 0)
            return;
        List<int>? due = null;
        foreach (var kv in _clientRespawn)
            if (tick >= kv.Value)
                (due ??= new()).Add(kv.Key);
        if (due is null)
            return;
        foreach (int cid in due)
        {
            _clientRespawn.Remove(cid);
            if (!_clientInfo.TryGetValue(cid, out var info))
                continue; // disconnected
            if (_byClient.ContainsKey(cid))
                continue; // already flying
            // A miner hull is a team drone, never a player ship: a MsgSpawn asking for it is
            // dropped like a locked class (buy one from the Build tab instead). Guards the sim even
            // when a client fails to hide ore hulls from its hangar.
            if (IsMinerClass(info.cls))
                continue;
            // Stage-2 economy gate: a locked or unaffordable buy is dropped (no ship, no charge,
            // no reschedule). The client's spawn-pending retry times out and its pre-check stops it
            // re-spamming a request it can predict will fail.
            if (TryReserveSpawn(info.team, info.cls) != SpawnDecision.Allowed)
                continue;
            SpawnCombatShip(cid, info.team, info.cls, tick, info.cargo, info.launchBaseId, info.mounts);
        }
    }

    // Resolve a client-picked launch base id to a validated friendly, alive base site — anything
    // else (0, unknown id, enemy base, dead base) silently falls back to the default first-team-base
    // pick inside PlaceAtBase (null).
    private World.BaseSite? ResolveLaunchBase(byte team, ulong launchBaseId)
    {
        if (launchBaseId == 0)
            return null;
        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (b.Id == launchBaseId)
                return (b.Team == team && World.BaseHealth[i] > 0f) ? b : null;
        }
        return null;
    }

    // A class id a PLAYER may request via MsgSpawn: a known hull def that isn't the pod or a miner
    // drone. Replaces the hub's old hardcoded `cls > 2 -> scout` clamp (v36), so researched hulls
    // with any class id can spawn once unlocked (TryReserveSpawn still gates lock/cost).
    public bool IsPlayerSpawnableClass(byte cls) =>
        cls != GameContent.PodClassId && !IsMinerClass(cls) && !IsConstructorClass(cls) && ShipDefs.ContainsKey(cls);

    // A constructor drone chassis (HullAbility.IsBuilder projected to ShipClassDef.IsConstructor):
    // AI-owned, bought from the Build tab, never a personal spawn.
    public bool IsConstructorClass(byte cls) => ShipDefs.TryGetValue(cls, out var d) && d.IsConstructor;

    public enum SpawnDecision
    {
        Allowed,
        Locked,
        TooPoor,
    }

    // Authoritative spawn gate + charge (the buy seam): reject if the requested hull is locked for
    // this team or it can't afford the cost, otherwise deduct the cost and allow. Deduct and check
    // happen at the same authoritative moment (spawn time), so credits checked == credits charged.
    // Authority for a PLAYER's own hull stays any-player-spends; team-drone buys (MsgBuyMiner) are
    // commander-gated upstream at the hub (ClientHub.CommanderOrWarn) before they reach this seam.
    public SpawnDecision TryReserveSpawn(byte team, byte cls)
    {
        if (!World.TeamStates.TryGetValue(team, out var ts))
            return SpawnDecision.Locked;
        if (!ts.UnlockedClasses.Contains(cls))
            return SpawnDecision.Locked;
        int cost = ShipDefs.TryGetValue(cls, out var d) ? d.Cost : 0;
        if (ts.Credits < cost)
            return SpawnDecision.TooPoor;
        ts.Credits -= cost;
        TeamStateChangedThisStep = true;
        return SpawnDecision.Allowed;
    }

    // The input that drives a ship this tick: PIGs are server-brained, players (incl. their
    // pods) replay their exact-tick / held stick state (auth == client prediction).
    private ShipInputState InputFor(ShipSim s, uint tick)
    {
        if (s.IsPig && s.IsPod)
            return PodThink(s, tick);
        if (s.IsPig)
            return PigExecute(s, tick);
        if (s.IsMiner)
            return MinerExecute(s, tick); // server-driven drone: no input ring, no autopilot flags
        if (s.Kind == ShipKind.Constructor)
            return ConstructorExecute(s, tick); // server-driven build drone (Simulation.Constructors.cs)

        int slot = (int)(tick % InputRingSize);
        if (s.InputRingTick[slot] == tick)
            s.HeldInput = s.InputRing[slot];

        // Player autopilot: byte-identical to the pre-autopilot path when disengaged. Cruise-control
        // style — a significant manual flight input on the CURRENT held stick disengages instantly and
        // hands control back. Otherwise the ship flies itself, but the pilot may still fire/lock/dispense
        // (those flags copy through from the held input; firing never disengages).
        if (!s.ApEngaged)
            return s.HeldInput;
        if (ManualOverride(s.HeldInput))
        {
            s.ApEngaged = false;
            return s.HeldInput;
        }
        var ap = AutopilotStep(s, tick);
        ap.Firing = s.HeldInput.Firing;
        ap.Firing2 = s.HeldInput.Firing2;
        ap.LockTargetId = s.HeldInput.LockTargetId;
        ap.DropChaff = s.HeldInput.DropChaff;
        ap.DropMine = s.HeldInput.DropMine;
        ap.DropProbe = s.HeldInput.DropProbe;
        return ap;
    }

    // A "significant" manual flight input that disengages autopilot (cruise control). Autopilot owns
    // throttle, so Thrust counts as an override axis too; Boost held is always an override.
    private static bool ManualOverride(in ShipInputState i) =>
        MathF.Abs(i.Yaw) > 0.25f
        || MathF.Abs(i.Pitch) > 0.25f
        || MathF.Abs(i.Roll) > 0.25f
        || MathF.Abs(i.StrafeX) > 0.25f
        || MathF.Abs(i.StrafeY) > 0.25f
        || MathF.Abs(i.Thrust) > 0.25f
        || i.Boost;

    // One tick of synthesized autopilot steering for a player ship. Resolves the destination by
    // ApKind and reuses the shared AutoSteer geometry (same avoidance delegate the PIGs use).
    // Returns steering/thrust only — the caller copies the player's fire/lock/dispense flags through.
    // Disengages (clears ApEngaged and coasts) when the target is gone / unreachable / arrived.
    // Extra distance (world units) kept between the model's computed stopping distance and the point
    // the autopilot commits to braking — a discretization cushion so the ship settles at/just short of
    // the arrival shell rather than overshooting it. The arrival bands are standoff-generous, so a small
    // early stop still counts as "arrived"; only the friendly-base dock (which needs the door window)
    // opts out of this by aiming its stop shell just inside the door.
    private const float ApBrakeMargin = 16f;

    // Arrival-band generosity multiplier applied to a point-destination's standoff distance when
    // testing Arrived (see ArriveAt) — the ship counts as "arrived" a bit outside its brake target.
    private const float ApArrivalBandMult = 1.2f;

    // ---- Friendly-base docking maneuver tuning (server-only; see DockApproach) ----
    // Compile-time geometry gates (the maneuver's feel is stable; only the three world.yaml knobs
    // below are worth per-server tuning).
    private const float ApDockHullMargin = 10f; // padding added to BaseRadius for the LOS/detour sphere
    private const float ApDockLosSlack = 35f; // endSlack in the LOS test — excuses the terminal door pocket
    private const float ApDockDetourStepRad = 0.6f; // per-tick azimuth advance of the orbit carrot (rad)

    // MUST exceed ApBrakeMargin (16): the Transit clear-line ApproachPoint brakes to rest ~ApBrakeMargin
    // short of the standoff point, so a capture radius below that margin is a dead zone the ship can
    // never enter — Transit would park just outside it and never promote to Align/Creep (the maneuver
    // would only ever dock by sliding into the door during Transit, never via the intended align+creep).
    private const float ApDockCapture = 20f; // within this of the standoff point promotes Transit -> Align

    // Outer axis-acquire point for Transit: far enough out that (unlike the standoff point, which may
    // sit inside a recessed door pocket WITHIN the padded sphere) it clears the padded base sphere with
    // margin, so the straight-in leg's LOS test doesn't flap on small lateral drift.
    private const float ApDockOuterStandoff = 60f;
    private const float ApDockAxisSlop = 12f; // lateral slack past the door half-extents that counts as "on the corridor axis"
    private const float ApDockDescentMargin = 8f; // arrest cushion on the on-axis descent (speeds are low; ApBrakeMargin would park short)
    private const float ApDockDescentMaxThrottle = 0.3f; // descent speed cap (fraction of MaxSpeed) — dock-pattern pace, not cruise
    private const float ApDockCaptureSpeedSq = 9f; // ...and speed^2 below this (~3 u/s) so the ship has settled
    private const float ApDockRollGain = 3f; // FaceAndRoll roll proportional gain
    private const float ApDockFacingDot = 0.995f; // Align -> Creep: nose-onto-door facing threshold
    private const float ApDockRollTol = 0.10f; // Align -> Creep: |localUp.X| roll-alignment tolerance
    private const float ApDockCreepFacingDot = 0.9f; // Creep demotes below this facing dot
    private const uint ApDockAlignTimeout = 300; // ticks in Align before demoting to Transit
    private const uint ApDockCreepTimeout = 200; // ticks in Creep before demoting to Transit

    // world.yaml-overridable (ai.dock-*) — resolved in InitPigTuning from WorldAiTuning.
    private float ApDockStandoff = 25f; // standoff point distance outside the door plane
    private float ApDockClearance = 40f; // detour ring radius = BaseRadius + this
    private float ApDockCreepThrottle = 0.12f; // creep throttle fraction (~19 u/s for a Scout)

    private ShipInputState AutopilotStep(ShipSim s, uint tick)
    {
        Vec3 myPos = s.State.Pos;
        Quat myRot = s.State.Rot;
        Vec3 myVel = s.State.Vel;
        var stats = StatsFor(s.Class, s.IsPod);
        // Rocks + base hulls (bases are solid to every ship — see AvoidObstacles). avoidBaseId is set
        // by the base leg (ApKind 1) below so avoidance never steers the nose off the very base being
        // flown at — the dock maneuver / arrival stop shell owns that base's clearance.
        ulong avoidBaseId = 0;
        Vec3 Avoid(Vec3 p, Vec3 d) => AvoidObstacles(s.SectorId, p, d, excludeBaseId: avoidBaseId);

        // Physics-based approach steer to `point`, coming to rest `stopDistance` from it (see
        // AutoSteer.ApproachPoint / StoppingDistance). Replaces the PIG combat schedule (AttackPoint),
        // whose coast/reverse thresholds overshot a max-speed approach and rammed the target.
        ShipInputState Approach(Vec3 point, float stopDistance, float margin) =>
            AutoSteer.ApproachPoint(
                myPos,
                myRot,
                myVel,
                point,
                stopDistance,
                stats.MaxSpeed,
                stats.Accel,
                stats.BackMult,
                PigTurnGain,
                margin,
                Avoid
            );

        // Fly to a point that lives in `destSector`: if it's another sector, steer to the next-hop aleph
        // on a shortest route there (World.NextGateTo — multi-hop capable, so a destination several
        // sectors away is reached one gate at a time; full-thrust steer to the gate mouth, TryWarp handles
        // transit). No route → give up. Returns true and fills `input` when it handled a cross-sector leg;
        // false = point is in-sector.
        bool CrossSector(uint destSector, out ShipInputState input)
        {
            input = default;
            if (destSector == s.SectorId)
                return false;
            if (World.NextGateTo(s.SectorId, destSector) is World.Gate gate)
            {
                input = AutoSteer.SteerToPoint(myPos, myRot, gate.Pos, PigTurnGain, 1f, Avoid);
                return true;
            }
            s.ApEngaged = false; // unreachable (no route to the destination sector) — disengage
            return true;
        }

        // Approach `point` (see Approach) and disengage once Arrived within `band` of it — shared
        // by the point-destination autopilot kinds (enemy-base standoff, rock standoff, waypoint).
        ShipInputState ArriveAt(Vec3 point, float approachStop, float band)
        {
            var input = Approach(point, approachStop, ApBrakeMargin);
            if (Arrived(myPos, s.State.Vel, point, band))
                s.ApEngaged = false;
            return input;
        }

        switch (s.ApKind)
        {
            case 0: // ship — follow at standoff indefinitely; never auto-fire
            {
                if (
                    !_ships.TryGetValue(s.ApTargetId, out var tgt)
                    || !tgt.Alive
                    || tgt.ShipId == s.ShipId
                    || (FogEnabled && !TeamRadarSees(s.Team, tgt.ShipId))
                )
                {
                    s.ApEngaged = false;
                    return default;
                }
                if (tgt.SectorId != s.SectorId)
                {
                    if (CrossSector(tgt.SectorId, out var xin))
                        return xin;
                }
                // Brake to the standoff shell and station-keep there (never disengages, never auto-fires);
                // a static/slow target is braked short of, not rammed.
                return Approach(tgt.State.Pos, PigStandoff, ApBrakeMargin);
            }
            case 1: // base — friendly: fly to the docking door (auto-docks); enemy: standoff then arrive
            {
                if (World.BaseById(s.ApTargetId) is not World.BaseSite eb)
                {
                    s.ApEngaged = false;
                    return default;
                }
                avoidBaseId = eb.Id; // the destination base — its clearance belongs to the dock/arrival logic
                if (eb.SectorId != s.SectorId)
                {
                    if (CrossSector(eb.SectorId, out var xin))
                        return xin;
                }
                if (eb.Team == s.Team)
                {
                    // Friendly base: proper 3-phase docking maneuver (decelerate to a standoff point
                    // outside the door, align + roll to the door, creep down the corridor until the
                    // collision-pass dock trigger fires). Needs the parsed door geometry AND a base hull;
                    // without either (modelless base / no doors) keep the legacy full-thrust SteerToPoint
                    // at the aggregate door centre (or the base centre for a modelless base) — it bounces/
                    // slides into the door and still docks, just crudely. Preserved EXACTLY as before.
                    // Geometry MUST be per-base-type: a newly-built outpost is a different BaseTypeId than
                    // the home garrison, and the dock trigger (see IntersectsDockFace ~L775) tests the
                    // OUTPOST's own door face. Reading the global (garrison-only) door/hull here aimed the
                    // ship at a phantom door pinned at the outpost's position, so the capture never fired.
                    if (World.BaseDockFacesOf(eb.BaseTypeId).Length == 0 || World.BaseHullOf(eb.BaseTypeId) is null)
                    {
                        Vec3 aim = World.BaseHullOf(eb.BaseTypeId) is not null
                            ? eb.Pos + World.BaseDoorCenterOf(eb.BaseTypeId)
                            : eb.Pos;
                        return AutoSteer.SteerToPoint(myPos, myRot, aim, PigTurnGain, 1f, Avoid);
                    }
                    return DockApproach(s, tick, eb, stats, Avoid);
                }
                float hostileR = World.BaseRadiusOf(eb.BaseTypeId); // per-type — enemy base need not be a garrison
                return ArriveAt(eb.Pos, hostileR + PigStandoff, hostileR + PigStandoff * ApArrivalBandMult);
            }
            case 2: // rock — approach to standoff, then arrive + disengage
            {
                if (World.RockById(s.ApTargetId) is not World.Rock rock || rock.SectorId != s.SectorId)
                {
                    s.ApEngaged = false;
                    return default;
                }
                // Standoff off the rock's CURRENT (possibly mined-down) surface, so a player autopilot
                // to a shrunk rock stops at the live shell rather than the stale spawn radius.
                float rockR = World.RockCurrentRadius(rock.Id);
                return ArriveAt(rock.Pos, rockR + PigStandoff, rockR + PigStandoff * ApArrivalBandMult);
            }
            default: // 3 waypoint — fly to a free point, then arrive + disengage
            {
                if (CrossSector(s.ApWaypointSector, out var xin))
                    return xin;
                return ArriveAt(s.ApWaypointPos, 0f, PigStandoff * ApArrivalBandMult);
            }
        }
    }

    // One tick of the friendly-base docking maneuver — a server-only Transit -> Align -> Creep state
    // machine over the sticky ShipSim.ApDock* fields. INVARIANT: bases are identity-oriented (World.cs
    // bakes the base hull untransformed; the collision pass tests `s.Pos - b.Pos`), so a door's world
    // geometry is simply `eb.Pos + face.Center` with `Normal/U/V` used as-is. If bases ever gain a
    // rotation this method must transform the face by it.
    //
    // Guards re-validate geometry EVERY tick and demote back to Transit on drift / bad alignment /
    // timeout, so a hull bounce or overshoot self-heals rather than wedging a phase. Preconditions
    // (checked by the caller): same sector as `eb`, BaseDockFaces non-empty, BaseHull present.
    private ShipInputState DockApproach(
        ShipSim s,
        uint tick,
        World.BaseSite eb,
        ShipStats stats,
        Func<Vec3, Vec3, Vec3> avoid
    )
    {
        Vec3 myPos = s.State.Pos;
        Quat myRot = s.State.Rot;
        Vec3 myVel = s.State.Vel;
        Vec3 angVel = s.State.AngVel; // ship-local turn rates (X=pitch,Y=yaw,Z=roll) — the frame FaceAndRollAnticipated expects
        // Per-base-type geometry: `eb` may be an outpost/refinery, NOT the home garrison (typeId 0), and
        // every consumer below (door faces, hull-clearance radius) must match the type the dock trigger
        // tests. The global World.BaseDockFaces/BaseRadius are garrison-only aliases — using them here
        // parked non-garrison bases at a phantom door.
        byte baseType = eb.BaseTypeId;
        DockFace[] faces = World.BaseDockFacesOf(baseType);
        float baseRadius = World.BaseRadiusOf(baseType);

        // Conservative per-axis angular-accel budgets for the anticipation profile: the at-rest
        // TorqueMultiplier (0.5) times turnTorque/mass — i.e. the slew cap FlightModel.Integrate applies
        // while docking (speed ~ 0). Targeting this floor means the integrator can always out-decelerate
        // the sqrt profile, so the actual angular velocity is arrested AT the null, never past it.
        float angAccelPitch = 0.5f * stats.TorquePitchRad / stats.Mass;
        float angAccelYaw = 0.5f * stats.TorqueYawRad / stats.Mass;
        float angAccelRoll = 0.5f * stats.TorqueRollRad / stats.Mass;

        DockFace f = faces[SelectDockDoor(s, eb, faces)];
        Vec3 doorW = eb.Pos + f.Center; // door face plane, world space (identity-oriented base)
        Vec3 pstand = doorW - f.Normal * ApDockStandoff; // arrival point `standoff` outside the door mouth

        switch (s.ApDockPhase)
        {
            case 1: // ALIGN — hold at the standoff point (throttle 0 = active brake) and turn+roll onto the door
                return DockAlign(s, tick, myPos, myRot, angVel, f, doorW, pstand, stats, angAccelPitch, angAccelYaw, angAccelRoll);
            case 2: // CREEP — slow throttle down the corridor; the collision-pass dock trigger ends the run
                return DockCreep(s, tick, myPos, myRot, angVel, f, doorW, stats, angAccelPitch, angAccelYaw, angAccelRoll);
            default: // 0 TRANSIT — acquire the corridor axis outside the door, then descend it governed
                return DockTransit(
                    s,
                    tick,
                    myPos,
                    myRot,
                    myVel,
                    angVel,
                    f,
                    doorW,
                    pstand,
                    eb,
                    baseRadius,
                    stats,
                    avoid,
                    angAccelPitch,
                    angAccelYaw,
                    angAccelRoll
                );
        }
    }

    // ALIGN phase (ApDockPhase 1): hold at the standoff point (throttle 0 = active brake) and
    // turn+roll onto the door. Promotes to CREEP once nose-on-door facing + roll settle within
    // tolerance; demotes back to TRANSIT on drift off the standoff point or an align timeout.
    private ShipInputState DockAlign(
        ShipSim s,
        uint tick,
        Vec3 myPos,
        Quat myRot,
        Vec3 angVel,
        DockFace f,
        Vec3 doorW,
        Vec3 pstand,
        ShipStats stats,
        float angAccelPitch,
        float angAccelYaw,
        float angAccelRoll
    )
    {
        Vec3 up = DockUpAxis(f, myRot);
        var input = AutoSteer.FaceAndRollAnticipated(
            myPos,
            myRot,
            angVel,
            doorW,
            up,
            stats.MaxRatePitchRad,
            stats.MaxRateYawRad,
            stats.MaxRateRollRad,
            angAccelPitch,
            angAccelYaw,
            angAccelRoll,
            ApDockRollGain,
            0f
        );

        // Facing = nose (local +Z) alignment with the direction to the door; roll error read from
        // the door up-axis in ship-local space (localUp.Y > 0 = right way up, |localUp.X| = roll off).
        Vec3 fwd = myRot.Rotate(new Vec3(0f, 0f, 1f)); // unit
        Vec3 toDoor = doorW - myPos;
        float td = toDoor.Length();
        float facingDot = td > 1e-4f ? (toDoor.X * fwd.X + toDoor.Y * fwd.Y + toDoor.Z * fwd.Z) / td : 1f;
        Vec3 localUp = myRot.Conjugate().Rotate(up);

        if (facingDot >= ApDockFacingDot && localUp.Y > 0f && MathF.Abs(localUp.X) < ApDockRollTol)
        {
            s.ApDockPhase = 2; // aligned — creep in
            s.ApDockPhaseTick = tick;
        }
        else if (
            (pstand - myPos).LengthSquared() > (2f * ApDockCapture) * (2f * ApDockCapture)
            || tick - s.ApDockPhaseTick > ApDockAlignTimeout
        )
        {
            s.ApDockPhase = 0; // drifted off the standoff point or stuck oscillating — re-approach
            s.ApDockPhaseTick = tick;
        }
        return input;
    }

    // CREEP phase (ApDockPhase 2): slow throttle down the corridor; the collision-pass dock
    // trigger ends the run. Demotes back to TRANSIT on falling out of the corridor, facing away,
    // drifting, or a creep timeout.
    private ShipInputState DockCreep(
        ShipSim s,
        uint tick,
        Vec3 myPos,
        Quat myRot,
        Vec3 angVel,
        DockFace f,
        Vec3 doorW,
        ShipStats stats,
        float angAccelPitch,
        float angAccelYaw,
        float angAccelRoll
    )
    {
        Vec3 up = DockUpAxis(f, myRot);
        // Aim PAST the plane (doorW + Normal*DockFaceDepth) so the aim direction never degenerates
        // as the nose reaches the door — the ship keeps a valid heading right up to the trigger.
        Vec3 creepAim = doorW + f.Normal * World.DockFaceDepth;
        var input = AutoSteer.FaceAndRollAnticipated(
            myPos,
            myRot,
            angVel,
            creepAim,
            up,
            stats.MaxRatePitchRad,
            stats.MaxRateYawRad,
            stats.MaxRateRollRad,
            angAccelPitch,
            angAccelYaw,
            angAccelRoll,
            ApDockRollGain,
            ApDockCreepThrottle
        );

        Vec3 fwd = myRot.Rotate(new Vec3(0f, 0f, 1f));
        Vec3 toAim = creepAim - myPos;
        float ta = toAim.Length();
        float facingDot = ta > 1e-4f ? (toAim.X * fwd.X + toAim.Y * fwd.Y + toAim.Z * fwd.Z) / ta : 1f;

        // Lateral offset from the door's corridor axis (project ship->door onto the U/V plane).
        Vec3 rel = myPos - doorW;
        float alongN = rel.X * f.Normal.X + rel.Y * f.Normal.Y + rel.Z * f.Normal.Z;
        Vec3 lateral = rel - f.Normal * alongN;
        float latU = MathF.Abs(lateral.X * f.U.X + lateral.Y * f.U.Y + lateral.Z * f.U.Z);
        float latV = MathF.Abs(lateral.X * f.V.X + lateral.Y * f.V.Y + lateral.Z * f.V.Z);
        bool outsideCorridor = latU > f.Eu + World.ShipRadius || latV > f.Ev + World.ShipRadius;

        if (
            outsideCorridor
            || facingDot < ApDockCreepFacingDot
            || (doorW - myPos).Length() > 2f * ApDockStandoff
            || tick - s.ApDockPhaseTick > ApDockCreepTimeout
        )
        {
            s.ApDockPhase = 0; // fell out of the corridor / faced away / drifted / timed out — re-approach
            s.ApDockPhaseTick = tick;
        }
        return input;
    }

    // TRANSIT phase (ApDockPhase 0, the default): acquire the corridor axis outside the door, then
    // descend it governed. Two-stage: OFF the axis, fly/detour to an outer axis point; ON the axis,
    // governed descent to the standoff point. Promotes to ALIGN once captured (close + settled).
    private ShipInputState DockTransit(
        ShipSim s,
        uint tick,
        Vec3 myPos,
        Quat myRot,
        Vec3 myVel,
        Vec3 angVel,
        DockFace f,
        Vec3 doorW,
        Vec3 pstand,
        World.BaseSite eb,
        float baseRadius,
        ShipStats stats,
        Func<Vec3, Vec3, Vec3> avoid,
        float angAccelPitch,
        float angAccelYaw,
        float angAccelRoll
    )
    {
        // Two-stage transit. The standoff point can sit INSIDE the padded base sphere (a
        // recessed door pocket — true for the stock base), so a direct bang-bang run at it
        // flaps the blocked test on any lateral drift and hands the terminal approach over
        // tangentially, sliding the ship sideways through the dock trigger before it ever
        // aligns. Instead:
        //  - OFF the corridor axis: fly to an outer axis point (`ApDockOuterStandoff` out —
        //    beyond the padded sphere, so the LOS geometry has real margin), detouring around
        //    the clearance ring while the straight line is hull-blocked.
        //  - ON the axis: governed descent to the standoff point — speed-command the highest
        //    arrestable speed for the room that remains (capped), FaceAndRoll pre-aligning
        //    nose and roll on the door. Overshoot is impossible by construction: at every
        //    tick the commanded speed can be arrested inside the remaining distance.
        Vec3 relT = myPos - doorW;
        float alongT = relT.X * f.Normal.X + relT.Y * f.Normal.Y + relT.Z * f.Normal.Z;
        Vec3 latT = relT - f.Normal * alongT;
        float gap = -alongT; // distance OUTSIDE the door plane (negative = past/inside it)
        bool onAxis = gap > ApDockStandoff * 0.8f && latT.Length() < MathF.Max(f.Eu, f.Ev) + ApDockAxisSlop;

        ShipInputState input;
        if (onAxis)
        {
            // Aim at the door plane (not the standoff point) so the heading stays defined
            // through the stop; the arrest-governed throttle is what actually parks the ship
            // at the standoff point. No avoidance delegate — the corridor is base clearance.
            float distP = (pstand - myPos).Length();
            float vAllow = AutoSteer.MaxArrestableSpeed(
                MathF.Max(0f, distP - ApDockDescentMargin),
                stats.MaxSpeed,
                stats.Accel,
                stats.BackMult
            );
            float throttle = MathF.Min(ApDockDescentMaxThrottle, vAllow / stats.MaxSpeed);
            input = AutoSteer.FaceAndRollAnticipated(
                myPos,
                myRot,
                angVel,
                doorW,
                DockUpAxis(f, myRot),
                stats.MaxRatePitchRad,
                stats.MaxRateYawRad,
                stats.MaxRateRollRad,
                angAccelPitch,
                angAccelYaw,
                angAccelRoll,
                ApDockRollGain,
                throttle
            );
        }
        else
        {
            Vec3 goal = doorW - f.Normal * ApDockOuterStandoff;
            float sphereR = baseRadius + ApDockHullMargin;
            if (AutoSteer.SegmentEntersSphere(myPos, goal, eb.Pos, sphereR, ApDockLosSlack))
            {
                // Goal is behind the base hull: steer a live-recomputed carrot around the
                // clearance ring instead of driving straight through the structure.
                float ring = baseRadius + ApDockClearance;
                Vec3 carrot = AutoSteer.OrbitWaypoint(
                    myPos,
                    goal,
                    eb.Pos,
                    ring,
                    ApDockDetourStepRad,
                    new Vec3(0f, 1f, 0f),
                    new Vec3(1f, 0f, 0f)
                );
                carrot = ClampInsideSector(carrot, s.SectorId); // bases hug the sector edge — keep the arc off the eroding boundary
                // Speed governor on the arc: command the highest speed the brake model can
                // still arrest within the straight-line (<= actual arc) distance to the goal,
                // so however late on the arc LOS clears, the handoff never carries more speed
                // than the room that remains can stop. (A threshold cut — "brake once inside
                // the envelope" — fires when the envelope is already reached, i.e. by
                // construction too late, and overshoots the goal.)
                float distToGoal = (goal - myPos).Length();
                float vAllow = AutoSteer.MaxArrestableSpeed(
                    MathF.Max(0f, distToGoal - ApBrakeMargin),
                    stats.MaxSpeed,
                    stats.Accel,
                    stats.BackMult
                );
                float throttle = MathF.Min(1f, vAllow / stats.MaxSpeed);
                input = AutoSteer.SteerToPoint(myPos, myRot, carrot, PigTurnGain, throttle, avoid);
            }
            else
            {
                // Clear line to the axis point: physics-braked approach (avoidance included).
                input = AutoSteer.ApproachPoint(
                    myPos,
                    myRot,
                    myVel,
                    goal,
                    0f,
                    stats.MaxSpeed,
                    stats.Accel,
                    stats.BackMult,
                    PigTurnGain,
                    ApBrakeMargin,
                    avoid
                );
            }
        }

        if (
            (pstand - myPos).LengthSquared() <= ApDockCapture * ApDockCapture
            && myVel.LengthSquared() < ApDockCaptureSpeedSq
        )
        {
            s.ApDockPhase = 1; // arrived + settled at the standoff point — align to the door
            s.ApDockPhaseTick = tick;
        }
        return input;
    }

    // Pick (once per engagement, then sticky) which docking door to use: argmin over |P - pstand_i|
    // plus a half-ring detour penalty when the straight line to that standoff point is blocked by the
    // base sphere, so a reachable door beats a nearer one hidden behind the hull. Stock content is N=1
    // (this just returns door 0), but the selection stays correct for multi-door bases.
    private int SelectDockDoor(ShipSim s, World.BaseSite eb, DockFace[] faces)
    {
        if (s.ApDockDoor >= 0 && s.ApDockDoor < faces.Length)
            return s.ApDockDoor; // already chosen — keep it for the whole engagement

        Vec3 myPos = s.State.Pos;
        float baseRadius = World.BaseRadiusOf(eb.BaseTypeId); // per-type — eb may be a non-garrison base
        float sphereR = baseRadius + ApDockHullMargin;
        float detourPenalty = MathF.PI * (baseRadius + ApDockClearance); // ~half-ring arc cost
        int best = 0;
        float bestCost = float.MaxValue;
        for (int i = 0; i < faces.Length; i++)
        {
            Vec3 doorW = eb.Pos + faces[i].Center;
            Vec3 pstand = doorW - faces[i].Normal * ApDockStandoff;
            float cost = (pstand - myPos).Length();
            if (AutoSteer.SegmentEntersSphere(myPos, pstand, eb.Pos, sphereR, ApDockLosSlack))
                cost += detourPenalty;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = i;
            }
        }
        s.ApDockDoor = best;
        return best;
    }

    // The door's "up" axis for roll alignment: whichever in-plane axis (U or V) is more aligned with
    // world +Y (tie -> V), sign-flipped toward the ship's current up so FaceAndRoll rolls the short way.
    private static Vec3 DockUpAxis(DockFace f, Quat shipRot)
    {
        // dot(axis, worldY) is just the axis's Y component (U/V are unit).
        Vec3 axis = MathF.Abs(f.U.Y) > MathF.Abs(f.V.Y) ? f.U : f.V; // strict > ⇒ tie falls to V
        Vec3 shipUp = shipRot.Rotate(new Vec3(0f, 1f, 0f));
        float d = axis.X * shipUp.X + axis.Y * shipUp.Y + axis.Z * shipUp.Z;
        return d >= 0f ? axis : axis * -1f;
    }

    // Clamp a point to stay inside the sector's sphere (origin-centered — the boundary-erosion pass
    // damages by `Pos.Length() - SectorRadius`), leaving a 30 u margin off the eroding boundary. Used to
    // keep the detour carrot from routing an edge-hugging base's arc out into the boundary hazard.
    private Vec3 ClampInsideSector(Vec3 p, uint sector)
    {
        float limit = World.SectorRadius(sector) - 30f;
        if (limit <= 0f)
            return p;
        float len = p.Length();
        return len > limit ? p * (limit / len) : p;
    }

    // Arrival test shared by the point-destination autopilot kinds: within `band` of the point AND
    // nearly stopped (speed < 2 u/s) so the ship has actually settled, not just passed through.
    private static bool Arrived(Vec3 pos, Vec3 vel, Vec3 point, float band) =>
        (point - pos).LengthSquared() <= band * band && vel.LengthSquared() < 4f;

    // Dispatch a ship that reached 0 health: a pod just vanishes (player pod -> respawn
    // scheduled; PIG pod -> slot freed), a PIG combat drone ejects a PIG pod, a player combat
    // ship ejects a player-flown escape pod. All deferred (collected in _toRemove/_toAdd).
    private void ResolveDeath(ShipSim s, uint tick)
    {
        s.ApEngaged = false; // autopilot never survives the ship it was flying
        if (s.IsMiner)
            KillMiner(s, tick); // slot dies with the drone — no pod, repurchase only
        else if (s.Kind == ShipKind.Constructor)
            KillConstructor(s, tick); // slot dies with the drone — no pod, repurchase only
        else if (s.IsPod)
            KillPod(s, tick);
        else if (s.IsPig)
            KillPigCombat(s, tick);
        else
            EjectPlayerPod(s, tick);
    }

    // A player combat ship died: replace it with a player-flown escape pod at the wreck,
    // flung clear on a random high-speed tumbling trajectory (decays via drag). The client
    // keeps flying — its controlled ship flips to the pod (the hub re-sends YouAre).
    private void EjectPlayerPod(ShipSim dead, uint tick)
    {
        _toRemove.Add(dead); // ShipGone -> client plays the death FX
        var pod = MakePod(dead, tick);
        pod.OwnerClientId = dead.OwnerClientId;
        if (dead.OwnerClientId >= 0)
            _byClient[dead.OwnerClientId] = pod; // client now flies the pod
        _toAdd.Add(pod);
    }

    // Build an escape pod ShipSim at a wreck's pose, inheriting team/owner with a random
    // eject impulse + tumble. Shared by player (EjectPlayerPod) and PIG (KillPigCombat) death.
    private ShipSim MakePod(ShipSim dead, uint tick)
    {
        Vec3 dir = RandomUnitVec();
        Vec3 spin = RandomUnitVec();
        var pod = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = -1,
            Team = dead.Team,
            Class = dead.Class,
            SectorId = dead.SectorId,
            Kind = ShipKind.Pod,
            IsPig = dead.IsPig,
            Alive = true,
            Health = HullFor(GameContent.PodClassId),
            Shield = ShieldDefFor(GameContent.PodClassId).ShieldCapacity, // 0 unless a pod hull authors a shield
            SigBias = ShieldDefFor(GameContent.PodClassId).SignatureBias, // pods fly the Pod def's bias
            LastInputTick = tick,
            State = new ShipState
            {
                Pos = dead.State.Pos,
                Vel = dead.State.Vel + dir * PodEjectSpeed,
                Rot = dead.State.Rot,
                AngVel = spin * PodEjectSpin,
                Mass = StatsFor(dead.Class, true).Mass,
                AbPower = 0f,
                Fuel = StatsFor(dead.Class, true).MaxFuel, // 0 today; content-driven if pods ever get boost
            },
        };
        return pod;
    }

    // A pod resolved by reaching 0 health: a player pod returns its owner to the spawn menu; a
    // PIG pod frees its slot. Pods never eject pods.
    private void KillPod(ShipSim pod, uint tick)
    {
        _toRemove.Add(pod);
        if (pod.IsPig)
            FreePigPodSlot(pod, 0u, tick); // destroyed: slot rejoins the next squad wave
        else if (pod.OwnerClientId >= 0)
            ClearClientShip(pod.OwnerClientId); // player: wait for a manual relaunch (spawn menu)
    }

    // A ship/pod reached its OWN base (voluntary dock, pod flew home, or rescue): a clean
    // resolution — no pod ejection. A player is returned to the spawn menu and relaunches on
    // demand (no auto-respawn); a PIG pod frees its slot with an immediate respawn so the drone
    // rejoins the wave.
    private void DockShip(ShipSim s, uint tick)
    {
        s.ApEngaged = false; // arriving home ends any autopilot leg
        // Dock refund (D7): a voluntary dock returns the hull's paid cost to the team, so dock→relaunch
        // is a net-free full rearm/repair. Only real player hulls that actually paid (PaidCost>0)
        // refund — pods never inherit PaidCost via MakePod and PIGs pay nothing, so death refunds
        // nothing. Capped at exactly what was paid (zeroed after) → no exploit.
        if (
            !s.IsPod
            && !s.IsPig
            && s.OwnerClientId >= 0
            && s.PaidCost > 0
            && World.TeamStates.TryGetValue(s.Team, out var ts)
        )
        {
            ts.Credits += s.PaidCost;
            s.PaidCost = 0;
            TeamStateChangedThisStep = true;
        }
        // Clean exit (flew home / rescued) — the client despawns it silently, no death blast.
        s.GoneReason = GoneClean;
        _toRemove.Add(s);
        if (s.IsPig && s.IsPod)
            FreePigPodSlot(s, tick + 1u, tick);
        else if (s.OwnerClientId >= 0)
            ClearClientShip(s.OwnerClientId); // player: wait for a manual relaunch (spawn menu)
    }

    // A player lost their live ship (docked safely, or their escape pod was destroyed). Drop the
    // ship binding so the client's spawn menu reopens, but DON'T schedule a respawn: the player
    // chooses when (and which class) to relaunch by sending MsgSpawn. Replaces the old timed
    // auto-respawn so a dock no longer flings you straight back out.
    private void ClearClientShip(int clientId)
    {
        _byClient.Remove(clientId);
        _clientRespawn.Remove(clientId);
    }

    // Remove a ship from the world immediately (used at join-drain time, before the step's
    // passes iterate _order). Emits a ShipGone via DeathsThisStep.
    private void RemoveShipNow(ShipSim s)
    {
        _ships.Remove(s.ShipId);
        _order.Remove(s);
        DeathsThisStep.Add((s.ShipId, GoneDestroyed));
        if (s.MountWeaponIds is not null)
            LoadoutsChangedThisStep = true; // MsgShipLoadout table shrinks — reconcile-by-omission
    }

    // Reap held orphans whose reconnect window has elapsed: the player never came back, so the
    // ship is removed exactly as a leave would (ShipGone emitted) and its slot cleared. Runs on
    // the sim thread right after DrainQueues; collect-then-remove to avoid mutating mid-enumerate.
    private void ExpireHeldOrphans(uint tick)
    {
        if (_heldOrphans.Count == 0)
            return;
        List<string>? due = null;
        foreach (var kv in _heldOrphans)
            if (tick >= kv.Value.expiryTick)
                (due ??= new()).Add(kv.Key);
        if (due == null)
            return;
        foreach (var token in due)
        {
            var orphan = _heldOrphans[token];
            _heldOrphans.Remove(token);
            if (_byClient.Remove(orphan.oldClientId, out var ship))
                RemoveShipNow(ship);
            _clientInfo.Remove(orphan.oldClientId);
            _clientRespawn.Remove(orphan.oldClientId);
        }
    }

    // Apply the structural changes collected during a pass: remove dead/docked ships (each a
    // ShipGone) and add freshly-ejected pods. O(removed × n) on _order — deaths are rare.
    private void ApplyStructural()
    {
        if (_toRemove.Count > 0)
        {
            foreach (var s in _toRemove)
            {
                _ships.Remove(s.ShipId);
                _order.Remove(s);
                DeathsThisStep.Add((s.ShipId, s.GoneReason));
                if (s.MountWeaponIds is not null)
                    LoadoutsChangedThisStep = true; // MsgShipLoadout table shrinks — reconcile-by-omission
            }
            _toRemove.Clear();
        }
        if (_toAdd.Count > 0)
        {
            foreach (var s in _toAdd)
            {
                _ships[s.ShipId] = s;
                _order.Add(s);
            }
            _toAdd.Clear();
        }
    }

    // A uniformly-distributed unit vector (server-only RNG; baked into the spawned pod state).
    private Vec3 RandomUnitVec()
    {
        float z = (float)(_rng.NextDouble() * 2.0 - 1.0);
        float phi = (float)(_rng.NextDouble() * 2.0 * Math.PI);
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vec3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
    }

    // ---- Firing (module TryFire: analytic first-entry solve over grid ray walk) ----

    private void TryFire(ShipSim ship, uint tick)
    {
        var muzzles = ship.Class < ClassMuzzles.Length ? ClassMuzzles[ship.Class] : System.Array.Empty<Muzzle>();
        if (muzzles.Length == 0)
            return; // no authored weapon hardpoint ⇒ this hull doesn't fire (e.g. a pod)

        // Primary fire is the GUNS, and each gun mount gates on its OWN cadence (mixed loadouts):
        // FireCadence.MountFires is THE shared eligibility rule — the client derives WHICH mounts
        // fired at a replicated LastFireTick by replaying it against a per-ship shadow, so the
        // wire carries no per-mount data. TryGetValue skips an empty/unbound mount (WeaponId ==
        // HardpointDef.NoWeapon never resolves); missile racks have their own cadence in
        // TryFireMissile. IMPORTANT: the loop still visits every weapon hardpoint and KEEPS the
        // array index as `barrel` (the per-barrel spread seed) — skipped slots consume their
        // index, so gun seeds stay aligned with the client (SpawnBoltFor/PredictionController)
        // regardless of where racks or emptied slots sit in the array.
        bool fired = false;
        for (byte barrel = 0; barrel < muzzles.Length; barrel++)
        {
            if (!WeaponDefs.TryGetValue(WeaponIdAt(ship, barrel), out var w) || w.Kind != WeaponKind.Bolt)
                continue; // empty/unbound mount, or a rack (fired by Firing2, not primary fire)
            ship.MountLastFire ??= new uint[muzzles.Length];
            if (!FireCadence.MountFires(tick, ship.MountLastFire[barrel], w.FireIntervalTicks))
                continue;
            ship.MountLastFire[barrel] = tick;
            FireBolt(ship, tick, w, muzzles[barrel], barrel);
            fired = true;
        }
        if (fired)
            ship.LastFireTick = tick; // wire stamp: "a gun fired this tick" — clients derive which
    }

    // Cast one bolt from a single muzzle: spawn it at the hardpoint, walk the spatial grid for the
    // first hull/base/rock it enters, and queue the damage at the impact tick. The bolt direction
    // is seeded by (ShipId, fire tick, barrel), so the client renders the same bolt from the same
    // muzzle and the per-barrel scatter agrees on both sides.
    private void FireBolt(ShipSim ship, uint tick, WeaponDef w, in Muzzle muzzle, byte barrel)
    {
        Vec3 fwd = ship.State.Rot.Rotate(muzzle.Dir);
        Vec3 shotDir = FlightModel.SpreadDirection(fwd, w.SpreadRad, ship.ShipId, tick, barrel);
        Vec3 mp = ship.State.Pos + ship.State.Rot.Rotate(muzzle.Off);
        Vec3 mv = shotDir * w.ProjectileSpeed + ship.State.Vel;

        float maxT = w.ProjectileLifeTicks * FlightModel.Dt;
        float bestT = maxT;
        ulong targetShip = 0;
        int targetBase = -1;
        ulong targetProbe = 0;

        if (w.CanDamageBase)
        {
            for (int i = 0; i < World.Bases.Count; i++)
            {
                var b = World.Bases[i];
                if (b.SectorId != ship.SectorId || b.Team == ship.Team)
                    continue;
                bool hit = World.BaseHullOf(b.BaseTypeId) is not null
                    ? BaseHullsRayEntry(b.Pos, b.BaseTypeId, mp, mv, World.ProjectileRadius, bestT, out float t)
                    : FirstEntryTime(
                        mp,
                        mv,
                        b.Pos,
                        default,
                        World.BaseRadiusOf(b.BaseTypeId) + World.ProjectileRadius,
                        bestT,
                        out t
                    );
                if (hit && t < bestT)
                {
                    bestT = t;
                    targetBase = i;
                    targetShip = 0;
                }
            }
        }

        var shipGrid = _shipGrid.TryGetValue(ship.SectorId, out var sg) ? sg : null;
        var rockGrid = World.RockGrid(ship.SectorId);
        foreach (var cell in CellsAlongRay(mp, mv, bestT))
        {
            if (shipGrid is not null && shipGrid.TryGetValue(cell, out var shipsInCell))
            {
                foreach (var s in shipsInCell)
                {
                    if (!s.Alive)
                        continue;
                    // A HEALING gun (ER Nanite) targets SAME-team ships (to heal them) and skips the
                    // shooter itself; a normal gun targets the ENEMY and skips friendlies. Enemy hits by
                    // a heal bolt are a pure client-side spark (zero server effect), so the server simply
                    // never targets them here — the heal path can only ever resolve on a friendly.
                    if (w.IsHealing ? (s.Team != ship.Team || s.ShipId == ship.ShipId) : s.Team == ship.Team)
                        continue;
                    var body = World.ShipHull(s.Class, s.IsPod);
                    if (body is World.ShipBody sb)
                    {
                        // Bounding-sphere pre-test (accounts for the ship's drift via its velocity),
                        // then the ship's convex hull. The hull is static at the ship's current pose
                        // for the bolt's short flight; the ship's linear drift is folded into the ray
                        // by using the bolt-relative velocity (mv − ship velocity), exactly as the
                        // sphere FirstEntryTime uses the relative velocity.
                        float br = sb.BoundingRadius + World.ProjectileRadius;
                        if (!FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, br, bestT, out _))
                            continue;
                        Vec3 vrel = mv - s.State.Vel;
                        if (
                            HullRayEntry(
                                sb.Hull,
                                s.State.Pos,
                                s.State.Rot,
                                1f,
                                mp,
                                vrel,
                                World.ProjectileRadius,
                                bestT,
                                out float th
                            )
                            && th < bestT
                        )
                        {
                            bestT = th;
                            targetShip = s.ShipId;
                            targetBase = -1;
                        }
                    }
                    else
                    {
                        float r = World.ShipRadius + World.ProjectileRadius;
                        if (FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, r, bestT, out float t) && t < bestT)
                        {
                            bestT = t;
                            targetShip = s.ShipId;
                            targetBase = -1;
                        }
                    }
                }
            }
            if (rockGrid.TryGetValue(cell, out var rocks))
            {
                foreach (var a in rocks)
                {
                    // Resolution radius = the rock's CURRENT (mined-down) surface; a mined He3 rock
                    // stops a bolt only at its live shell. The broad-phase pre-test stays at the SPAWN
                    // radius (conservative — rocks only shrink, so it never rejects a real hit; the hull
                    // path below tests the live-scaled body, the sphere fallback tests the live `r`).
                    float r = World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale + World.ProjectileRadius;
                    if (!FirstEntryTime(mp, mv, a.Pos, default, a.Radius + World.ProjectileRadius, bestT, out _))
                        continue;
                    bool hit = World.RockBodies.TryGetValue(a.Id, out var body)
                        ? HullRayEntry(
                            body.Hull,
                            a.Pos,
                            body.Rot,
                            body.Scale,
                            mp,
                            mv,
                            World.ProjectileRadius,
                            bestT,
                            out float t
                        )
                        : FirstEntryTime(mp, mv, a.Pos, default, r, bestT, out t);
                    if (hit && t < bestT)
                    {
                        bestT = t;
                        targetShip = 0;
                        targetBase = -1; // stopped by a rock
                    }
                }
            }
        }

        // Alephs are solid barriers to weapon fire: a gate mouth absorbs a bolt with no damage target.
        // The mouth's known extent is its warp-trigger radius (a ship warps at that distance, so it
        // never reaches the barrier — only projectiles are stopped). Few gates per sector, not in any
        // grid → a linear scan (like the probe scan below) is cheap and replay-deterministic in list
        // order. A closer aleph hit wins and clears any target.
        float alephR = _mech.AlephTriggerRadius + World.ProjectileRadius;
        for (int i = 0; i < World.Alephs.Count; i++)
        {
            var g = World.Alephs[i];
            if (g.SectorId != ship.SectorId)
                continue;
            if (FirstEntryTime(mp, mv, g.Pos, default, alephR, bestT, out float at) && at < bestT)
            {
                bestT = at;
                targetShip = 0;
                targetBase = -1;
                targetProbe = 0; // stopped by an aleph
            }
        }

        // Deployed enemy probes are destructible: a stationary hit-sphere scan (probes are few and
        // not in the ship grid, so a linear scan is cheap and stays replay-deterministic in list
        // order). A closer probe hit wins and clears any ship/base target.
        for (int i = 0; i < _probes.Count && !w.IsHealing; i++) // a healing gun never targets enemy probes
        {
            var p = _probes[i];
            if (p.SectorId != ship.SectorId || p.Team == ship.Team || p.Health <= 0f)
                continue;
            float pr = (WeaponDefs.TryGetValue(p.WeaponId, out var pw) ? pw.ProbeHitRadius : 0f) + World.ProjectileRadius;
            if (pr > World.ProjectileRadius && FirstEntryTime(mp, mv, p.Pos, default, pr, bestT, out float pt) && pt < bestT)
            {
                bestT = pt;
                targetProbe = p.ProbeId;
                targetShip = 0;
                targetBase = -1;
            }
        }

        if (targetShip != 0 || targetBase >= 0 || targetProbe != 0)
        {
            uint resolveTicks = Math.Max(1u, (uint)MathF.Ceiling(bestT / FlightModel.Dt));
            // GunDamage (v41): scale by the shooter team's faction multiplier. Applies to healing bolts
            // too — Iron's better guns heal harder (ApplyHeal reads this same Damage field as heal power),
            // consistent + simpler than a separate heal path. Bakes into PendingShot so the base-damage
            // path (ApplyBaseDamage via shot.Damage) inherits it as well.
            float dmg = w.Damage * TeamAttr(ship.Team, Allegiance.Factions.Model.GameAttribute.GunDamage);
            _shotRing[(tick + resolveTicks) % ShotRingSize]
                .Add(new PendingShot(targetShip, targetBase, dmg, w.ShieldMult, targetProbe, w.IsHealing));
        }
    }

    // ---- Guided missiles (server-authoritative lock + launch + turn-rate pursuit) ----

    // Advance this ship's missile lock timer from its input's LockTargetId. A hull with no rack
    // never locks (early out). A valid target (alive enemy ship — NOT a pod — in the same sector,
    // within LockRange, inside the LockAngle nose cone) advances progress; anything else resets it.
    // Progress reaching LockTicks latches Locked. Also bakes the wire LockState byte.
    private void UpdateLock(ShipSim ship, in ShipInputState input, uint tick)
    {
        if (MissileMountFor(ship) is not (_, WeaponDef w))
            return; // no effective launcher on this ship (none authored, or rack emptied) — never locks

        ship.LockTargetId = input.LockTargetId; // mirror the client's requested target
        bool valid = false;
        ShipSim? threatTarget = null; // the ship being locked (A2 being-locked warning), if any
        if (GameContent.IsBaseLock(input.LockTargetId))
        {
            // Base lock: only a CanDamageBase weapon may lock a base at all (D3); otherwise
            // reuse the exact same range/cone test as a ship target, aimed at the base's pos.
            if (w.CanDamageBase && TryGetLockableBase(input.LockTargetId, ship.Team, ship.SectorId, out int bi))
            {
                Vec3 to = World.Bases[bi].Pos - ship.State.Pos;
                float d = to.Length();
                if (d > 1e-4f && d <= w.LockRange)
                {
                    Vec3 nose = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
                    if (Dot(nose, to * (1f / d)) >= MathF.Cos(w.LockAngleRad))
                        valid = true;
                }
            }
        }
        else if (
            input.LockTargetId != 0
            && _ships.TryGetValue(input.LockTargetId, out var t)
            && t.Alive
            && t.Team != ship.Team
            && !t.IsPod // locking a helpless pod is bad feel — pods are never lockable
            && t.SectorId == ship.SectorId
            // Fog of war: only a RADAR-detected foe is lockable (eyeball-tier / ghosts are not). An
            // in-flight missile keeps tracking a target that later fogs out — this gates ACQUISITION.
            && TeamRadarSees(ship.Team, t.ShipId)
        )
        {
            Vec3 to = t.State.Pos - ship.State.Pos;
            float d = to.Length();
            if (d > 1e-4f && d <= w.LockRange)
            {
                Vec3 nose = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
                if (Dot(nose, to * (1f / d)) >= MathF.Cos(w.LockAngleRad))
                {
                    valid = true;
                    threatTarget = t; // resolve its warning below, after ship.Locked updates this tick
                }
            }
        }

        if (valid)
        {
            ship.LockProgress++;
            if (ship.LockProgress >= w.LockTicks)
                ship.Locked = true;
        }
        else
        {
            ship.LockProgress = 0;
            ship.Locked = false;
        }

        // Being-locked warning (A2): raise the threat state on the TARGET ship so its wire record
        // carries the amber (locking) / red (locked) flag bits. Uses the freshly-updated ship.Locked
        // so the red banner fires the same tick the lock completes. A completed lock wins over a
        // merely-progressing one from another attacker (max, not overwrite). Base locks never warn.
        if (threatTarget is not null)
            threatTarget.ThreatLockState = ship.Locked ? (byte)2 : Math.Max(threatTarget.ThreatLockState, (byte)1);

        uint pct = w.LockTicks > 0 ? ship.LockProgress * 100u / w.LockTicks : 100u;
        if (pct > 100u)
            pct = 100u;
        ship.LockState = (byte)((ship.Locked ? 0x80 : 0) | (int)pct);
    }

    // Secondary fire (Firing2): launch one missile from the rack when armed and off cooldown.
    // A lock is NOT required — unlocked launches are dumbfire (no target, ballistic straight
    // out of the tube); a completed lock makes the round guided. Spawns at the mount
    // hardpoint's world pose (like FireBolt), inheriting ship velocity plus the missile's
    // initial boost along the mount forward. Appended directly to _missiles (safe — Pass A
    // iterates _order, not _missiles).
    private void TryFireMissile(ShipSim ship, uint tick)
    {
        if (MissileMountFor(ship) is not (Muzzle mount, WeaponDef w))
            return; // no effective rack (none authored, or emptied in the hangar)
        if (ship.MissileAmmo == 0)
            return;
        if (ship.LastMissileTick != 0 && tick - ship.LastMissileTick < w.FireIntervalTicks)
            return;

        ship.MissileAmmo--;
        ship.LastMissileTick = tick;

        Vec3 fwd = ship.State.Rot.Rotate(mount.Dir);
        Vec3 mp = ship.State.Pos + ship.State.Rot.Rotate(mount.Off);
        _missiles.Add(
            new MissileSim
            {
                MissileId = _nextShipId++,
                OwnerShipId = ship.ShipId,
                Team = ship.Team,
                WeaponId = w.WeaponId,
                SectorId = ship.SectorId,
                Pos = mp,
                Vel = ship.State.Vel + fwd * w.ProjectileSpeed, // ProjectileSpeed = InitialSpeed
                // Guided only when the lock completed; LockTargetId alone is just the client's
                // REQUEST (mirrored every tick by UpdateLock) and must not steer a dumbfire.
                TargetShipId = ship.Locked ? ship.LockTargetId : 0,
                ExpireAtTick = tick + w.ProjectileLifeTicks,
            }
        );
    }

    // Resolve a missile's seeker target — the isolated chaff/flare substitution seam. Returns the
    // homed ship, or null (missile coasts ballistic) when the target is dead / friendly / a pod /
    // in another sector / gone.
    private ShipSim? ResolveSeekerTarget(MissileSim mis)
    {
        if (mis.TargetShipId == 0 || !_ships.TryGetValue(mis.TargetShipId, out var t))
            return null;
        if (!t.Alive || t.Team == mis.Team || t.IsPod || t.SectorId != mis.SectorId)
            return null;
        return t;
    }

    // Step every in-flight missile: steer (turn-rate-limited pure pursuit) + accelerate, sweep this
    // tick's segment for the first enemy ship (skip owner, friendlies, pods) or rock, detonate
    // (direct Damage * DirectHitMult on the fuse-triggering ship + ApplyBlast splash around the
    // detonation point) or expire. Deterministic f32 math, no RNG. Between Pass A and Pass C.
    // Removals collected then applied post-loop.
    private void StepMissiles(uint tick, float dt)
    {
        if (_missiles.Count == 0)
            return;

        List<MissileSim>? remove = null;
        foreach (var mis in _missiles)
        {
            var w = WeaponDefs[mis.WeaponId];

            // (1) aim point (chaff seam for ships; base-lock resolves to the base's pos, else the
            // missile coasts unguided — D4) → (2) steer + accelerate. ResolveSeekerTarget stays the
            // untouched chaff substitution seam.
            Vec3? aimPos = null;
            bool chaffDetonate = false;
            Vec3 chaffDetonatePos = default;
            // Chaff substitution seam (Track A fills TryChaffAim; the Track-0 stub returns false so a
            // seeker behaves exactly as before). A decoyed missile homes on the puff, and once it
            // reaches the puff `detonateAtChaff` forces a proximity detonation at the chaff position.
            if (TryChaffAim(mis, out Vec3 chaffAim, out bool detonateAtChaff))
            {
                aimPos = chaffAim;
                chaffDetonate = detonateAtChaff;
                chaffDetonatePos = chaffAim;
            }
            else if (GameContent.IsBaseLock(mis.TargetShipId))
            {
                if (TryGetLockableBase(mis.TargetShipId, mis.Team, mis.SectorId, out int aimBase))
                    aimPos = World.Bases[aimBase].Pos;
            }
            else if (ResolveSeekerTarget(mis) is ShipSim tg)
            {
                aimPos = tg.State.Pos;
            }
            float speed = mis.Vel.Length();
            Vec3 dir = speed > 1e-4f ? mis.Vel * (1f / speed) : new Vec3(0f, 0f, 1f);
            if (aimPos is Vec3 ap)
            {
                Vec3 to = ap - mis.Pos;
                float d = to.Length();
                if (d > 1e-4f)
                    dir = TurnToward(dir, to * (1f / d), w.MissileTurnRateRad * dt);
            }
            float newSpeed = speed + w.MissileAccel * dt;
            if (w.MissileMaxSpeed > 0f && newSpeed > w.MissileMaxSpeed)
                newSpeed = w.MissileMaxSpeed;
            Vec3 vel = dir * newSpeed;
            mis.Vel = vel;

            // Chaff detonation: TryChaffAim reported the missile is now within fuse range of the puff
            // it was decoyed onto — detonate here (splash only; no direct-hit ship) and drop it.
            if (chaffDetonate)
            {
                var cg = _shipGrid.TryGetValue(mis.SectorId, out var csg) ? csg : null;
                ApplyBlast(mis.Team, w, chaffDetonatePos, 0, cg, tick, mis.SectorId);
                MissileGoneThisStep.Add((mis.MissileId, 1, mis.SectorId, chaffDetonatePos));
                (remove ??= new()).Add(mis);
                continue;
            }

            // (3) sweep the tick segment (mp + vel·t, t∈[0,dt]) against enemy bases + ships + rocks.
            // Bases go first (mirrors FireBolt's base-then-grid order): EVERY missile sweeps bases
            // regardless of lock, using the RAW hull/sphere test (no w.ProjectileRadius fuse-margin
            // inflation — D5) so a torpedo always registers an exact hull-surface contact, never a
            // near-miss short of the base. The ship/rock grid walk below then competes against the
            // (possibly already-reduced) bestT exactly as it did before bases existed; whichever is
            // closer wins and clears the other's hit slot.
            Vec3 mp = mis.Pos;
            float bestT = dt;
            ulong hitShip = 0;
            int hitBase = -1;
            bool detonate = false;
            for (int bi = 0; bi < World.Bases.Count; bi++)
            {
                var b = World.Bases[bi];
                if (b.SectorId != mis.SectorId || b.Team == mis.Team)
                    continue; // friendly bases are non-colliding, matching bolts
                bool bhit = World.BaseHullOf(b.BaseTypeId) is not null
                    ? BaseHullsRayEntry(b.Pos, b.BaseTypeId, mp, vel, 0f, bestT, out float bt)
                    : FirstEntryTime(mp, vel, b.Pos, default, World.BaseRadiusOf(b.BaseTypeId), bestT, out bt);
                if (bhit && bt < bestT)
                {
                    bestT = bt;
                    hitBase = bi;
                    hitShip = 0;
                    detonate = true;
                }
            }
            var shipGrid = _shipGrid.TryGetValue(mis.SectorId, out var sg) ? sg : null;
            var rockGrid = World.RockGrid(mis.SectorId);
            foreach (var cell in CellsAlongRay(mp, vel, bestT))
            {
                if (shipGrid is not null && shipGrid.TryGetValue(cell, out var shipsInCell))
                {
                    foreach (var s in shipsInCell)
                    {
                        // Skip the owner, friendlies, dead ships, and pods (strays must not gib
                        // podded pilots; seekers only ever target ships anyway).
                        if (s.Team == mis.Team || !s.Alive || s.ShipId == mis.OwnerShipId || s.IsPod)
                            continue;
                        var body = World.ShipHull(s.Class, s.IsPod);
                        if (body is World.ShipBody sb)
                        {
                            float br = sb.BoundingRadius + w.ProjectileRadius;
                            if (!FirstEntryTime(mp, vel, s.State.Pos, s.State.Vel, br, bestT, out _))
                                continue;
                            Vec3 vrel = vel - s.State.Vel;
                            if (
                                HullRayEntry(
                                    sb.Hull,
                                    s.State.Pos,
                                    s.State.Rot,
                                    1f,
                                    mp,
                                    vrel,
                                    w.ProjectileRadius,
                                    bestT,
                                    out float th
                                )
                                && th < bestT
                            )
                            {
                                bestT = th;
                                hitShip = s.ShipId;
                                hitBase = -1;
                                detonate = true;
                            }
                        }
                        else
                        {
                            float r = World.ShipRadius + w.ProjectileRadius;
                            if (FirstEntryTime(mp, vel, s.State.Pos, s.State.Vel, r, bestT, out float t) && t < bestT)
                            {
                                bestT = t;
                                hitShip = s.ShipId;
                                hitBase = -1;
                                detonate = true;
                            }
                        }
                    }
                }
                if (rockGrid.TryGetValue(cell, out var rocks))
                {
                    foreach (var a in rocks)
                    {
                        // Pre-test at spawn radius (conservative broad-phase); resolve against the live
                        // (mined-down) surface — the hull path uses the live-scaled body, the sphere
                        // fallback uses `r` = current radius.
                        if (!FirstEntryTime(mp, vel, a.Pos, default, a.Radius + w.ProjectileRadius, bestT, out _))
                            continue;
                        float r = World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale + w.ProjectileRadius;
                        bool hit = World.RockBodies.TryGetValue(a.Id, out var rbody)
                            ? HullRayEntry(
                                rbody.Hull,
                                a.Pos,
                                rbody.Rot,
                                rbody.Scale,
                                mp,
                                vel,
                                w.ProjectileRadius,
                                bestT,
                                out float t
                            )
                            : FirstEntryTime(mp, vel, a.Pos, default, r, bestT, out t);
                        if (hit && t < bestT)
                        {
                            bestT = t;
                            hitShip = 0; // a rock kills the missile (no damage dealt)
                            hitBase = -1;
                            detonate = true;
                        }
                    }
                }
            }

            // Alephs are solid barriers: a gate mouth (sized by its warp-trigger radius) on the
            // segment stops the missile, which detonates on the barrier (blast splash still applies)
            // with no direct-hit target.
            float alephR = _mech.AlephTriggerRadius + w.ProjectileRadius;
            for (int i = 0; i < World.Alephs.Count; i++)
            {
                var g = World.Alephs[i];
                if (g.SectorId != mis.SectorId)
                    continue;
                if (FirstEntryTime(mp, vel, g.Pos, default, alephR, bestT, out float at) && at < bestT)
                {
                    bestT = at;
                    hitShip = 0;
                    hitBase = -1;
                    detonate = true; // an aleph stops the missile
                }
            }

            if (detonate)
            {
                Vec3 hitPos = mp + vel * bestT;
                // MissileDamage (v41): scale the warhead by the firing team's faction multiplier (direct
                // hit + base hit here; the splash inherits it inside ApplyBlast off the same mis.Team).
                float md = TeamAttr(mis.Team, Allegiance.Factions.Model.GameAttribute.MissileDamage);
                if (hitShip != 0 && _ships.TryGetValue(hitShip, out var victim) && victim.Alive)
                    ApplyDamage(victim, w.Damage * w.DirectHitMult * md, tick, w.ShieldMult); // end-of-step death pass resolves 0 health
                else if (hitBase >= 0 && w.CanDamageBase)
                    ApplyBaseDamage(hitBase, w.Damage * w.DirectHitMult * md, tick); // blast never touches the base
                ApplyBlast(mis.Team, w, hitPos, hitShip, shipGrid, tick, mis.SectorId);
                MissileGoneThisStep.Add((mis.MissileId, 1, mis.SectorId, hitPos)); // impact
                (remove ??= new()).Add(mis);
                continue;
            }

            // (6) integrate, then expiry / world-boundary.
            mis.Pos = mp + vel * dt;
            if (tick >= mis.ExpireAtTick || mis.Pos.Length() > World.SectorRadius(mis.SectorId))
            {
                MissileGoneThisStep.Add((mis.MissileId, 0, mis.SectorId, mis.Pos)); // expired
                (remove ??= new()).Add(mis);
            }
        }

        if (remove is not null)
            foreach (var mis in remove)
                _missiles.Remove(mis);
    }

    // Warhead splash on detonation (ship OR rock impact): every enemy ship within BlastRadius of the
    // detonation point takes BlastPower, full inside the fuse radius (ProjectileRadius = authored
    // width) and inverse-square beyond it — falloff = (fuse/d)². The direct-hit victim is excluded
    // (it already took Damage * DirectHitMult); friendlies/pods never take splash, matching the
    // sweep's no-friendly-fire rule. Grid cube query keeps this off the O(ships) path; fixed
    // dx/dy/dz iteration order + one damage write per ship keeps it deterministic.
    private void ApplyBlast(
        byte team,
        WeaponDef w,
        Vec3 hitPos,
        ulong directHitShip,
        Dictionary<(int, int, int), List<ShipSim>>? shipGrid,
        uint tick,
        uint sector
    )
    {
        if (w.BlastRadius <= 0f || w.BlastPower <= 0f)
            return;
        float fuseR = w.ProjectileRadius;
        // MissileDamage (v41): the splash inherits the firing team's faction multiplier (same factor the
        // direct hit applied), so a whole detonation scales consistently.
        float md = TeamAttr(team, Allegiance.Factions.Model.GameAttribute.MissileDamage);

        // Enemy probes within the blast take splash too (same inverse-square falloff), so a missile
        // detonation is a valid probe counter. Probes aren't gridded — a linear scan (few probes) in
        // the detonation sector, deterministic in list order. Done before the ship grid so a probe
        // killed here is removed for any same-tick follow-up.
        for (int i = 0; i < _probes.Count; i++)
        {
            var p = _probes[i];
            if (p.SectorId != sector || p.Team == team || p.Health <= 0f)
                continue;
            float d = (p.Pos - hitPos).Length();
            if (d > w.BlastRadius)
                continue;
            float f = d <= fuseR ? 1f : (fuseR / d) * (fuseR / d);
            DamageProbe(p, w.BlastPower * f * md, tick);
        }

        if (shipGrid is null)
            return;
        int x0 = World.CellOf(hitPos.X - w.BlastRadius),
            x1 = World.CellOf(hitPos.X + w.BlastRadius);
        int y0 = World.CellOf(hitPos.Y - w.BlastRadius),
            y1 = World.CellOf(hitPos.Y + w.BlastRadius);
        int z0 = World.CellOf(hitPos.Z - w.BlastRadius),
            z1 = World.CellOf(hitPos.Z + w.BlastRadius);
        for (int cx = x0; cx <= x1; cx++)
        for (int cy = y0; cy <= y1; cy++)
        for (int cz = z0; cz <= z1; cz++)
        {
            if (!shipGrid.TryGetValue((cx, cy, cz), out var shipsInCell))
                continue;
            foreach (var s in shipsInCell)
            {
                if (s.Team == team || !s.Alive || s.IsPod || s.ShipId == directHitShip)
                    continue;
                float d = (s.State.Pos - hitPos).Length();
                if (d > w.BlastRadius)
                    continue;
                float falloff = d <= fuseR ? 1f : (fuseR / d) * (fuseR / d);
                ApplyDamage(s, w.BlastPower * falloff * md, tick, w.ShieldMult); // end-of-step death pass resolves 0 health
            }
        }
    }

    // Rotate unit `dir` toward unit `desired` by at most `maxRad`. Linear blend + renormalize (a
    // small-angle slerp approximation, exact enough per tick); deterministic f32 (server-only).
    private static Vec3 TurnToward(Vec3 dir, Vec3 desired, float maxRad)
    {
        float dot = Dot(dir, desired);
        if (dot > 1f)
            dot = 1f;
        else if (dot < -1f)
            dot = -1f;
        float ang = MathF.Acos(dot);
        if (ang <= maxRad || ang < 1e-5f)
            return desired;
        float f = maxRad / ang;
        Vec3 blended = dir * (1f - f) + desired * f;
        float n = blended.Length();
        return n > 1e-6f ? blended * (1f / n) : desired;
    }

    // The BaseDef for a live base's type (garrison 0, outpost 1, …); null if the content has none.
    private Dictionary<byte, BaseDef>? _baseDefByType;

    private BaseDef? BaseDefForType(byte typeId)
    {
        if (_baseDefByType is null)
        {
            _baseDefByType = new Dictionary<byte, BaseDef>();
            foreach (var d in Content.Bases)
                _baseDefByType[d.BaseTypeId] = d;
        }
        return _baseDefByType.TryGetValue(typeId, out var def) ? def : null;
    }

    // A base type is a win-condition ("headquarters") base when its def carries WinCondition (the
    // `start` ability — only the garrison, in stock content). A team loses when ALL its WinCondition
    // bases are destroyed; a forward base (outpost) at 0 HP never ends the match.
    private bool IsWinConditionBase(byte typeId) => BaseDefForType(typeId)?.WinCondition ?? false;

    // Does `team` still hold a live WinCondition base (optionally excluding one index)?
    private bool TeamHasAliveWinBase(byte team, int excludeIndex)
    {
        for (int i = 0; i < World.Bases.Count; i++)
            if (
                i != excludeIndex
                && World.Bases[i].Team == team
                && World.BaseHealth[i] > 0f
                && IsWinConditionBase(World.Bases[i].BaseTypeId)
            )
                return true;
        return false;
    }

    // Apply damage to a base (health floor at 0), flag it as changed/dirty, and — when a team's LAST
    // win-condition (headquarters) base drops to 0 — latch the match end (winner = the OTHER team) and
    // schedule the return-to-lobby. Destroying a forward base (outpost) removes it from play but never
    // ends the match. Shared by the bolt path (ResolveDueShots) and missile detonation (StepMissiles).
    private void ApplyBaseDamage(int baseIndex, float damage, uint tick)
    {
        bool wasAlive = World.BaseHealth[baseIndex] > 0f;
        float hp = MathF.Max(0f, World.BaseHealth[baseIndex] - damage);
        World.BaseHealth[baseIndex] = hp;
        BasesChangedThisStep = true;
        _matchDirty = true;
        if (hp <= 0f && wasAlive && Phase != PhaseEnded)
        {
            byte loser = World.Bases[baseIndex].Team;
            // Only the loss of a team's FINAL win-condition base ends the match.
            if (IsWinConditionBase(World.Bases[baseIndex].BaseTypeId) && !TeamHasAliveWinBase(loser, baseIndex))
            {
                Winner = (byte)(loser == 0 ? 1 : 0);
                Phase = PhaseEnded;
                JustEnded = true;
                _returnToLobbyAtTick = tick + EndedToLobbyTicks;
            }
        }
    }

    // Find the enemy base this lock id refers to: `lockId` must decode (GameContent.BaseIdOf) to a
    // base on the OTHER team, in the ship's sector, still standing (BaseHealth > 0). Used by both
    // UpdateLock (ship-issued base locks) and StepMissiles (in-flight missile aim-point/collision).
    private bool TryGetLockableBase(ulong lockId, byte team, uint sectorId, out int baseIndex)
    {
        ulong id = GameContent.BaseIdOf(lockId);
        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (b.Id == id && b.Team != team && b.SectorId == sectorId && World.BaseHealth[i] > 0f)
            {
                baseIndex = i;
                return true;
            }
        }
        baseIndex = -1;
        return false;
    }

    private void ResolveDueShots(uint tick)
    {
        var due = _shotRing[tick % ShotRingSize];
        foreach (var shot in due)
        {
            if (shot.TargetProbeId != 0)
            {
                // The probe may have expired/been killed since the bolt was queued — skip if gone.
                if (FindDamageableProbe(shot.TargetProbeId) is { } probe)
                    DamageProbe(probe, shot.Damage, tick);
            }
            else if (shot.BaseIndex >= 0)
            {
                ApplyBaseDamage(shot.BaseIndex, shot.Damage, tick);
            }
            else if (_ships.TryGetValue(shot.TargetShipId, out var s) && s.Alive)
            {
                if (shot.Heal)
                    // ER Nanite: FireBolt only ever queues a heal shot against a same-team ship, so
                    // restore hull here (clamp/no-shield inside ApplyHeal). Damage is the heal power.
                    ApplyHeal(s, shot.Damage);
                else
                    // Apply damage only; the end-of-step death/dock pass detects 0 health and
                    // ejects the pod / frees the slot — one death path, like the module.
                    ApplyDamage(s, shot.Damage, tick, shot.ShieldMult);
            }
        }
        due.Clear();
    }

    private static bool FirstEntryTime(
        Vec3 shotPos,
        Vec3 shotVel,
        Vec3 targetPos,
        Vec3 targetVel,
        float radius,
        float maxT,
        out float t
    )
    {
        Vec3 d = targetPos - shotPos;
        Vec3 vrel = targetVel - shotVel;
        float a = vrel.LengthSquared();
        float b = 2f * Dot(d, vrel);
        float c = d.LengthSquared() - radius * radius;

        if (c <= 0f)
        {
            t = 0f;
            return true;
        }
        if (a < 1e-6f)
        {
            if (b >= -1e-6f)
            {
                t = 0f;
                return false;
            }
            t = -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f)
            {
                t = 0f;
                return false;
            }
            t = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (t < 0f)
            {
                t = 0f;
                return false;
            }
        }
        return t <= maxT;
    }

    private IEnumerable<(int, int, int)> CellsAlongRay(Vec3 start, Vec3 vel, float maxT)
    {
        _rayCells.Clear();
        float dist = vel.Length() * maxT;
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / World.GridCell));
        for (int i = 0; i <= steps; i++)
        {
            Vec3 p = start + vel * (maxT * i / steps);
            int cx = World.CellOf(p.X),
                cy = World.CellOf(p.Y),
                cz = World.CellOf(p.Z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var key = (cx + dx, cy + dy, cz + dz);
                if (_rayCells.Add(key))
                    yield return key;
            }
        }
    }

    private void RebuildShipGrid()
    {
        _shipGrid.Clear();
        foreach (var s in _order)
        {
            if (!s.Alive)
                continue;
            if (!_shipGrid.TryGetValue(s.SectorId, out var grid))
                _shipGrid[s.SectorId] = grid = new Dictionary<(int, int, int), List<ShipSim>>();
            var key = (World.CellOf(s.State.Pos.X), World.CellOf(s.State.Pos.Y), World.CellOf(s.State.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = new List<ShipSim>();
            cell.Add(s);
        }
    }

    // ---- Collisions (module Pass C, mass-weighted) ------------------------

    // Ship-vs-ship contact (any pair, friend or foe). With both ships' GLB hulls loaded the contact is resolved as a
    // ShipRadius sphere against the OTHER ship's convex hull (the same kernel asteroids/bases use),
    // so a long bomber or a wide fighter collides on its real silhouette; without hulls it falls
    // back to the legacy equal-radius sphere overlap. The contact math lives in the SHARED
    // Collide.ShipShipContact so the client's local-ship prediction resolves the identical bounce.
    // The resolution is the module's mass-weighted impulse + inverse-mass-split push-out along the
    // contact normal n (b → a).
    private void CollideShips(ShipSim a, ShipSim b)
    {
        var ha = World.ShipHull(a.Class, a.IsPod);
        var hb = World.ShipHull(b.Class, b.IsPod);

        if (
            !Collide.ShipShipContact(
                a.State.Pos,
                a.State.Rot,
                ha?.Hull,
                ha?.BoundingRadius ?? World.ShipRadius,
                b.State.Pos,
                b.State.Rot,
                hb?.Hull,
                hb?.BoundingRadius ?? World.ShipRadius,
                World.ShipRadius,
                out Vec3 n,
                out float pen
            )
        )
            return;

        ResolveShipImpulse(a, b, n, pen);
    }

    // Module-identical mass-weighted bounce: restitution impulse + collision damage when closing,
    // and an inverse-mass-split positional correction along n (which points b → a).
    private void ResolveShipImpulse(ShipSim a, ShipSim b, Vec3 n, float pen)
    {
        a.LastCollisionTick = b.LastCollisionTick = _tick; // any resolved contact, closing or not
        float iA = a.State.Mass > 0f ? 1f / a.State.Mass : 1f;
        float iB = b.State.Mass > 0f ? 1f / b.State.Mass : 1f;
        float invSum = iA + iB;

        float relVn = Dot(a.State.Vel - b.State.Vel, n);
        if (relVn < 0f)
        {
            float jimp = -(1f + World.CollisionRestitution) * relVn / invSum;
            a.State.Vel += n * (jimp * iA);
            b.State.Vel -= n * (jimp * iB);
            float dmg = CollisionDamage(-relVn, (1f / invSum) * _combat.ShipShipDamageScale);
            ApplyDamage(a, dmg, _tick);
            ApplyDamage(b, dmg, _tick);
        }
        a.State.Pos += n * (pen * (iA / invSum));
        b.State.Pos -= n * (pen * (iB / invSum));
    }

    private void ResolveAsteroidCollisions(ShipSim s)
    {
        // A constructor deliberately sinks into and embeds in its target rock during the build phases;
        // resolving that penetration would shove it back out every tick (the visible flicker). Skip
        // collision with just that one rock while it's aligning/sinking/building on it.
        ulong ignoreRock = s.Kind == ShipKind.Constructor ? ConstructorEmbeddedRock(s) : 0;

        var grid = World.RockGrid(s.SectorId);
        int cx = World.CellOf(s.State.Pos.X),
            cy = World.CellOf(s.State.Pos.Y),
            cz = World.CellOf(s.State.Pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
            {
                if (a.Id == ignoreRock)
                    continue;
                // Cheap bounding-sphere reject at the SPAWN radius (conservative — rocks only shrink,
                // so this never skips a real contact), then the convex hull if this rock has one — else
                // the legacy sphere sized to the rock's CURRENT (mined-down) surface.
                Vec3 dd = s.State.Pos - a.Pos;
                float bound = a.Radius + World.ShipRadius;
                if (dd.LengthSquared() >= bound * bound)
                    continue;
                if (World.RockBodies.TryGetValue(a.Id, out var body))
                {
                    // Live tumble: compose the spawn pose with the spin at the current tick so the
                    // authoritative hull matches the rendered rock (Collide.RockRotationAt, shared).
                    // body.Scale tracks the live radius (SetOreRemaining re-scales it as ore is mined).
                    Quat rot = Collide.RockRotationAt(body.Rot, body.SpinAxis, body.SpinSpeed, _tick * FlightModel.Dt);
                    ResolveHullCollision(s, body.Hull, a.Pos, rot, body.Scale);
                }
                else
                    ResolveStaticCollision(s, a.Pos, World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale);
            }
        }
    }

    // Bounce a ship off a base: the loaded compound world hull if present, else the legacy radius
    // sphere. Runs through the shared Collide.SphereVsBody kernel over the authored sub-hulls (deepest
    // contact = one BounceShip), so an enemy base bounces exactly as the client predicts it.
    private void ResolveBaseCollision(ShipSim s, Vec3 center, byte baseTypeId)
    {
        if (World.BaseHullOf(baseTypeId) is not null)
        {
            if (Collide.SphereVsBody(s.State.Pos, World.ShipRadius, BaseBody(center, baseTypeId), out Vec3 n, out float pen))
                BounceShip(s, n, pen);
        }
        else
            ResolveStaticCollision(s, center, World.BaseRadiusOf(baseTypeId));
    }

    // The base as a shared compound StaticBody at `center`: `BaseHull` is the merged shrink-wrap (kept
    // non-null for the struct + broadphase parity), `BaseSubHulls` are the authored parts the kinematic
    // kernel actually resolves against. Team/discs are irrelevant to SphereVsBody (it never gates on
    // them — dock carve-out is handled by the caller), so a fixed team/the discs are passed for shape.
    // Callers guard World.BaseHull is not null first.
    private Collide.StaticBody BaseBody(Vec3 center, byte baseTypeId) =>
        Collide.StaticBody.BaseHull(
            World.BaseHullOf(baseTypeId)!,
            World.BaseSubHullsOf(baseTypeId),
            center,
            0,
            World.BaseDockFacesOf(baseTypeId)
        );

    // Min-entry-t of a ray (mp + mv·t) across the base's authored sub-hulls (world-scaled, identity
    // frame), the ray analogue of SphereVsBody: the CLOSEST sub-hull surface stops the bolt/missile, so
    // a shot threading a gap between parts passes through exactly as the client renders it. Reuses
    // HullRayEntry per part; the caller's `bestT` plumbing still picks the closest target overall.
    private bool BaseHullsRayEntry(Vec3 center, byte baseTypeId, Vec3 mp, Vec3 mv, float margin, float maxT, out float t)
    {
        t = maxT;
        bool hit = false;
        var subs = World.BaseSubHullsOf(baseTypeId);
        for (int i = 0; i < subs.Length; i++)
            if (HullRayEntry(subs[i], center, Quat.Identity, 1f, mp, mv, margin, t, out float th) && th < t)
            {
                t = th;
                hit = true;
            }
        return hit;
    }

    // Resolve a ship against every DEPLOYABLE solid body in its sector. A deployable is a small,
    // stationary, low-HP object a ship can bounce off AND wreck by ramming — unlike a base (which uses
    // the base-health system, and is only DESTROYED via that system), a deployable dies from the same
    // collision/weapon damage anything else does. Recon probes are the only deployable today; a future
    // drop-turret / sensor buoy / decoy drone slots in here the SAME way: iterate its live list,
    // resolve each element through the ResolveDeployableSphere kernel, and feed the returned impact
    // damage into that deployable's own HP sink. Keep the generic bounce in the kernel; keep only the
    // per-type "which list / which radius / which damage sink" here. O(ships × deployables); few of each.
    private void ResolveDeployableCollisions(ShipSim s, uint tick)
    {
        // Recon probes (Simulation.Probes.cs): solid sphere = the combat hit radius, so "what you
        // shoot is what you bump"; a hard ram spends the impact on the probe's low HP → gone reason 2.
        for (int i = 0; i < _probes.Count; i++)
        {
            var p = _probes[i];
            if (p.SectorId != s.SectorId)
                continue;
            if (!WeaponDefs.TryGetValue(p.WeaponId, out var w) || w.ProbeHitRadius <= 0f)
                continue;
            float dmg = ResolveDeployableSphere(s, p.Pos, w.ProbeHitRadius, tick);
            if (dmg > 0f && p.Health > 0f) // Health 0 = authored-invulnerable: solid but undamageable
            {
                DamageProbe(p, dmg, tick);
                if (p.Health <= 0f)
                    i--; // DamageProbe removed p from _probes; don't skip the next entry
            }
        }
    }

    // Bounce a ship off ONE solid deployable sphere (the shared kernel behind ResolveDeployableCollisions).
    // Pushes the ship out of penetration (solid) and, on a closing contact, applies collision damage to
    // the SHIP (exactly like a base) and RETURNS that same damage so the caller can also spend it on the
    // deployable's own HP. Returns 0 on no contact or a below-min-speed kiss. Symmetric across teams —
    // you bounce off (and can wreck) your own deployables too.
    private float ResolveDeployableSphere(ShipSim s, Vec3 center, float radius, uint tick)
    {
        if (radius <= 0f)
            return 0f;
        if (
            Collide.ResolveStaticSphere(
                ref s.State,
                World.ShipRadius,
                center,
                radius,
                World.CollisionRestitution,
                out float vn
            )
            && vn < 0f
        )
        {
            float dmg = CollisionDamage(-vn, _combat.CollisionDamageScale);
            ApplyDamage(s, dmg, tick);
            return dmg;
        }
        return 0f;
    }

    // Sphere-vs-convex-hull bounce (the convex analogue of ResolveStaticCollision). The hull is
    // in its own authored frame at (center, rot, uniform scale); SphereVsHull maps the ship sphere
    // into that frame, resolves against the nearest face, and maps the contact back to world.
    private void ResolveHullCollision(ShipSim s, ConvexHull hull, Vec3 center, Quat rot, float scale)
    {
        if (Collide.SphereVsHull(s.State.Pos, World.ShipRadius, hull, center, rot, scale, out Vec3 n, out float pen))
            BounceShip(s, n, pen);
    }

    // Bounce a ship off a contact: shared kinematic push-out + velocity reflect (Collide.Bounce),
    // then the SERVER-ONLY collision damage from the inbound normal speed. Shared by
    // ResolveHullCollision (asteroids, enemy base) and the friendly-base solid-shell branch. The
    // client runs Collide.Bounce too (no damage — health is server-authoritative).
    private void BounceShip(ShipSim s, Vec3 worldNormal, float worldPenetration)
    {
        s.LastCollisionTick = _tick;
        Collide.Bounce(ref s.State, worldNormal, worldPenetration, World.CollisionRestitution, out float vn);
        if (vn < 0f)
            ApplyDamage(s, CollisionDamage(-vn, _combat.CollisionDamageScale), _tick);
    }

    // Ray (mp + mv·t) first-entry time against a transformed hull, expanded by `margin`. Maps the
    // ray into hull-local space; t is invariant under the rigid+uniform-scale transform.
    private static bool HullRayEntry(
        ConvexHull hull,
        Vec3 center,
        Quat rot,
        float scale,
        Vec3 mp,
        Vec3 mv,
        float margin,
        float maxT,
        out float t
    )
    {
        t = 0f;
        if (scale <= 1e-6f)
            return false;
        float inv = 1f / scale;
        Quat rotInv = rot.Conjugate();
        Vec3 o = rotInv.Rotate(mp - center) * inv;
        Vec3 dir = rotInv.Rotate(mv) * inv;
        return hull.RayEntry(o, dir, maxT, margin * inv, out t);
    }

    // Sphere-vs-sphere static bounce fallback (a rock without a hull, or a base without a model):
    // shared kinematic (Collide.ResolveStaticSphere) + server-only collision damage.
    private void ResolveStaticCollision(ShipSim s, Vec3 center, float radius)
    {
        if (
            !Collide.ResolveStaticSphere(
                ref s.State,
                World.ShipRadius,
                center,
                radius,
                World.CollisionRestitution,
                out float vn
            )
        )
            return;
        s.LastCollisionTick = _tick;
        if (vn < 0f)
            ApplyDamage(s, CollisionDamage(-vn, _combat.CollisionDamageScale), _tick);
    }

    // Server-only collision damage from a closing normal speed (m/s, always positive). Below
    // the authored collision-damage-min-speed it's a harmless kiss: 0 damage (the bounce still ran). Above it,
    // scaled and capped at max-collision-damage. Shared by ship-ship, hull, and sphere-fallback bounces.
    private float CollisionDamage(float closingSpeed, float scale) =>
        closingSpeed > _combat.CollisionDamageMinSpeed ? MathF.Min(closingSpeed * scale, _combat.MaxCollisionDamage) : 0f;

    // ---- Warp (module TryWarp): emerge out the partner mouth toward the dest sector
    // center, jittered by a small random cone so successive ships fan out instead of
    // stacking in a line. Server-authoritative RNG — clients read the result, never
    // reproduce it. The funnel discards heading; only raw speed carries through. ----

    private void TryWarp(ShipSim s)
    {
        foreach (var g in World.Alephs)
        {
            if (g.SectorId != s.SectorId)
                continue;
            float rr = _mech.AlephTriggerRadius + World.ShipRadius;
            if ((s.State.Pos - g.Pos).LengthSquared() > rr * rr)
                continue;

            float speed = s.State.Vel.Length();
            Vec3 mouth = g.PartnerPos * -1f; // toward the dest sector center (origin)
            float mlen = mouth.Length();
            Vec3 m = mlen > 0.001f ? mouth * (1f / mlen) : new Vec3(0f, 1f, 0f);

            // Jitter around the mouth axis (per-axis ±warp-exit-jitter), then renormalize so
            // ships emerging together spread into a cone rather than overlapping on one line.
            Vec3 e = new Vec3(
                m.X + (float)(_rng.NextDouble() * 2.0 - 1.0) * _mech.WarpExitJitter,
                m.Y + (float)(_rng.NextDouble() * 2.0 - 1.0) * _mech.WarpExitJitter,
                m.Z + (float)(_rng.NextDouble() * 2.0 - 1.0) * _mech.WarpExitJitter
            );
            float elen = e.Length();
            e = elen > 1e-4f ? e * (1f / elen) : m;

            float exit = _mech.AlephTriggerRadius + World.ShipRadius + _mech.WarpExitOffset;
            s.SectorId = g.DestSectorId;
            s.State.Pos = g.PartnerPos + e * exit;
            s.State.Vel = e * speed;
            // Emerge facing out of the aleph: point ship-local forward (+Z) along the
            // exit direction and drop any residual spin, so the ship comes through
            // pointed the way it's travelling instead of keeping its pre-warp heading.
            s.State.Rot = LookRotationZ(e);
            s.State.AngVel = default;
            // Fog: immediately scout the rocks around the arrival point (same tick) so gate-exit
            // surroundings reveal now instead of at the next 2 Hz vision boundary (Simulation.Vision.cs, F8).
            WarpDiscoverRocks(s);
            // Fog: a ship physically arriving in a sector reveals it (belt-and-braces — the gate
            // discovery usually already did via both aleph endpoints). Immediate write is safe
            // here, unlike DiscoveredRocks: the vision worker never reads DiscoveredSectors (only
            // the lock-holding Welcome/reveal builders do), so no _warpRevealPending deferral.
            if (FogEnabled && _teamVisions.TryGetValue(s.Team, out var wtv))
                lock (wtv.DiscoverLock)
                {
                    if (wtv.DiscoveredSectors.Add(g.DestSectorId))
                        wtv.RevealLogSectors.Add(g.DestSectorId);
                }
            return;
        }
    }

    // Shortest-arc rotation that aligns ship-local forward (+Z) with `dir` (unit),
    // with minimal roll. a=(0,0,1): cross(a,dir)=(-dir.Y,dir.X,0), dot(a,dir)=dir.Z.
    private static Quat LookRotationZ(Vec3 dir)
    {
        float d = dir.Z;
        // Antiparallel (facing -Z): the formula degenerates; spin 180° about X instead.
        if (d < -0.99999f)
            return new Quat(1f, 0f, 0f, 0f);
        return new Quat(-dir.Y, dir.X, 0f, 1f + d).Normalized();
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
