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
    // Match epoch: a counter bumped every time a match goes Active (and on RestartMatch).
    // It is folded into each minted JoinToken's signature so a token from a previous match
    // can't be replayed against the sim server's current epoch — see MintJoinToken and
    // shared/JoinTokens.cs. Clients ignore it; the sim server pins it from the first Hello.
    public ulong Epoch;
}

// ---- Native sim-server handoff (Phase 1c) -----------------------------
// The 20 Hz match sim can run on the native sim server (server/); STDB keeps the lobby.
// When a match goes Active the module mints one JoinToken per participating player
// (derived from a shared secret — see shared/JoinTokens.cs) and clients hand it to the
// sim server, which validates it offline. SimEndpoint advertises where to connect.

// Where the native sim server lives (singleton; row absent = native mode disabled and
// clients keep flying the in-module sim).
[SpacetimeDB.Table(Accessor = "SimEndpoint", Public = true)]
public partial struct SimEndpoint
{
    [PrimaryKey]
    public byte Id;       // always 0
    public string Url;    // e.g. ws://host:8090/game
}

// Per-player match credential. Public but RLS-filtered to the owning player (see
// JoinTokenVisible), so a client only ever sees its own token.
[SpacetimeDB.Table(Accessor = "JoinToken", Public = true)]
public partial struct JoinToken
{
    [PrimaryKey]
    public Identity Identity;
    public byte Team;
    public ulong MatchId;   // match epoch the token is bound to (replay guard)
    public long Expiry;     // absolute expiry, unix seconds (the sim server enforces it)
    public string Token;    // HMAC-SHA256 over (identity, team, MatchId, Expiry)
}

// The shared secret the tokens derive from. Private: reducers + DB owner only.
[SpacetimeDB.Table(Accessor = "SimConfig", Public = false)]
public partial struct SimConfig
{
    [PrimaryKey]
    public byte Id;       // always 0
    public string Secret;
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
    }

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
            Epoch = 0,
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

        // NOTE: there is no in-module sim loop any more. The 20 Hz authoritative match runs
        // entirely on the native sim server (server/); this module owns lobby/teams/chat/
        // defs/the static world/match-results and mints join tokens (see .PLAN/NATIVE-SIM.md).

        Log.Info($"[Init] done: 1 match, 2 sectors, 2 bases, {ctx.Db.Asteroid.Count} asteroids (scale {WorldConfigOrDefault(ctx).SectorScale}), 1 aleph pair, {ctx.Db.ShipClassDef.Count} ship/{ctx.Db.WeaponDef.Count} weapon/{ctx.Db.BaseDef.Count} base defs; native sim owns gameplay");
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
    }

    // A client disconnected: mark offline (the native sim owns any live ship).
    [SpacetimeDB.Reducer(ReducerKind.ClientDisconnected)]
    public static void ClientDisconnected(ReducerContext ctx)
    {
        var player = ctx.Db.Player.Identity.Find(ctx.Sender);
        if (player is null)
            return;

        var p = player.Value;
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
                    + "/start — force-start the match now (debug; skips waiting for others to ready)\n"
                    + "/resign — forfeit the match for your team");
                break;

            case "start":
                StartMatchNow(ctx, player);
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

    // A player may only read their OWN join token (the sim server validates it offline).
    [SpacetimeDB.ClientVisibilityFilter]
    public static readonly Filter JoinTokenVisible = new Filter.Sql(
        "SELECT * FROM JoinToken WHERE Identity = :sender");

    // ---- Native sim-server handoff (Phase 1c) --------------------------

    // Owner-only: advertise the native sim server and install the shared token secret.
    // Run once per deployment — in production the URL is the TLS-terminating ingress, e.g.
    // `spacetime call ... set_sim_endpoint '"wss://sim.example.com/game"' '"<secret>"'`
    // (the hosting layer terminates TLS and forwards to the sim container's plain ws:// :8090;
    // local dev uses ws://localhost:8090/game). The secret should be >=32 random bytes
    // (`openssl rand -hex 32`) and must match the sim server's SIM_SECRET. An empty url tears
    // native mode down (no gameplay until it's set again — the sim now lives only here).
    [SpacetimeDB.Reducer]
    public static void SetSimEndpoint(ReducerContext ctx, string url, string secret)
    {
        RequireOwner(ctx);
        foreach (var e in ctx.Db.SimEndpoint.Iter().ToList())
            ctx.Db.SimEndpoint.Id.Delete(e.Id);
        foreach (var c in ctx.Db.SimConfig.Iter().ToList())
            ctx.Db.SimConfig.Id.Delete(c.Id);
        foreach (var t in ctx.Db.JoinToken.Iter().ToList())
            ctx.Db.JoinToken.Identity.Delete(t.Identity);

        if (string.IsNullOrEmpty(url))
        {
            Log.Info("[SimEndpoint] cleared — native sim disabled");
            return;
        }
        ctx.Db.SimEndpoint.Insert(new SimEndpoint { Id = 0, Url = url });
        ctx.Db.SimConfig.Insert(new SimConfig { Id = 0, Secret = secret });
        Log.Info($"[SimEndpoint] native sim at {url}");
    }

    // Result writeback from the native sim server (Phase 2). When the authoritative match
    // ends on the sim server, it POSTs this reducer (HTTP API, owner Bearer token) so STDB —
    // which still owns the durable lobby/post-match flow — learns the winner and the existing
    // post-match UI (RestartMatch) lights up. Owner-gated; idempotent (no-op unless a match
    // is Active, so a duplicate POST or a manual /resign that already ended it is harmless).
    [SpacetimeDB.Reducer]
    public static void ReportMatchResult(ReducerContext ctx, byte winner)
    {
        RequireOwner(ctx);
        if (ctx.Db.Match.Id.Find(0) is not Match m || m.Phase != MatchPhase.Active)
            return;
        ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Ended, Winner = winner });
        SystemAll(ctx, $"Team {TeamName(winner)} wins.");
        Log.Info($"[Match] native sim reported winner: team {winner}");
    }

    // A minted join token is valid this long (one match plus generous margin) before the
    // sim server rejects it against its own clock — bounds the replay window of a leaked token.
    private const long TokenTtlSeconds = 6 * 3600;

    // Absolute expiry (unix seconds) for a token minted now. ctx.Timestamp is the
    // deterministic reducer clock (microseconds since epoch).
    private static long TokenExpiry(ReducerContext ctx) =>
        ctx.Timestamp.MicrosecondsSinceUnixEpoch / 1_000_000 + TokenTtlSeconds;

    // Mint (or refresh) one player's match credential. No-op when native mode is off.
    // The token is an HMAC-SHA256 binding identity+team+match-epoch+expiry (shared/JoinTokens.cs);
    // the sim server recomputes the same MAC from the Hello fields and constant-time compares,
    // so a client can't claim another side, replay a previous match's token (different epoch),
    // or use a stale one (past expiry). The epoch + expiry ride the JoinToken row to the client.
    private static void MintJoinToken(ReducerContext ctx, Identity identity, byte team, ulong epoch, long expiryUnix)
    {
        if (ctx.Db.SimConfig.Id.Find((byte)0) is not SimConfig cfg)
            return;
        var row = new JoinToken
        {
            Identity = identity,
            Team = team,
            MatchId = epoch,
            Expiry = expiryUnix,
            Token = JoinTokens.Compute(cfg.Secret, identity.ToString(), team, epoch, expiryUnix),
        };
        if (ctx.Db.JoinToken.Identity.Find(identity) is null)
            ctx.Db.JoinToken.Insert(row);
        else
            ctx.Db.JoinToken.Identity.Update(row);
    }

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
        // Joining a LIVE match: hand over a credential for the native sim server too, bound
        // to the running match's epoch so the sim server accepts it alongside the others.
        if (ctx.Db.Match.Id.Find(0) is Match am && am.Phase == MatchPhase.Active)
            MintJoinToken(ctx, ctx.Sender, team, am.Epoch, TokenExpiry(ctx));
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
        if (ctx.Db.Match.Id.Find(0) is Match am && am.Phase == MatchPhase.Active)
            MintJoinToken(ctx, ctx.Sender, team, am.Epoch, TokenExpiry(ctx));
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

        // Invalidate every match credential — the next match mints fresh tokens (a stale
        // token must not let a client rejoin the native sim across the lobby boundary).
        foreach (var t in ctx.Db.JoinToken.Iter().ToList())
            ctx.Db.JoinToken.Identity.Delete(t.Identity);

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

        // Bump the epoch so any token that survived (shouldn't — we cleared them above) is
        // invalid against the next match, and reset to the lobby.
        ctx.Db.Match.Id.Update(m with { Phase = MatchPhase.Lobby, Winner = null, EngagedTeams = 0, Epoch = m.Epoch + 1 });
        Log.Info("[RestartMatch] world reset -> Lobby");
    }

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

    // Reset the static world to a fresh state for a new match: every base back to full
    // hull. (Ships/drones/shots no longer live in this module — the native sim server owns
    // and resets the live battlefield itself.) Used when a match starts and when one restarts.
    private static void ResetWorld(ReducerContext ctx)
    {
        float baseHp = BaseMaxHealthOf(ctx);
        foreach (var b in ctx.Db.Base.Iter().ToList())
            ctx.Db.Base.BaseId.Update(b with { Health = baseHp });
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

    // Debug force-start (/start): launch immediately with whoever is ready, instead of
    // waiting for every teamed player to ready up (MaybeStartMatch's normal gate). Readies
    // the caller so they spawn; un-readied teammates can still hop in mid-match via JoinTeam.
    private static void StartMatchNow(ReducerContext ctx, Player caller)
    {
        var match = ctx.Db.Match.Id.Find(0);
        if (match is null || match.Value.Phase != MatchPhase.Lobby)
        {
            SystemTo(ctx, caller.Identity, "Can only force-start from the lobby.");
            return;
        }
        if (caller.Team is null)
        {
            SystemTo(ctx, caller.Identity, "Pick a team first, then /start.");
            return;
        }

        ctx.Db.Player.Identity.Update(caller with { Ready = true });

        // Re-read from the DB so the caller's just-set Ready flag is included.
        byte engaged = 0;
        foreach (var p in ctx.Db.Player.Iter())
            if (p.Online && p.Team is byte t && p.Ready)
                engaged |= (byte)(1 << t);

        ResetWorld(ctx);
        ulong epoch = match.Value.Epoch + 1;
        long expiry = TokenExpiry(ctx);
        foreach (var p in ctx.Db.Player.Iter().ToList())
            if (p.Ready)
            {
                if (p.Team is byte pt)
                    MintJoinToken(ctx, p.Identity, pt, epoch, expiry);
                ctx.Db.Player.Identity.Update(p with { Ready = false });
            }

        ctx.Db.Match.Id.Update(match.Value with { Phase = MatchPhase.Active, Winner = null, EngagedTeams = engaged, Epoch = epoch });
        SystemAll(ctx, $"{DisplayName(caller)} force-started the match.");
        Log.Info($"[Match] force-start by {caller.Identity} -> Active (engaged sides 0b{System.Convert.ToString(engaged, 2)})");
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

        // Fresh battlefield, a new match epoch, and consume the ready flags so the next lobby
        // starts clean. Every participant gets a fresh native-sim credential bound to this
        // epoch (no-op when native mode is off / no secret installed).
        ResetWorld(ctx);
        ulong epoch = match.Value.Epoch + 1;
        long expiry = TokenExpiry(ctx);
        foreach (var p in ctx.Db.Player.Iter().ToList())
            if (p.Ready)
            {
                if (p.Team is byte pt)
                    MintJoinToken(ctx, p.Identity, pt, epoch, expiry);
                ctx.Db.Player.Identity.Update(p with { Ready = false });
            }

        ctx.Db.Match.Id.Update(match.Value with { Phase = MatchPhase.Active, Winner = null, EngagedTeams = engaged, Epoch = epoch });
        Log.Info($"[Match] {readied} pilot(s) ready -> Active (epoch {epoch}, engaged sides 0b{System.Convert.ToString(engaged, 2)})");
    }
}
