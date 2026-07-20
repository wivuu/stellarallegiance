using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;
// Alias just the two library set types (TechSet/CapabilitySet) rather than `using` the whole model
// namespace, which would collide with Shared.WorldConfig (the ctor's `cfg` type).
using TechSet = Allegiance.Factions.Model.TechSet;
using CapabilitySet = Allegiance.Factions.Model.CapabilitySet;

namespace SimServer.Sim;

// Static world: sectors, bases, asteroid fields, aleph pair — deterministically generated from one
// seed. A given seed always produces the exact same layout, so the server is authoritative and the
// world is reproducible: pin the seed (--seed / SIM_SEED) to rebuild a specific arena for tests /
// benchmarks / bug repro. Clients never re-derive anything from the seed — every static (bases,
// rocks, alephs, sector env/dust) is streamed per-entity via Welcome / MsgReveal — so the seed lives
// server-side only. By default it is rolled fresh per match (Program.BuildWorldForMap), so each match
// reshuffles even on the same map. Asteroids are bucketed once into a uniform spatial grid
// (cell = GridCell) for broad-phase collision/shot tests.
public sealed class World
{
    // ---- Tuning ported from module/spacetimedb/Lib.cs (keep in sync) ----
    // Kinematic collision constants are sourced from shared CollisionConfig so the client predicts
    // collisions identically (single source — these aliases keep existing World.* call sites).
    public const float ShipRadius = CollisionConfig.ShipRadius;
    public const float ProjectileRadius = 1f;
    public const float BaseRadius = CollisionConfig.BaseRadius;
    public const float DockFaceDepth = CollisionConfig.DockFaceDepth; // docking-door depth window (own base carve-out)
    public readonly float BaseMaxHealth; // win-condition base hull, sourced from the content base def
    private readonly IReadOnlyList<BaseDef>? _baseDefs; // per-type base defs (mesh/radius); null = garrison-only
    private ulong _nextBaseId = 1; // stable per-match base id; garrisons seed it, CreateBase extends it
    public readonly int GarrisonCount; // count of match-start garrisons (all type 0); runtime bases append after
    public const float AsteroidCollisionScale = CollisionConfig.AsteroidCollisionScale;
    public const float CollisionRestitution = CollisionConfig.CollisionRestitution;
    // Collision DAMAGE, boundary hazard, and warp-gate knobs are CONTENT now — authored in
    // world.yaml (`combat:` / `mechanics:`) and read by the sim from Content.World.
    // The SINGLE default sector radius (before × SectorScale) for any sector whose YAML omits `radius`
    // and whose map/world sets no `sector-radius`. Replaces the old per-sector-id CoreRadius/VergeRadius
    // — no value is chosen by sector id anymore.
    public const float DefaultSectorRadius = 700f;
    // World-scale knobs (SectorScale / AsteroidDensity) are CONTENT now: they arrive via the
    // WorldConfig passed to the ctor (authored in YAML), so a per-server world.yaml override changes
    // the generated map, not just what's streamed. No compile-in defaults live here.
    public const float GridCell = 160f; // broad-phase cell size (matches the module's AsteroidGridCell)

    // Asteroid shape + base-placement knobs — ONE shared default set per shape (field=disc,
    // belt=ring), applied to any sector by its declared `asteroids` kind (no per-sector-id choice).
    // Authored in world.yaml (`seeding:`); stock values live on WorldSeedingTuning's
    // initializers. Counts derive from the filled area so density (spacing) is invariant to sector
    // size — a bigger sector just gets proportionally more rocks at the same spacing.
    private readonly WorldSeedingTuning _seed;

    // Mining/ore tuning (world.yaml `mining:`), consumed by the post-seeding ore-assignment pass and
    // the shrink helper. Server-side only — like _seed, never streamed.
    public readonly WorldMiningTuning Mining;

    // Constructor/base-building tuning (world.yaml `constructor:`), consumed by the constructor
    // brain/steering (Simulation.Constructors.cs). Server-side only — like Mining, never streamed.
    public readonly WorldConstructorTuning Constructor;

    // Build-pipeline tuning (world.yaml `build:`): the per-garrison order queue shared by constructor
    // and miner purchases (parallel + queue limits). QueueLimit is streamed to the client for the
    // Build-tab gray-out; ParallelLimit is server-only (drives promotion).
    public readonly WorldBuildTuning Build;

    // Env carries the STREAMED per-sector environment (sun/god-rays + nebula override + dust visual
    // knobs); the seeded dust CLOUDS themselves live in DustClouds. Null Env → legacy backdrop. MapX/
    // MapY (valid when HasMapPos) are the authored 2D map-diagram position, streamed to the client.
    public readonly record struct Sector(
        uint Id, float Radius, string Name, SectorEnvironment? Env = null,
        float MapX = 0f, float MapY = 0f, bool HasMapPos = false);

    // One procedurally-seeded dust cloud: a soft volumetric sphere that hazes visuals AND attenuates
    // radar/vision (Simulation.Vision). Immutable after the World ctor → the vision worker reads the
    // list lock-free, exactly like the rock grid.
    public readonly record struct DustCloud(uint SectorId, Vec3 Pos, float Radius, float Density);

    // BaseTypeId selects the base's mesh/def/collision (0 = garrison; forward bases built by
    // constructors carry their station's type). Match-start garrisons are always type 0.
    public readonly record struct BaseSite(ulong Id, byte Team, uint SectorId, Vec3 Pos, byte BaseTypeId = 0);

    // Variant/Rot* are cosmetic only (the sim ignores them) but are drawn from the same DetRng
    // sequence the module uses, so they ride along to the client via the Welcome frame instead
    // of leaving rocks as grey spheres. Variant is the index into Shared.AsteroidShapes.Variants.
    public readonly record struct Rock(
        ulong Id,
        uint SectorId,
        Vec3 Pos,
        float Radius,
        byte Variant,
        float RotX,
        float RotY,
        float RotZ
    );

    public readonly record struct Gate(ulong Id, uint SectorId, uint DestSectorId, Vec3 Pos, Vec3 PartnerPos);

    public readonly List<Sector> Sectors = new();
    // Bases grow at runtime (v37 base building appends constructor-built bases). Appending never
    // invalidates an existing index, so BaseHealth/ResearchByBase (indexed like Bases) stay in
    // lockstep — every consumer iterates `for i < Bases.Count`. Bases are never removed/reordered
    // (a destroyed base keeps its slot with BaseHealth 0).
    public readonly List<BaseSite> Bases = new();
    public readonly List<float> BaseHealth = new(); // indexed like Bases

    // Stage-4 research: one mutable research-order state per base, indexed like Bases. Lives on
    // World so a match-start world swap resets it for free (StartMatch builds a fresh World).
    // Mutated ONLY on the sim thread (Simulation.Research.cs).
    public sealed class BaseResearchState
    {
        // In-flight research orders at this base: catalog development index + integer tick window.
        // Count is capped at the base type's ResearchSlots by the start validation.
        public readonly List<(ushort DevIndex, uint StartTick, uint DurationTicks)> Active = new();

        // The single on-deck queue slot: starts automatically when an active slot frees. Credits
        // were already deducted at queue time (reservation), so promotion can never fail on funds.
        public ushort? OnDeck;
    }

    public readonly List<BaseResearchState> ResearchByBase = new(); // indexed like Bases
    public readonly List<Rock> Asteroids = new();
    public readonly List<DustCloud> DustClouds = new();
    public readonly List<Gate> Alephs = new();
    public readonly ulong Seed;

    // The default/fallback sector id — the first authored sector. Used where code needs *a* sector but
    // has no better context (e.g. a spectator with no ship, or a team with no garrison). Not a "home"
    // in the gameplay sense — a player's home is their team's garrison sector (scan Bases by team).
    public uint DefaultSector => Sectors.Count > 0 ? Sectors[0].Id : 0;

    // Authored display name for a sector id ("Sector <id>" for an unknown id — callers use this in
    // chat-facing text and never need to distinguish the miss).
    public string SectorName(uint id)
    {
        foreach (var s in Sectors)
            if (s.Id == id)
                return s.Name;
        return $"Sector {id}";
    }

    // Stage-2 strategy spine: per-team economy/owned state, keyed by team byte (parallel to the
    // per-team bases). Credits accrue in the sim step (Simulation.AccrueTeamCredits); OwnedTechs/
    // OwnedCapabilities are mutable per-team clones of the faction seed, fed to the unlock-gating
    // resolver in Phase 5. The sim mutates these; World only owns + (re)seeds them (SeedEconomy).
    public sealed class TeamState
    {
        public int Credits;
        public int Score; // placeholder (no scoring logic yet — wired to the client in Phase 4)
        public TechSet OwnedTechs = new();
        public CapabilitySet OwnedCapabilities = new();

        // Hull ClassIds this team may currently build, resolved from OwnedTechs/OwnedCapabilities via
        // BuildableResolver (Simulation.ResolveTeamUnlocks, refreshed at match start). The spawn gate
        // checks membership here; the wire snapshot (Protocol.BuildTeamState) streams it so the client
        // can predict locks and gray out the buy menu.
        public HashSet<byte> UnlockedClasses = new();

        // Sectors this team's miners may SELECT rocks in (Stage-4 mining). Seeded to the team's
        // garrison sector(s) each match (SeedEconomy); grown by a commander's mouse mining order. Gates
        // rock selection only — a miner freely TRANSITS any sector en route (returning to base is
        // always allowed).
        public HashSet<uint> AuthorizedMiningSectors = new();

        // RockClass bitmask (1 << (byte)RockClass) of asteroid classes this team's fog of war has
        // revealed at least one rock of. Derived from TeamVision.DiscoveredRocks (monotonic, like the
        // set itself) at its sim-thread write sites; fog OFF stamps 0xFF at ResetVision. Gates
        // constructor purchases (TryBuyConstructor) and streams via MsgTeamState so the client Build
        // tab can predict the lock.
        public byte DiscoveredRockClasses;
    }

    // One TeamState per team byte present in Bases (0 and 1 today). Seeded from the faction snapshot
    // at construction and re-seeded on each match start (SeedEconomy).
    public readonly Dictionary<byte, TeamState> TeamStates = new();

    // One launch bay per authored HP_DockingExit node: Pos = where a launching ship first appears
    // (the hardpoint, base-local world units), Dir = the launch axis out of the bay (opposite the
    // node's inward forward). A base may author N exits — the sim picks one at random per launch.
    // Always at least one element (a modelless/exitless base gets a single default entry).
    public readonly record struct BaseExit(Vec3 Pos, Vec3 Dir);

    // Server-side collision/hardpoint model for ONE base type (v37 — base building generalized bases
    // to be per-type, like ship hulls). Garrison (type 0) carries the full compound COL_ hull + docking
    // doors; a constructor-built forward base (outpost) shrink-wraps to a single convex hull with no
    // doors (sphere/convex collision, no docking). Hull null => sphere fallback at Radius.
    public sealed class BaseModelData
    {
        public SimModel? Model;
        public ConvexHull? Hull; // base hull in WORLD units (base is identity-oriented)
        public ConvexHull[] SubHulls = System.Array.Empty<ConvexHull>();
        public BaseExit[] Exits = System.Array.Empty<BaseExit>();
        public Vec3 EntryAxis; // mean inward face-normal across doors, for AI aim
        public Vec3 DoorCenter; // centroid of the door face centres (AI aim target)
        public DockFace[] DockFaces = System.Array.Empty<DockFace>();
        public float Radius = BaseRadius; // world-unit collision radius for this type
        public float MaxHealth; // per-type base hull (from the base def's max-armor); 0 => fall back to World.BaseMaxHealth
    }

    // Loaded base models keyed by BaseTypeId. Type 0 (garrison) is always present. Populated in the
    // ctor from the content base defs (per-type ModelName + Radius); a minimal caller with no base
    // defs falls back to the single garrison model, keeping legacy behavior byte-identical.
    private readonly Dictionary<byte, BaseModelData> _baseModels = new();

    // Garrison (type 0) model — the fallback for any type not loaded, and the source of the legacy
    // single-model accessors below (call sites without a base-type context).
    private BaseModelData Model0 => _baseModels.TryGetValue(0, out var m) ? m : EmptyBaseModel;
    private static readonly BaseModelData EmptyBaseModel = new();

    public BaseModelData BaseModelFor(byte typeId) =>
        _baseModels.TryGetValue(typeId, out var m) ? m : Model0;

    // Per-type collision accessors — the call sites that iterate bases resolve by b.BaseTypeId.
    public ConvexHull? BaseHullOf(byte typeId) => BaseModelFor(typeId).Hull;
    public ConvexHull[] BaseSubHullsOf(byte typeId) => BaseModelFor(typeId).SubHulls;
    public BaseExit[] BaseExitsOf(byte typeId) => BaseModelFor(typeId).Exits;
    public Vec3 BaseDoorCenterOf(byte typeId) => BaseModelFor(typeId).DoorCenter;
    public Vec3 BaseEntryAxisOf(byte typeId) => BaseModelFor(typeId).EntryAxis;
    public DockFace[] BaseDockFacesOf(byte typeId) => BaseModelFor(typeId).DockFaces;
    public float BaseRadiusOf(byte typeId) => BaseModelFor(typeId).Radius;

    // Per-type base max hull. Sourced from the type's projected BaseDef (max-armor); falls back to the
    // legacy single BaseMaxHealth (garrison, type 0) for a type with no def / no authored health —
    // mirroring BaseRadiusOf's Model0 fallback so callers without base defs stay byte-identical.
    public float BaseMaxHealthOf(byte typeId)
    {
        var m = BaseModelFor(typeId);
        return m.MaxHealth > 0f ? m.MaxHealth : BaseMaxHealth;
    }

    // Team-aware per-type base max hull (v41): the raw per-type max scaled by the team's MaxArmorStation
    // faction multiplier (TeamAttr; 1.0 when unseeded). Every base-health STAMP/refill site resolves the
    // owning team so Iron Coalition's tougher bases (×1.15) hold consistently. UpgradeBaseAt's
    // fraction-of-max rescale stays correct because both old and new max carry the same team factor.
    public float BaseMaxHealthOf(byte typeId, byte team) =>
        BaseMaxHealthOf(typeId) * TeamAttr(team, (int)Allegiance.Factions.Model.GameAttribute.MaxArmorStation);

    // ---- Per-team resolved stat multipliers (v41) --------------------------------------------------
    // A flat float[attrId] cache per team (faction base-attributes × completed developments), populated
    // by the sim (Simulation.RecomputeTeamAttributes) at match start + on research completion. World owns
    // it so the team-aware BaseMaxHealthOf can read it; the sim reads it through TeamAttr(team, attr).
    // Unseeded (empty / pre-match / attributes-disabled tests) ⇒ every attr resolves 1.0 (neutral).
    private readonly Dictionary<byte, float[]> _teamAttr = new();

    public float TeamAttr(byte team, int attrId) =>
        _teamAttr.TryGetValue(team, out var a) && (uint)attrId < (uint)a.Length ? a[attrId] : 1f;

    public void SetTeamAttributes(byte team, float[] attrs) => _teamAttr[team] = attrs;

    public void ClearTeamAttributes() => _teamAttr.Clear();

    // Legacy single-model accessors (garrison, type 0) — for call sites without a base-type context
    // (SelfTest, launch fallback). Behavior byte-identical to the pre-v37 single-model fields.
    public ConvexHull? BaseHull => Model0.Hull;
    public ConvexHull[] BaseSubHulls => Model0.SubHulls;
    public BaseExit[] BaseExits => Model0.Exits;
    public Vec3 BaseEntryAxis => Model0.EntryAxis;
    public Vec3 BaseDoorCenter => Model0.DoorCenter;
    public DockFace[] BaseDockFaces => Model0.DockFaces;
    public readonly Dictionary<ulong, RockBody> RockBodies = new();

    // Per-rock collision body: the variant's authored-space hull plus this rock's world rotation
    // and the uniform scale mapping authored units → this rock's collision size.
    // Rot is the SPAWN pose; SpinAxis/SpinSpeed give the live tumble — the resolver composes them at
    // the current tick (Collide.RockRotationAt) so the hull rotates with the rendered rock.
    // Scale is the LIVE scale (shrinks as a He3 rock is mined, kept in lockstep with CurrentRadius by
    // SetOreRemaining); SpawnScale is the immutable spawn-size scale, so a live re-scale recomputes
    // ABSOLUTELY (SpawnScale·currentRadius/spawnRadius) and repeated harvests never compound drift.
    public readonly record struct RockBody(ConvexHull Hull, Quat Rot, float Scale, Vec3 SpinAxis, float SpinSpeed, float SpawnScale);

    // Per-rock MUTABLE resource state, assigned once after asteroid seeding (AssignOre) and keyed by
    // rock id. Every rock gets an entry: its RockClass, plus (He3 rocks only) an ore hold that a miner
    // depletes. Non-He3 rocks carry Capacity/Remaining = 0 (never harvested, never shrink). CurrentRadius
    // starts at the spawn radius and shrinks volume-proportionally as He3 ore is pulled (SetOreRemaining;
    // the harvest transfer itself lands in a later stream). A class so the sim mutates it in place.
    public sealed class OreState
    {
        public RockClass Class;
        public float OreCapacity;  // total He3 units this rock holds (0 = non-He3)
        public float OreRemaining; // He3 units left to mine
        public float CurrentRadius; // live (shrunk) radius; == the spawn radius until mined
    }

    // Rock id → ore state. Rebuilt from scratch every World (so a match restart re-rolls it via the
    // StartMatch world swap — nothing else caches it). Read via RockClassOf / RockCurrentRadius.
    public readonly Dictionary<ulong, OreState> RockOre = new();

    // Rocks whose ore/radius actually changed this sim step (appended by SetOreRemaining on a real
    // change, skipped on no-ops). Mirrors the Minefields/TeamState "changed-this-step" seam so a later
    // wire stream can drain only the deltas (live shrink); cleared once per step alongside the other
    // change flags (Simulation.Step). Nothing consumes it yet — the wire hookup lands in a later stream.
    public readonly HashSet<ulong> RocksChangedThisStep = new();

    // Per-class ship collision hulls, loaded from the same GLBs the client renders and pre-scaled
    // to the client's per-class silhouette length (ShipModelLoader.TargetLength), so the hull a
    // bolt or another ship tests against matches what the player sees. The hull lives in the ship's
    // local frame at the ship's pose (center = Pos, rotation = Rot); BoundingRadius is its
    // world-space bounding sphere for broad-phase. Null when a class GLB is absent — the sim then
    // falls back to the ShipRadius sphere for that class (like asteroids/bases do without a model).
    public readonly record struct ShipBody(ConvexHull Hull, float BoundingRadius);

    private readonly Dictionary<byte, ShipBody> _shipHulls = new(); // keyed by ShipClassDef.ClassId
    private readonly ShipBody? _podHull;
    private readonly ILogger _log;

    // The collision hull for a ship of this class (pods ignore class and use the pod hull), or null
    // when its GLB is missing — the caller then falls back to the ShipRadius sphere.
    public ShipBody? ShipHull(byte cls, bool isPod) => isPod ? _podHull : (_shipHulls.TryGetValue(cls, out var b) ? b : null);

    // Per-sector asteroid grid (static between regenerations, like the module's).
    private readonly Dictionary<uint, Dictionary<(int, int, int), List<Rock>>> _rockGrid = new();
    private static readonly Dictionary<(int, int, int), List<Rock>> NoGrid = new();

    // Precomputed all-pairs sector routing: (fromSector, toSector) -> the gate to leave fromSector by
    // as the next hop of a shortest route toward toSector. Built once at construction from Alephs
    // (BuildSectorRouting); NextGateTo is an O(1) read into it.
    private readonly Dictionary<(uint from, uint to), Gate> _nextHop = new();

    // Hop distances backing _nextHop (same BFS). Absent = unreachable; (S, S) is not stored (0 hops).
    private readonly Dictionary<(uint from, uint to), int> _hops = new();

    public static int CellOf(float v) => (int)MathF.Floor(v / GridCell);

    public World(ulong seed, WorldConfig cfg, float baseMaxHealth, FactionStart start, IReadOnlyList<ShipClassDef> ships, IReadOnlyList<BaseDef>? baseDefs = null, ILogger? log = null)
    {
        _baseDefs = baseDefs;
        _log = log ?? NullLogger.Instance;
        Seed = seed;
        BaseMaxHealth = baseMaxHealth;
        _seed = cfg.Seeding;
        Mining = cfg.Mining;
        Constructor = cfg.Constructor;
        Build = cfg.Build;

        // ---- Sectors: geometry is entirely data-driven (see SeedSectors). ----
        var (secCfg, defaultRadius, density) = SeedSectors(cfg);

        // ---- Garrisons → team bases + team-count validation (see SeedGarrisonsAndValidateTeams). ----
        SeedGarrisonsAndValidateTeams(seed, secCfg, defaultRadius);

        // ---- Per-base health stamp + per-team economy seed (see SeedBaseHealthAndEconomy).
        // GarrisonCount is a readonly field, so — like the ship-hull tuple below — its assignment must
        // stay lexically inside the constructor even though the value is computed by the helper. ----
        SeedBaseHealthAndEconomy(start, out int garrisonCount);
        GarrisonCount = garrisonCount;

        // ---- Asteroids, then gates/dust: both phases SHARE one DetRng stream (asteroids draw first;
        // gates continue the exact same stream where asteroids left off). Dust draws its own
        // independent rng internally (see SeedGatesAndDust), so it never perturbs this shared stream. ----
        var rng = new DetRng(seed);
        SeedAsteroids(ref rng, secCfg, defaultRadius, density);
        SeedGatesAndDust(ref rng, cfg, secCfg, defaultRadius);

        // ---- Spatial grid + resource/cosmetic assignment (see BucketRockGrid / AssignOreAndVariants). ----
        BucketRockGrid();
        AssignOreAndVariants(secCfg);

        // ---- Collision/hardpoint models (see LoadModels). _shipHulls/_podHull are readonly fields, so
        // the tuple assignment must stay directly in the constructor even though the loading work lives
        // in the helper (same rule as GarrisonCount above). ----
        (_shipHulls, _podHull) = LoadModels(ships);

        // Collapse the aleph gate graph into an all-pairs next-hop table (players route multi-hop across
        // sectors, not just through a single direct gate). The graph is tiny (~2-10 nodes); deterministic
        // (lowest gate Id breaks equal-hop ties, no Random, no dictionary-order dependence) so it is safe
        // to consult from the 20Hz sim step.
        BuildSectorRouting();
    }

    // ---- Sectors: geometry is entirely data-driven. Radius = explicit override, else the single
    // shared default. Name/env/map-pos come straight from the authored per-sector config. Returns the
    // effective sector config list + the computed default radius/density so later phases (garrisons,
    // asteroids, gates/dust) can consume them without re-deriving anything from `cfg`. ----
    private (List<WorldSectorConfig> secCfg, float defaultRadius, float density) SeedSectors(WorldConfig cfg)
    {
        // Live world-scale knobs from the loaded content (the authored world.yaml).
        float sectorScale = cfg.SectorScale;
        float density = cfg.AsteroidDensity;
        // ONE shared default radius for any sector that omits its own — no per-sector-id defaults.
        float defaultRadius = (cfg.SectorRadius > 0f ? cfg.SectorRadius : DefaultSectorRadius) * sectorScale;

        // Effective sector config: the authored list, or — when a config declares no sectors at all
        // (e.g. a bare test world) — a single shared DEFAULT arena. This is the "fallback to a single
        // set of defaults": one uniform template applied to every sector, NOT a per-sector-id layout.
        var secCfg = cfg.Sectors.Count > 0 ? cfg.Sectors : DefaultArena(sectorScale);

        foreach (var sc in secCfg)
        {
            float radius = sc.Radius ?? defaultRadius;
            bool hasPos = sc.MapPosX.HasValue && sc.MapPosY.HasValue;
            Sectors.Add(new Sector(
                sc.Id, radius, sc.Name ?? "", sc.Env,
                sc.MapPosX ?? 0f, sc.MapPosY ?? 0f, hasPos));
        }
        return (secCfg, defaultRadius, density);
    }

    // Sector radius lookup shared by every phase that runs after SeedSectors (garrisons/asteroids/
    // gates/dust) — Sectors is already fully populated by then. Mirrors the ctor's old local
    // `RadiusOf` function exactly (including the defensive fallback, which real configs never hit
    // since every id consulted below was seeded into Sectors above).
    private float RadiusOf(uint id, float defaultRadius)
    {
        foreach (var s in Sectors)
            if (s.Id == id)
                return s.Radius;
        return defaultRadius;
    }

    // ---- Garrisons → team bases. The SET of garrisons across the map defines the teams. Positions
    // are drawn from a dedicated RNG (so the asteroid/aleph sequence is unaffected) and placed
    // relative to each sector's radius. When a config declares sectors but NO garrison anywhere,
    // fall back to one garrison per sector (team = index) for the first DefaultTeamCount sectors, so
    // a bare arena is still playable — real maps declare garrisons explicitly. ----
    private void SeedGarrisonsAndValidateTeams(ulong seed, List<WorldSectorConfig> secCfg, float defaultRadius)
    {
        bool anyGarrison = false;
        foreach (var sc in secCfg)
            if (sc.Garrison is not null)
            {
                anyGarrison = true;
                break;
            }
        var baseRng = new DetRng(seed ^ 0xB453_BA53_B453_BA53UL);
        for (int i = 0; i < secCfg.Count; i++)
        {
            var sc = secCfg[i];
            var garrison = sc.Garrison
                ?? (!anyGarrison && i < DefaultTeamCount ? new SectorGarrison { Team = (byte)i } : null);
            if (garrison is null)
                continue;
            float r = RadiusOf(sc.Id, defaultRadius);
            Vec3 pos = RandomBasePos(ref baseRng, r * _seed.BaseInnerFrac, r * _seed.BaseOuterFrac, _seed.BaseYJitter);
            Bases.Add(new BaseSite(_nextBaseId++, garrison.Team, sc.Id, pos)); // garrisons are type 0
        }
        if (Bases.Count == 0)
            throw new InvalidOperationException(
                "map declares no garrisons — at least one team home base is required.");
        // Fail fast: the map can DECLARE any number of garrisons, but the sim is currently 2-team
        // (Simulation.Pig.NumTeams, the win condition, lobby team validation). A map that asks for more
        // teams than the sim supports must error at boot rather than misbehave mid-match.
        var teams = new HashSet<byte>();
        byte maxTeam = 0;
        foreach (var b in Bases)
        {
            teams.Add(b.Team);
            if (b.Team > maxTeam)
                maxTeam = b.Team;
        }
        if (teams.Count > MaxSupportedTeams || maxTeam >= MaxSupportedTeams)
            throw new InvalidOperationException(
                $"map declares {teams.Count} garrison team(s) (max id {maxTeam}) — the sim currently "
                    + $"supports {MaxSupportedTeams} teams (ids 0..{MaxSupportedTeams - 1}).");
    }

    // Per-base health stamp + per-team economy seed. `garrisonCount` is handed back via `out` because
    // GarrisonCount is a readonly field (the ctor assigns it immediately after calling this).
    private void SeedBaseHealthAndEconomy(FactionStart start, out int garrisonCount)
    {
        for (int i = 0; i < Bases.Count; i++)
        {
            // All match-start bases are garrisons (type 0); BaseMaxHealthOf(0) == BaseMaxHealth here
            // (base models load below, so the fallback returns it) — kept per-type for uniformity. The
            // v41 team attr cache is empty at ctor ⇒ ×1.0; StartMatch's ResetMatchBases re-stamps once
            // the sim has seeded it (Iron's ×1.15 lands there, not here — no active match at ctor).
            BaseHealth.Add(BaseMaxHealthOf(Bases[i].BaseTypeId, Bases[i].Team));
            ResearchByBase.Add(new BaseResearchState());
        }
        // The match-start garrisons (all type 0). ResetMatchBases trims runtime-built bases back to
        // these on a World-reuse rematch, so a reused World starts each match with only its garrisons.
        garrisonCount = Bases.Count;

        // One economy state per team (Stage-1 = every team seeds from the single stock faction).
        foreach (var b in Bases)
            TeamStates[b.Team] = new TeamState();
        SeedEconomy(start);
    }

    // ---- Asteroids: each sector seeds by its declared shape from the shared shape constants. Draws
    // from the SHARED `rng` (passed by ref) — the same stream SeedGatesAndDust continues afterward. ----
    private void SeedAsteroids(ref DetRng rng, List<WorldSectorConfig> secCfg, float defaultRadius, float density)
    {
        ulong rockId = 1;
        foreach (var sc in secCfg)
        {
            float r = RadiusOf(sc.Id, defaultRadius);
            float d = density * (sc.AsteroidDensityMult ?? 1f);
            switch (sc.Asteroids)
            {
                case AsteroidKind.Field:
                    SeedAsteroidField(ref rng, sc.Id, r, d, ref rockId);
                    break;
                case AsteroidKind.Belt:
                    SeedAsteroidBelt(ref rng, sc.Id, r, d, ref rockId);
                    break;
                case AsteroidKind.None:
                    break;
            }
        }
    }

    // ---- Gates / alephs: one bidirectional pair per authored link (empty → ring by id), placed
    // toward the outer reaches of each endpoint sector — continues the SAME `rng` stream SeedAsteroids
    // left off at. Dust clouds are then seeded on their OWN rng (independent of `rng`), so authoring
    // dust never shifts a single asteroid or aleph — a map with dust reads byte-identical rocks to one
    // without. ----
    private void SeedGatesAndDust(ref DetRng rng, WorldConfig cfg, List<WorldSectorConfig> secCfg, float defaultRadius)
    {
        var links = cfg.Links.Count > 0 ? cfg.Links : DefaultRing(secCfg);
        ulong gateId = 1;
        // Mouths already placed in each sector, so a sector's SECOND+ gate can be rejection-sampled
        // away from its neighbours (see PlaceGateMouth). A sector's first mouth always accepts its
        // first roll, so single-gate sectors — and the aleph-min-gap = 0 case — draw the `rng` stream
        // exactly as the pre-spacing code did (keeps pinned-seed layouts stable where nothing overlapped).
        var mouthsBySector = new Dictionary<uint, List<Vec3>>();
        foreach (var link in links)
        {
            Vec3 aPos = PlaceGateMouth(ref rng, link.A, RadiusOf(link.A, defaultRadius), mouthsBySector);
            Vec3 bPos = PlaceGateMouth(ref rng, link.B, RadiusOf(link.B, defaultRadius), mouthsBySector);
            Alephs.Add(new Gate(gateId++, link.A, link.B, aPos, bPos));
            Alephs.Add(new Gate(gateId++, link.B, link.A, bPos, aPos));
        }

        foreach (var sc in secCfg)
            SeedDustClouds(sc.Id, RadiusOf(sc.Id, defaultRadius), sc.Env?.Dust);
    }

    // Place one gate mouth toward the outer reaches of `sector` (RandomOuterPos), kept at least
    // `seeding.aleph-min-gap` world units (centre-to-centre) from every mouth already placed in that
    // sector so two alephs in one sector never overlap by chance. Deterministic rejection sampling:
    // re-roll up to MaxPlaceAttempts times, take the first candidate that clears the gap, else keep the
    // best-separated roll — a gate is REQUIRED for connectivity and, unlike a rock, is never dropped.
    // With the gap disabled (<= 0) or for a sector's first mouth, this returns the first roll (one
    // RandomOuterPos), so the `rng` stream matches the pre-spacing placement in those cases.
    private Vec3 PlaceGateMouth(ref DetRng rng, uint sector, float sectorRadius, Dictionary<uint, List<Vec3>> mouthsBySector)
    {
        if (!mouthsBySector.TryGetValue(sector, out var placed))
            mouthsBySector[sector] = placed = new List<Vec3>();

        float gap = _seed.AlephMinGap;
        Vec3 pos = RandomOuterPos(ref rng, sectorRadius);
        if (gap > 0f && placed.Count > 0)
        {
            float gapSq = gap * gap;
            Vec3 best = pos;
            float bestSep = MinSepSq(pos, placed);
            for (int attempt = 1; attempt < MaxPlaceAttempts && bestSep < gapSq; attempt++)
            {
                Vec3 cand = RandomOuterPos(ref rng, sectorRadius);
                float sep = MinSepSq(cand, placed);
                if (sep > bestSep)
                {
                    bestSep = sep;
                    best = cand;
                }
            }
            pos = best;
        }
        placed.Add(pos);
        return pos;

        static float MinSepSq(Vec3 p, List<Vec3> others)
        {
            float min = float.MaxValue;
            foreach (var o in others)
            {
                float d = (p - o).LengthSquared();
                if (d < min)
                    min = d;
            }
            return min;
        }
    }

    // Per-sector asteroid broad-phase grid, bucketed once from the fully-seeded Asteroids list. Must
    // run after SeedAsteroids.
    private void BucketRockGrid()
    {
        foreach (var r in Asteroids)
        {
            if (!_rockGrid.TryGetValue(r.SectorId, out var grid))
                _rockGrid[r.SectorId] = grid = new Dictionary<(int, int, int), List<Rock>>();
            var key = (CellOf(r.Pos.X), CellOf(r.Pos.Y), CellOf(r.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = new List<Rock>();
            cell.Add(r);
        }
    }

    // Assign each rock a resource class + (He3 rocks) an ore hold, then remap each rock's cosmetic
    // mesh Variant to a member of its class's pool so the shape/texture reads the resource type.
    // Both draw ONLY from per-rock derived sub-RNGs (OreMix) — never the shared world-gen `rng` — so
    // the rock/aleph layout for a pinned seed stays byte-identical no matter how the mining knobs are
    // tuned (the canary). Runs BEFORE LoadRockBodies so the collision hull is built from the same
    // class-derived variant the client renders (LoadRockBodies keys convex hulls off r.Variant).
    private void AssignOreAndVariants(List<WorldSectorConfig> secCfg)
    {
        AssignOre(secCfg);
        AssignVariants();
    }

    // Load the per-type GLB collision/hardpoint models (best-effort; falls back to spheres). Type 0
    // (garrison) is always loaded — from the content garrison def if present, else the legacy
    // "garrison"/BaseRadius fallback so a minimal caller (map preview) stays byte-identical. Returns
    // the ship-hull tables so the ctor can assign them (_shipHulls/_podHull are readonly fields).
    private (Dictionary<byte, ShipBody>, ShipBody?) LoadModels(IReadOnlyList<ShipClassDef> ships)
    {
        LoadBaseModels();
        LoadRockBodies();
        return LoadShipBodies(ships);
    }

    // Rewrite every rock's cosmetic mesh Variant to a variant from its RockClass's pool, so a rock's
    // shape/texture matches its resource type. Keyed on the same per-rock OreMix hash that AssignOre
    // used for the class, so it's fully seed-deterministic and never touches the shared world RNG (the
    // NextShape variant draw is retained only to keep that RNG stream — and thus positions — stable;
    // its value is overwritten here). Must run after AssignOre (needs RockClassOf) and before
    // LoadRockBodies (collision hulls key off the final Variant).
    private void AssignVariants()
    {
        for (int i = 0; i < Asteroids.Count; i++)
        {
            var r = Asteroids[i];
            byte variant = AsteroidShapes.VariantForClass(RockClassOf(r.Id), OreMix(Seed, r.Id));
            Asteroids[i] = r with { Variant = variant };
        }
    }

    // Stateless strong mix of the world seed with a per-rock id, feeding a private DetRng so each rock's
    // class/capacity is drawn from its OWN scrambled stream. Independent of the shared world-gen rng and
    // of any Dictionary/list iteration order (the id alone selects the stream), so ore assignment can
    // never perturb the rock/aleph layout. splitmix-style spread; DetRng's own scrambler does the rest.
    private static ulong OreMix(ulong seed, ulong id) => seed ^ (id * 0x9E3779B97F4A7C15UL);

    // Post-seeding ore-assignment pass. For each sector (rocks taken from Asteroids in list order — a
    // deterministic order), pick a guaranteed count of Helium3 rocks and give them ore holds; the rest
    // are cosmetic classes. Selection + capacity come entirely from per-rock derived sub-RNGs (OreMix),
    // so the shared world-gen RNG stream is untouched and the layout stays byte-identical for a seed.
    private void AssignOre(List<WorldSectorConfig> secCfg)
    {
        // Capacity scales with a rock's SIZE across the FIELD rock-size range (field is the primary/
        // most-common shape): the volume (radius³) fraction of a rock between the min- and max-size
        // volumes picks where in the [capMin, capMax] band its ore capacity lands. Precompute the two
        // reference volumes here; guard a degenerate (min == max) size range against a zero span.
        float vMinRef = Cube(_seed.FieldRockMin);
        float vSpan = MathF.Max(Cube(_seed.FieldRockMax) - vMinRef, 1e-3f);

        // Per-sector config lookup for the optional overrides (a bare test world may have no config for
        // a sector id — then the world-level WorldSeedingTuning defaults apply).
        var scById = new Dictionary<uint, WorldSectorConfig>();
        foreach (var sc in secCfg)
            scById[sc.Id] = sc;

        // Group rocks by sector, preserving Asteroids list order (deterministic across runs).
        var bySector = new Dictionary<uint, List<Rock>>();
        foreach (var r in Asteroids)
        {
            if (!bySector.TryGetValue(r.SectorId, out var list))
                bySector[r.SectorId] = list = new List<Rock>();
            list.Add(r);
        }

        // Rock id → its index in Asteroids, so the special-rock oversize below can write the scaled
        // radius back to the canonical list (the per-sector `rocks` above hold value-type copies).
        var idToIndex = new Dictionary<ulong, int>(Asteroids.Count);
        for (int i = 0; i < Asteroids.Count; i++)
            idToIndex[Asteroids[i].Id] = i;

        // Each sector is processed independently and results are stored keyed by rock id, so the final
        // RockOre is identical regardless of which order the sectors are visited in.
        foreach (var (sectorId, rocks) in bySector)
        {
            int n = rocks.Count;
            if (n == 0)
                continue;
            scById.TryGetValue(sectorId, out var sc); // sc may be null (test world)

            // Per-rock derived hash from each rock's own sub-RNG, for ranking + cosmetic-class
            // selection. Capacity is no longer a random roll — it is a pure function of the rock's
            // size (see the He3 branch below) — so the hash is the only draw needed here.
            var hash = new ulong[n];
            for (int i = 0; i < n; i++)
            {
                var rr = new DetRng(OreMix(Seed, rocks[i].Id));
                hash[i] = rr.NextULong();       // ranking + cosmetic-class selector
            }

            // Guaranteed He3 count: the sector override ?? world default, clamped to the actual rock
            // count. Zero-rock sectors bailed above, so he3Count ∈ [0, n]. A team's HOME sector
            // carries a leaner He3Count stamped by MapLoader.ApplyTo (he3-per-home-sector), so the
            // garrison case needs no special-casing here — it arrives as an ordinary override.
            int he3Count = Math.Clamp(sc?.He3Count ?? _seed.He3PerSector, 0, n);

            // Rank the sector's rocks by hash (descending); ties broken by rock id (ascending) so the
            // order is fully deterministic. The top he3Count become Helium3.
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = hash[b].CompareTo(hash[a]); // descending hash
                return c != 0 ? c : rocks[a].Id.CompareTo(rocks[b].Id); // ascending id tie-break
            });
            var isHe3 = new bool[n];
            for (int k = 0; k < he3Count; k++)
                isHe3[order[k]] = true;

            // RARE special rocks: the next `specialCount` ranked rocks (immediately after the He3
            // block, so He3 and special never collide) become special classes (Carbonaceous/Silicon/
            // Uranium — no longer cosmetic: rock-class-gated stations like the Supremacy build on
            // them). Clamped to the rocks left after He3, so a small sector never over-allocates.
            // Everything else is common.
            // A HOME (garrison) sector only gets its specials when the per-sector chance roll passes
            // (stock home-special-chance is 0 — home fields hold no landmark rock); a map-authored
            // special-count bypasses the roll entirely.
            int specialCount = sc?.SpecialCount ?? _seed.SpecialPerSector;
            if (sc?.SpecialCount is null && sc?.Garrison is not null && _seed.HomeSpecialChance < 1f)
            {
                // Salted per-sector sub-RNG: sector ids overlap the rock-id space, so a raw
                // OreMix(Seed, sectorId) stream could collide with a rock's own sub-RNG. The salt
                // keeps the domains disjoint; the shared world-gen RNG stream stays untouched.
                var hr = new DetRng(OreMix(Seed ^ 0x484F4D45_53504543UL /* "HOMESPEC" */, sectorId));
                if (hr.NextDouble() >= _seed.HomeSpecialChance)
                    specialCount = 0;
            }
            specialCount = Math.Clamp(specialCount, 0, Math.Max(0, n - he3Count));
            var isSpecial = new bool[n];
            for (int k = he3Count; k < he3Count + specialCount; k++)
                isSpecial[order[k]] = true;

            // Which special CLASS a selected rock becomes is drawn from the sector's weights (its own
            // override else the world seeding default). Uniform weights reproduce the legacy hash%3.
            var specialWeights = sc?.SpecialWeights ?? _seed.SpecialWeights;

            float richness = sc?.OreRichnessMult ?? 1f;
            // Capacity band this sector clamps to, resolved sector → map → world. MapLoader.ApplyTo
            // already folds any map-level ore-capacity-min/max into each sector's config (a sector's
            // own value winning), so here it is just the sector override else the world default.
            float capMin = sc?.OreCapacityMin ?? Mining.OreCapacityMin;
            float capMax = sc?.OreCapacityMax ?? Mining.OreCapacityMax;
            for (int i = 0; i < n; i++)
            {
                var rock = rocks[i];
                if (isHe3[i])
                {
                    // Size-scaled capacity: the rock's volume fraction across the field size range
                    // interpolates the [capMin, capMax] band, richness scales it, and the result is
                    // clamped hard back into the band (so a tiny rock never falls below capMin nor a
                    // giant rises above capMax).
                    float vFrac = Math.Clamp((Cube(rock.Radius) - vMinRef) / vSpan, 0f, 1f);
                    float cap = Math.Clamp(Lerp(capMin, capMax, vFrac) * richness, capMin, capMax);
                    RockOre[rock.Id] = new OreState
                    {
                        Class = RockClass.Helium3,
                        OreCapacity = cap,
                        OreRemaining = cap,
                        CurrentRadius = rock.Radius,
                    };
                }
                else
                {
                    // Non-He3 rocks carry no ore hold — capacity 0, never shrinks. A selected special
                    // rock gets one of the three special classes by the weighted draw over the same
                    // per-rock hash (default uniform = legacy hash%3); every other rock is Regolith.
                    RockClass cls = isSpecial[i]
                        ? specialWeights.Pick(hash[i])
                        : RockClass.Regolith;

                    // Special (rare) rocks are landmark-sized: scale the spawn radius (collision + visual)
                    // by the tuning mult and write it back to the canonical Asteroids list, so the rock is
                    // genuinely bigger everywhere (hull in LoadRockBodies keys off r.Radius). Common
                    // Regolith keeps its rolled size. He3 is handled in the branch above (never scaled).
                    float radius = rock.Radius;
                    if (isSpecial[i] && _seed.SpecialRockRadiusMult != 1f)
                    {
                        radius = rock.Radius * _seed.SpecialRockRadiusMult;
                        Asteroids[idToIndex[rock.Id]] = rock with { Radius = radius };
                    }
                    RockOre[rock.Id] = new OreState
                    {
                        Class = cls,
                        OreCapacity = 0f,
                        OreRemaining = 0f,
                        CurrentRadius = radius,
                    };
                }
            }
        }
    }

    // The resource class assigned to a rock at world-gen (default Carbonaceous for an unknown id, e.g.
    // a test-seam rock added after construction — those carry no ore state).
    public RockClass RockClassOf(ulong id) =>
        RockOre.TryGetValue(id, out var s) ? s.Class : RockClass.Carbonaceous;

    // The rock's CURRENT (possibly shrunk) collision/render radius, falling back to its static spawn
    // radius for any id with no ore state (non-assigned/test rocks).
    public float RockCurrentRadius(ulong id) =>
        RockOre.TryGetValue(id, out var s) ? s.CurrentRadius : (RockById(id)?.Radius ?? 0f);

    // Set a He3 rock's remaining ore and recompute its volume-proportional CurrentRadius:
    //   radius = floor + (spawn − floor) · (remaining / capacity)^(1/3),  floor = ShrinkFloorFrac·spawn.
    // Encapsulates the shrink formula so the harvest stream just calls this. A no-op for non-He3 /
    // unknown rocks (they never shrink). The spawn radius is the static rock radius (RockById).
    public void SetOreRemaining(ulong id, float remaining)
    {
        if (!RockOre.TryGetValue(id, out var s) || s.OreCapacity <= 0f)
            return;
        float clamped = Math.Clamp(remaining, 0f, s.OreCapacity);
        float spawn = RockById(id)?.Radius ?? s.CurrentRadius;
        float floor = Mining.ShrinkFloorFrac * spawn;
        float t = s.OreCapacity > 0f ? clamped / s.OreCapacity : 0f;
        float newRadius = floor + (spawn - floor) * MathF.Pow(t, 1f / 3f);
        // No-op guard: a redundant set (same ore, same derived radius — e.g. a keepalive or a
        // full→full call) neither flags the rock changed nor re-scales its body. Matches the
        // change-flag discipline of the minefield/team seams (only real deltas mark the step dirty).
        if (clamped == s.OreRemaining && newRadius == s.CurrentRadius)
            return;
        s.OreRemaining = clamped;
        s.CurrentRadius = newRadius;
        RocksChangedThisStep.Add(id);
        // Keep the collision body's scale tracking the live radius. Recompute ABSOLUTELY from the
        // immutable SpawnScale (never multiply the current Scale) so repeated harvests can't compound
        // rounding drift: collision scale ∝ radius, so liveScale = SpawnScale · currentRadius/spawn.
        if (spawn > 1e-6f && RockBodies.TryGetValue(id, out var body))
            RockBodies[id] = body with { Scale = body.SpawnScale * (s.CurrentRadius / spawn) };
    }

    // Rocks fully DESPAWNED this step (base-building consumes the asteroid — RemoveRock). Drained by the
    // hub into a MsgRockGone broadcast, then cleared at the top of the next Step alongside the other
    // change flags. Distinct from RocksChangedThisStep (a shrink delta): a removed rock has no size to
    // stream, only its disappearance.
    public readonly HashSet<ulong> RocksRemovedThisStep = new();

    // Fully remove a rock from the world (a constructor's finished base consumes the asteroid it sits on).
    // Drops it from the asteroid list, the spatial grid, the ore + collision-body state, and the id cache,
    // and records it in RocksRemovedThisStep so the hub tells clients to delete their rock. Idempotent —
    // an unknown id is a no-op. Sim thread only (mutates the rock lists the sim/hub read).
    public bool RemoveRock(ulong id)
    {
        if (RockById(id) is not Rock r)
            return false;
        if (_rockGrid.TryGetValue(r.SectorId, out var grid))
        {
            var key = (CellOf(r.Pos.X), CellOf(r.Pos.Y), CellOf(r.Pos.Z));
            if (grid.TryGetValue(key, out var cell))
                cell.RemoveAll(x => x.Id == id);
        }
        Asteroids.RemoveAll(x => x.Id == id);
        RockOre.Remove(id);
        RockBodies.Remove(id);
        _rockById = null;                 // invalidate the lazy id→Rock cache (rebuilt on next RockById)
        RocksChangedThisStep.Remove(id);  // a removed rock has no shrink delta to send
        RocksRemovedThisStep.Add(id);
        return true;
    }

    // Build the all-pairs next-hop table over the aleph gate graph. For every ordered sector pair (S, D)
    // reachable through gates, record the one gate to leave S by on a shortest-hop route to D. BFS gives
    // the hop distances; among S's outgoing gates that step one hop closer to D the LOWEST gate Id wins.
    // Fully deterministic: node/gate lists are sorted by id, so the result never depends on Dictionary or
    // Alephs iteration order, and there is no Random anywhere (routing runs inside the deterministic sim).
    private void BuildSectorRouting()
    {
        // Adjacency: outgoing gates per sector, each list sorted by gate Id (the equal-hop tie-break).
        var gatesFrom = new Dictionary<uint, List<Gate>>();
        var nodes = new HashSet<uint>();
        foreach (var s in Sectors)
            nodes.Add(s.Id);
        foreach (var g in Alephs)
        {
            nodes.Add(g.SectorId);
            nodes.Add(g.DestSectorId);
            if (!gatesFrom.TryGetValue(g.SectorId, out var list))
                gatesFrom[g.SectorId] = list = new List<Gate>();
            list.Add(g);
        }
        foreach (var list in gatesFrom.Values)
            list.Sort((a, b) => a.Id.CompareTo(b.Id));

        // Hop distance from every source to every reachable sector (unweighted BFS on the gate graph).
        var sorted = new List<uint>(nodes);
        sorted.Sort();
        var dist = new Dictionary<uint, Dictionary<uint, int>>();
        foreach (var src in sorted)
        {
            var d = new Dictionary<uint, int> { [src] = 0 };
            var queue = new Queue<uint>();
            queue.Enqueue(src);
            while (queue.Count > 0)
            {
                uint cur = queue.Dequeue();
                if (!gatesFrom.TryGetValue(cur, out var outs))
                    continue;
                foreach (var g in outs)
                    if (!d.ContainsKey(g.DestSectorId))
                    {
                        d[g.DestSectorId] = d[cur] + 1;
                        queue.Enqueue(g.DestSectorId);
                    }
            }
            dist[src] = d;
        }

        // Keep the hop distances too (SectorHops) — consumers rank cross-sector candidates ("nearest
        // rock/base by route") without re-walking the gate graph.
        foreach (var src in sorted)
            foreach (var (dst, hops) in dist[src])
                if (dst != src)
                    _hops[(src, dst)] = hops;

        // Next hop: for each (S, D), the lowest-Id outgoing gate of S whose destination sits one hop
        // closer to D than S is. `outs` is Id-sorted, so the first match is the lowest-Id gate.
        foreach (var src in sorted)
        {
            if (!gatesFrom.TryGetValue(src, out var outs))
                continue;
            var dSrc = dist[src];
            foreach (var dst in sorted)
            {
                if (dst == src || !dSrc.TryGetValue(dst, out int need))
                    continue; // self, or unreachable from src
                foreach (var g in outs)
                    if (dist.TryGetValue(g.DestSectorId, out var dMid)
                        && dMid.TryGetValue(dst, out int viaHop)
                        && viaHop == need - 1)
                    {
                        _nextHop[(src, dst)] = g;
                        break;
                    }
            }
        }
    }

    // The gate to take FROM `fromSector` as the next hop of a shortest route toward `toSector`, or null
    // when they are the same sector or `toSector` is unreachable through gates. Precomputed all-pairs
    // (BuildSectorRouting) → O(1), safe to call from the deterministic sim step.
    public Gate? NextGateTo(uint fromSector, uint toSector)
    {
        if (fromSector == toSector)
            return null;
        return _nextHop.TryGetValue((fromSector, toSector), out var g) ? g : null;
    }

    // Shortest gate count from one sector to another: 0 = same sector, -1 = unreachable. Same BFS
    // that feeds NextGateTo, precomputed → O(1) from the sim step.
    public int SectorHops(uint fromSector, uint toSector)
    {
        if (fromSector == toSector)
            return 0;
        return _hops.TryGetValue((fromSector, toSector), out int h) ? h : -1;
    }

    // (Re)seed every team's economy from the faction snapshot: reset Credits to the starting grant,
    // Score to 0, and re-clone the faction's base tech/capability sets into fresh per-team owned sets
    // (so a prior match's unlocks don't carry over and the owned sets stay isolated per team). Called
    // at construction and on each match start (Simulation.StartMatch).
    public void SeedEconomy(FactionStart start)
    {
        foreach (var (teamId, team) in TeamStates)
        {
            team.Credits = start.StartingCredits;
            team.Score = 0;
            team.OwnedTechs = start.BaseTechs.Clone();
            team.OwnedCapabilities = start.BaseCapabilities.Clone();
            // Miners start authorized only where the team already lives: its garrison sector(s).
            team.AuthorizedMiningSectors.Clear();
            foreach (var b in Bases)
                if (b.Team == teamId)
                    team.AuthorizedMiningSectors.Add(b.SectorId);
        }
    }

    // Per-class ship hulls: load each ship def's GLB and pre-scale its hull to the def's authored
    // ModelLength (the same silhouette length the client uniform-scales the GLB to), so the
    // world-frame collision hull matches the rendered ship and can never diverge from content. A
    // def with no ModelName (no GLB) is skipped — that class falls back to the ShipRadius sphere,
    // same as a missing/degenerate GLB. The escape pod is identified by its reserved ClassId
    // (GameContent.PodClassId), not by name.
    private static (Dictionary<byte, ShipBody>, ShipBody?) LoadShipBodies(IReadOnlyList<ShipClassDef> ships)
    {
        var classes = new Dictionary<byte, ShipBody>();
        ShipBody? pod = null;
        foreach (var def in ships)
        {
            if (string.IsNullOrEmpty(def.ModelName))
                continue;
            var body = LoadShipHull($"ships/{def.ModelName}.glb", def.ModelLength);
            if (body is null)
                continue;
            if (def.ClassId == GameContent.PodClassId)
                pod = body;
            else
                classes[def.ClassId] = body.Value;
        }
        return (classes, pod);
    }

    private static ShipBody? LoadShipHull(string relPath, float targetLen)
    {
        var model = SimAssets.TryLoad(relPath);
        if (model is null || model.LongestAxis <= 1e-3f)
            return null;
        float ws = targetLen / model.LongestAxis;
        return new ShipBody(model.Hull.Scaled(ws), model.Hull.BoundingRadius * ws);
    }

    // Populate _baseModels, one BaseModelData per runtime base type. Type 0 (garrison) is always
    // loaded. From the content base defs when supplied (per-type ModelName + Radius); else the legacy
    // single "garrison"/BaseRadius model so a minimal caller stays byte-identical. A type whose GLB is
    // missing keeps a sphere-collision entry (Hull null) at its def radius.
    private void LoadBaseModels()
    {
        if (_baseDefs is { Count: > 0 })
        {
            foreach (var def in _baseDefs)
            {
                string model = string.IsNullOrEmpty(def.ModelName) ? "garrison" : def.ModelName;
                var data = LoadBaseModel(model, def.Radius > 0f ? def.Radius : BaseRadius);
                data.MaxHealth = def.MaxHealth; // per-type hull; BaseMaxHealthOf falls back to BaseMaxHealth if 0
                _baseModels[def.BaseTypeId] = data;
            }
        }
        // Guarantee a garrison (type 0) entry regardless (legacy/minimal callers).
        if (!_baseModels.ContainsKey(0))
            _baseModels[0] = LoadBaseModel("garrison", BaseRadius);
    }

    // One base type's sim-model → world hull + bay frame. The base mesh is authored +90° off about X,
    // so we bake the same orientation correction the client renders with (CollisionConfig.BaseModel
    // Rotation) into the parsed hull + hardpoints, then uniform-scale so the longest axis spans
    // radius*2 as the client does — so the sim hull, docking doors and launch bays match the rendered,
    // corrected superstructure. A missing GLB yields a sphere-collision entry (Hull null) at `radius`.
    private static BaseModelData LoadBaseModel(string modelName, float radius)
    {
        var fallbackExits = new[] { new BaseExit(default, new Vec3(0f, 0f, 1f)) };
        var model = SimAssets.TryLoad($"bases/{modelName}.glb", CollisionConfig.BaseModelRotation);
        if (model is null)
            return new BaseModelData { Exits = fallbackExits, Radius = radius };
        float ws = radius * 2f / MathF.Max(1e-3f, model.LongestAxis);
        ConvexHull hull = model.Hull.Scaled(ws);
        // World-scale each authored sub-hull the SAME way as the merged hull (identity-oriented base ⇒
        // just uniform scale). Partless models: model.Hulls aliases the merged hull ⇒ one entry whose
        // geometry equals `hull` (built independently but from the identical planes ⇒ same result).
        var subHulls = new ConvexHull[model.Hulls.Count];
        for (int i = 0; i < model.Hulls.Count; i++)
            subHulls[i] = model.Hulls[i].Scaled(ws);
        // Exits: one launch bay per HP_DockingExit node. A ship appears at the hardpoint
        // (world-scaled) and launches OPPOSITE the node's authored forward — the HP_ +Z points back
        // into the bay, so the launch axis is its negation. A degenerate forward falls back to the
        // old radially-outward guess; a model with no exit nodes gets the single default entry.
        var exits = new List<BaseExit>();
        foreach (var hp in model.Hardpoints)
            if (hp.Name.StartsWith("HP_DockingExit", StringComparison.Ordinal))
                exits.Add(
                    new BaseExit(
                        hp.Pos * ws,
                        hp.Forward.LengthSquared() > 1e-6f ? Normalize(hp.Forward * -1f) : Normalize(hp.Pos)
                    )
                );
        BaseExit[] exitArr = exits.Count > 0 ? exits.ToArray() : fallbackExits;

        // Rectangular docking doors from the grouped HP_DockingEntrance markers (5 per door), parsed
        // by the SHARED DockFaceParser so the client's CollisionWorld builds a bit-identical DockFace[]
        // from the same GLB bytes. A base may author N doors. For AI aim we degrade to aggregates
        // across every face: BaseDoorCenter = centroid of the door face centres, BaseEntryAxis = mean
        // inward face-normal (the direction a pod should fly to enter a door).
        DockFace[] faces = DockFaceParser.Build(model.Hardpoints, ws, msg => Console.WriteLine($"  [World] {msg}"));
        Vec3 centerSum = default,
            normalSum = default;
        foreach (var f in faces)
        {
            centerSum += f.Center;
            normalSum += f.Normal;
        }
        Vec3 doorCenter = faces.Length > 0 ? centerSum * (1f / faces.Length) : default;
        Vec3 entryAxis = faces.Length > 0 ? Normalize(normalSum) : exitArr[0].Dir;
        if (entryAxis.LengthSquared() < 0.5f)
            entryAxis = exitArr[0].Dir; // faces' normals canceled (opposed doors) — fall back to a unit axis
        return new BaseModelData
        {
            Model = model,
            Hull = hull,
            SubHulls = subHulls,
            Exits = exitArr,
            EntryAxis = entryAxis,
            DoorCenter = doorCenter,
            DockFaces = faces,
            Radius = radius,
        };
    }

    // Per-rock collision bodies: one cached hull per asteroid variant, instanced by each rock's
    // scale + rotation. Rocks whose variant GLB is missing stay sphere-collided (no body added).
    private void LoadRockBodies()
    {
        if (Asteroids.Count == 0)
            return;
        var variants = new Dictionary<byte, SimModel?>();
        foreach (var r in Asteroids)
        {
            if (!variants.TryGetValue(r.Variant, out var vm))
            {
                string name = AsteroidShapes.NameForIndex(r.Variant);
                vm = string.IsNullOrEmpty(name) ? null : SimAssets.TryLoad($"asteroids/{name}.glb");
                variants[r.Variant] = vm;
            }
            if (vm is null || vm.Hull.BoundingRadius <= 1e-3f)
                continue;
            float scale = r.Radius * AsteroidCollisionScale / vm.Hull.BoundingRadius;
            var (spinAxis, spinSpeed) = Collide.RockSpin(r.Id);
            RockBodies[r.Id] = new RockBody(vm.Hull, Collide.RockRotation(r.RotX, r.RotY, r.RotZ), scale, spinAxis, spinSpeed, scale);
        }
        // ponytail: one-line proof of hull-vs-sphere collision. 0/N here == every rock is a sphere
        // (assets dir not found by THIS running server — check the [SimAssets] line above it).
        Log.RockHullsLoaded(_log, RockBodies.Count, Asteroids.Count);
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v * (1f / l) : new Vec3(0f, 0f, 1f);
    }

    public Dictionary<(int, int, int), List<Rock>> RockGrid(uint sector) =>
        _rockGrid.TryGetValue(sector, out var g) ? g : NoGrid;

    // Resolve an asteroid by id (player-autopilot rock targets). Rocks are static per match, so a
    // linear scan is fine; lazily memoize an id→Rock map on first use to keep repeated lookups cheap.
    public Rock? RockById(ulong id)
    {
        if (_rockById is null)
        {
            _rockById = new Dictionary<ulong, Rock>(Asteroids.Count);
            foreach (var r in Asteroids)
                _rockById[r.Id] = r;
        }
        return _rockById.TryGetValue(id, out var rock) ? rock : null;
    }

    private Dictionary<ulong, Rock>? _rockById; // lazily built id→Rock cache for RockById

    // Resolve a base by id (player-autopilot base targets). Bases is tiny (a handful) — linear scan.
    public BaseSite? BaseById(ulong id)
    {
        foreach (var b in Bases)
            if (b.Id == id)
                return b;
        return null;
    }

    // Runtime base creation (v37 base building). APPENDS a new base of `baseTypeId` for `team` at
    // (sector, pos), full health + a fresh research state — never reorders/removes, so every existing
    // Bases index stays valid (BaseHealth/ResearchByBase are appended in lockstep). Returns the new
    // base id. Sim-thread only (called from the constructor build completion). The caller re-resolves
    // team unlocks (the new base type may grant capabilities) and reveals it to the owning team.
    public ulong CreateBase(byte team, byte baseTypeId, uint sectorId, Vec3 pos)
    {
        ulong id = _nextBaseId++;
        Bases.Add(new BaseSite(id, team, sectorId, pos, baseTypeId));
        BaseHealth.Add(BaseMaxHealthOf(baseTypeId, team)); // per-type hull × team factor (v41)
        ResearchByBase.Add(new BaseResearchState());
        return id;
    }

    // Match reset on a REUSED World (StartMatch when no fresh World is built): trim runtime-built
    // bases back to the original garrisons, refill health, and clear research so the next match starts
    // clean. (The production path builds a fresh World per match; this covers World reuse in tests.)
    public void ResetMatchBases()
    {
        if (Bases.Count > GarrisonCount)
        {
            Bases.RemoveRange(GarrisonCount, Bases.Count - GarrisonCount);
            BaseHealth.RemoveRange(GarrisonCount, BaseHealth.Count - GarrisonCount);
            ResearchByBase.RemoveRange(GarrisonCount, ResearchByBase.Count - GarrisonCount);
        }
        for (int i = 0; i < BaseHealth.Count; i++)
        {
            // Every match-start base is a type-0 garrison (world-gen places only garrisons). A station
            // upgrade (v39) may have swapped one to a higher tier mid-match — restore it to type 0 so a
            // reused-World restart starts pristine (health then refills at the type-0 max).
            if (Bases[i].BaseTypeId != 0)
                Bases[i] = Bases[i] with { BaseTypeId = 0 };
            BaseHealth[i] = BaseMaxHealthOf(Bases[i].BaseTypeId, Bases[i].Team); // per-type refill × team factor (v41)
            ResearchByBase[i].Active.Clear();
            ResearchByBase[i].OnDeck = null;
        }
        _nextBaseId = (ulong)GarrisonCount + 1;
    }

    // TEST SEAM: hand-place an occluder rock into a sector's spatial grid (and the flat list) so the
    // sentinel empty sector 999 can exercise the fog cone-occlusion path — the grid is immutable in
    // production (built once at world-gen), so this must only be called from tests before ticking.
    public Rock AddRockForTest(uint sector, Vec3 pos, float radius, byte variant = 0)
    {
        ulong id = 1;
        foreach (var r in Asteroids)
            if (r.Id >= id)
                id = r.Id + 1;
        var rock = new Rock(id, sector, pos, radius, variant, 0f, 0f, 0f);
        Asteroids.Add(rock);
        _rockById = null; // invalidate the lazy id→Rock cache (test seam mutates the rock list)
        if (!_rockGrid.TryGetValue(sector, out var grid))
            _rockGrid[sector] = grid = new Dictionary<(int, int, int), List<Rock>>();
        var key = (CellOf(pos.X), CellOf(pos.Y), CellOf(pos.Z));
        if (!grid.TryGetValue(key, out var cell))
            grid[key] = cell = new List<Rock>();
        cell.Add(rock);
        return rock;
    }

    // TEST SEAM: hand-place a gate (aleph) mouth into a sector so the sentinel empty sector 999 can
    // exercise the projectile-barrier path. Only the mouth Pos/SectorId matter for blocking; the
    // partner endpoint is irrelevant here. Must only be called from tests before ticking.
    public Gate AddAlephForTest(uint sector, Vec3 pos)
    {
        ulong id = 1;
        foreach (var g in Alephs)
            if (g.Id >= id)
                id = g.Id + 1;
        var gate = new Gate(id, sector, sector, pos, pos);
        Alephs.Add(gate);
        return gate;
    }

    public float SectorRadius(uint sector)
    {
        foreach (var s in Sectors)
            if (s.Id == sector)
                return s.Radius;
        return float.MaxValue;
    }

    // Core pattern: a SHALLOW DISC filling outward to ~field-fill-frac of the sector radius, at
    // constant areal density (count ∝ disc area) so a bigger sector gets proportionally more rocks
    // at the same spacing. sqrt-uniform radius = even density; the disc stays thin in Y (shallow).
    private void SeedAsteroidField(ref DetRng rng, uint sector, float sectorRadius, float density, ref ulong id)
    {
        float fillFrac = _seed.FieldFillFrac;
        float flatten = _seed.FieldFlatten;
        float areaDensity = _seed.FieldAreaDensity;
        float maxR = sectorRadius * fillFrac;
        float hY = maxR * flatten;
        int count = (int)MathF.Round(density * areaDensity * MathF.PI * maxR * maxR);
        var placed = new PlacementGrid(_seed.FieldRockMax, _seed.RockMinGap);
        for (int i = 0; i < count; i++)
        {
            // Size FIRST — the fit check needs the radius before a position can be judged.
            float radius = RockRadius(ref rng, _seed.FieldRockMin, _seed.FieldRockMax, _seed.RockSizeSkew);
            Vec3 pos = default;
            bool ok = false;
            for (int attempt = 0; attempt < MaxPlaceAttempts && !ok; attempt++)
            {
                double ang = rng.NextDouble() * Math.PI * 2.0;
                double rr = Math.Sqrt(rng.NextDouble()) * maxR; // sqrt → uniform areal density, bounded by maxR
                float px = (float)(Math.Cos(ang) * rr);
                float pz = (float)(Math.Sin(ang) * rr);
                float py = (float)((rng.NextDouble() * 2.0 - 1.0) * hY);
                pos = new Vec3(px, py, pz);
                ok = RockFits(pos, radius, sector, placed);
            }
            if (!ok)
                continue; // crowded pocket — drop rather than overlap (still deterministic per seed)
            var (variant, rx, ry, rz) = NextShape(ref rng);
            Asteroids.Add(new Rock(id++, sector, pos, radius, variant, rx, ry, rz));
            placed.Add(pos, radius);
        }
    }

    // Verge pattern: a flattened ANNULAR BELT spanning belt-inner-frac..belt-outer-frac of the
    // sector radius, reaching outward toward the edge. Count ∝ annulus area at constant density;
    // sqrt-in-area radius draw keeps the ring evenly filled rather than bunched at the inner edge.
    private void SeedAsteroidBelt(ref DetRng rng, uint sector, float sectorRadius, float density, ref ulong id)
    {
        float innerFrac = _seed.BeltInnerFrac;
        float outerFrac = _seed.BeltOuterFrac;
        float flatten = _seed.BeltFlatten;
        float areaDensity = _seed.BeltAreaDensity;
        float rIn = sectorRadius * innerFrac;
        float rOut = sectorRadius * outerFrac;
        float hY = sectorRadius * flatten;
        float area = MathF.PI * (rOut * rOut - rIn * rIn);
        int count = (int)MathF.Round(density * areaDensity * area);
        var placed = new PlacementGrid(_seed.BeltRockMax, _seed.RockMinGap);
        for (int i = 0; i < count; i++)
        {
            // Size FIRST — the fit check needs the radius before a position can be judged.
            float radius = RockRadius(ref rng, _seed.BeltRockMin, _seed.BeltRockMax, _seed.RockSizeSkew);
            Vec3 pos = default;
            bool ok = false;
            for (int attempt = 0; attempt < MaxPlaceAttempts && !ok; attempt++)
            {
                double ang = rng.NextDouble() * Math.PI * 2.0;
                double t = rng.NextDouble();
                double rr = Math.Sqrt(rIn * rIn + t * (rOut * rOut - rIn * rIn)); // uniform areal density across the annulus
                float px = (float)(Math.Cos(ang) * rr);
                float pz = (float)(Math.Sin(ang) * rr);
                float py = (float)((rng.NextDouble() * 2.0 - 1.0) * hY);
                pos = new Vec3(px, py, pz);
                ok = RockFits(pos, radius, sector, placed);
            }
            if (!ok)
                continue; // crowded pocket — drop rather than overlap (still deterministic per seed)
            var (variant, rx, ry, rz) = NextShape(ref rng);
            Asteroids.Add(new Rock(id++, sector, pos, radius, variant, rx, ry, rz));
            placed.Add(pos, radius);
        }
    }

    // ---- Seeding-time minimum spacing (rock↔rock via seeding.rock-min-gap, rock↔base via
    // seeding.base-clearance; both surface-to-surface, 0 disables). Candidate positions are
    // rejection-sampled: a rock re-rolls its position up to MaxPlaceAttempts times and is DROPPED
    // if it never fits — accepting anyway would reintroduce overlap exactly in the crowded spots
    // the gap exists to prevent. The fixed attempt loop keeps layouts per-seed deterministic, and
    // ore classes are assigned AFTER seeding (AssignOre) from the surviving rocks, so drops never
    // eat into a sector's guaranteed He3/special counts. Caveat: the rare special rocks are
    // inflated ×SpecialRockRadiusMult after seeding, so an oversized special (≤1/sector) may still
    // brush a neighbour. At stock knobs the drop rate is ~0. ----
    private const int MaxPlaceAttempts = 12;

    private bool RockFits(Vec3 pos, float radius, uint sector, PlacementGrid placed)
    {
        float clearance = _seed.BaseClearance;
        if (clearance > 0f)
            foreach (var b in Bases)
            {
                if (b.SectorId != sector)
                    continue;
                float need = BaseRadius + radius + clearance;
                if ((pos - b.Pos).LengthSquared() < need * need)
                    return false;
            }
        float gap = _seed.RockMinGap;
        return gap <= 0f || !placed.AnyCloserThan(pos, radius, gap);
    }

    // Already-placed rocks for ONE Seed* call, bucketed into a coarse grid whose cell covers the
    // worst-case interacting pair (2·rockMax + gap) so a fit check scans only its 3×3×3
    // neighbourhood even when modded densities balloon rock counts (same broad-phase idea as the
    // sim's _rockGrid).
    private sealed class PlacementGrid
    {
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private readonly List<(Vec3 Pos, float Radius)> _placed = new();
        private readonly float _cell;

        public PlacementGrid(float rockMax, float gap) =>
            _cell = MathF.Max(2f * rockMax + MathF.Max(gap, 0f), 1f);

        private (int, int, int) KeyOf(Vec3 p) =>
            (
                (int)MathF.Floor(p.X / _cell),
                (int)MathF.Floor(p.Y / _cell),
                (int)MathF.Floor(p.Z / _cell)
            );

        public void Add(Vec3 pos, float radius)
        {
            var key = KeyOf(pos);
            if (!_cells.TryGetValue(key, out var cell))
                _cells[key] = cell = new List<int>();
            cell.Add(_placed.Count);
            _placed.Add((pos, radius));
        }

        public bool AnyCloserThan(Vec3 pos, float radius, float gap)
        {
            var (cx, cy, cz) = KeyOf(pos);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var cell))
                            continue;
                        foreach (int j in cell)
                        {
                            float need = radius + _placed[j].Radius + gap;
                            if ((pos - _placed[j].Pos).LengthSquared() < need * need)
                                return true;
                        }
                    }
            return false;
        }
    }

    // Dust distribution is derived ENTIRELY from the "amount" feel knob, RELATIVE to sector size — so
    // an identical dust block reads identically in any-sized sector (the bug this whole change fixes).
    // amount → cloud radius (a fraction of the sector radius), cloud count (enough to tile the covered
    // disc at an amount-scaled overlap, clamped for fill-rate), and per-puff density. Own DetRng
    // (authored seed, or world-seed ^ sector) keeps it independent of the asteroid/aleph draw.
    private const int MaxDustClouds = 120;      // fill-rate cap; huge sectors use fewer, bigger clouds
    private const float DustCoverageFrac = 0.9f; // clouds fill this fraction of the sector radius
    private const float DustFlatten = 0.15f;     // shallow disc (Y half-thickness / coverage radius)

    private void SeedDustClouds(uint sector, float sectorRadius, SectorDust? dust)
    {
        if (dust is null || dust.Amount <= 0f)
            return;
        float amount = Math.Clamp(dust.Amount, 0f, 1f);
        ulong s = dust.Seed ?? (Seed ^ 0xD005_7D05_D005_7D05UL ^ ((ulong)sector * 0x9E37_79B9_7F4A_7C15UL));
        var rng = new DetRng(s);

        float coverageR = sectorRadius * DustCoverageFrac;
        // Cloud radius scales with the sector (and grows with dustiness), so coverage is intrinsic.
        float cloudR = sectorRadius * Lerp(0.15f, 0.5f, amount);
        float density = Lerp(0.3f, 0.7f, amount); // per-puff opacity feel

        // Tile the covered disc: (coverageR / cloudR)² single-cover clouds, times an amount-scaled
        // overlap factor. Clamp to a perf cap — at the cap a very large sector just gets bigger clouds.
        float overlap = Lerp(0.6f, 2.8f, amount);
        int count = (int)MathF.Round(overlap * (coverageR * coverageR) / (cloudR * cloudR));
        count = Math.Clamp(count, 1, MaxDustClouds);

        float hY = coverageR * DustFlatten;
        for (int i = 0; i < count; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2.0;
            double rr = Math.Sqrt(rng.NextDouble()) * coverageR; // sqrt → uniform areal density
            float px = (float)(Math.Cos(ang) * rr);
            float pz = (float)(Math.Sin(ang) * rr);
            float py = (float)((rng.NextDouble() * 2.0 - 1.0) * hY);
            float radius = cloudR * (float)(0.7 + 0.6 * rng.NextDouble()); // ±30% per-cloud jitter
            DustClouds.Add(new DustCloud(sector, new Vec3(px, py, pz), radius, density));
        }
    }

    // Radar/vision range multiplier through FULLY dense dust. `amount` sets the baseline shortening
    // (1 = no attenuation at amount 0, 0.15 at amount 1); `opacity` (0..1, default 1) then scales that
    // shortening toward "no impact" so an author can decouple radar impact from the VISUAL thickness —
    // opacity 0 leaves radar untouched (floor 1) regardless of amount, opacity 1 is the legacy behaviour.
    // Consumed by the vision sim (Simulation.Vision).
    public static float DustVisionFloor(float amount, float opacity = 1f)
    {
        float baseFloor = Lerp(1f, 0.15f, Math.Clamp(amount, 0f, 1f));
        return 1f - (1f - baseFloor) * Math.Clamp(opacity, 0f, 1f); // scale the deviation from clear
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // r³ — a rock's volume proportion, used to scale ore capacity by size.
    private static float Cube(float r) => r * r * r;

    // Teams seeded by the default-arena fallback (below) when a config authors sectors but no garrison.
    private const int DefaultTeamCount = 2;
    // The number of teams the sim currently supports (Simulation.Pig.NumTeams / win condition / lobby).
    // A map that declares more garrison teams than this fails fast at World construction.
    public const int MaxSupportedTeams = 2;

    // The legacy fallback arena, used ONLY when a config declares no sectors at all (a bare test/boot
    // world). It reproduces the historical two-sector layout so bare-config callers behave exactly as
    // before this became data-driven: a large field home for team 0 and a smaller belt for team 1,
    // linked. Real maps author every sector explicitly and never touch this.
    private const float LegacyHomeRadius = 2100f; // before × sector-scale
    private const float LegacyVergeRadius = 700f;
    private static List<WorldSectorConfig> DefaultArena(float sectorScale) =>
        new()
        {
            new WorldSectorConfig
            {
                Id = 0, Radius = LegacyHomeRadius * sectorScale,
                Asteroids = AsteroidKind.Field, Garrison = new SectorGarrison { Team = 0 },
            },
            new WorldSectorConfig
            {
                Id = 1, Radius = LegacyVergeRadius * sectorScale,
                Asteroids = AsteroidKind.Belt, Garrison = new SectorGarrison { Team = 1 },
            },
        };

    // Default gate topology when a map authors no `links`: connect sectors in a ring by id (a single
    // edge for two sectors; the wrap-around edge for three or more). Empty for a lone sector.
    private static List<SectorLink> DefaultRing(List<WorldSectorConfig> secs)
    {
        var links = new List<SectorLink>();
        if (secs.Count < 2)
            return links;
        if (secs.Count == 2)
        {
            links.Add(new SectorLink(secs[0].Id, secs[1].Id));
            return links;
        }
        for (int i = 0; i < secs.Count; i++)
            links.Add(new SectorLink(secs[i].Id, secs[(i + 1) % secs.Count].Id));
        return links;
    }

    // Mirror the module's NextAsteroidShape (Lib.cs): one variant index + three orientation
    // angles, drawn in the exact same order so positions stay byte-identical with the module's
    // map AND the cosmetic shape matches. The index count comes from the shared variant list so
    // server draw-count and client name-lookup can never drift apart.
    private static (byte variant, float rx, float ry, float rz) NextShape(ref DetRng rng)
    {
        byte variant = (byte)rng.NextInt(AsteroidShapes.Variants.Length);
        float rx = (float)rng.NextRange(0, Math.PI * 2.0);
        float ry = (float)rng.NextRange(0, Math.PI * 2.0);
        float rz = (float)rng.NextRange(0, Math.PI * 2.0);
        return (variant, rx, ry, rz);
    }

    // Rock size draw: one RNG sample mapped across [minR, maxR] with a mild power-law skew
    // (exponent > 1 biases toward the small end) so a field is mostly modest rocks with the
    // occasional large one — more visual variety than a flat uniform draw, still bounded to a
    // reasonable range. Consumes exactly one NextDouble so the asteroid RNG sequence is unchanged.
    private static float RockRadius(ref DetRng rng, float minR, float maxR, float skew)
    {
        double t = Math.Pow(rng.NextDouble(), skew);
        return (float)(minR + t * (maxR - minR));
    }

    private static Vec3 RandomOuterPos(ref DetRng rng, float sectorRadius)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double frac = 0.6 + 0.3 * Math.Sqrt(rng.NextDouble());
        float r = (float)(sectorRadius * frac);
        float y = (float)((rng.NextDouble() - 0.5) * sectorRadius * 0.2);
        return new Vec3((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }

    private static Vec3 RandomBasePos(ref DetRng rng, float minR, float maxR, float yJitter)
    {
        double ang = rng.NextDouble() * Math.PI * 2.0;
        double r = minR + rng.NextDouble() * (maxR - minR);
        float y = (float)((rng.NextDouble() - 0.5) * yJitter);
        return new Vec3((float)(Math.Cos(ang) * r), y, (float)(Math.Sin(ang) * r));
    }
}

// splitmix64 — ported verbatim from the module so seeds reproduce the same map.
public struct DetRng
{
    private ulong _state;

    public DetRng(ulong seed)
    {
        _state = seed;
    }

    public ulong NextULong()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public double NextRange(double lo, double hi) => lo + (hi - lo) * NextDouble();

    public int NextInt(int n) => (int)(NextDouble() * n);
}
