using StellarAllegiance.Shared;

// Guard for the SHARED collision response the client and server both run. The server self-test
// already covers ConvexHull/SphereVsHull geometry; this pins the new kinematic response + the
// own-base dock-disc carve-out, which is exactly what makes the client predict the server's bounce.

int failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok)
        failures++;
}

bool Near(float a, float b, float eps = 1e-3f) => MathF.Abs(a - b) <= eps;

// A square docking door: centre `c`, INWARD unit normal `n` (direction a ship travels entering),
// half-extent `e` on both in-plane axes. Used to build test doors without the full 5-marker parse.
DockFace SquareDoor(Vec3 c, Vec3 n, float e)
{
    Vec3 seed = MathF.Abs(n.Y) < 0.9f ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
    Vec3 u = Vec3.Cross(n, seed);
    u *= 1f / u.Length();
    Vec3 v = Vec3.Cross(n, u);
    v *= 1f / v.Length();
    return new DockFace(c, n, u, v, e, e);
}

// A unit cube hull, axis-aligned at the origin (faces at ±1).
var cube = ConvexHull.Build(
    new[]
    {
        new Vec3(-1, -1, -1),
        new Vec3(1, -1, -1),
        new Vec3(-1, 1, -1),
        new Vec3(1, 1, -1),
        new Vec3(-1, -1, 1),
        new Vec3(1, -1, 1),
        new Vec3(-1, 1, 1),
        new Vec3(1, 1, 1),
    }
);

const float shipR = 0.5f;
const float rest = CollisionConfig.CollisionRestitution; // 0.3

// 1) Sphere just inside the +X face → outward normal +X, penetration = radius − faceGap.
//    pos.X 1.4, faceGap = 1.0, so the 0.5-radius sphere overlaps by 0.5 − 0.4 = 0.1.
bool hit = Collide.SphereVsHull(new Vec3(1.4f, 0, 0), shipR, cube, default, Quat.Identity, 1f, out var n, out var pen);
Check("sphere-vs-cube: contacts +X face", hit && Near(n.X, 1f) && Near(n.Y, 0f) && Near(n.Z, 0f));
Check("sphere-vs-cube: penetration 0.1", Near(pen, 0.1f));

// 2) Bounce pushes the ship to the surface and reflects the inbound normal velocity.
//    Inbound vn = −1 (moving −X into the face); reflected vx = −1 − (1+0.3)·(−1) = 0.3.
var s = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.Bounce(ref s, n, pen, rest, out float vn);
Check("bounce: reports closing vn = −1", Near(vn, -1f));
Check("bounce: pushed out to x = 1.5", Near(s.Pos.X, 1.5f));
Check("bounce: reflected vx = 0.3", Near(s.Vel.X, 0.3f));

// 3) ResolveStatics over a body list: an asteroid hull bounces the ship out.
var rock = Collide.StaticBody.AsteroidHull(cube, default, Quat.Identity, 1f);
var st = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
bool any = Collide.ResolveStatics(
    ref st,
    shipR,
    new[] { rock },
    localTeam: -1,
    0,
    rest,
    CollisionConfig.DockFaceDepth,
    out _
);
Check("resolve-statics: asteroid bounces ship out", any && Near(st.Pos.X, 1.5f));

// 4) Own-base dock-door carve-out: a door at the +X face (inward normal −X, the way a ship enters)
//    lets the OWN team's ship through (no bounce), while an ENEMY ship still bounces off the hull.
var doors = new[] { SquareDoor(new Vec3(1, 0, 0), new Vec3(-1, 0, 0), 7f) };
var baseBody = Collide.StaticBody.BaseHull(
    cube,
    default,
    team: 0,
    doors,
    DockRules.UnknownStationClass,
    DockRules.LargestFaceIndex(doors)
);

var own = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.ResolveStatics(ref own, shipR, new[] { baseBody }, localTeam: 0, 0, rest, CollisionConfig.DockFaceDepth, out _);
Check("own base: dock door lets ship through (no bounce)", Near(own.Pos.X, 1.4f) && Near(own.Vel.X, -1f));

var enemy = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.ResolveStatics(ref enemy, shipR, new[] { baseBody }, localTeam: 1, 0, rest, CollisionConfig.DockFaceDepth, out _);
Check("enemy base: solid hull bounces ship out", Near(enemy.Pos.X, 1.5f));

// 5) Touches (audio probe) agrees with the geometry: overlapping = true, clear = false.
Check(
    "touches: overlapping ship reports contact",
    Collide.Touches(new Vec3(1.4f, 0, 0), shipR, new[] { rock }, -1, 0, CollisionConfig.DockFaceDepth)
);
Check(
    "touches: clear ship reports none",
    !Collide.Touches(new Vec3(3f, 0, 0), shipR, new[] { rock }, -1, 0, CollisionConfig.DockFaceDepth)
);

// 6) Rock tumble: the spin is deterministic (server/client parity) and the live rotation actually
//    moves the collision surface, so the hull tracks the rendered rock instead of staying frozen.
var (axis, speed) = Collide.RockSpin(0xABCDEF12);
var (axisB, speedB) = Collide.RockSpin(0xABCDEF12);
Check(
    "rockspin: deterministic for an id",
    Near(axis.X, axisB.X) && Near(axis.Y, axisB.Y) && Near(axis.Z, axisB.Z) && Near(speed, speedB)
);
Check("rockspin: unit axis", Near(axis.Length(), 1f));
Check("rockspin: rate in 0.03..0.15 band", speed >= 0.03f && speed <= 0.15f);

var spawn = Collide.RockRotation(0.3f, 0.7f, 1.1f);
var probe = new Vec3(1, 2, 3);
var at0 = Collide.RockRotationAt(spawn, axis, speed, 0f);
Check(
    "rockrotationat: t=0 is the spawn pose",
    Near(at0.Rotate(probe).X, spawn.Rotate(probe).X) && Near(at0.Rotate(probe).Y, spawn.Rotate(probe).Y)
);
var at5 = Collide.RockRotationAt(spawn, axis, speed, 5f);
Check(
    "rockrotationat: advances with time",
    !Near(at5.Rotate(probe).X, spawn.Rotate(probe).X) || !Near(at5.Rotate(probe).Y, spawn.Rotate(probe).Y)
);

// A sphere off the cube's +X face clears the axis-aligned face, but a 45° spin swings a corner out
// to meet it — proving SphereVsHull honours the live rotation the server/client now feed it.
var rot45 = Collide.RockRotationAt(Quat.Identity, new Vec3(0, 0, 1), (float)(System.Math.PI / 4), 1f);
bool clearWhenAligned = !Collide.SphereVsHull(new Vec3(1.5f, 0, 0), 0.3f, cube, default, Quat.Identity, 1f, out _, out _);
bool hitWhenSpun = Collide.SphereVsHull(new Vec3(1.5f, 0, 0), 0.3f, cube, default, rot45, 1f, out _, out _);
Check("spin: rotating the hull changes the contact", clearWhenAligned && hitWhenSpun);

// ---------------------------------------------------------------------------------------------
// COMPOUND (multi-hull) STATIC BODIES — the concavity win. A base baked with authored COL_ parts
// resolves against the DEEPEST-penetration sub-hull, so a ship bounces off the actual superstructure
// and passes through the gaps a single merged shrink-wrap would have (wrongly) filled.
// ---------------------------------------------------------------------------------------------

// Two separated unit cubes: A at x=-3 (faces ±1 → spans x ∈ [-4,-2]), B at x=+3 (x ∈ [2,4]).
ConvexHull CubeAt(float cx) =>
    ConvexHull.Build(
        new[]
        {
            new Vec3(cx - 1, -1, -1),
            new Vec3(cx + 1, -1, -1),
            new Vec3(cx - 1, 1, -1),
            new Vec3(cx + 1, 1, -1),
            new Vec3(cx - 1, -1, 1),
            new Vec3(cx + 1, -1, 1),
            new Vec3(cx - 1, 1, 1),
            new Vec3(cx + 1, 1, 1),
        }
    );
var cubeA = CubeAt(-3f);
var cubeB = CubeAt(3f);

// Merged shrink-wrap spans the whole x ∈ [-4,4] slab (its concavity is exactly the gap).
var mergedAB = ConvexHull.Build(
    new[]
    {
        new Vec3(-4, -1, -1),
        new Vec3(-2, -1, -1),
        new Vec3(-4, 1, -1),
        new Vec3(-2, 1, -1),
        new Vec3(-4, -1, 1),
        new Vec3(-2, -1, 1),
        new Vec3(-4, 1, 1),
        new Vec3(-2, 1, 1),
        new Vec3(2, -1, -1),
        new Vec3(4, -1, -1),
        new Vec3(2, 1, -1),
        new Vec3(4, 1, -1),
        new Vec3(2, -1, 1),
        new Vec3(4, -1, 1),
        new Vec3(2, 1, 1),
        new Vec3(4, 1, 1),
    }
);
var compound = Collide.StaticBody.BaseHull(
    mergedAB,
    new[] { cubeA, cubeB },
    center: default,
    team: 0,
    faces: System.Array.Empty<DockFace>(),
    DockRules.UnknownStationClass,
    -1
);

// Sphere contacting cube A (just off A's -X face at x=-4.4) resolves with A's normal (−X), not B's.
bool hitA = Collide.SphereVsBody(new Vec3(-4.4f, 0, 0), shipR, compound, out var na, out var pa);
Check(
    "compound: contacts cube A with A's −X normal",
    hitA && Near(na.X, -1f) && Near(na.Y, 0f) && Near(na.Z, 0f) && Near(pa, 0.1f)
);

// Sphere contacting cube B (just off B's +X face at x=4.4) resolves with B's normal (+X), not A's.
bool hitB = Collide.SphereVsBody(new Vec3(4.4f, 0, 0), shipR, compound, out var nb, out var pb);
Check("compound: contacts cube B with B's +X normal", hitB && Near(nb.X, 1f) && Near(pb, 0.1f));

// THE CONCAVITY WIN: a sphere centred in the empty gap (x=0) touches NEITHER sub-hull — a merged
// hull over the same cloud would (wrongly) report contact there.
bool gapClear = !Collide.SphereVsBody(new Vec3(0, 0, 0), shipR, compound, out _, out _);
bool mergedWouldHit = Collide.SphereVsHull(new Vec3(0, 0, 0), shipR, mergedAB, default, Quat.Identity, 1f, out _, out _);
Check("compound: no contact in the gap between parts", gapClear);
Check("compound: merged hull WOULD wrongly fill the gap", mergedWouldHit);

// Deepest-contact selection: a sphere overlapping BOTH cubes picks the deeper one. Place it so it
// digs 0.3 into A (x=-2 face, sphere at x=-1.8 ⇒ pen 0.5−0.2=0.3) but only 0.1 into B (x=2 face,
// sphere at x=-1.8 is 3.8 away — no B overlap). Instead centre it to straddle: shrink the gap idea —
// use a big sphere at the origin large enough to reach both, deeper toward A.
var bigCubeA = CubeAt(-1.2f); // A' spans x ∈ [-2.2,-0.2]
var bigCubeB = CubeAt(1.6f); // B' spans x ∈ [0.6,2.6]
var mergedBig = ConvexHull.Build(
    new[]
    {
        new Vec3(-2.2f, -1, -1),
        new Vec3(-0.2f, -1, -1),
        new Vec3(-2.2f, 1, -1),
        new Vec3(-0.2f, 1, -1),
        new Vec3(-2.2f, -1, 1),
        new Vec3(-0.2f, -1, 1),
        new Vec3(-2.2f, 1, 1),
        new Vec3(-0.2f, 1, 1),
        new Vec3(0.6f, -1, -1),
        new Vec3(2.6f, -1, -1),
        new Vec3(0.6f, 1, -1),
        new Vec3(2.6f, 1, -1),
        new Vec3(0.6f, -1, 1),
        new Vec3(2.6f, -1, 1),
        new Vec3(0.6f, 1, 1),
        new Vec3(2.6f, 1, 1),
    }
);
var compound2 = Collide.StaticBody.BaseHull(
    mergedBig,
    new[] { bigCubeA, bigCubeB },
    default,
    0,
    System.Array.Empty<DockFace>(),
    DockRules.UnknownStationClass,
    -1
);

// Sphere radius 0.8 at x=0: overlaps A' (right face x=−0.2, sphere reaches x=−0.8 ⇒ pen 0.8−0.2=0.6)
// and B' (left face x=0.6, sphere reaches x=0.8 ⇒ pen 0.8−0.6=0.2). Deeper = A' ⇒ normal +X (out of A').
bool bothHit = Collide.SphereVsBody(new Vec3(0, 0, 0), 0.8f, compound2, out var nBoth, out var pBoth);
Check("compound: deepest contact wins (cube A' over B')", bothHit && Near(nBoth.X, 1f) && Near(pBoth, 0.6f));

// Dock-door skip bypasses the WHOLE compound body (all sub-hulls), same as the single-hull carve-out.
// Door at cube A's −X face, inward normal +X (the way a docker at x=−4.4 moving +X would enter).
var discBody = Collide.StaticBody.BaseHull(
    mergedAB,
    new[] { cubeA, cubeB },
    default,
    team: 0,
    faces: new[] { SquareDoor(new Vec3(-4, 0, 0), new Vec3(1, 0, 0), 7f) },
    DockRules.UnknownStationClass,
    0
);
var docker = new ShipState { Pos = new Vec3(-4.4f, 0, 0), Vel = new Vec3(1, 0, 0) };
Collide.ResolveStatics(ref docker, shipR, new[] { discBody }, localTeam: 0, 0, rest, CollisionConfig.DockFaceDepth, out _);
Check("compound: own dock door skips the whole body (no bounce)", Near(docker.Pos.X, -4.4f) && Near(docker.Vel.X, 1f));
Check(
    "compound: dock door still bypasses when Touches probes",
    !Collide.Touches(new Vec3(-4.4f, 0, 0), shipR, new[] { discBody }, localTeam: 0, 0, CollisionConfig.DockFaceDepth)
);

// Regression guard: a SINGLE-hull StaticBody routed through SphereVsBody === the old SphereVsHull.
Collide.SphereVsHull(new Vec3(1.4f, 0, 0), shipR, cube, default, Quat.Identity, 1f, out var nRef, out var pRef);
var single = Collide.StaticBody.BaseHull(
    cube,
    default,
    team: 0,
    System.Array.Empty<DockFace>(),
    DockRules.UnknownStationClass,
    -1
);
bool sHit = Collide.SphereVsBody(new Vec3(1.4f, 0, 0), shipR, single, out var nS, out var pS);
Check(
    "single-hull via SphereVsBody === SphereVsHull",
    sHit && Near(nS.X, nRef.X) && Near(nS.Y, nRef.Y) && Near(nS.Z, nRef.Z) && Near(pS, pRef)
);

// ---------------------------------------------------------------------------------------------
// REAL BAKED garrison.glb (the shipping garrison base — Outpost.glb is retained but unused):
// SimModel.FromGlb must expose the generated sub-hulls (tools/collision-hull --kind base: a voxel
// solid-fill decomposed by CoACD into convex parts) with the merged metrics UNCHANGED — the parts
// are each strictly interior to the visual convex hull, so they never enlarge the merged hull.
// Locate the repo GLB by probing up from the test binary (mirrors server/Assets/SimAssets.cs).
// ---------------------------------------------------------------------------------------------
string? FindBaseGlb()
{
    foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var d = new DirectoryInfo(start);
        for (int up = 0; up < 8 && d is not null; up++, d = d.Parent)
        {
            string cand = Path.Combine(d.FullName, "client", "assets", "bases", "garrison.glb");
            if (File.Exists(cand))
                return cand;
        }
    }
    return null;
}

string? glbPath = FindBaseGlb();
if (glbPath is null)
{
    Console.WriteLine("  [SKIP] garrison.glb not found — skipping baked compound-hull check");
}
else
{
    var baseModel = SimModel.FromGlb(File.ReadAllBytes(glbPath), glbPath);
    Check(
        $"baked base: generated sub-hulls (COL_ parts) present (got {baseModel.Hulls.Count}, expect 8..512)",
        baseModel.Hulls.Count is >= 8 and <= 512
    );
    Check(
        $"baked base: merged LongestAxis unchanged (~59.8492, got {baseModel.LongestAxis:0.######})",
        Near(baseModel.LongestAxis, 59.849224f, 1e-2f)
    );
    Check(
        $"baked base: merged BoundingRadius unchanged (~30.6818, got {baseModel.BoundingRadius:0.######})",
        Near(baseModel.BoundingRadius, 30.681801f, 1e-2f)
    );
    Check(
        $"baked base: merged Hull plane count unchanged (got {baseModel.Hull.Planes.Length}, expect 56)",
        baseModel.Hull.Planes.Length == 56
    );
    bool allNonDegenerate = true;
    bool allBounded = true;
    for (int i = 0; i < baseModel.Hulls.Count; i++)
    {
        if (baseModel.Hulls[i].Planes.Length <= 3)
            allNonDegenerate = false;
        // Every part is strictly interior to the merged hull (B1 containment bake), so its bounding
        // radius must be smaller than the merged envelope's.
        if (!(baseModel.Hulls[i].BoundingRadius < baseModel.BoundingRadius))
            allBounded = false;
    }
    Check("baked base: every sub-hull non-degenerate (>3 planes)", allNonDegenerate);
    Check("baked base: every sub-hull bounded by the merged hull", allBounded);

    // ---------------------------------------------------------------------------------------------
    // GROUPED DOCKING DOORS on the real garrison.glb: HP_DockingEntrance markers chunk in FIVES
    // (1 face marker + 4 boundary markers) into TWO rectangular doors. The original rework
    // regression stays covered generically: no HP_DockingExit point may trigger a dock, while every
    // door centre must.
    // ---------------------------------------------------------------------------------------------
    float ws = CollisionConfig.BaseRadius * 2f / MathF.Max(1e-3f, baseModel.LongestAxis); // ≈ 3.008
    DockFace[] baseFaces = DockFaceParser.Build(baseModel.Hardpoints, ws);
    Check($"garrison.glb: exactly two docking doors parsed (got {baseFaces.Length})", baseFaces.Length == 2);
    if (baseFaces.Length == 2)
    {
        const float shipR3 = CollisionConfig.ShipRadius; // 3
        const float depth = CollisionConfig.DockFaceDepth; // 9

        foreach (var (door, di) in baseFaces.Select((f, i) => (f, i)))
        {
            // (a) The door face centre DOES dock.
            Check(
                $"garrison.glb door {di}: door-centre point DOES dock",
                Collide.IntersectsDockFace(door.Center, baseFaces, depth, shipR3)
            );

            // (b) Just past a rectangle edge (half-extent + shipRadius + margin) does NOT dock —
            //     but only if that probe doesn't land inside the OTHER door's slab.
            Vec3 pastEdgeU = door.Center + door.U * (door.Eu + shipR3 + 1f);
            Check(
                $"garrison.glb door {di}: point past the U edge does NOT dock",
                !Collide.IntersectsDockFace(pastEdgeU, new[] { door }, depth, shipR3)
            );
            Vec3 pastEdgeV = door.Center + door.V * (door.Ev + shipR3 + 1f);
            Check(
                $"garrison.glb door {di}: point past the V edge does NOT dock",
                !Collide.IntersectsDockFace(pastEdgeV, new[] { door }, depth, shipR3)
            );
        }

        // (c) The exit-tube regression, generalized: neither HP_DockingExit hardpoint (world-scaled)
        //     may intersect any dock face — launching ships must not instantly re-dock.
        int exits = 0;
        foreach (var hp in baseModel.Hardpoints)
        {
            if (!hp.Name.StartsWith("HP_DockingExit", StringComparison.Ordinal))
                continue;
            exits++;
            Check(
                $"garrison.glb: exit hardpoint {hp.Name} does NOT dock",
                !Collide.IntersectsDockFace(hp.Pos * ws, baseFaces, depth, shipR3)
            );
        }
        Check($"garrison.glb: two exit hardpoints present (got {exits})", exits == 2);

        // Sanity on the derived rectangles: ss27 authors two big side-wall bays (~20.7 x 6.9
        // local), so the parsed half-diagonals land near 11.4 / 11.6 (measured from the authored
        // centre marker, which sits slightly off the exact rectangle centre). Matches bake.py's
        // dock_doors parse of the SAME pristine markers.
        float HalfDiag(DockFace f) => MathF.Sqrt(f.Eu * f.Eu + f.Ev * f.Ev) / ws;
        float dLo = MathF.Min(HalfDiag(baseFaces[0]), HalfDiag(baseFaces[1]));
        float dHi = MathF.Max(HalfDiag(baseFaces[0]), HalfDiag(baseFaces[1]));
        Check(
            $"garrison.glb doors: half-diagonals ≈ {{11.43, 11.61}} local (got {{{dLo:0.##}, {dHi:0.##}}})",
            Near(dLo, 11.43f, 0.25f) && Near(dHi, 11.61f, 0.25f)
        );
        // Door inward normals in the authored (z-up) frame: the bays are carved into opposite
        // side walls of the two arms — door 0 enters along −Y, door 1 along +Y.
        bool opposedY =
            baseFaces.Count(f => Near(f.Normal.Y, -1f, 1e-2f)) == 1
            && baseFaces.Count(f => Near(f.Normal.Y, 1f, 1e-2f)) == 1;
        Check("garrison.glb doors: inward normals are -Y and +Y (authored frame)", opposedY);
    }
}

// ---------------------------------------------------------------------------------------------
// SYNTHETIC MULTI-DOOR PARSE: N docking faces per base (base.glb is just the N=1 case). Two groups
// of 5 HP_DockingEntrance markers (indices 0–4 = door A, 5–9 = door B), boundary markers SHUFFLED to
// prove the order-agnostic axis derivation. worldScale = 1 so authored == world.
// ---------------------------------------------------------------------------------------------
{
    var hps = new List<(string Name, Vec3 Pos, Vec3 Forward)>
    {
        // Door A: face at origin, inward normal +Z; boundary ±X (half 4) and ±Y (half 3), shuffled.
        ("HP_DockingEntrance_0", new Vec3(0, 0, 0), new Vec3(0, 0, 1)),
        ("HP_DockingEntrance_1", new Vec3(0, 3, 0), new Vec3(0, 1, 0)),
        ("HP_DockingEntrance_2", new Vec3(-4, 0, 0), new Vec3(-1, 0, 0)),
        ("HP_DockingEntrance_3", new Vec3(0, -3, 0), new Vec3(0, -1, 0)),
        ("HP_DockingEntrance_4", new Vec3(4, 0, 0), new Vec3(1, 0, 0)),
        // Door B: face at (100,0,0), inward normal +X; boundary ±Y (half 2) and ±Z (half 5), shuffled.
        ("HP_DockingEntrance_5", new Vec3(100, 0, 0), new Vec3(1, 0, 0)),
        ("HP_DockingEntrance_6", new Vec3(100, 0, 5), new Vec3(0, 0, 1)),
        ("HP_DockingEntrance_7", new Vec3(100, -2, 0), new Vec3(0, -1, 0)),
        ("HP_DockingEntrance_8", new Vec3(100, 0, -5), new Vec3(0, 0, -1)),
        ("HP_DockingEntrance_9", new Vec3(100, 2, 0), new Vec3(0, 1, 0)),
        // A non-entrance hardpoint that must be ignored by the parser.
        ("HP_DockingExit_0", new Vec3(0, -20, 0), new Vec3(0, -1, 0)),
    };
    DockFace[] faces2 = DockFaceParser.Build(hps, 1f);
    Check($"multi-door: two doors parsed from 10 markers (got {faces2.Length})", faces2.Length == 2);
    if (faces2.Length == 2)
    {
        DockFace a = faces2[0],
            b = faces2[1];
        Check(
            "multi-door A: centre at origin, inward normal +Z",
            Near(a.Center.X, 0) && Near(a.Center.Y, 0) && Near(a.Center.Z, 0) && Near(a.Normal.Z, 1f, 1e-3f)
        );
        Check(
            "multi-door A: half-extents {4,3} (order-agnostic)",
            Near(MathF.Max(a.Eu, a.Ev), 4f, 1e-3f) && Near(MathF.Min(a.Eu, a.Ev), 3f, 1e-3f)
        );
        Check("multi-door B: centre at (100,0,0), inward normal +X", Near(b.Center.X, 100f) && Near(b.Normal.X, 1f, 1e-3f));
        Check(
            "multi-door B: half-extents {5,2} (order-agnostic)",
            Near(MathF.Max(b.Eu, b.Ev), 5f, 1e-3f) && Near(MathF.Min(b.Eu, b.Ev), 2f, 1e-3f)
        );

        const float shipR3 = CollisionConfig.ShipRadius;
        const float depth = CollisionConfig.DockFaceDepth;
        // A ship at door A's centre docks; a ship out at (50,0,0) — between the doors — docks at neither.
        Check(
            "multi-door: ship at door A centre docks",
            Collide.IntersectsDockFace(new Vec3(0, 0, 0), faces2, depth, shipR3)
        );
        Check(
            "multi-door: ship at door B centre docks",
            Collide.IntersectsDockFace(new Vec3(100, 0, 0), faces2, depth, shipR3)
        );
        Check(
            "multi-door: ship midway between doors docks at neither",
            !Collide.IntersectsDockFace(new Vec3(50, 0, 0), faces2, depth, shipR3)
        );
    }

    // Leftover-marker fallback: 6 entrance markers (not a multiple of 5) ⇒ one door + one legacy disc.
    var hps2 = new List<(string Name, Vec3 Pos, Vec3 Forward)>(hps.GetRange(0, 5))
    {
        ("HP_DockingEntrance_5", new Vec3(0, 50, 0), new Vec3(0, 1, 0)),
    };
    DockFace[] faces3 = DockFaceParser.Build(hps2, 1f);
    Check($"multi-door: 6 markers ⇒ 1 door + 1 legacy disc (got {faces3.Length} faces)", faces3.Length == 2);
}

// ---------------------------------------------------------------------------------------------
// LAUNCH-STATION-CLASSES DOCK RULES (2026-07-21): a restricted hull (LaunchClassMask != 0) gets a
// dock carve-out ONLY at own bases of an allowed station class, and there ONLY through the base's
// LARGEST door; unrestricted hulls (mask 0) keep every legacy behaviour above bit-for-bit.
// ---------------------------------------------------------------------------------------------
{
    const ushort shipyardMask = 1 << (int)StationClassId.Shipyard; // launch-station-classes: [shipyard]

    Check(
        "dockrules: mask 0 allows any class (incl. unknown)",
        DockRules.ClassAllowed(0, (byte)StationClassId.Garrison) && DockRules.ClassAllowed(0, DockRules.UnknownStationClass)
    );
    Check(
        "dockrules: shipyard mask allows shipyard only",
        DockRules.ClassAllowed(shipyardMask, (byte)StationClassId.Shipyard)
            && !DockRules.ClassAllowed(shipyardMask, (byte)StationClassId.Garrison)
            && !DockRules.ClassAllowed(shipyardMask, DockRules.UnknownStationClass)
    );

    // Two doors on the cube base: a big one carved into the +X face, a small one into −X.
    var bigDoor = SquareDoor(new Vec3(1, 0, 0), new Vec3(-1, 0, 0), 7f);
    var smallDoor = SquareDoor(new Vec3(-1, 0, 0), new Vec3(1, 0, 0), 2f);
    var twoDoors = new[] { bigDoor, smallDoor };
    Check(
        "dockrules: largest-face pick by area (tie → lowest index, empty → -1)",
        DockRules.LargestFaceIndex(twoDoors) == 0
            && DockRules.LargestFaceIndex(new[] { smallDoor, bigDoor }) == 1
            && DockRules.LargestFaceIndex(new[] { bigDoor, bigDoor }) == 0
            && DockRules.LargestFaceIndex(System.Array.Empty<DockFace>()) == -1
    );
    Check(
        "dockrules: allowed-face is -1 unrestricted / largest restricted",
        DockRules.AllowedFace(0, 0) == -1 && DockRules.AllowedFace(shipyardMask, 0) == 0
    );

    int largest = DockRules.LargestFaceIndex(twoDoors);
    var shipyardBase = Collide.StaticBody.BaseHull(cube, default, team: 0, twoDoors, (byte)StationClassId.Shipyard, largest);
    var garrisonBase = Collide.StaticBody.BaseHull(cube, default, team: 0, twoDoors, (byte)StationClassId.Garrison, largest);

    // Restricted hull at a class-DISALLOWED own base: fully solid, even dead-centre in the big door.
    var capAtGarrison = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
    Collide.ResolveStatics(
        ref capAtGarrison,
        shipR,
        new[] { garrisonBase },
        localTeam: 0,
        shipyardMask,
        rest,
        CollisionConfig.DockFaceDepth,
        out _
    );
    Check("restricted hull: own garrison-class base is solid (bounced)", Near(capAtGarrison.Pos.X, 1.5f));

    // Restricted hull at an ALLOWED base: the LARGEST door admits it…
    var capBigDoor = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
    Collide.ResolveStatics(
        ref capBigDoor,
        shipR,
        new[] { shipyardBase },
        localTeam: 0,
        shipyardMask,
        rest,
        CollisionConfig.DockFaceDepth,
        out _
    );
    Check(
        "restricted hull: shipyard's largest door admits it (no bounce)",
        Near(capBigDoor.Pos.X, 1.4f) && Near(capBigDoor.Vel.X, -1f)
    );

    // …but the SMALL side door stays solid for it, while an unrestricted hull still glides through.
    var capSmallDoor = new ShipState { Pos = new Vec3(-1.4f, 0, 0), Vel = new Vec3(1, 0, 0) };
    Collide.ResolveStatics(
        ref capSmallDoor,
        shipR,
        new[] { shipyardBase },
        localTeam: 0,
        shipyardMask,
        rest,
        CollisionConfig.DockFaceDepth,
        out _
    );
    Check("restricted hull: side door is solid for it (bounced)", Near(capSmallDoor.Pos.X, -1.5f));
    var scoutSmallDoor = new ShipState { Pos = new Vec3(-1.4f, 0, 0), Vel = new Vec3(1, 0, 0) };
    Collide.ResolveStatics(
        ref scoutSmallDoor,
        shipR,
        new[] { shipyardBase },
        localTeam: 0,
        0,
        rest,
        CollisionConfig.DockFaceDepth,
        out _
    );
    Check("unrestricted hull: side door still admits it (no bounce)", Near(scoutSmallDoor.Pos.X, -1.4f));

    // Touches mirrors the same gating (collision-thud parity with the bounce above).
    Check(
        "touches: restricted hull thuds in the side door",
        Collide.Touches(new Vec3(-1.4f, 0, 0), shipR, new[] { shipyardBase }, 0, shipyardMask, CollisionConfig.DockFaceDepth)
    );
    Check(
        "touches: restricted hull silent in the largest door",
        !Collide.Touches(new Vec3(1.4f, 0, 0), shipR, new[] { shipyardBase }, 0, shipyardMask, CollisionConfig.DockFaceDepth)
    );

    // The onlyFace overload the filter rides on: exactly one door is tested.
    Check(
        "dock-face onlyFace: largest-only accepts a big-door point",
        Collide.IntersectsDockFace(new Vec3(1f, 0, 0), twoDoors, CollisionConfig.DockFaceDepth, shipR, 0)
    );
    Check(
        "dock-face onlyFace: largest-only rejects a small-door point",
        !Collide.IntersectsDockFace(new Vec3(-1f, 0, 0), twoDoors, CollisionConfig.DockFaceDepth, shipR, 0)
    );

    // REAL acs05.glb (the shipyard model): two authored doors — the big +X capital entrance
    // (~97x91 at world scale) and the small top door (~46x37). LargestFaceIndex must pick the
    // capital entrance, and the restricted filter must reject a point in the top door.
    string? acs05 = null;
    foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var d = new DirectoryInfo(start);
        for (int up = 0; up < 8 && d is not null; up++, d = d.Parent)
        {
            string cand = Path.Combine(d.FullName, "client", "assets", "bases", "acs05.glb");
            if (File.Exists(cand))
                acs05 = cand;
        }
    }
    if (acs05 is null)
        Console.WriteLine("  [SKIP] acs05.glb not found — skipping shipyard door-rule check");
    else
    {
        var yard = SimModel.FromGlb(File.ReadAllBytes(acs05), acs05);
        float yws = 115f * 2f / MathF.Max(1e-3f, yard.LongestAxis); // stations.yaml shipyard radius 115
        DockFace[] yardFaces = DockFaceParser.Build(yard.Hardpoints, yws);
        Check($"acs05.glb: two docking doors parsed (got {yardFaces.Length})", yardFaces.Length == 2);
        if (yardFaces.Length == 2)
        {
            int yl = DockRules.LargestFaceIndex(yardFaces);
            int ysmall = 1 - yl;
            Check(
                $"acs05.glb: largest door is the ~97x91 capital entrance (got {yardFaces[yl].Eu * 2:0}x{yardFaces[yl].Ev * 2:0})",
                yardFaces[yl].Eu * yardFaces[yl].Ev > yardFaces[ysmall].Eu * yardFaces[ysmall].Ev
                    && MathF.Min(yardFaces[yl].Eu, yardFaces[yl].Ev) * 2f > 80f
            );
            Check(
                "acs05.glb: largest-only filter accepts the capital door centre",
                Collide.IntersectsDockFace(
                    yardFaces[yl].Center,
                    yardFaces,
                    CollisionConfig.DockFaceDepth,
                    CollisionConfig.ShipRadius,
                    yl
                )
            );
            Check(
                "acs05.glb: largest-only filter rejects the top-door centre",
                !Collide.IntersectsDockFace(
                    yardFaces[ysmall].Center,
                    yardFaces,
                    CollisionConfig.DockFaceDepth,
                    CollisionConfig.ShipRadius,
                    yl
                )
            );
            Check(
                $"acs05.glb: one authored launch exit",
                yard.Hardpoints.Count(hp => hp.Name.StartsWith("HP_DockingExit", StringComparison.Ordinal)) == 1
            );
        }
    }
}

// ---------------------------------------------------------------------------------------------
// SHIP-VS-SHIP (shared Pass C kernel + the client's local-share resolver). ShipShipContact is the
// contact math the server's CollideShips and the client's prediction both call; ResolveShipsLocal
// applies only the LOCAL ship's mass-weighted share of the server's impulse + push-out.
// ---------------------------------------------------------------------------------------------
{
    // (a) Hull-less pair: legacy equal-radius sphere overlap. A at x=0.8, B at origin, shipR 0.5
    //     ⇒ minD 1.0, dist 0.8, n = +X (b → a), pen = 0.2.
    bool ssHit = Collide.ShipShipContact(
        new Vec3(0.8f, 0, 0),
        Quat.Identity,
        null,
        shipR,
        new Vec3(0, 0, 0),
        Quat.Identity,
        null,
        shipR,
        shipR,
        out var ssN,
        out var ssPen
    );
    Check("ship-ship: sphere pair contacts with n = b→a (+X)", ssHit && Near(ssN.X, 1f) && Near(ssPen, 0.2f));
    bool ssClear = !Collide.ShipShipContact(
        new Vec3(1.2f, 0, 0),
        Quat.Identity,
        null,
        shipR,
        new Vec3(0, 0, 0),
        Quat.Identity,
        null,
        shipR,
        shipR,
        out _,
        out _
    );
    Check("ship-ship: separated sphere pair reports none", ssClear);

    // (b) Hull-aware: A's center (a shipR sphere) vs B's cube hull — same +X face contact as test 1.
    bool hullHit = Collide.ShipShipContact(
        new Vec3(1.4f, 0, 0),
        Quat.Identity,
        null,
        shipR,
        new Vec3(0, 0, 0),
        Quat.Identity,
        cube,
        cube.BoundingRadius,
        shipR,
        out var hn,
        out var hp
    );
    Check("ship-ship: sphere vs other's hull (+X face)", hullHit && Near(hn.X, 1f) && Near(hp, 0.1f));

    // (c) The mirrored case: A carries the hull, B is the sphere — the normal is negated to b → a.
    bool mirHit = Collide.ShipShipContact(
        new Vec3(0, 0, 0),
        Quat.Identity,
        cube,
        cube.BoundingRadius,
        new Vec3(1.4f, 0, 0),
        Quat.Identity,
        null,
        shipR,
        shipR,
        out var mn,
        out var mp
    );
    Check("ship-ship: own hull contact negates n to b→a (−X)", mirHit && Near(mn.X, -1f) && Near(mp, 0.1f));

    // (d) ResolveShipsLocal, equal masses (1 vs 1): the local ship takes HALF the push-out and half
    //     the restitution impulse. n = +X, pen 0.2, relVn = −1 ⇒ Δv = (1+0.3)·1/2 = 0.65,
    //     Δpos = 0.2·(1/2) = 0.1.
    var others = new[] { new Collide.MovingShip(new Vec3(0, 0, 0), Quat.Identity, new Vec3(0, 0, 0), 1f, null, shipR) };
    var loc = new ShipState
    {
        Pos = new Vec3(0.8f, 0, 0),
        Vel = new Vec3(-1, 0, 0),
        Mass = 1f,
    };
    bool locHit = Collide.ResolveShipsLocal(ref loc, shipR, null, shipR, others, rest, out _);
    Check("ship-ship local: half push-out (x = 0.9)", locHit && Near(loc.Pos.X, 0.9f));
    Check("ship-ship local: half impulse (vx = −0.35)", Near(loc.Vel.X, -0.35f));

    // (e) Mass weighting: a 3× heavier other ship ⇒ iA=1, iB=1/3, invSum=4/3. The light local ship
    //     absorbs more: Δv = 1.3·1/(4/3) = 0.975 ⇒ vx = −0.025; Δpos = 0.2·(1/(4/3)) = 0.15 ⇒ 0.95.
    var heavy = new[] { new Collide.MovingShip(new Vec3(0, 0, 0), Quat.Identity, new Vec3(0, 0, 0), 3f, null, shipR) };
    var loc2 = new ShipState
    {
        Pos = new Vec3(0.8f, 0, 0),
        Vel = new Vec3(-1, 0, 0),
        Mass = 1f,
    };
    Collide.ResolveShipsLocal(ref loc2, shipR, null, shipR, heavy, rest, out _);
    Check("ship-ship local: heavy other ⇒ larger local share (x = 0.95)", Near(loc2.Pos.X, 0.95f));
    Check("ship-ship local: heavy other ⇒ vx = −0.025", Near(loc2.Vel.X, -0.025f));

    // (f) Separating contact (relVn ≥ 0): overlap still pushes out, but NO impulse (same gate as the
    //     server's ResolveShipImpulse).
    var loc3 = new ShipState
    {
        Pos = new Vec3(0.8f, 0, 0),
        Vel = new Vec3(1, 0, 0),
        Mass = 1f,
    };
    Collide.ResolveShipsLocal(ref loc3, shipR, null, shipR, others, rest, out _);
    Check("ship-ship local: separating contact pushes out, keeps velocity", Near(loc3.Pos.X, 0.9f) && Near(loc3.Vel.X, 1f));

    // (g) The other ship's velocity feeds the relative-velocity gate: local drifting +X at 1 while a
    //     faster other chases at +2 ⇒ relVn = −1 (closing) despite the local ship moving away.
    var chasing = new[] { new Collide.MovingShip(new Vec3(0, 0, 0), Quat.Identity, new Vec3(2, 0, 0), 1f, null, shipR) };
    var loc4 = new ShipState
    {
        Pos = new Vec3(0.8f, 0, 0),
        Vel = new Vec3(1, 0, 0),
        Mass = 1f,
    };
    Collide.ResolveShipsLocal(ref loc4, shipR, null, shipR, chasing, rest, out _);
    Check("ship-ship local: chased from behind ⇒ knocked forward (vx = 1.65)", Near(loc4.Vel.X, 1.65f));
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
