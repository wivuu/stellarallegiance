using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  wivuullegiance — server module
//  T1: full game schema, seed data, and lifecycle reducers.
//  SimTick is scheduled here but its body is a stub until T4.
//  Spec: .PLAN/03-DATA-MODEL.md, .PLAN/04-REDUCERS.md
// =====================================================================

// SpacetimeDB enums disallow explicit values; declaration order fixes them
// (Scout=0, Fighter=1) and (Lobby=0, Active=1, Ended=2).
[SpacetimeDB.Type]
public enum ShipClass : byte { Scout, Fighter, Bomber }

[SpacetimeDB.Type]
public enum MatchPhase : byte { Lobby, Active, Ended }

// AI drone behaviour state (see PigAI.cs). Declaration order fixes the values
// (Idle=0, Seek=1, Attack=2, Patrol=3, Rescue=4). APPEND-ONLY so the existing
// 0/1/2 values stay stable for clients that already decoded them.
[SpacetimeDB.Type]
public enum PigState : byte { Idle, Seek, Attack, Patrol, Rescue }

// Chat delivery scope. Declaration order fixes the values (All=0, Team=1, Direct=2);
// the row-level visibility filters on ChatMessage key off these exact numbers, so the
// server — not the client — decides who can read a message.
[SpacetimeDB.Type]
public enum ChatScope : byte { All, Team, Direct }

// ---- Tables ----------------------------------------------------------

[SpacetimeDB.Table(Accessor = "Player", Public = true)]
public partial struct Player
{
    [PrimaryKey]
    public Identity Identity;   // provided by SpacetimeDB on connect
    public byte? Team;          // 0 or 1; null means "in the lobby, no team yet"
    public bool Ready;          // readied up in the lobby; consumed when the match starts
    public ulong? ShipId;       // controlled ship; null when docked/dead
    public bool Online;         // false on disconnect; row retained for match
    public string Name;         // cosmetic
    // Non-nullable mirror of Team (255 = no team) used solely by the team-chat visibility
    // filter: STDB's RLS SQL can't compare the nullable Team column, so we join on this.
    // Kept in lockstep wherever Team is written. Indexed: subscriptions require an index
    // on the columns an RLS filter joins on.
    [SpacetimeDB.Index.BTree]
    public byte ChatTeam;
}

// Lobby + in-game chat, and the responses to dev commands. Public, but the
// [ClientVisibilityFilter]s on Module restrict each client to the rows it may read:
// every All row, the Team rows for its own side, and Direct rows addressed to it. So
// team chat is genuinely private — a modified client never receives the enemy channel.
[SpacetimeDB.Table(Accessor = "ChatMessage", Public = true)]
public partial struct ChatMessage
{
    [PrimaryKey]
    [AutoInc]
    public ulong MessageId;
    public Identity Sender;      // default(Identity) for system lines
    public string SenderName;    // denormalized at insert (Name or ShortId) so history stays stable
    public byte Scope;           // ChatScope (All=0 / Team=1 / Direct=2) — drives the visibility
                                 // filters, which need a numeric column to compare against
    // The side that may read it when Scope==Team (read only for Scope==Team rows; 0
    // otherwise). Joined against Player.ChatTeam in the team-visibility filter; indexed
    // because subscriptions require an index on RLS join columns.
    [SpacetimeDB.Index.BTree]
    public byte Team;
    public Identity Recipient;   // the only reader when Scope==Direct (default otherwise)
    public bool IsSystem;        // true => server line (match events, command replies); styled apart
    public string Text;
    public Timestamp CreatedAt;
}

// The Ship table + ship lifecycle (spawn/respawn/kill/pod/dock) live in Ships.cs (M2).

// Per-tick input buffer. One row per (ship, tick) so SimTick can apply the EXACT
// input the client predicted with for that tick, rather than "latest" — this makes
// the server replay the client's input sequence and drives prediction/authority
// divergence to zero (.PLAN/07, /99). Server-private: clients write it via
// ApplyInput and never read it, so it isn't synced. Pruned to a short window.
// Multi-column index on (ShipId, Tick) so SimTick can fetch the exact input row for a ship
// and tick with a direct index lookup (ByShipTick.Filter) instead of scanning the ship's whole
// ~InputKeep-row buffer. The single-column ShipId index is kept for the latest-before fallback
// and for bulk delete on despawn.
[SpacetimeDB.Table(Accessor = "ShipInput", Public = false)]
[SpacetimeDB.Index.BTree(Accessor = "ByShipTick", Columns = ["ShipId", "Tick"])]
public partial struct ShipInput
{
    [PrimaryKey]
    [AutoInc]
    public ulong InputId;
    [SpacetimeDB.Index.BTree]
    public ulong ShipId;
    public uint Tick;           // the sim tick this input is FOR (client _predTick)
    public float Thrust;        // -1..1 forward/back
    public float StrafeX;       // -1..1 left/right
    public float StrafeY;       // -1..1 up/down
    public float Yaw;           // -1..1
    public float Pitch;         // -1..1
    public float Roll;          // -1..1
    public bool Firing;         // trigger held
    public bool Boost;          // afterburner held
    public bool Coast;          // vector lock (thrust cancels drag, holds velocity)
}

// The Base table + base content (radius/health from BaseDef) live in Bases.cs (M2).

[SpacetimeDB.Table(Accessor = "Asteroid", Public = true)]
public partial struct Asteroid
{
    [PrimaryKey]
    [AutoInc]
    public ulong AsteroidId;
    public uint SectorId;       // which sector this asteroid belongs to
    public float PosX;
    public float PosY;
    public float PosZ;
    public float Radius;        // collision + render scale
    // Which generated mesh the client renders (GLB stem, e.g. "asteroid-flint"); the
    // sim treats every asteroid as a sphere of Radius regardless. Empty => client falls
    // back to a plain sphere.
    public string Variant;
    // Fixed orientation (radians) so identical variants don't all face the same way.
    public float RotX;
    public float RotY;
    public float RotZ;
}

// A sector is one self-contained slice of the world. All sectors share the same
// coordinate origin (objects are partitioned by SectorId, not by world region), so
// CenterX/Y/Z are the boundary origin — currently (0,0,0) for every sector. A ship
// whose distance from its sector center exceeds Radius is outside the playable area
// and takes mounting hull damage until it returns or is destroyed (the "invisible
// boundary"). Sectors are linked by Aleph pairs.
[SpacetimeDB.Table(Accessor = "Sector", Public = true)]
public partial struct Sector
{
    [PrimaryKey]
    public uint SectorId;
    public string Name;
    public float CenterX;
    public float CenterY;
    public float CenterZ;
    public float Radius;        // soft play-area radius; beyond it the hull is eroded
}

// An aleph is a warp gate rendered as a spinning funnel. Alephs come in LINKED
// PAIRS: one row per sector, each pointing at its partner. A ship that touches an
// aleph is moved to the partner's sector and repositioned just past the partner
// aleph (so it doesn't immediately warp back). PartnerId/DestSectorId are wired up
// after both rows of a pair are inserted (AlephId is autoinc).
[SpacetimeDB.Table(Accessor = "Aleph", Public = true)]
public partial struct Aleph
{
    [PrimaryKey]
    [AutoInc]
    public ulong AlephId;
    public uint SectorId;       // the sector this funnel lives in
    public ulong PartnerId;     // the aleph in the destination sector
    public uint DestSectorId;   // partner's sector (denormalized for the warp)
    public float PosX;
    public float PosY;
    public float PosZ;
}

// Fire control + analytic shot resolution live in Weapons.cs (Phase-1 M2). Bolts are
// client-synthesized visuals — no projectile rows exist anywhere.

[SpacetimeDB.Table(Accessor = "Match", Public = true)]
public partial struct Match
{
    [PrimaryKey]
    public uint Id;             // always 0 (singleton)
    public uint Tick;           // authoritative sim tick counter
    public MatchPhase Phase;
    public byte? Winner;        // team id when ended, else null
    // Bitmask of sides that have fielded a human pilot this match (bit t = team t). Set
    // when the match starts (every side that readied in) and when a player joins mid-game.
    // Lets us tell "a side that had pilots is now empty → end the match" from "a side that
    // was empty from the start" (solo play vs the AI drones). Reset to 0 in the lobby.
    public byte EngagedTeams;
    // Seed that generated the current map (asteroid field + aleph placement). Stored so
    // a map is reproducible: GenerateMap(seed) always yields the same world, and
    // RegenerateWorld(seed) rebuilds it. Init picks a random seed; clients ignore this.
    public ulong Seed;
    // Real-time pacing so the sim runs at wall-clock speed regardless of how often
    // the scheduler actually fires SimTick (Maincloud delivers it at ~10 Hz, local
    // at ~20 Hz). Each call integrates `elapsed / Dt` fixed-dt sub-steps; the carry
    // is kept here so the rate is exact over time. (Clients ignore these fields.)
    public long LastTickMicros; // ctx.Timestamp of the previous SimTick (0 = first)
    public long AccumMicros;    // leftover sub-tick time not yet integrated
    // AI drones spawn only while this is true (toggled by the /pigs dev command). Default
    // FALSE — drones stay dormant until someone runs `/pigs on`; turning it off makes
    // SimTick despawn the field on its next pass (see Pass 0).
    public bool PigsEnabled;
}

// Per-team PIG squad-wave timing. The drones of a side now spawn as a SQUAD (the whole
// team's slots) rather than trickling per-slot: once a squad is fielded (Active) no new
// drones arrive until the whole squad is wiped, then a short delay (PigSquadDelayTicks)
// later the next squad scrambles. One row per team, created lazily by EnsurePigSlots and
// reset by DespawnAllPigs. (See SimulatePigLifecycle in PigAI.cs.)
[SpacetimeDB.Table(Accessor = "PigSquad", Public = true)]
public partial struct PigSquad
{
    [PrimaryKey]
    public byte Team;
    public uint NextSquadTick;   // earliest tick the next squad may scramble (0 = ready now)
    public bool Active;          // a squad is currently fielded (≥1 drone alive, or just spawned)
}

// Scheduled-reducer table that drives SimTick at a fixed interval.
[SpacetimeDB.Table(
    Accessor = "SimTickTimer",
    Scheduled = nameof(Module.SimTick),
    ScheduledAt = nameof(ScheduledAt),
    Public = true)]
public partial struct SimTickTimer
{
    [PrimaryKey]
    [AutoInc]
    public ulong ScheduledId;
    public ScheduleAt ScheduledAt;
}

public static partial class Module
{
    // ---- Constants ----------------------------------------------------

    private const uint SimTickHz = 20;
    private const byte NumTeams = 2;
    private const int AsteroidCount = 4;
    private const uint InputKeep = 64;   // per-tick input buffer window (ticks)
    private const long DtMicros = 1_000_000 / SimTickHz;  // 50 ms — one fixed sim step
    private const int  MaxCatchupSteps = 8;               // cap sub-steps/call (anti-spiral)

    // Combat tuning (server-only; clients render bolts they synthesize from ship rows).
    private const float ProjectileSpeed = 200f;      // u/s muzzle speed (added to ship velocity);
                                                     // 700 * 0.85 — 15% slower than the original M0 calibration
    private const uint  ProjectileLifeTicks = 16;    // ~1.45 s lifespan, then culled — combined with the
                                                     // 15% speed cut this halves max shot range (1750u -> ~863u)
    private const float NoseOffset = 3f;             // spawn this far ahead of ship center
    private const float ProjectileRadius = 1f;       // projectile hit sphere
    private const float ShipRadius = 3f;             // ship hit / collision sphere
    private const float BaseRadius = 45f;            // matches the client's base render radius
    // Asteroid.Radius is the silhouette's *circumscribing* radius (the client scales the mesh
    // so its farthest spike sits exactly on it). Collisions use a fraction of that so the hit
    // sphere tracks the rock's solid body rather than its outermost spikes, instead of bounding
    // empty space between them. Render scale is unchanged — this only tightens the sim.
    private const float AsteroidCollisionScale = 0.82f;
    private const float BaseMaxHealth = 2000f;       // starting/restored base hull (win condition target)
    private const float CollisionRestitution = 0.3f; // bounce factor on impact
    private const float CollisionDamageScale = 0.6f; // ship-vs-static hull damage per (u/s) of inward impact
    // Ship-vs-ship damage per (u/s) of inward impact, multiplied by the pair's reduced
    // mass. Set to 2x CollisionDamageScale so a baseline Scout-vs-Scout pair (reduced
    // mass 0.5) still deals ~0.6/u·s as before; heavier pairs deal proportionally more.
    private const float ShipShipDamageScale = 1.2f;
    private const float MaxCollisionDamage = 30f;    // cap per collision per tick

    // ---- Escape pods + docking (Gamification) -------------------------
    private const float PodMaxHull = 20f;            // an ejected escape pod's (low) starting hull
    // Pods present a smaller WEAPON hit sphere than a combat ship — harder to gun down (still
    // fragile via their low hull) so a downed pilot can run for it. Physical collision still
    // uses the full ShipRadius; this only shrinks projectile hit tests (see HitRadius).
    private const float PodHitRadius = ShipRadius * 0.5f;
    // Dock radius fraction: a ship/pod intersecting its OWN base within this fraction of the
    // base radius despawns (player → spawn menu; pod → resolved). 0.9 keeps it comfortably
    // inside the spawn offset (baseRadius + ShipRadius) so a freshly-spawned ship doesn't
    // instantly re-dock. Multiplied against the live BaseRadiusOf (def-driven) at the use site.
    private const float DockRadiusFrac = 0.9f;
    // Rescue radius: a pod is rescued only on DIRECT hull contact with a friendly non-pod
    // ship (same sector) — the two collision spheres touching (2·ShipRadius). Kept this tight
    // so a pod isn't accidentally resolved by a ship merely passing near it; the rescuer has
    // to actually reach it. Resolves the pod like docking (player → spawn menu, PIG pod despawns).
    private const float RescueRadius = ShipRadius * 2f;
    // Eject kinematics: a freshly-spawned pod (player OR PIG) is flung clear of the wreck —
    // a high-speed impulse in a random direction plus a tumble. The flight model's
    // exponential drag bleeds the over-speed off smoothly (maxSpeed is an equilibrium, not
    // a snap), so this speed persists for a second or two before the pod settles to its slow
    // crawl; the spin likewise winds down as the pod's turn rate slews toward its (near-zero)
    // commanded rate. Server-only RNG — the result is baked into the spawned Ship row, so
    // client prediction just reads it (no RNG to reproduce).
    private const float PodEjectSpeed = 90f;   // u/s initial fling (decays to FlightModel.Pod.MaxSpeed)
    private const float PodEjectSpin  = 5f;    // rad/s initial tumble (decays via AngularDrag)

    // A uniformly-distributed unit vector from the reducer's deterministic RNG.
    private static Vec3 RandomUnitVec(ReducerContext ctx)
    {
        float z = (float)(ctx.Rng.NextDouble() * 2.0 - 1.0);            // cos(polar), uniform on the sphere
        float phi = (float)(ctx.Rng.NextDouble() * 2.0 * Math.PI);
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vec3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
    }

    // ---- Sectors & alephs ---------------------------------------------
    private const uint  HomeSector = 0;              // bases + spawn live here (the battlefield)
    private const uint  VergeSector = 1;             // the linked outpost sector across the aleph
    private const float CoreRadius = 2100f;          // sector 0 boundary (contains bases ±500, field ±800)
    private const float VergeRadius = 700f;          // sector 1 boundary (a tighter outpost)
    private const int   VergeAsteroidCount = 4;     // smaller asteroid field in the Verge
    private const float VergeBeltRadius = 380f;       // ring radius of the Verge's asteroid belt
    private const float AlephTriggerRadius = 18f;    // touch this close to a funnel to warp through
    private const float WarpExitOffset = 60f;        // placed this far past the dest aleph (no instant re-warp)
    private const float WarpExitJitter = 0.12f;      // random spread (per-axis) on the exit vector out of the mouth
    // Out-of-bounds hull erosion: a flat base rate plus a ramp with how far past the
    // edge you are, capped — so skimming the edge is survivable but straying deep is
    // quickly fatal. Applied per-second (scaled by dt) while a ship is outside.
    private const float BoundaryBaseDps = 8f;
    private const float BoundaryRampDps = 0.12f;     // extra dps per unit beyond the edge
    private const float BoundaryMaxDps = 60f;

    // MaxHull / WeaponDamage / FireInterval are the compiled-in defaults SeedDefaults pours
    // into the def tables, and the fallback values the def-read helpers use when a row is
    // missing (ShipMaxHull, ShipWeaponDamage). Instance mass now comes from ShipStatsFor.
    private static float MaxHull(ShipClass c) => c switch
    {
        ShipClass.Bomber => 240f,
        ShipClass.Fighter => 120f,
        _ => 60f,
    };
    private static float WeaponDamage(ShipClass c) => c switch
    {
        ShipClass.Bomber => 22f,
        ShipClass.Fighter => 10f,
        _ => 4f,
    };
    private static uint FireInterval(ShipClass c) => c switch
    {
        ShipClass.Bomber => 14u,
        ShipClass.Fighter => 8u,
        _ => 4u,
    };
    // Weapon hit sphere for a ship: a pod is a small, hard-to-hit target (PodHitRadius);
    // every other ship uses the full ShipRadius. Physical collisions ignore this.
    private static float HitRadius(Ship s) => s.IsPod ? PodHitRadius : ShipRadius;

    // ---- World seeding (Init) -----------------------------------------

    // Deterministic PRNG (splitmix64) for MAP generation. ctx.Rng is deterministic but
    // its seed isn't ours to set or read, so a map it builds can't be reproduced on
    // demand. DetRng is seeded from an explicit value we store on Match.Seed, so the
    // same seed always yields the same world. Mutable struct — pass by ref so draws
    // advance one shared stream.
    private struct DetRng
    {
        private ulong _state;
        public DetRng(ulong seed) { _state = seed; }

        public ulong NextULong()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        // [0, 1)
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));
        // [lo, hi)
        public double NextRange(double lo, double hi) => lo + (hi - lo) * NextDouble();
        // [0, n)
        public int NextInt(int n) => (int)(NextDouble() * n);
    }

    // The generated asteroid meshes the client can render (GLB stems under
    // client/assets/asteroids/). KEEP IN SYNC with tools/asteroid-gen/asteroids.json —
    // a name here with no matching GLB just falls back to a sphere client-side.
    private static readonly string[] AsteroidVariants =
    {
        "asteroid-flint", "asteroid-boulder", "asteroid-quartz", "asteroid-geode",
        "asteroid-shard", "asteroid-gravel", "asteroid-pebble", "asteroid-hunk",
        "asteroid-blob", "asteroid-gourd", "asteroid-nodule", "asteroid-prism",
        "asteroid-facet", "asteroid-gem", "asteroid-opal", "asteroid-beryl",
        "asteroid-chunk", "asteroid-rubble", "asteroid-scree", "asteroid-slag",
        "asteroid-crag", "asteroid-marble", "asteroid-lump", "asteroid-spire",
        "asteroid-flake", "asteroid-monolith", "asteroid-debris", "asteroid-cobble",
        "asteroid-slab", "asteroid-ore", "asteroid-knob",
    };

    // Pick a uniformly-random variant + a random fixed orientation for one asteroid.
    private static (string variant, float rx, float ry, float rz) NextAsteroidShape(ref DetRng rng)
    {
        string variant = AsteroidVariants[rng.NextInt(AsteroidVariants.Length)];
        float rx = (float)rng.NextRange(0, Math.PI * 2.0);
        float ry = (float)rng.NextRange(0, Math.PI * 2.0);
        float rz = (float)rng.NextRange(0, Math.PI * 2.0);
        return (variant, rx, ry, rz);
    }

    // Core pattern: a handful of diffuse BANDS (ribbons) rather than an even box-fill, so the
    // field reads as something you thread between with open lanes between, not uniform static.
    // Each band is a slab of rocks centred on a pseudo-random plane that's slightly sheared
    // (tilted) across X, giving diagonal ribbons; rocks scatter freely along the ribbon but
    // cluster tightly across its thickness (triangular jitter), leaving the lanes clear. The
    // band layout is drawn from the same DetRng so the map stays byte-stable per seed. Extents
    // scale with the world SectorScale (rock SIZES stay fixed) exactly like the old cloud.
    private static void SeedAsteroidField(ReducerContext ctx, ref DetRng rng, uint sector, int count, float scale)
    {
        const double halfX = 800.0, halfY = 200.0, halfZ = 800.0;

        // Lay out 3..5 ribbons up front: each gets a pseudo-random centre along Z, a half-
        // thickness, and a shear (dz per unit x) that tilts its plane so bands aren't axis walls.
        int bandCount = 3 + rng.NextInt(3);
        var bandZ = new double[bandCount];
        var bandThick = new double[bandCount];
        var bandShear = new double[bandCount];
        for (int b = 0; b < bandCount; b++)
        {
            bandZ[b] = (rng.NextDouble() * 2.0 - 1.0) * halfZ;       // ribbon centre along Z
            bandThick[b] = 40.0 + rng.NextDouble() * 70.0;           // half-thickness across the ribbon
            bandShear[b] = (rng.NextDouble() * 2.0 - 1.0) * 0.6;     // tilt: dz per unit x
        }

        for (int i = 0; i < count; i++)
        {
            int b = rng.NextInt(bandCount);
            double ux = rng.NextDouble() * 2.0 - 1.0;                // free along the ribbon (X)
            // Triangular jitter (sum of two uniforms) packs rocks toward the band centre.
            double across = (rng.NextDouble() + rng.NextDouble() - 1.0) * bandThick[b];
            double zc = bandZ[b] + bandShear[b] * (ux * halfX);
            float px = (float)(ux * halfX * scale);
            float py = (float)((rng.NextDouble() * 2.0 - 1.0) * halfY * scale);
            float pz = (float)((zc + across) * scale);
            float radius = (float)(rng.NextDouble() * 30.0 + 10.0);
            var (variant, rx, ry, rz) = NextAsteroidShape(ref rng);
            ctx.Db.Asteroid.Insert(new Asteroid
            {
                AsteroidId = 0,
                SectorId = sector,
                PosX = px, PosY = py, PosZ = pz,
                Radius = radius,
                Variant = variant, RotX = rx, RotY = ry, RotZ = rz,
            });
        }
    }

    // Verge pattern: a flattened belt ringing the sector center. Asteroids sit near
    // VergeBeltRadius in the XZ plane (with radial + vertical jitter) so the field reads
    // as a band you thread, distinct from the Core's open cloud. Belt radius + jitter scale
    // with the world SectorScale (rock sizes fixed, like the Core field).
    private static void SeedAsteroidBelt(ReducerContext ctx, ref DetRng rng, uint sector, int count, float scale)
    {
        for (int i = 0; i < count; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            double r = (VergeBeltRadius + (rng.NextDouble() - 0.5) * 160.0) * scale;  // ±80 radial jitter (scaled)
            float px = (float)(Math.Cos(ang) * r);
            float py = (float)((rng.NextDouble() - 0.5) * 90.0 * scale);  // thin vertical band (scaled)
            float pz = (float)(Math.Sin(ang) * r);
            float radius = (float)(rng.NextDouble() * 18.0 + 8.0);
            var (variant, rx, ry, rz) = NextAsteroidShape(ref rng);
            ctx.Db.Asteroid.Insert(new Asteroid
            {
                AsteroidId = 0,
                SectorId = sector,
                PosX = px, PosY = py, PosZ = pz,
                Radius = radius,
                Variant = variant, RotX = rx, RotY = ry, RotZ = rz,
            });
        }
    }

    // A random position biased toward the OUTER part of a sector: a random azimuth at a
    // radius in ~[0.6, 0.9] of the sector radius (sqrt-weighted so it leans outward),
    // with modest vertical spread. Kept inside the boundary so a funnel never sits in
    // the out-of-bounds zone.
    private static (float, float, float) RandomOuterPos(ref DetRng rng, float sectorRadius)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double frac = 0.6 + 0.3 * Math.Sqrt(rng.NextDouble());   // weighted toward 0.9
        float r = (float)(sectorRadius * frac);
        float y = (float)((rng.NextDouble() - 0.5) * sectorRadius * 0.2);
        return ((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }

    // Build the whole map (sector radii + asteroid fields + the Core<->Verge aleph pair)
    // deterministically from one seed AND the WorldConfig: clears any existing asteroids/
    // alephs, then re-creates them off a single DetRng stream. Same seed + same config =>
    // byte-identical map. World scale (config) drives sector radii, asteroid counts, and
    // field/belt/aleph spread; the authored CoreRadius/VergeRadius/AsteroidCount constants
    // are the BASE values the config multiplies. Bases are fixed and not touched here.
    // Used by Init, RegenerateWorld, and UpsertWorldConfig.
    private static void GenerateMap(ReducerContext ctx, ulong seed)
    {
        var cfg = WorldConfigOrDefault(ctx);
        float scale = cfg.SectorScale;
        float density = cfg.AsteroidDensity;
        float scale3 = scale * scale * scale;

        float coreR = CoreRadius * scale;
        float vergeR = VergeRadius * scale;
        // Count scales with sector volume (scale³) at constant density, so a bigger sector
        // holds proportionally more rocks. (Cube law is the starting point per .PLAN/CONFIG.md;
        // drop the exponent to square-law here if big sectors feel cluttered.)
        int coreCount = (int)MathF.Round(density * AsteroidCount * scale3);
        int vergeCount = (int)MathF.Round(density * VergeAsteroidCount * scale3);

        foreach (var a in ctx.Db.Asteroid.Iter().ToList())
            ctx.Db.Asteroid.AsteroidId.Delete(a.AsteroidId);
        foreach (var al in ctx.Db.Aleph.Iter().ToList())
            ctx.Db.Aleph.AlephId.Delete(al.AlephId);

        // Sector radii are config-driven: write the scaled values onto the Sector rows the
        // client already subscribes to (boundary warnings + minimap pick it up for free).
        if (ctx.Db.Sector.SectorId.Find(HomeSector) is Sector hs)
            ctx.Db.Sector.SectorId.Update(hs with { Radius = coreR });
        if (ctx.Db.Sector.SectorId.Find(VergeSector) is Sector vsec)
            ctx.Db.Sector.SectorId.Update(vsec with { Radius = vergeR });

        var rng = new DetRng(seed);

        // Each sector gets a DIFFERENT asteroid pattern so they read as distinct places.
        //   • Core  — a diffuse 3D field scattered across a wide box (the open battlefield).
        SeedAsteroidField(ctx, ref rng, HomeSector, coreCount, scale);
        //   • Verge — a flattened belt: asteroids ring the sector center in the XZ plane
        //     with only slight vertical spread, so flying it feels like threading a band.
        SeedAsteroidBelt(ctx, ref rng, VergeSector, vergeCount, scale);

        // One linked aleph pair joining Core <-> Verge. Each funnel is placed at a random
        // spot biased toward the OUTER reaches of its (scaled) sector (so warps sit out near
        // the frontier, not on top of the bases). A bigger sector spreads the alephs farther
        // apart by construction. Insert both, then wire each to its partner (AlephId is
        // autoinc, so the ids aren't known until after insert).
        var (cx, cy, cz) = RandomOuterPos(ref rng, coreR);
        var (vx, vy, vz) = RandomOuterPos(ref rng, vergeR);
        var alephCore = ctx.Db.Aleph.Insert(new Aleph
        {
            AlephId = 0, SectorId = HomeSector, PartnerId = 0, DestSectorId = VergeSector,
            PosX = cx, PosY = cy, PosZ = cz,
        });
        var alephVerge = ctx.Db.Aleph.Insert(new Aleph
        {
            AlephId = 0, SectorId = VergeSector, PartnerId = 0, DestSectorId = HomeSector,
            PosX = vx, PosY = vy, PosZ = vz,
        });
        ctx.Db.Aleph.AlephId.Update(alephCore with { PartnerId = alephVerge.AlephId });
        ctx.Db.Aleph.AlephId.Update(alephVerge with { PartnerId = alephCore.AlephId });

        // The asteroid set just changed — drop the spatial-grid cache (see AsteroidGridForSector).
        _asteroidGridDirty = true;
    }

    // ---- Per-sector asteroid spatial grid (read cache) ----------------
    // The asteroid field is STATIC between map regenerations (GenerateMap is its only mutator),
    // but the AI drones each scan it every tick to steer around rocks. With the world now
    // holding hundreds of asteroids (WorldConfig scale), re-reading the whole Asteroid table
    // from the datastore per drone per tick was the dominant sim cost. Bucket the field ONCE
    // into a uniform spatial grid (per sector), and a drone only examines asteroids in its own
    // cell + the 26 neighbours — O(asteroids near the drone) instead of O(asteroids in sector).
    // The cell is sized to the avoidance look-ahead so a 3x3x3 query provably covers a ball of
    // that radius around the drone (a point in cell C lies >= one cell from the edge of the
    // C±1 block). One uniform level suffices: the field is sparse and roughly uniform, so cells
    // hold ~0-1 rocks; a deeper hierarchy (octree) would only help under heavy clustering.
    // Server-only (PIG steering is authoritative), so the captured Iter() order needn't match a
    // client. Reused across every drone and tick until GenerateMap invalidates it.
    private const float AsteroidGridCell = PigAvoidLookahead;

    private static bool _asteroidGridDirty = true;
    private static readonly Dictionary<uint, Dictionary<(int, int, int), List<Asteroid>>> _asteroidGrid = new();
    private static readonly Dictionary<(int, int, int), List<Asteroid>> _noGrid = new();

    // The grid cell index for one world coordinate. Shared by the asteroid grid above and
    // the per-tick ship grid below (and the shot ray-cell walk in Weapons.cs) — one uniform
    // cell size for all spatial broad-phasing.
    private static int GridCellOf(float v) => (int)MathF.Floor(v / AsteroidGridCell);

    private static Dictionary<(int, int, int), List<Asteroid>> AsteroidGridForSector(ReducerContext ctx, uint sector)
    {
        if (_asteroidGridDirty)
        {
            _asteroidGrid.Clear();
            foreach (var a in ctx.Db.Asteroid.Iter())
            {
                if (!_asteroidGrid.TryGetValue(a.SectorId, out var grid))
                    _asteroidGrid[a.SectorId] = grid = new Dictionary<(int, int, int), List<Asteroid>>();
                var key = (GridCellOf(a.PosX), GridCellOf(a.PosY), GridCellOf(a.PosZ));
                if (!grid.TryGetValue(key, out var cell))
                    grid[key] = cell = new List<Asteroid>();
                cell.Add(a);
            }
            _asteroidGridDirty = false;
        }
        return _asteroidGrid.TryGetValue(sector, out var g) ? g : _noGrid;
    }

    // True if the point (x,y,z) lies within (asteroid radius + extraRadius) of ANY asteroid in
    // its sector. Broad-phased through the spatial grid — only the rocks in the point's cell +
    // 26 neighbours are tested. This is EQUIVALENT to scanning the whole field: the hit radius
    // (~34u) is far below the block's guaranteed coverage (AsteroidGridCell = 160u), so every
    // omitted asteroid is a guaranteed miss. Used by the projectile-vs-asteroid pass.
    private static bool HitsAsteroid(ReducerContext ctx, uint sector, float x, float y, float z, float extraRadius)
    {
        var grid = AsteroidGridForSector(ctx, sector);
        int cx = GridCellOf(x), cy = GridCellOf(y), cz = GridCellOf(z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
            {
                float rr = a.Radius * AsteroidCollisionScale + extraRadius;
                if (Dist2(x, y, z, a.PosX, a.PosY, a.PosZ) <= rr * rr)
                    return true;
            }
        }
        return false;
    }

    // Bounce a ship out of any asteroid it overlaps, broad-phased through the spatial grid so
    // only nearby rocks are tested. Equivalent to resolving against the whole field: a rock the
    // ship doesn't overlap is a no-op in ResolveCollision, and any rock it COULD overlap (within
    // ~43u) is well inside the queried block (160u). The ship's collision push (<= a rock's
    // radius) keeps it inside that block, so re-querying per push is unnecessary.
    private static Ship ResolveAsteroidCollisions(ReducerContext ctx, Ship s)
    {
        var grid = AsteroidGridForSector(ctx, s.SectorId);
        int cx = GridCellOf(s.PosX), cy = GridCellOf(s.PosY), cz = GridCellOf(s.PosZ);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
                s = ResolveCollision(s, a.PosX, a.PosY, a.PosZ, a.Radius * AsteroidCollisionScale);
        }
        return s;
    }

    // ---- Per-tick ship spatial grid (broad-phase for shot resolution) -
    // Unlike the asteroid field, ships move every tick, so this grid is rebuilt once per
    // SimulateTick (lazily, on the first shot fired that tick) from a single Iter() snapshot
    // — O(ships) total, vs. the O(firing_ships x ships_in_sector) cost of re-querying per shot.
    // TryFire then walks only the cells along EACH SHOT'S flight path (see CellsAlongRay)
    // instead of every ship in the sector, narrowing the candidate set to roughly the shot's
    // corridor regardless of how many ships/asteroids are elsewhere in the (large) sector.
    private static uint _shipGridTick = uint.MaxValue;
    private static readonly Dictionary<uint, Dictionary<(int, int, int), List<Ship>>> _shipGrid = new();
    private static readonly Dictionary<(int, int, int), List<Ship>> _noShipGrid = new();

    private static Dictionary<(int, int, int), List<Ship>> ShipGridForSector(ReducerContext ctx, uint tick, uint sector)
    {
        if (_shipGridTick != tick)
        {
            _shipGrid.Clear();
            foreach (var s in ctx.Db.Ship.Iter())
            {
                if (!_shipGrid.TryGetValue(s.SectorId, out var grid))
                    _shipGrid[s.SectorId] = grid = new Dictionary<(int, int, int), List<Ship>>();
                var key = (GridCellOf(s.PosX), GridCellOf(s.PosY), GridCellOf(s.PosZ));
                if (!grid.TryGetValue(key, out var cell))
                    grid[key] = cell = new List<Ship>();
                cell.Add(s);
            }
            _shipGridTick = tick;
        }
        return _shipGrid.TryGetValue(sector, out var g) ? g : _noShipGrid;
    }

    // Every grid cell (plus its 26 neighbours, for the same radius-coverage reason as
    // HitsAsteroid) that a shot travelling from `start` at constant velocity `vel` passes
    // through over [0, maxT]. Sampled at ~AsteroidGridCell intervals along the path and
    // de-duplicated — a shot's corridor through a sector this size is a small fraction of
    // the sector's total cells, so this is the "filter to the quadrants the shot crosses"
    // narrowing without needing a DB-side composite index.
    private static IEnumerable<(int, int, int)> CellsAlongRay(Vec3 start, Vec3 vel, float maxT)
    {
        var seen = new HashSet<(int, int, int)>();
        float dist = vel.Length() * maxT;
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / AsteroidGridCell));
        for (int i = 0; i <= steps; i++)
        {
            Vec3 p = start + vel * (maxT * i / steps);
            int cx = GridCellOf(p.X), cy = GridCellOf(p.Y), cz = GridCellOf(p.Z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var key = (cx + dx, cy + dy, cz + dz);
                if (seen.Add(key))
                    yield return key;
            }
        }
    }

    // ---- Held input (replay between buffered rows) ---------------------
    // The input applied to each player ship on its most recent sim tick. A buffered
    // ShipInput row now only has to exist for ticks where the stick state CHANGED (clients
    // send on change + keepalive); on every other tick the sim replays this held value —
    // which is exactly what the client's prediction did, so auth == prediction holds with
    // ~10x fewer input rows/transactions. Static WASM memory like the spatial grids:
    // survives between reducer calls, self-heals after a hot-swap via the dirty/cold
    // re-derive scan in Pass A. _inputDirty marks ships whose buffer changed since the
    // last derive (covers rows that arrive LATE, for ticks already simulated).
    private static readonly Dictionary<ulong, ShipInputState> _heldInput = new();
    private static readonly HashSet<ulong> _inputDirty = new();

    // ---- Lifecycle ----------------------------------------------------

    // Runs once when the module is first published.
    [SpacetimeDB.Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx)
    {
        // Pick the map seed once, off ctx.Rng (deterministic but not ours to reproduce),
        // so a fresh DB gets a fresh-but-recorded map. Everything random about the map
        // then derives from this stored value — RegenerateWorld(seed) reproduces it.
        ulong seed = ((ulong)(uint)ctx.Rng.Next() << 32) | (uint)ctx.Rng.Next();
        Log.Info($"[Init] seeding match state (map seed {seed})");

        // Record the publisher as the server owner (the only place ctx.Sender is the owner)
        // and seed the runtime def tables from the compiled-in defaults. Both live in Defs.cs.
        CaptureOwner(ctx);
        SeedDefaults(ctx);

        // Singleton match row.
        ctx.Db.Match.Insert(new Match
        {
            Id = 0,
            Tick = 0,
            Phase = MatchPhase.Lobby,
            Winner = null,
            EngagedTeams = 0,
            Seed = seed,
            PigsEnabled = false,
        });

        // Two sectors sharing the world origin: the Core battlefield (bases + spawn)
        // and the Verge outpost across the aleph. CenterX/Y/Z are 0 — boundary is a
        // radius from the origin. Radii are inserted at the authored base values, then
        // rescaled by WorldConfig.SectorScale in GenerateMap below.
        ctx.Db.Sector.Insert(new Sector { SectorId = HomeSector, Name = "Core Sector", CenterX = 0f, CenterY = 0f, CenterZ = 0f, Radius = CoreRadius });
        ctx.Db.Sector.Insert(new Sector { SectorId = VergeSector, Name = "The Verge", CenterX = 0f, CenterY = 0f, CenterZ = 0f, Radius = VergeRadius });

        // Two bases at opposite ends of the Core sector (hull from BaseDef; see Bases.cs).
        SeedBases(ctx);

        // Asteroid fields + the Core<->Verge aleph pair, all derived from the seed above.
        GenerateMap(ctx, seed);

        // NOTE: SimTick is intentionally NOT scheduled here. The sim loop is
        // started on the first client connect and stopped when the last client
        // disconnects (see StartSim/StopSim) so an empty server burns no CPU.
        // This is a prototype, not a persistent universe — nothing needs to
        // advance while nobody is watching.

        Log.Info($"[Init] done: 1 match, 2 sectors, 2 bases, {ctx.Db.Asteroid.Count} asteroids (scale {WorldConfigOrDefault(ctx).SectorScale}), 1 aleph pair, {ctx.Db.ShipClassDef.Count} ship/{ctx.Db.WeaponDef.Count} weapon/{ctx.Db.BaseDef.Count} base defs, SimTick paused until first client");
    }

    // Rebuild the asteroid field + aleph pair from an explicit seed, and record it on the
    // Match. Same seed => the same map every time. Gated to the Lobby phase: regenerating
    // mid-match would re-place the alephs out from under flying ships. Sectors/bases are
    // untouched.
    [SpacetimeDB.Reducer]
    public static void RegenerateWorld(ReducerContext ctx, ulong seed)
    {
        var m = ctx.Db.Match.Id.Find(0);
        if (m is null)
        {
            Log.Info("[RegenerateWorld] no match row");
            return;
        }
        if (m.Value.Phase != MatchPhase.Lobby)
        {
            Log.Info("[RegenerateWorld] refused: only in the lobby");
            return;
        }
        GenerateMap(ctx, seed);
        ctx.Db.Match.Id.Update(m.Value with { Seed = seed });
        Log.Info($"[RegenerateWorld] map regenerated from seed {seed}");
    }

    // A client connected: create or reactivate their Player row.
    [SpacetimeDB.Reducer(ReducerKind.ClientConnected)]
    public static void ClientConnected(ReducerContext ctx)
    {
        var existing = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (existing is not null)
        {
            ctx.Db.Player.Identity.Update(existing.Value with { Online = true });
            Log.Info($"[ClientConnected] reactivated {ctx.Sender}");
        }
        else
        {
            // New connections land in the LOBBY with no team (Team = null). They pick a
            // side and ready up via JoinTeam/SetReady (or QuickJoin). We don't auto-assign
            // a team here so a connection that never commits — a CLI subscriber, the owner
            // dashboard — stays teamless and is pruned on disconnect rather than lingering
            // as a phantom roster entry.
            ctx.Db.Player.Insert(new Player
            {
                Identity = ctx.Sender,
                Team = null,
                Ready = false,
                ShipId = null,
                Online = true,
                Name = "",
                ChatTeam = NoTeam,
            });
            Log.Info($"[ClientConnected] new player {ctx.Sender} -> lobby");
        }

        // Someone is here now — make sure the sim loop is running.
        StartSim(ctx);
    }

    // A client disconnected: mark offline and remove their live ship.
    [SpacetimeDB.Reducer(ReducerKind.ClientDisconnected)]
    public static void ClientDisconnected(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;

        var p = player.Value;
        if (p.ShipId is ulong shipId)
        {
            ctx.Db.Ship.ShipId.Delete(shipId);
            DeleteShipInputs(ctx, shipId);
        }

        if (p.Team is null)
        {
            // Never committed to a side (still in the lobby, or a non-game connection like
            // a CLI subscriber / the owner dashboard): drop the row entirely so it doesn't
            // haunt the lobby roster.
            ctx.Db.Player.Identity.Delete(ctx.Sender);
            Log.Info($"[ClientDisconnected] {ctx.Sender} left the lobby (row pruned)");
        }
        else
        {
            // A teamed player: keep the row (marked offline) so their slot survives a
            // brief reconnect during a match. RestartMatch prunes any still-offline rows.
            ctx.Db.Player.Identity.Update(p with { Online = false, ShipId = null });
            Log.Info($"[ClientDisconnected] {ctx.Sender} offline");
        }

        // If a whole side just emptied out, the match is over.
        EndMatchIfSideAbandoned(ctx);

        // If that was the last connected client, pause the sim loop so an
        // empty server idles at ~0 CPU instead of ticking 20 Hz forever.
        if (!AnyOnline(ctx))
            StopSim(ctx);
    }

    // ---- Player actions (called by clients) ---------------------------

    // Cosmetic display name.
    [SpacetimeDB.Reducer]
    public static void SetName(ReducerContext ctx, string name)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        ctx.Db.Player.Identity.Update(player.Value with { Name = name });
    }

    // ---- Chat & dev commands ------------------------------------------

    private const int ChatMaxLen = 240;
    private const byte NoTeam = 255;   // Player.ChatTeam sentinel: not on a side (see Player.ChatTeam)

    // Send a chat line, or run a dev command if the text starts with '/'. teamOnly routes a
    // normal message to the sender's team channel (private via the visibility filters);
    // without a team it falls back to all-chat. Rejected (logged, not thrown) for the usual
    // expected conditions, matching the other reducers.
    [SpacetimeDB.Reducer]
    public static void SendChat(ReducerContext ctx, string text, bool teamOnly)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null || !player.Value.Online)
            return;

        text = (text ?? "").Trim();
        if (text.Length == 0)
            return;
        if (text.Length > ChatMaxLen)
            text = text[..ChatMaxLen];

        if (text[0] == '/')
        {
            HandleCommand(ctx, player.Value, text);
            return;
        }

        bool team = teamOnly && player.Value.Team is byte;
        ctx.Db.ChatMessage.Insert(new ChatMessage
        {
            MessageId = 0,
            Sender = ctx.Sender,
            SenderName = DisplayName(player.Value),
            Scope = (byte)(team ? ChatScope.Team : ChatScope.All),
            Team = player.Value.Team ?? 0,
            Recipient = default,
            IsSystem = false,
            Text = text,
            CreatedAt = ctx.Timestamp,
        });
    }

    // Dev commands. The verb is the first whitespace-delimited token, lowercased.
    private static void HandleCommand(ReducerContext ctx, Player player, string raw)
    {
        var parts = raw[1..].Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        switch (verb)
        {
            case "help":
                SystemTo(ctx, player.Identity,
                    "Commands:\n"
                    + "/help — show this list\n"
                    + "/pigs on|off — toggle AI drone spawns\n"
                    + "/resign — forfeit the match for your team");
                break;

            case "pigs":
                if (arg != "on" && arg != "off")
                {
                    SystemTo(ctx, player.Identity, "Usage: /pigs on|off");
                    break;
                }
                bool enable = arg == "on";
                if (ctx.Db.Match.Id.Find(0) is Match m)
                    ctx.Db.Match.Id.Update(m with { PigsEnabled = enable });
                SystemAll(ctx, $"{DisplayName(player)} turned AI drones {(enable ? "ON" : "OFF")}.");
                break;

            case "resign":
                ResignMatch(ctx, player);
                break;

            default:
                SystemTo(ctx, player.Identity, $"Unknown command: /{verb}. Try /help.");
                break;
        }
    }

    // /resign: forfeit the current match for the caller's team. Only meaningful while a
    // match is Active and the caller is on a side; the other team is awarded the win
    // (mirrors the base-destroyed path in SimulateTick).
    private static void ResignMatch(ReducerContext ctx, Player player)
    {
        if (player.Team is not byte t)
        {
            SystemTo(ctx, player.Identity, "You're not on a team.");
            return;
        }
        if (ctx.Db.Match.Id.Find(0) is not Match m || m.Phase != MatchPhase.Active)
        {
            SystemTo(ctx, player.Identity, "No match in progress.");
            return;
        }
        byte winner = (byte)(t == 0 ? 1 : 0);
        ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Ended, Winner = winner });
        SystemAll(ctx, $"Team {TeamName(t)} resigned — Team {TeamName(winner)} wins.");
        Log.Info($"[Resign] {player.Identity} (team {t}) resigned -> team {winner} wins");
    }

    // System line visible only to one player (command replies).
    private static void SystemTo(ReducerContext ctx, Identity recipient, string text) =>
        ctx.Db.ChatMessage.Insert(new ChatMessage
        {
            MessageId = 0,
            Sender = default,
            SenderName = "",
            Scope = (byte)ChatScope.Direct,
            Team = 0,
            Recipient = recipient,
            IsSystem = true,
            Text = text,
            CreatedAt = ctx.Timestamp,
        });

    // System line visible to everyone (match events).
    private static void SystemAll(ReducerContext ctx, string text) =>
        ctx.Db.ChatMessage.Insert(new ChatMessage
        {
            MessageId = 0,
            Sender = default,
            SenderName = "",
            Scope = (byte)ChatScope.All,
            Team = 0,
            Recipient = default,
            IsSystem = true,
            Text = text,
            CreatedAt = ctx.Timestamp,
        });

    private static string TeamName(byte team) => team == 0 ? "BLUE" : "RED";

    // Display name for chat, with the same fallback the lobby roster uses (Lobby.ShortId).
    private static string DisplayName(Player p)
    {
        if (!string.IsNullOrEmpty(p.Name))
            return p.Name;
        string s = p.Identity.ToString();
        return "Pilot " + (s.Length > 6 ? s[..6] : s);
    }

    // ---- Chat row-level visibility (server-enforced team privacy) ------
    // A client may read a ChatMessage if it matches ANY of these (the filters are unioned):
    // every All row, the Team rows for the team it is on, and Direct rows addressed to it.
    [SpacetimeDB.ClientVisibilityFilter]
    public static readonly Filter ChatAllVisible = new Filter.Sql(
        "SELECT * FROM ChatMessage WHERE Scope = 0");

    // Joins on Player.ChatTeam (a non-nullable mirror of Team) because STDB's RLS SQL can't
    // compare nullable/option columns — see the ChatTeam field on Player.
    [SpacetimeDB.ClientVisibilityFilter]
    public static readonly Filter ChatTeamVisible = new Filter.Sql(
        "SELECT c.* FROM ChatMessage c JOIN Player p ON p.ChatTeam = c.Team WHERE c.Scope = 1 AND p.Identity = :sender");

    [SpacetimeDB.ClientVisibilityFilter]
    public static readonly Filter ChatDirectVisible = new Filter.Sql(
        "SELECT * FROM ChatMessage WHERE Scope = 2 AND Recipient = :sender");

    // SpawnShip / Respawn reducers live in Ships.cs (M2).

    // ---- Lobby actions ------------------------------------------------

    // Pick a side — in the lobby, or to jump into an already-running match. Rejected
    // (logged, not thrown) when the chosen side would unbalance the rosters past the cap.
    // Changing teams clears the ready flag; joining a live match marks that side engaged
    // so its later abandonment ends the match.
    [SpacetimeDB.Reducer]
    public static void JoinTeam(ReducerContext ctx, byte team)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (!p.Online || team >= NumTeams)
            return;
        if (ctx.Db.Match.Id.Find(0)?.Phase == MatchPhase.Ended)
        {
            // The post-match screen routes through RestartMatch, not a direct join.
            Log.Info("[JoinTeam] match has ended; return to the lobby first");
            return;
        }
        if (!CanJoinTeam(ctx, team, ctx.Sender))
        {
            Log.Info($"[JoinTeam] team {team} is full (balance cap)");
            return;
        }

        ctx.Db.Player.Identity.Update(p with { Team = team, Ready = false, ChatTeam = team });
        MarkEngagedIfActive(ctx, team);
        Log.Info($"[JoinTeam] {ctx.Sender} -> team {team}");
    }

    // Drop back to the lobby with no team (and despawn any ship). Clears ready. If this
    // empties a side during a live match, the match ends.
    [SpacetimeDB.Reducer]
    public static void LeaveTeam(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (p.ShipId is ulong sid)
        {
            ctx.Db.Ship.ShipId.Delete(sid);
            DeleteShipInputs(ctx, sid);
        }
        ctx.Db.Player.Identity.Update(p with { Team = null, Ready = false, ShipId = null, ChatTeam = NoTeam });
        Log.Info($"[LeaveTeam] {ctx.Sender} -> lobby");
        EndMatchIfSideAbandoned(ctx);
    }

    // Toggle the lobby ready flag. Requires a team. Starting the match is checked
    // immediately so the last pilot to ready up kicks it off.
    [SpacetimeDB.Reducer]
    public static void SetReady(ReducerContext ctx, bool ready)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (p.Team is null)
        {
            Log.Info("[SetReady] pick a team first");
            return;
        }
        ctx.Db.Player.Identity.Update(p with { Ready = ready });
        MaybeStartMatch(ctx);
    }

    // One-tap "quick play": drop onto the smaller side and ready up. Used by the
    // headless autofly client and offered as a lobby shortcut.
    [SpacetimeDB.Reducer]
    public static void QuickJoin(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;
        var p = player.Value;
        if (!p.Online)
            return;
        if (ctx.Db.Match.Id.Find(0)?.Phase == MatchPhase.Ended)
            return;   // post-match: go through RestartMatch, not a direct join

        byte team = SmallestOnlineTeam(ctx, ctx.Sender);
        ctx.Db.Player.Identity.Update(p with { Team = team, Ready = true, ChatTeam = team });
        MarkEngagedIfActive(ctx, team);
        Log.Info($"[QuickJoin] {ctx.Sender} -> team {team} (ready)");
        MaybeStartMatch(ctx);
    }

    // After a match ends, wipe the battlefield and return everyone to the lobby. Only
    // valid from the Ended phase (the post-match screen's "Return to Lobby" button).
    [SpacetimeDB.Reducer]
    public static void RestartMatch(ReducerContext ctx)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null)
            return;
        var m = match.Value;
        if (m.Phase != MatchPhase.Ended)
        {
            Log.Info("[RestartMatch] only valid once the match has ended");
            return;
        }

        ResetWorld(ctx);

        // Prune players who left while the match was running; un-ready the rest but keep
        // their team so a rematch is one click away.
        foreach (var pl in ctx.Db.Player.Iter().ToList())
        {
            if (!pl.Online)
            {
                ctx.Db.Player.Identity.Delete(pl.Identity);
                continue;
            }
            ctx.Db.Player.Identity.Update(pl with { Ready = false, ShipId = null });
        }

        ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Lobby, Winner = null, EngagedTeams = 0, PigsEnabled = false });
        Log.Info("[RestartMatch] world reset -> Lobby");
    }

    // Record the input the client produced FOR sim tick `clientTick`, stored under
    // that tick so SimTick can apply the exact input the client predicted with.
    // Does NOT integrate motion — that happens only in SimTick. Highest-frequency
    // client call (~20 Hz). Overwrites if this (ship, tick) was already recorded.
    [SpacetimeDB.Reducer]
    public static void ApplyInput(
        ReducerContext ctx,
        float thrust, float strafeX, float strafeY,
        float yaw, float pitch, float roll,
        bool firing, bool boost, bool coast, uint clientTick)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null || player.Value.ShipId is not ulong shipId)
            return;

        ShipInput? existing = null;
        foreach (var r in ctx.Db.ShipInput.ByShipTick.Filter((shipId, clientTick)))
        {
            existing = r;
            break;
        }

        if (existing is ShipInput e)
        {
            ctx.Db.ShipInput.InputId.Update(e with
            {
                Thrust = thrust, StrafeX = strafeX, StrafeY = strafeY,
                Yaw = yaw, Pitch = pitch, Roll = roll, Firing = firing, Boost = boost, Coast = coast,
            });
        }
        else
        {
            ctx.Db.ShipInput.Insert(new ShipInput
            {
                InputId = 0,
                ShipId = shipId,
                Tick = clientTick,
                Thrust = thrust, StrafeX = strafeX, StrafeY = strafeY,
                Yaw = yaw, Pitch = pitch, Roll = roll, Firing = firing, Boost = boost, Coast = coast,
            });
        }

        // The buffer changed: make Pass A re-derive this ship's held input even if this
        // row's tick was already simulated (a late arrival under lag).
        _inputDirty.Add(shipId);

        // Amortized per-ship prune (~once per InputKeep ticks of this ship's sending):
        // replaces the old every-tick full-table sweep in SimulateTick. Despawn paths
        // still clear the whole buffer via DeleteShipInputs.
        if (clientTick % InputKeep == 0)
        {
            foreach (var r in ctx.Db.ShipInput.ShipId.Filter(shipId).ToList())
                if (r.Tick + InputKeep < clientTick)
                    ctx.Db.ShipInput.InputId.Delete(r.InputId);
        }
    }

    // ---- Scheduled simulation ----------------------------------------

    [SpacetimeDB.Reducer]
    public static void SimTick(ReducerContext ctx, SimTickTimer timer)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null)
            return;
        var m = match.Value;

        // Invariant: while the sim loop runs, the AI brain loop runs too. Self-heal if they
        // ever desync — chiefly after a hot-swap that ADDED the brain timer table to an
        // already-running sim (StartSim seeds it only on client connect, so without this the
        // brain wouldn't fire until a reconnect).
        if (ctx.Db.PigBrainTimer.Count == 0)
            ctx.Db.PigBrainTimer.Insert(new PigBrainTimer
            {
                ScheduledId = 0,
                ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(1000.0 / PigBrainHz)),
            });

        // Pace by REAL elapsed time, not by how often the scheduler fires us. Each
        // call integrates as many fixed-dt steps as wall-clock time has passed since
        // the last call, carrying the sub-step remainder so the rate is exact. This
        // keeps the sim at wall-clock speed on Maincloud (~10 Hz scheduling → 2
        // steps/call) and locally (~20 Hz → 1 step/call) alike, while every step is
        // still a deterministic fixed-dt integration the client predicts against.
        long now = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        long elapsed = m.LastTickMicros == 0 ? DtMicros : now - m.LastTickMicros;
        if (elapsed < 0) elapsed = 0;
        long accum = m.AccumMicros + elapsed;
        int steps = (int)(accum / DtMicros);
        accum -= (long)steps * DtMicros;
        if (steps > MaxCatchupSteps) { steps = MaxCatchupSteps; accum = 0; }

        uint tick = m.Tick;
        for (int s = 0; s < steps; s++)
            SimulateTick(ctx, ++tick);

        // Write the tick counter + timing once. Re-read so we keep any Phase/Winner
        // change a step made (the win condition writes Match inside SimulateTick).
        var cur = ctx.Db.Match.Id.Find(0)!.Value;
        ctx.Db.Match.Id.Update(cur with { Tick = tick, LastTickMicros = now, AccumMicros = accum });

        // Benchmark instrumentation (ai-benching): log every 100 ticks so the wall-clock
        // gap between log lines / 100 gives the real per-tick cost of SimTick (incl.
        // catch-up steps), independent of the host's invocation cadence.
        if (tick % 100 < (uint)steps)
            Log.Info($"[Bench] tick={tick} steps={steps} ships={ctx.Db.Ship.Count} pendingShots={ctx.Db.ShotResolution.Count}");
    }

    // One fixed-dt authoritative step for sim tick `tick`.
    private static void SimulateTick(ReducerContext ctx, uint tick)
    {
        float dt = FlightModel.Dt;
        var worldCfg = WorldConfigOrDefault(ctx);

        // Shots whose analytic impact/expiry lands on this tick (see TryFire/ResolveDueShots
        // in Weapons.cs) — applied before integration, where the old per-shot scheduler
        // would have fired between sim transactions.
        ResolveDueShots(ctx, tick);

        // AI drone (PIG) lifecycle + target SELECTION no longer live here — they run in the
        // separate, slower PigBrainTick reducer (PigAI.cs), which caches one PigDecision per
        // live drone. This sim tick just integrates whatever ships exist and (in Pass A)
        // re-steers each drone toward its cached decision. Drone DEATHS still happen here
        // (Pass C / KillPig), and pods are still flown per-tick by PodThink below.

        // --- Pass A: integrate every ship, fire if the trigger is held & cooled, and warp it
        // through any aleph it flew into — all in ONE pass. Snapshot to a list first (we mutate
        // rows while iterating), and collect the post-integration ships into `live` so the
        // hit/collision passes below reuse it instead of re-reading the whole Ship table.
        var live = new List<Ship>();
        foreach (var ship in ctx.Db.Ship.Iter().ToList())
        {
            // PIGs are server-driven: the AI brain (PigAI.cs) synthesizes this tick's
            // input from world state and updates the drone's behaviour row. Player ships
            // instead replay the EXACT per-tick input the client stamped (or the most
            // recent one with Tick <= this tick if it hasn't arrived) — matching the
            // client's input sequence is what makes auth == prediction.
            ShipInputState input;
            if (ship.IsPig && ship.IsPod)
            {
                // A PIG pod auto-flies to the nearest friendly base, decided fresh each tick
                // (cheap — pods aren't brained on the slower schedule).
                input = PodThink(ctx, ship, tick);
            }
            else if (ship.IsPig)
            {
                // Combat drone: replay the decision PigBrainTick last cached for it. PigExecute
                // cheaply re-steers/leads/fires toward that decision against current world state
                // (the expensive target selection ran in the separate brain reducer). No row yet
                // — just spawned, brain hasn't fired since — so coast a tick or two until it does.
                input = ctx.Db.PigDecision.ShipId.Find(ship.ShipId) is PigDecision pd
                    ? PigExecute(ctx, ship, pd, tick)
                    : default;
            }
            else
            {
                // Exact hit first: the client stamped this very tick — direct index hit on
                // (ShipId, Tick). With on-change senders this only happens on ticks where
                // the stick state changed; in between, the held input below replays for free.
                ShipInput? src = null;
                foreach (var r in ctx.Db.ShipInput.ByShipTick.Filter((ship.ShipId, tick)))
                {
                    src = r;
                    break;
                }
                if (src is ShipInput si)
                {
                    input = ToInputState(si);
                    _heldInput[ship.ShipId] = input;
                    _inputDirty.Remove(ship.ShipId);
                }
                else if (!_inputDirty.Contains(ship.ShipId)
                         && _heldInput.TryGetValue(ship.ShipId, out var held))
                {
                    // Nothing arrived since the last derive: the stick state is unchanged —
                    // replay it with zero datastore reads. This is the common per-tick path.
                    input = held;
                }
                else
                {
                    // A row arrived since the last derive (possibly LATE, for a tick already
                    // simulated), or the cache is cold (fresh spawn / module hot-swap):
                    // re-derive as the most recent buffered input before this tick — the
                    // same value the client predicted those ticks with.
                    ShipInput? latest = null;
                    foreach (var r in ctx.Db.ShipInput.ShipId.Filter(ship.ShipId))
                    {
                        if (r.Tick < tick && (latest is null || r.Tick > latest.Value.Tick))
                            latest = r;
                    }
                    input = latest is ShipInput li ? ToInputState(li) : default;
                    _heldInput[ship.ShipId] = input;
                    _inputDirty.Remove(ship.ShipId);
                }
            }
            // Per-ship flight stats from the class's ShipClassDef (pods resolve to the Pod
            // def), derived once and cached; falls back to FlightModel defaults if unseeded.
            var stats = ShipStatsFor(ctx, ClassIdOf(ship));

            var state = new ShipState
            {
                Pos = new Vec3(ship.PosX, ship.PosY, ship.PosZ),
                Vel = new Vec3(ship.VelX, ship.VelY, ship.VelZ),
                Rot = new Quat(ship.RotX, ship.RotY, ship.RotZ, ship.RotW),
                AngVel = new Vec3(ship.AngVelX, ship.AngVelY, ship.AngVelZ),
                Mass = ship.Mass,
                AbPower = ship.AbPower,
            };

            state = FlightModel.Integrate(state, input, stats);

            // Fire control: spawn a projectile from the ship's Weapon hardpoint when held
            // and the per-weapon cooldown has elapsed (tracked against Match.Tick). Muzzle
            // offset/forward, damage, interval, speed, life and spread all come from the
            // ship's ShipClassDef hardpoint + the WeaponDef it names (see Weapons.cs). Pods
            // are unarmed — TryFire never fires them.
            uint lastFire = TryFire(ctx, ship, state, input.Firing && !worldCfg.DebugNoFire, tick, ship.LastFireTick);

            var updated = ship with
            {
                PosX = state.Pos.X, PosY = state.Pos.Y, PosZ = state.Pos.Z,
                VelX = state.Vel.X, VelY = state.Vel.Y, VelZ = state.Vel.Z,
                RotX = state.Rot.X, RotY = state.Rot.Y, RotZ = state.Rot.Z, RotW = state.Rot.W,
                AngVelX = state.AngVel.X, AngVelY = state.AngVel.Y, AngVelZ = state.AngVel.Z,
                AbPower = state.AbPower,
                // Stamp with the SERVER tick (this state's integration index, since
                // Match.Tick increments once per integration). Gives the client a
                // shared, drift-free anchor so predicted[N] and auth[N] are the same
                // step count. The client (ShipController) predicts in this tick space.
                LastInputTick = tick,
                LastFireTick = lastFire,
            };

            // Aleph warp folded into the same pass: if the freshly-integrated position is
            // inside an aleph trigger, carry the ship THROUGH the funnel before persisting.
            updated = TryWarp(ctx, updated);

            ctx.Db.Ship.ShipId.Update(updated);
            live.Add(updated);
        }

        // The post-integration ship set IS `live` (every ship was updated above, none
        // spawned/deleted since) — reuse it for the hit/collision passes instead of re-reading
        // the whole Ship table. Damage is accumulated here and applied once at the end.
        var ships = live;
        var bases = ctx.Db.Base.Iter().ToList();
        // Asteroids are NOT snapshotted into a flat list — the hit/collision passes below
        // broad-phase them through the per-sector spatial grid (HitsAsteroid /
        // ResolveAsteroidCollisions), so a tick costs ~O(entities) instead of O(ships+projectiles
        // × asteroids). The grid is built once and reused (invalidated only by GenerateMap).
        var damage = new Dictionary<ulong, float>();   // shipId -> hull damage this tick

        // --- Pass C: collisions, then apply all damage and kill at <= 0 health.
        // Enemy ship-vs-ship: mutual damage + separation (pairwise; N is tiny here).
        for (int i = 0; i < ships.Count; i++)
        {
            for (int j = i + 1; j < ships.Count; j++)
            {
                var a = ships[i];
                var b = ships[j];
                if (a.Team == b.Team || a.SectorId != b.SectorId) continue;

                float dx = a.PosX - b.PosX, dy = a.PosY - b.PosY, dz = a.PosZ - b.PosZ;
                float dist2 = dx * dx + dy * dy + dz * dz;
                float minD = 2f * ShipRadius;
                if (dist2 >= minD * minD) continue;

                float dist = MathF.Sqrt(dist2);
                float nx, ny, nz;
                if (dist > 1e-4f) { nx = dx / dist; ny = dy / dist; nz = dz / dist; }
                else { nx = 0f; ny = 1f; nz = 0f; }

                // Mass-weighted response: heavier ships deflect less and are shoved
                // out less. iA/iB are inverse masses (guard against a missing/zero
                // mass with a unit fallback). With equal mass this reduces exactly to
                // the old 50/50 split.
                float iA = a.Mass > 0f ? 1f / a.Mass : 1f;
                float iB = b.Mass > 0f ? 1f / b.Mass : 1f;
                float invSum = iA + iB;

                float relVn = (a.VelX - b.VelX) * nx + (a.VelY - b.VelY) * ny + (a.VelZ - b.VelZ) * nz;
                if (relVn < 0f)
                {
                    // Momentum-conserving normal impulse: J = -(1+e) * relVn / (iA+iB).
                    float jimp = -(1f + CollisionRestitution) * relVn / invSum;
                    a.VelX += jimp * iA * nx; a.VelY += jimp * iA * ny; a.VelZ += jimp * iA * nz;
                    b.VelX -= jimp * iB * nx; b.VelY -= jimp * iB * ny; b.VelZ -= jimp * iB * nz;

                    // Damage scales with the impact's reduced mass and closing speed,
                    // so heavier/faster collisions hurt more (both ships feel the same
                    // mutual impulse). reducedMass = (mA*mB)/(mA+mB) = 1/(iA+iB).
                    float reducedMass = 1f / invSum;
                    float dmg = MathF.Min(-relVn * reducedMass * ShipShipDamageScale, MaxCollisionDamage);
                    damage[a.ShipId] = (damage.TryGetValue(a.ShipId, out var da) ? da : 0f) + dmg;
                    damage[b.ShipId] = (damage.TryGetValue(b.ShipId, out var db) ? db : 0f) + dmg;
                }
                // Mass-weighted positional separation (heavy ship moves out less).
                float pen = minD - dist;
                float pushA = pen * (iA / invSum);
                float pushB = pen * (iB / invSum);
                a.PosX += nx * pushA; a.PosY += ny * pushA; a.PosZ += nz * pushA;
                b.PosX -= nx * pushB; b.PosY -= ny * pushB; b.PosZ -= nz * pushB;
                ships[i] = a;
                ships[j] = b;
            }
        }

        // Sector lookup for the out-of-bounds check (table is tiny — a couple of rows).
        var sectors = ctx.Db.Sector.Iter().ToList();

        foreach (var s0 in ships)
        {
            var s = s0;
            if (damage.TryGetValue(s.ShipId, out var d))
                s.Health -= d;

            // Sector boundary: a ship beyond its sector radius takes mounting hull
            // damage (the "invisible boundary") until it returns to bounds or dies.
            foreach (var sec in sectors)
            {
                if (sec.SectorId != s.SectorId) continue;
                float ddx = s.PosX - sec.CenterX, ddy = s.PosY - sec.CenterY, ddz = s.PosZ - sec.CenterZ;
                float over = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) - sec.Radius;
                if (over > 0f)
                {
                    float dps = MathF.Min(BoundaryBaseDps + over * BoundaryRampDps, BoundaryMaxDps);
                    s.Health -= dps * dt;
                }
                break;
            }

            // Asteroids in this SHIP's sector bounce it (broad-phased via the spatial grid).
            s = ResolveAsteroidCollisions(ctx, s);

            // Bases in this ship's sector: an ENEMY base is solid (bounce + damage); your
            // OWN base is your dock. Fly into its core (within dockR) and you DOCK —
            // the ship/pod despawns. For a player ship that reopens the spawn menu (a
            // voluntary re-ship); for a player pod or a PIG pod it's "reached a friendly
            // base" and resolves the pod. Docking ends this ship's tick, so skip the rest.
            bool docked = false;
            float baseR = BaseRadiusOf(ctx);
            float dockR = baseR * DockRadiusFrac;
            foreach (var b in bases)
            {
                if (b.SectorId != s.SectorId) continue;
                if (b.Team != s.Team)
                {
                    s = ResolveCollision(s, b.PosX, b.PosY, b.PosZ, baseR);
                    continue;
                }
                if (Dist2(s.PosX, s.PosY, s.PosZ, b.PosX, b.PosY, b.PosZ) <= dockR * dockR)
                {
                    DockShip(ctx, s, tick);
                    docked = true;
                    break;
                }
            }
            if (docked)
                continue;

            if (s.Health <= 0f)
            {
                // A dying POD just vanishes: a player pod clears the owner's ShipId (spawn
                // menu reappears) via KillShip; a PIG pod has no Player row so KillShip is a
                // clean delete. Pods don't eject pods.
                if (s.IsPod)
                    KillShip(ctx, s);
                // A dying PIG combat drone ejects a PIG pod and frees its slot (KillPig).
                else if (s.IsPig)
                    KillPig(ctx, s, tick);
                // A dying player COMBAT ship ejects a player-piloted escape pod instead of
                // going straight to the spawn menu (SpawnPodFor repoints Player.ShipId).
                else
                    SpawnPodFor(ctx, s);
            }
            else
                ctx.Db.Ship.ShipId.Update(s);
        }

        // --- Rescue pass: a pod in DIRECT hull contact with a friendly non-pod ship (player
        // OR drone) in the same sector is rescued — the same resolution as docking (player pod
        // → spawn menu; PIG pod despawns). Reads LIVE rows because the death/dock pass above
        // mutated the table. The tight RescueRadius (hulls touching) means rescue only happens
        // when a teammate/drone actually flies onto the pod, not by merely passing nearby — so
        // a pod ejected mid-dogfight isn't instantly resolved; left alone it floats home or dies.
        // Read the live rows ONCE (the death/dock pass above mutated the table) and reuse the
        // snapshot for both the pod scan and the friend scan, instead of re-iterating per pod.
        var rescueShips = ctx.Db.Ship.Iter().ToList();
        foreach (var pod in rescueShips)
        {
            if (!pod.IsPod)
                continue;
            foreach (var friend in rescueShips)
            {
                if (friend.IsPod || friend.Team != pod.Team || friend.SectorId != pod.SectorId)
                    continue;
                if (Dist2(pod.PosX, pod.PosY, pod.PosZ, friend.PosX, friend.PosY, friend.PosZ)
                    <= RescueRadius * RescueRadius)
                {
                    DockShip(ctx, pod, tick);
                    break;
                }
            }
        }

        // Input-buffer pruning no longer happens here: ApplyInput prunes each ship's own
        // buffer amortized (every InputKeep-th stamped tick), and despawn paths clear it
        // outright — the old every-tick full-table sweep was O(ships x InputKeep) rows.
    }

    // ---- Helpers ------------------------------------------------------

    private static float Dist2(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }

    // If `ship`'s (freshly-integrated) position is inside an aleph trigger in its sector, return
    // it carried THROUGH the funnel: same velocity/orientation magnitude (momentum carries
    // through), but its SectorId becomes the partner's and it re-emerges just past the partner
    // aleph so it doesn't immediately warp back. Otherwise returns `ship` unchanged. A pure
    // transform on one ship + the (static) aleph/sector tables; the caller persists the result.
    private static Ship TryWarp(ReducerContext ctx, Ship ship)
    {
        foreach (var al in ctx.Db.Aleph.Iter())
        {
            if (al.SectorId != ship.SectorId)
                continue;
            float rr = AlephTriggerRadius + ShipRadius;
            if (Dist2(ship.PosX, ship.PosY, ship.PosZ, al.PosX, al.PosY, al.PosZ) > rr * rr)
                continue;
            if (ctx.Db.Aleph.AlephId.Find(al.PartnerId) is not Aleph partner)
                break;

            // Emerge OUT THE MOUTH. The funnel (AlephView) is oriented so its mouth faces
            // toward the sector center. The ship pops out of the partner's mouth along that
            // axis — toward the partner's sector center — far enough to clear the partner's
            // trigger sphere (no instant re-warp).
            //
            // The funnel discards the ship's heading: only the RAW SPEED carries through. The
            // exit vector is the mouth axis (partner aleph → its sector center), jittered by a
            // small random cone so successive ships fan out instead of stacking in a line.
            // Position and velocity share the one jittered direction so the ship travels along
            // the axis it emerged on. Warp is server-authoritative, so this RNG never has to be
            // reproduced by client prediction. Orientation/angular momentum are left untouched.
            float speed = MathF.Sqrt(ship.VelX * ship.VelX + ship.VelY * ship.VelY + ship.VelZ * ship.VelZ);

            // Mouth direction: from partner aleph toward its sector center.
            var destSec = ctx.Db.Sector.SectorId.Find(al.DestSectorId);
            float mx = (destSec?.CenterX ?? 0f) - partner.PosX;
            float my = (destSec?.CenterY ?? 0f) - partner.PosY;
            float mz = (destSec?.CenterZ ?? 0f) - partner.PosZ;
            float mlen = MathF.Sqrt(mx * mx + my * my + mz * mz);
            if (mlen < 0.001f) { mx = 0f; my = 1f; mz = 0f; mlen = 1f; } // fallback: up
            mx /= mlen; my /= mlen; mz /= mlen;

            // Jitter around the mouth axis.
            float jx = (float)(ctx.Rng.NextDouble() * 2.0 - 1.0) * WarpExitJitter;
            float jy = (float)(ctx.Rng.NextDouble() * 2.0 - 1.0) * WarpExitJitter;
            float jz = (float)(ctx.Rng.NextDouble() * 2.0 - 1.0) * WarpExitJitter;
            float ex = mx + jx;
            float ey = my + jy;
            float ez = mz + jz;
            float elen = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            ex /= elen; ey /= elen; ez /= elen;

            float exit = AlephTriggerRadius + ShipRadius + WarpExitOffset;
            Log.Info($"[Warp] ship {ship.ShipId} {al.SectorId} -> {al.DestSectorId}");
            return ship with
            {
                SectorId = al.DestSectorId,
                PosX = partner.PosX + ex * exit,
                PosY = partner.PosY + ey * exit,
                PosZ = partner.PosZ + ez * exit,
                VelX = ex * speed,
                VelY = ey * speed,
                VelZ = ez * speed,
            };
        }
        return ship;
    }

    // Resolve a ship overlapping a static sphere (asteroid / base): on inward
    // impact apply impact-scaled damage and a damped bounce, then push the ship
    // back to the sphere surface so it can't sink through. No-op when separated.
    private static Ship ResolveCollision(Ship s, float cx, float cy, float cz, float radius)
    {
        float dx = s.PosX - cx, dy = s.PosY - cy, dz = s.PosZ - cz;
        float dist2 = dx * dx + dy * dy + dz * dz;
        float minD = radius + ShipRadius;
        if (dist2 >= minD * minD)
            return s;

        float dist = MathF.Sqrt(dist2);
        float nx, ny, nz;
        if (dist > 1e-4f) { nx = dx / dist; ny = dy / dist; nz = dz / dist; }
        else { nx = 0f; ny = 1f; nz = 0f; }

        float vn = s.VelX * nx + s.VelY * ny + s.VelZ * nz;
        if (vn < 0f) // moving into the obstacle
        {
            float dmg = MathF.Min(-vn * CollisionDamageScale, MaxCollisionDamage);
            s.Health -= dmg;
            float jimp = (1f + CollisionRestitution) * vn;
            s.VelX -= jimp * nx; s.VelY -= jimp * ny; s.VelZ -= jimp * nz;
        }
        s.PosX = cx + nx * minD; s.PosY = cy + ny * minD; s.PosZ = cz + nz * minD;
        return s;
    }

    // KillShip / SpawnPodFor / DockShip live in Ships.cs (M2).

    private static ShipInputState ToInputState(ShipInput i) => new ShipInputState
    {
        Thrust = i.Thrust,
        StrafeX = i.StrafeX,
        StrafeY = i.StrafeY,
        Yaw = i.Yaw,
        Pitch = i.Pitch,
        Roll = i.Roll,
        Firing = i.Firing,
        Boost = i.Boost,
        Coast = i.Coast,
    };

    // Delete every buffered input for a ship (ShipId is an index, not the PK now), and
    // drop its held-input cache entries (ShipIds are never recycled, but stay tidy).
    private static void DeleteShipInputs(ReducerContext ctx, ulong shipId)
    {
        foreach (var r in ctx.Db.ShipInput.ShipId.Filter(shipId).ToList())
            ctx.Db.ShipInput.InputId.Delete(r.InputId);
        _heldInput.Remove(shipId);
        _inputDirty.Remove(shipId);
    }

    // SpawnShipInternal lives in Ships.cs (M2).

    // Count online, teamed players per side, optionally excluding one identity (so a
    // player switching teams isn't counted on their old side).
    private static int[] OnlineTeamCounts(ReducerContext ctx, Identity? exclude)
    {
        var counts = new int[NumTeams];
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (!p.Online || (exclude is Identity ex && p.Identity == ex))
                continue;
            if (p.Team is byte t && t < NumTeams)
                counts[t]++;
        }
        return counts;
    }

    // Balance cap: a player may only join a side that isn't already larger than another
    // side (so rosters never differ by more than one). `self` is excluded from the count.
    private static bool CanJoinTeam(ReducerContext ctx, byte team, Identity self)
    {
        var counts = OnlineTeamCounts(ctx, self);
        int min = counts[0];
        for (byte t = 1; t < NumTeams; t++)
            if (counts[t] < min) min = counts[t];
        return counts[team] <= min;
    }

    // The side with the fewest online players (ties -> team 0). Used by QuickJoin.
    private static byte SmallestOnlineTeam(ReducerContext ctx, Identity self)
    {
        var counts = OnlineTeamCounts(ctx, self);
        byte best = 0;
        for (byte t = 1; t < NumTeams; t++)
            if (counts[t] < counts[best]) best = t;
        return best;
    }

    // Tear the battlefield down to a fresh state: despawn all ships (player + drone)
    // and their inputs, clear projectiles, reset every base to full hull. Used when a
    // match starts and when one restarts. Players' ShipId is cleared by the caller.
    private static void ResetWorld(ReducerContext ctx)
    {
        DespawnAllPigs(ctx);   // deletes drone ships and resets their slots to dormant
        foreach (var s in ctx.Db.Ship.Iter().ToList())
        {
            DeleteShipInputs(ctx, s.ShipId);
            ctx.Db.Ship.ShipId.Delete(s.ShipId);
        }
        // Pending shot outcomes die with the battlefield (their targets are being reset).
        foreach (var sr in ctx.Db.ShotResolution.Iter().ToList())
            ctx.Db.ShotResolution.ShotId.Delete(sr.ShotId);
        float baseHp = BaseMaxHealthOf(ctx);
        foreach (var b in ctx.Db.Base.Iter().ToList())
            ctx.Db.Base.BaseId.Update(b with { Health = baseHp });
    }

    // True if any player connection is currently online.
    private static bool AnyOnline(ReducerContext ctx)
    {
        foreach (var p in ctx.Db.Player.Iter())
            if (p.Online)
                return true;
        return false;
    }

    // True if any ONLINE player is on a team (i.e. actually in the match, not a teamless
    // observer/CLI connection). Drives the PIG combat gate: drones keep fighting while a
    // teamed pilot is present, even between their ships, and stop when the last one leaves.
    private static bool AnyTeamedPlayerOnline(ReducerContext ctx)
    {
        foreach (var p in ctx.Db.Player.Iter())
            if (p.Online && p.Team is byte)
                return true;
        return false;
    }

    // Start (resume) the 20 Hz sim loop if it isn't already scheduled. Idempotent:
    // multiple connecting clients only ever leave one timer row in place.
    private static void StartSim(ReducerContext ctx)
    {
        if (ctx.Db.SimTickTimer.Count > 0)
            return;

        // Reset the real-time pacing anchor so the first tick after a pause
        // integrates a single fixed step instead of trying to "catch up" the
        // entire idle gap (LastTickMicros == 0 makes SimTick use one DtMicros).
        var match = ctx.Db.Match.Id.Find(0);
        if (match is Match m)
            ctx.Db.Match.Id.Update(m with { LastTickMicros = 0, AccumMicros = 0 });

        ctx.Db.SimTickTimer.Insert(new SimTickTimer
        {
            ScheduledId = 0,
            ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(1000.0 / SimTickHz)),
        });

        // The AI decision loop rides the same lifecycle, but on its own slower schedule.
        if (ctx.Db.PigBrainTimer.Count == 0)
            ctx.Db.PigBrainTimer.Insert(new PigBrainTimer
            {
                ScheduledId = 0,
                ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(1000.0 / PigBrainHz)),
            });
        Log.Info($"[Sim] resumed @ {SimTickHz}Hz (AI brain @ {PigBrainHz}Hz)");
    }

    // Stop the sim loop by removing the scheduled-timer row(s). With no rows,
    // SimTick stops firing entirely until a client reconnects and StartSim runs.
    private static void StopSim(ReducerContext ctx)
    {
        foreach (var t in ctx.Db.SimTickTimer.Iter().ToList())
            ctx.Db.SimTickTimer.ScheduledId.Delete(t.ScheduledId);
        foreach (var t in ctx.Db.PigBrainTimer.Iter().ToList())
            ctx.Db.PigBrainTimer.ScheduledId.Delete(t.ScheduledId);
        Log.Info("[Sim] paused (no clients connected)");
    }

    // Mark a side as having fielded a human this match, but only while one is running —
    // so a player who jumps into a live match makes that side's later abandonment count.
    private static void MarkEngagedIfActive(ReducerContext ctx, byte team)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is Match m && m.Phase == MatchPhase.Active && team < NumTeams)
        {
            byte engaged = (byte)(m.EngagedTeams | (1 << team));
            if (engaged != m.EngagedTeams)
                ctx.Db.Match.Id.Update(m with { EngagedTeams = engaged });
        }
    }

    // During a live match, end it if every pilot on an ENGAGED side has left (disconnect
    // or back to the lobby). If anyone is still flying, their side wins by forfeit; if the
    // server emptied out entirely, quietly reset to the lobby (no audience for a result).
    // A no-op unless a match is actually Active and an engaged side just hit zero.
    private static void EndMatchIfSideAbandoned(ReducerContext ctx)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null || match.Value.Phase != MatchPhase.Active)
            return;
        var m = match.Value;

        var counts = OnlineTeamCounts(ctx, null);
        int total = 0;
        for (byte t = 0; t < NumTeams; t++) total += counts[t];

        bool abandoned = false;
        for (byte t = 0; t < NumTeams; t++)
            if ((m.EngagedTeams & (1 << t)) != 0 && counts[t] == 0)
                abandoned = true;
        if (!abandoned)
            return;

        if (total == 0)
        {
            // Everyone left — wipe the battlefield and drop back to an empty lobby. Prune
            // the now-orphaned offline rows so the next session starts clean.
            ResetWorld(ctx);
            foreach (var pl in ctx.Db.Player.Iter().ToList())
            {
                if (!pl.Online)
                    ctx.Db.Player.Identity.Delete(pl.Identity);
                else
                    ctx.Db.Player.Identity.Update(pl with { Ready = false, ShipId = null });
            }
            ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Lobby, Winner = null, EngagedTeams = 0 });
            Log.Info("[Match] all pilots left -> Lobby");
            return;
        }

        // A side was abandoned but the other is still fighting — award them the win.
        byte winner = 0;
        for (byte t = 0; t < NumTeams; t++)
            if (counts[t] > 0) { winner = t; break; }
        ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Ended, Winner = winner });
        Log.Info($"[Match] team {winner} wins by forfeit (enemy side left)");
    }

    // Lobby -> Active once everyone who has joined a side is readied up. Solo is
    // allowed: the AI drones (PIGs) provide opposition, so one readied pilot can launch.
    private static void MaybeStartMatch(ReducerContext ctx)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null || match.Value.Phase != MatchPhase.Lobby)
            return;

        int teamed = 0, readied = 0;
        byte engaged = 0;
        foreach (var p in ctx.Db.Player.Iter())
        {
            if (!p.Online || p.Team is not byte t)
                continue;
            teamed++;
            if (p.Ready) readied++;
            engaged |= (byte)(1 << t);
        }

        // Need at least one readied pilot and NO teamed player still un-ready.
        if (teamed == 0 || readied == 0 || readied != teamed)
            return;

        // Fresh battlefield, and consume the ready flags so the next lobby starts clean.
        ResetWorld(ctx);
        foreach (var p in ctx.Db.Player.Iter().ToList())
            if (p.Ready)
                ctx.Db.Player.Identity.Update(p with { Ready = false });

        ctx.Db.Match.Id.Update(match.Value with { Phase = MatchPhase.Active, Winner = null, EngagedTeams = engaged });
        Log.Info($"[Match] {readied} pilot(s) ready -> Active (engaged sides 0b{System.Convert.ToString(engaged, 2)})");
    }
}
