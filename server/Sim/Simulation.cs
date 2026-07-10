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

    // A class's primary GUN — the first Bolt-kind muzzle, or the Scout gun if the hull carries no
    // bolt weapon (missile racks are ignored). Drives the PIG threat heuristic + the gun cadence
    // gate; single-sourced from the same muzzles/defs the sim fires from.
    private WeaponDef PrimaryWeapon(byte cls)
    {
        var m = cls < ClassMuzzles.Length ? ClassMuzzles[cls] : System.Array.Empty<Muzzle>();
        foreach (var mz in m)
            if (WeaponDefs.TryGetValue(mz.WeaponId, out var wd) && wd.Kind == WeaponKind.Bolt)
                return wd;
        return WeaponDefs[GameContent.ScoutWeaponId];
    }

    // The first missile mount for a class + its projected WeaponDef, or null if the hull has none.
    // Used to init a spawn's ammo and by TryFireMissile/UpdateLock (the missile launch/lock path).
    private (Muzzle mount, WeaponDef w)? MissileMountFor(byte cls)
    {
        var mounts = cls < ClassMissileMounts.Length ? ClassMissileMounts[cls] : System.Array.Empty<Muzzle>();
        if (mounts.Length == 0)
            return null;
        return (mounts[0], WeaponDefs[mounts[0].WeaponId]);
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
        public ShipInputState HeldInput; // replayed on ticks with no exact-stamped input
        public bool Alive;
        public uint RespawnAtTick; // when !Alive

        // AI combat drone — server-driven via the PIG brain (Simulation.Pig.cs), not client
        // input. An escape pod ejected on death — slow, unarmed, flown by its owner (player
        // pod) or auto-flown home by PodThink (PIG pod). A ship is at most one of these.
        public bool IsPig;
        public bool IsPod;

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

    private readonly record struct PendingShot(ulong TargetShipId, int BaseIndex, float Damage, float ShieldMult, ulong TargetProbeId = 0);

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
    private readonly Queue<(int clientId, byte team, byte cls, (uint cargoId, byte count)[] cargo)> _joinQueue = new();
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

    // Remembered join class/team/cargo per connected client, so a respawn re-creates the same ship
    // with the same validated consumable hold.
    private readonly Dictionary<int, (byte team, byte cls, (uint cargoId, byte count)[] cargo)> _clientInfo = new();

    // Chaff/mine dispenser WeaponDefs keyed by the cargo id they consume (D8 — dispensers are not
    // hardpoint-mounted; a spawn's cargo id names which dispenser its ammo feeds). Cargo item mass
    // by id, for the spawn-time payload validation. Built once from Content in the ctor.
    private readonly Dictionary<uint, WeaponDef> _dispenserByCargo = new();
    private readonly Dictionary<uint, float> _cargoMass = new();
    private readonly Dictionary<uint, byte> _chargesPerPack = new(); // dispenser ammo = packs × this

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
    // hull's authored DefaultCargo is seeded at spawn.
    public void EnqueueJoin(int clientId, byte team, byte cls, (uint cargoId, byte count)[] cargo)
    {
        lock (_qLock)
            _joinQueue.Enqueue((clientId, team, cls, cargo ?? System.Array.Empty<(uint, byte)>()));
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
            AccrueTeamCredits(tick); // Stage-2: flat per-team credit paycheck every PaycheckTicks
            // Fog of war: 2 Hz per-team vision (apply previous kick + kick next, Simulation.Vision.cs).
            // Off the sim tick's critical path — only ever delays the NEXT vision result, never a tick.
            if (FogEnabled && tick % VisionEvery == 0)
                VisionStep(tick);
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

        // Pass C: enemy ship-vs-ship collisions (mass-weighted impulse, module-identical),
        // O(n²) over live ships — 200 ships = 20k pairs, trivial natively.
        for (int i = 0; i < _order.Count; i++)
        {
            var a = _order[i];
            for (int j = i + 1; j < _order.Count; j++)
            {
                var b = _order[j];
                if (a.Team == b.Team || a.SectorId != b.SectorId)
                    continue;
                CollideShips(a, b);
            }
        }

        // Boundary erosion, asteroid/base bounces, docking, death resolution. Structural
        // changes (pod ejection, despawn, dock) are deferred via _toRemove/_toAdd so we
        // don't mutate _order while iterating it.
        foreach (var s in _order)
        {
            float over = s.State.Pos.Length() - World.SectorRadius(s.SectorId);
            if (over > 0f)
                ApplyDamage(s, MathF.Min(_combat.BoundaryBaseDps + over * _combat.BoundaryRampDps, _combat.BoundaryMaxDps) * dt, tick);

            ResolveAsteroidCollisions(s);
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
                    ResolveBaseCollision(s, b.Pos); // enemy base: fully solid hull
                    continue;
                }
                // Own base: with a loaded hull you dock ONLY by flying your ship into a rectangular
                // docking door (a bounded face authored as a group of 5 HP_DockingEntrance markers) —
                // the rest of the base is a solid hull that bounces you. Without a model, fall back to
                // the legacy core-sphere dock so docking can't break.
                Vec3 d = s.State.Pos - b.Pos;
                if (World.BaseHull is not null)
                {
                    if (Collide.IntersectsDockFace(d, World.BaseDockFaces, World.DockFaceDepth, World.ShipRadius))
                    {
                        DockShip(s, tick); // intersected a rectangular docking door
                        docked = true;
                        break;
                    }
                    // Solid shell everywhere else: the DEEPEST contact across the authored compound
                    // sub-hulls, resolved through the shared Collide.SphereVsBody kernel so the client
                    // predicts the identical bounce (same contact-selection rule — deepest wins). A
                    // partless base collapses to the single merged hull, matching the old behaviour.
                    if (Collide.SphereVsBody(s.State.Pos, World.ShipRadius, BaseBody(b.Pos), out Vec3 bn, out float bpen))
                        BounceShip(s, bn, bpen);
                }
                else
                {
                    float dockR = World.BaseRadius * DockRadiusFrac;
                    if (d.LengthSquared() <= dockR * dockR)
                    {
                        DockShip(s, tick);
                        docked = true;
                        break;
                    }
                }
            }
            if (docked)
                continue;

            if (s.Health <= 0f)
                ResolveDeath(s, tick);
        }
        ApplyStructural();

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

    private void DrainQueues(uint tick)
    {
        lock (_qLock)
        {
            while (_joinQueue.Count > 0)
            {
                var (cid, team, cls, cargo) = _joinQueue.Dequeue();
                // Remember the slot (team/cls/hold) and spawn this very step (ProcessRespawns, tick now).
                _clientInfo[cid] = (team, cls, cargo);
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
                if (_heldOrphans.Remove(token, out var orphan)
                    && _byClient.Remove(orphan.oldClientId, out var ship))
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
        }
    }

    // Restore a fresh lobby on an empty server (called from the sim loop a grace period after
    // the last client leaves). Tears the match down to a clean idle Lobby so the next handoff
    // readies up afresh, and the server sits idle until then.
    public void ResetMatch() => ReturnToLobby();

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
        Array.Fill(World.BaseHealth, World.BaseMaxHealth);
        BasesChangedThisStep = true;
        Phase = PhaseActive;
        Winner = NoWinner;
        _matchDirty = false;
        foreach (var ring in _shotRing)
            ring.Clear();
        // Fresh economy each match: reset every team to its starting credits + base unlocks.
        World.SeedEconomy(Content.Start);
        ResolveTeamUnlocks();
        ResetVision(); // clear/reseed per-team fog vision, drain any in-flight compute (Simulation.Vision.cs)
        TeamStateChangedThisStep = true;
        Log.MatchStarted(_log);
        // World may have just been swapped to a new map — let the hub re-Welcome every client onto it
        // and invalidate its world-derived caches. Runs on the sim thread; Welcome frames are queued
        // before AfterStep streams the first Active snapshot, so clients rebuild geometry in order.
        OnMatchStart?.Invoke();
    }

    // Resolve each team's buildable hulls from its owned techs/capabilities (the Stage-2 unlock hook,
    // riding the library's forward-closure so it stays correct when Stage-4 grants techs mid-match).
    // Runs at match start — Stage-2 teams don't gain techs mid-match and spawns only happen while
    // Active. The spawn gate (TryReserveSpawn) and the wire snapshot both read the resulting set.
    private void ResolveTeamUnlocks()
    {
        foreach (var ts in World.TeamStates.Values)
            ts.UnlockedClasses = Allegiance.Factions.Resolution.BuildableResolver
                .GetBuildables(Content.Catalog, ts.OwnedTechs, ts.OwnedCapabilities)
                .OfType<Allegiance.Factions.Model.Hull>()
                .Where(h => h.ClassId is not null)
                .Select(h => h.ClassId!.Value)
                .ToHashSet();
    }

    // -> Lobby. Tears down every ship (players + drones), refills bases, clears the win state
    // and shot ring, and lets the hub clear ready flags. Called a few seconds after a match
    // ends and whenever the server empties out.
    public void ReturnToLobby()
    {
        DespawnAllPigs();
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
        Array.Fill(World.BaseHealth, World.BaseMaxHealth);
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
    private ShipSim SpawnCombatShip(int clientId, byte team, byte cls, uint tick, (uint cargoId, byte count)[] cargo)
    {
        var s = new ShipSim
        {
            ShipId = _nextShipId++,
            OwnerClientId = clientId,
            Team = team,
            Class = cls,
            Alive = true,
        };
        PlaceAtBase(s, World.ShipRadius, tick);
        s.State.Mass = StatsFor(cls, false).Mass;
        s.State.Fuel = StatsFor(cls, false).MaxFuel; // dock-refill: dock despawns, relaunch = full tank
        s.Health = HullFor(cls);
        s.Shield = ShieldsEnabled ? ShieldCapacityFor(s) : 0f; // full shield at spawn; relaunch = full recharge
        s.ShieldDamageTick = 0;
        s.SigBias = ShieldDefFor(s).SignatureBias; // projected default-loadout signature bias

        if (MissileMountFor(cls) is (_, WeaponDef mw)) // full magazine at spawn (no rearm yet)
            s.MissileAmmo = mw.MagazineSize;
        // D7: remember what the team paid for this hull (TryReserveSpawn just deducted it) so a
        // voluntary dock can refund it. PIGs/pods never go through here, so they keep PaidCost 0.
        s.PaidCost = ShipDefs.TryGetValue(cls, out var cd) ? cd.Cost : 0;
        // D6/D9: seed the chaff/mine dispenser ammo from the validated spawn cargo (empty ⇒ hull default).
        SeedDispenserAmmo(s, cls, cargo);
        _ships[s.ShipId] = s;
        _order.Add(s);
        if (clientId >= 0)
            _byClient[clientId] = s;
        return s;
    }

    // Validate a requested consumable hold and seed the ship's dispenser ammo/weapon-ids from it.
    // A cargo id must resolve to a Chaff/Mine dispenser WeaponDef, and mounted-weapon mass + the
    // hold's mass must fit PayloadCapacity — otherwise the whole request is rejected (logged) and the
    // hull's authored DefaultCargo is used instead.
    private void SeedDispenserAmmo(ShipSim s, byte cls, (uint cargoId, byte count)[] cargo)
    {
        var chosen = ResolveCargo(cls, cargo);
        foreach (var (cargoId, count) in chosen)
        {
            if (!_dispenserByCargo.TryGetValue(cargoId, out var w))
                continue;
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

    // Resolve the hold to seed: the requested cargo if it's valid (all ids are dispenser cargo AND
    // mounted-weapon mass + hold mass ≤ PayloadCapacity), else the hull's authored DefaultCargo.
    private (uint cargoId, byte count)[] ResolveCargo(byte cls, (uint cargoId, byte count)[] requested)
    {
        ShipDefs.TryGetValue(cls, out var def);
        (uint, byte)[] fallback = def is null
            ? System.Array.Empty<(uint, byte)>()
            : def.DefaultCargo.Select(c => (c.CargoId, c.Count)).ToArray();
        if (requested is null || requested.Length == 0)
            return fallback;

        float used = 0f;
        if (def is not null)
            foreach (var h in def.Hardpoints)
                if (h.Kind == HardpointKind.Weapon && WeaponDefs.TryGetValue(h.WeaponId, out var wm))
                    used += wm.Mass;
        foreach (var (cargoId, count) in requested)
        {
            if (!_dispenserByCargo.ContainsKey(cargoId))
            {
                Log.SpawnCargoNotDispenser(_log, cargoId);
                return fallback;
            }
            used += count * (_cargoMass.TryGetValue(cargoId, out var m) ? m : 0f);
        }
        float cap = def?.PayloadCapacity ?? 0f;
        if (used > cap)
        {
            Log.SpawnCargoPayloadExceeds(_log, used, cap);
            return fallback;
        }
        return requested;
    }

    // Position a ship just outside its team base, launched out of the base's DOCKING-EXIT
    // hardpoint (World.BaseExitDir, from the GLB). Without a loaded model it falls back to the
    // pre-hull behavior: outward toward the sector center. `clearance` is added past the base
    // radius so the spawn sits clear of the solid shell (won't instantly re-dock).
    private void PlaceAtBase(ShipSim s, float clearance, uint tick)
    {
        Vec3 basePos = default;
        uint sector = World.DefaultSector;
        foreach (var b in World.Bases)
            if (b.Team == s.Team)
            {
                basePos = b.Pos;
                sector = b.SectorId;
                break;
            }

        Vec3 outward;
        Quat rot;
        Vec3 spawnPos;
        if (World.BaseHull is not null)
        {
            // Catapult out of the exit cone: start at its base disc (the DockingExit hardpoint) and
            // fling along the cone axis toward the tip, nudged out by `clearance` so the ship clears
            // the bay mouth. (The cone base sits at the hull surface, so any residual overlap is a
            // benign outward pop — ApplyBounce never damages a ship already moving outward.)
            outward = World.BaseExitDir;
            rot = LookRotationZ(outward);
            spawnPos = basePos + World.BaseExitPos + outward * clearance;
        }
        else
        {
            float dirLen = basePos.Length();
            outward = dirLen > 1e-3f ? basePos * (-1f / dirLen) : new Vec3(0f, 0f, 1f);
            float yaw = MathF.Atan2(-basePos.X, -basePos.Z);
            rot = new Quat(0f, MathF.Sin(yaw * 0.5f), 0f, MathF.Cos(yaw * 0.5f));
            spawnPos = basePos + outward * (World.BaseRadius + clearance);
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
            // Stage-2 economy gate: a locked or unaffordable buy is dropped (no ship, no charge,
            // no reschedule). The client's spawn-pending retry times out and its pre-check stops it
            // re-spamming a request it can predict will fail.
            if (TryReserveSpawn(info.team, info.cls) != SpawnDecision.Allowed)
                continue;
            SpawnCombatShip(cid, info.team, info.cls, tick, info.cargo);
        }
    }

    public enum SpawnDecision { Allowed, Locked, TooPoor }

    // Authoritative spawn gate + charge (the buy seam): reject if the requested hull is locked for
    // this team or it can't afford the cost, otherwise deduct the cost and allow. Deduct and check
    // happen at the same authoritative moment (spawn time), so credits checked == credits charged.
    // Authority is bootstrap-simple (any-player-spends / auto) — no commander.
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

        int slot = (int)(tick % InputRingSize);
        if (s.InputRingTick[slot] == tick)
            s.HeldInput = s.InputRing[slot];
        return s.HeldInput;
    }

    // Dispatch a ship that reached 0 health: a pod just vanishes (player pod -> respawn
    // scheduled; PIG pod -> slot freed), a PIG combat drone ejects a PIG pod, a player combat
    // ship ejects a player-flown escape pod. All deferred (collected in _toRemove/_toAdd).
    private void ResolveDeath(ShipSim s, uint tick)
    {
        if (s.IsPod)
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
            IsPod = true,
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
        // Dock refund (D7): a voluntary dock returns the hull's paid cost to the team, so dock→relaunch
        // is a net-free full rearm/repair. Only real player hulls that actually paid (PaidCost>0)
        // refund — pods never inherit PaidCost via MakePod and PIGs pay nothing, so death refunds
        // nothing. Capped at exactly what was paid (zeroed after) → no exploit.
        if (!s.IsPod && !s.IsPig && s.OwnerClientId >= 0 && s.PaidCost > 0
            && World.TeamStates.TryGetValue(s.Team, out var ts))
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

        // Primary fire is the GUNS: one cadence per ship, gated off the first Bolt muzzle's weapon
        // (missile racks have their own cadence in TryFireMissile). A hull with no gun (only racks)
        // has nothing to fire here. Per-weapon cadence arrives with mixed loadouts (Stage 2).
        // TryGetValue skips an empty/unbound mount (WeaponId == HardpointDef.NoWeapon never
        // resolves) — same guard every other muzzle consumer uses (PrimaryWeapon, ClassMissileMounts).
        WeaponDef? primary = null;
        foreach (var mz in muzzles)
            if (WeaponDefs.TryGetValue(mz.WeaponId, out var pwd) && pwd.Kind == WeaponKind.Bolt)
            {
                primary = pwd;
                break;
            }
        if (primary is null)
            return; // no gun on this hull — primary fire is a no-op (missiles fire via Firing2)
        if (tick - ship.LastFireTick < primary.FireIntervalTicks && ship.LastFireTick != 0)
            return;
        ship.LastFireTick = tick;

        // One shot per muzzle, in hardpoint order (the Fighter's twin cannons fire together). Each
        // muzzle fires its own weapon, dispatched by kind. IMPORTANT: the loop still visits missile
        // mounts and KEEPS the hardpoint-array index as `barrel` (the per-barrel spread seed) — it
        // just skips firing a bolt for them. The client's SpawnBoltFor mirrors this exactly so the
        // gun barrel seeds stay aligned on both sides regardless of where racks sit in the array.
        for (byte barrel = 0; barrel < muzzles.Length; barrel++)
        {
            if (!WeaponDefs.TryGetValue(muzzles[barrel].WeaponId, out var w))
                continue; // empty/unbound mount (NoWeapon) — assignable slot, nothing to fire
            switch (w.Kind)
            {
                case WeaponKind.Bolt:
                    FireBolt(ship, tick, w, muzzles[barrel], barrel);
                    break;
                case WeaponKind.Missile:
                    break; // racks are fired by Firing2 (TryFireMissile), not primary fire
            }
        }
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
                bool hit = World.BaseHull is not null
                    ? BaseHullsRayEntry(b.Pos, mp, mv, World.ProjectileRadius, bestT, out float t)
                    : FirstEntryTime(mp, mv, b.Pos, default, World.BaseRadius + World.ProjectileRadius, bestT, out t);
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
                    if (s.Team == ship.Team || !s.Alive)
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
                    // Bounding-sphere pre-test, then the rock's convex hull if it has one.
                    float r = a.Radius * World.AsteroidCollisionScale + World.ProjectileRadius;
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
        for (int i = 0; i < _probes.Count; i++)
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
            _shotRing[(tick + resolveTicks) % ShotRingSize].Add(new PendingShot(targetShip, targetBase, w.Damage, w.ShieldMult, targetProbe));
        }
    }

    // ---- Guided missiles (server-authoritative lock + launch + turn-rate pursuit) ----

    // Advance this ship's missile lock timer from its input's LockTargetId. A hull with no rack
    // never locks (early out). A valid target (alive enemy ship — NOT a pod — in the same sector,
    // within LockRange, inside the LockAngle nose cone) advances progress; anything else resets it.
    // Progress reaching LockTicks latches Locked. Also bakes the wire LockState byte.
    private void UpdateLock(ShipSim ship, in ShipInputState input, uint tick)
    {
        if (MissileMountFor(ship.Class) is not (_, WeaponDef w))
            return; // no launcher on this hull — never locks

        ship.LockTargetId = input.LockTargetId; // mirror the client's requested target
        bool valid = false;
        ShipSim? threatTarget = null; // the ship being locked (A2 being-locked warning), if any
        if (GameContent.IsBaseLock(input.LockTargetId))
        {
            // Base lock: only a CanDamageBase weapon may lock a base at all (D3); otherwise
            // reuse the exact same range/cone test as a ship target, aimed at the base's pos.
            if (
                w.CanDamageBase
                && TryGetLockableBase(input.LockTargetId, ship.Team, ship.SectorId, out int bi)
            )
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
        if (MissileMountFor(ship.Class) is not (Muzzle mount, WeaponDef w))
            return;
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
                bool bhit = World.BaseHull is not null
                    ? BaseHullsRayEntry(b.Pos, mp, vel, 0f, bestT, out float bt)
                    : FirstEntryTime(mp, vel, b.Pos, default, World.BaseRadius, bestT, out bt);
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
                                HullRayEntry(sb.Hull, s.State.Pos, s.State.Rot, 1f, mp, vrel, w.ProjectileRadius, bestT, out float th)
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
                        if (!FirstEntryTime(mp, vel, a.Pos, default, a.Radius + w.ProjectileRadius, bestT, out _))
                            continue;
                        float r = a.Radius * World.AsteroidCollisionScale + w.ProjectileRadius;
                        bool hit = World.RockBodies.TryGetValue(a.Id, out var rbody)
                            ? HullRayEntry(rbody.Hull, a.Pos, rbody.Rot, rbody.Scale, mp, vel, w.ProjectileRadius, bestT, out float t)
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
                if (hitShip != 0 && _ships.TryGetValue(hitShip, out var victim) && victim.Alive)
                    ApplyDamage(victim, w.Damage * w.DirectHitMult, tick, w.ShieldMult); // end-of-step death pass resolves 0 health
                else if (hitBase >= 0 && w.CanDamageBase)
                    ApplyBaseDamage(hitBase, w.Damage * w.DirectHitMult, tick); // blast never touches the base
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
    private void ApplyBlast(byte team, WeaponDef w, Vec3 hitPos, ulong directHitShip, Dictionary<(int, int, int), List<ShipSim>>? shipGrid, uint tick, uint sector)
    {
        if (w.BlastRadius <= 0f || w.BlastPower <= 0f)
            return;
        float fuseR = w.ProjectileRadius;

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
            DamageProbe(p, w.BlastPower * f, tick);
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
                        ApplyDamage(s, w.BlastPower * falloff, tick, w.ShieldMult); // end-of-step death pass resolves 0 health
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

    // Apply damage to a base (health floor at 0), flag it as changed/dirty, and — the first time
    // it drops to 0 — latch the match end (winner = the OTHER team, i.e. the side that destroyed
    // it) and schedule the return-to-lobby. Shared by the bolt path (ResolveDueShots) and missile
    // detonation (StepMissiles) so both apply base damage identically.
    private void ApplyBaseDamage(int baseIndex, float damage, uint tick)
    {
        float hp = MathF.Max(0f, World.BaseHealth[baseIndex] - damage);
        World.BaseHealth[baseIndex] = hp;
        BasesChangedThisStep = true;
        _matchDirty = true;
        if (hp <= 0f && Phase != PhaseEnded)
        {
            byte loser = World.Bases[baseIndex].Team;
            Winner = (byte)(loser == 0 ? 1 : 0);
            Phase = PhaseEnded;
            JustEnded = true;
            _returnToLobbyAtTick = tick + EndedToLobbyTicks;
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

    // Enemy ship-vs-ship contact. With both ships' GLB hulls loaded the contact is resolved as a
    // ShipRadius sphere against the OTHER ship's convex hull (the same kernel asteroids/bases use),
    // so a long bomber or a wide fighter collides on its real silhouette; without hulls it falls
    // back to the legacy equal-radius sphere overlap. Either way the resolution is the module's
    // mass-weighted impulse + inverse-mass-split push-out along the contact normal n (b → a).
    private void CollideShips(ShipSim a, ShipSim b)
    {
        var ha = World.ShipHull(a.Class, a.IsPod);
        var hb = World.ShipHull(b.Class, b.IsPod);

        Vec3 n;
        float pen;
        if (ha is null && hb is null)
        {
            if (!ShipSphereContact(a, b, out n, out pen))
                return;
        }
        else if (!ShipHullContact(a, b, ha, hb, out n, out pen))
        {
            return;
        }

        ResolveShipImpulse(a, b, n, pen);
    }

    // Legacy equal-radius sphere overlap. n points b → a (the separation axis), pen is the overlap.
    private static bool ShipSphereContact(ShipSim a, ShipSim b, out Vec3 n, out float pen)
    {
        Vec3 d = a.State.Pos - b.State.Pos;
        float dist2 = d.LengthSquared();
        float minD = 2f * World.ShipRadius;
        if (dist2 >= minD * minD)
        {
            n = default;
            pen = 0f;
            return false;
        }
        float dist = MathF.Sqrt(dist2);
        n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
        pen = minD - dist;
        return true;
    }

    // Hull-aware contact: each ship's center, as a ShipRadius sphere, tested against the other's
    // hull; the deeper of the two contacts wins (the convex analogue of the sphere overlap). n is
    // oriented b → a so the shared impulse step pushes them apart correctly.
    private static bool ShipHullContact(
        ShipSim a,
        ShipSim b,
        World.ShipBody? ha,
        World.ShipBody? hb,
        out Vec3 n,
        out float pen
    )
    {
        n = default;
        pen = 0f;

        // Broad-phase: the two world bounding spheres (hull bound, or ShipRadius without a hull).
        float ra = ha?.BoundingRadius ?? World.ShipRadius;
        float rb = hb?.BoundingRadius ?? World.ShipRadius;
        float bound = ra + rb;
        if ((a.State.Pos - b.State.Pos).LengthSquared() >= bound * bound)
            return false;

        // a's center vs b's hull → normal already points out of b toward a (= b → a).
        if (
            hb is World.ShipBody bbody
            && Collide.SphereVsHull(
                a.State.Pos,
                World.ShipRadius,
                bbody.Hull,
                b.State.Pos,
                b.State.Rot,
                1f,
                out Vec3 nB,
                out float pB
            )
        )
        {
            n = nB;
            pen = pB;
        }
        // b's center vs a's hull → normal points out of a toward b (a → b); negate to b → a.
        if (
            ha is World.ShipBody abody
            && Collide.SphereVsHull(
                b.State.Pos,
                World.ShipRadius,
                abody.Hull,
                a.State.Pos,
                a.State.Rot,
                1f,
                out Vec3 nA,
                out float pA
            )
            && pA > pen
        )
        {
            n = nA * -1f;
            pen = pA;
        }
        return pen > 0f;
    }

    // Module-identical mass-weighted bounce: restitution impulse + collision damage when closing,
    // and an inverse-mass-split positional correction along n (which points b → a).
    private void ResolveShipImpulse(ShipSim a, ShipSim b, Vec3 n, float pen)
    {
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
                // Cheap bounding-sphere reject (rock.Radius is the visual/world bound), then the
                // convex hull if this rock has one — else the legacy sphere.
                Vec3 dd = s.State.Pos - a.Pos;
                float bound = a.Radius + World.ShipRadius;
                if (dd.LengthSquared() >= bound * bound)
                    continue;
                if (World.RockBodies.TryGetValue(a.Id, out var body))
                {
                    // Live tumble: compose the spawn pose with the spin at the current tick so the
                    // authoritative hull matches the rendered rock (Collide.RockRotationAt, shared).
                    Quat rot = Collide.RockRotationAt(body.Rot, body.SpinAxis, body.SpinSpeed, _tick * FlightModel.Dt);
                    ResolveHullCollision(s, body.Hull, a.Pos, rot, body.Scale);
                }
                else
                    ResolveStaticCollision(s, a.Pos, a.Radius * World.AsteroidCollisionScale);
            }
        }
    }

    // Bounce a ship off a base: the loaded compound world hull if present, else the legacy radius
    // sphere. Runs through the shared Collide.SphereVsBody kernel over the authored sub-hulls (deepest
    // contact = one BounceShip), so an enemy base bounces exactly as the client predicts it.
    private void ResolveBaseCollision(ShipSim s, Vec3 center)
    {
        if (World.BaseHull is not null)
        {
            if (Collide.SphereVsBody(s.State.Pos, World.ShipRadius, BaseBody(center), out Vec3 n, out float pen))
                BounceShip(s, n, pen);
        }
        else
            ResolveStaticCollision(s, center, World.BaseRadius);
    }

    // The base as a shared compound StaticBody at `center`: `BaseHull` is the merged shrink-wrap (kept
    // non-null for the struct + broadphase parity), `BaseSubHulls` are the authored parts the kinematic
    // kernel actually resolves against. Team/discs are irrelevant to SphereVsBody (it never gates on
    // them — dock carve-out is handled by the caller), so a fixed team/the discs are passed for shape.
    // Callers guard World.BaseHull is not null first.
    private Collide.StaticBody BaseBody(Vec3 center) =>
        Collide.StaticBody.BaseHull(World.BaseHull!, World.BaseSubHulls, center, 0, World.BaseDockFaces);

    // Min-entry-t of a ray (mp + mv·t) across the base's authored sub-hulls (world-scaled, identity
    // frame), the ray analogue of SphereVsBody: the CLOSEST sub-hull surface stops the bolt/missile, so
    // a shot threading a gap between parts passes through exactly as the client renders it. Reuses
    // HullRayEntry per part; the caller's `bestT` plumbing still picks the closest target overall.
    private bool BaseHullsRayEntry(Vec3 center, Vec3 mp, Vec3 mv, float margin, float maxT, out float t)
    {
        t = maxT;
        bool hit = false;
        var subs = World.BaseSubHulls;
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
            Collide.ResolveStaticSphere(ref s.State, World.ShipRadius, center, radius, World.CollisionRestitution, out float vn)
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
            Collide.ResolveStaticSphere(ref s.State, World.ShipRadius, center, radius, World.CollisionRestitution, out float vn)
            && vn < 0f
        )
            ApplyDamage(s, CollisionDamage(-vn, _combat.CollisionDamageScale), _tick);
    }

    // Server-only collision damage from a closing normal speed (m/s, always positive). Below
    // the authored collision-damage-min-speed it's a harmless kiss: 0 damage (the bounce still ran). Above it,
    // scaled and capped at max-collision-damage. Shared by ship-ship, hull, and sphere-fallback bounces.
    private float CollisionDamage(float closingSpeed, float scale) =>
        closingSpeed > _combat.CollisionDamageMinSpeed
            ? MathF.Min(closingSpeed * scale, _combat.MaxCollisionDamage)
            : 0f;

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
