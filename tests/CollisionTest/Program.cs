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
        new Vec3(-1, -1, -1), new Vec3(1, -1, -1), new Vec3(-1, 1, -1), new Vec3(1, 1, -1),
        new Vec3(-1, -1, 1), new Vec3(1, -1, 1), new Vec3(-1, 1, 1), new Vec3(1, 1, 1),
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
bool any = Collide.ResolveStatics(ref st, shipR, new[] { rock }, localTeam: -1, rest, CollisionConfig.DockFaceDepth, out _);
Check("resolve-statics: asteroid bounces ship out", any && Near(st.Pos.X, 1.5f));

// 4) Own-base dock-door carve-out: a door at the +X face (inward normal −X, the way a ship enters)
//    lets the OWN team's ship through (no bounce), while an ENEMY ship still bounces off the hull.
var doors = new[] { SquareDoor(new Vec3(1, 0, 0), new Vec3(-1, 0, 0), 7f) };
var baseBody = Collide.StaticBody.BaseHull(cube, default, team: 0, doors);

var own = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.ResolveStatics(ref own, shipR, new[] { baseBody }, localTeam: 0, rest, CollisionConfig.DockFaceDepth, out _);
Check("own base: dock door lets ship through (no bounce)", Near(own.Pos.X, 1.4f) && Near(own.Vel.X, -1f));

var enemy = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.ResolveStatics(ref enemy, shipR, new[] { baseBody }, localTeam: 1, rest, CollisionConfig.DockFaceDepth, out _);
Check("enemy base: solid hull bounces ship out", Near(enemy.Pos.X, 1.5f));

// 5) Touches (audio probe) agrees with the geometry: overlapping = true, clear = false.
Check("touches: overlapping ship reports contact", Collide.Touches(new Vec3(1.4f, 0, 0), shipR, new[] { rock }, -1, CollisionConfig.DockFaceDepth));
Check("touches: clear ship reports none", !Collide.Touches(new Vec3(3f, 0, 0), shipR, new[] { rock }, -1, CollisionConfig.DockFaceDepth));

// 6) Rock tumble: the spin is deterministic (server/client parity) and the live rotation actually
//    moves the collision surface, so the hull tracks the rendered rock instead of staying frozen.
var (axis, speed) = Collide.RockSpin(0xABCDEF12);
var (axisB, speedB) = Collide.RockSpin(0xABCDEF12);
Check("rockspin: deterministic for an id", Near(axis.X, axisB.X) && Near(axis.Y, axisB.Y) && Near(axis.Z, axisB.Z) && Near(speed, speedB));
Check("rockspin: unit axis", Near(axis.Length(), 1f));
Check("rockspin: rate in 0.03..0.15 band", speed >= 0.03f && speed <= 0.15f);

var spawn = Collide.RockRotation(0.3f, 0.7f, 1.1f);
var probe = new Vec3(1, 2, 3);
var at0 = Collide.RockRotationAt(spawn, axis, speed, 0f);
Check("rockrotationat: t=0 is the spawn pose", Near(at0.Rotate(probe).X, spawn.Rotate(probe).X) && Near(at0.Rotate(probe).Y, spawn.Rotate(probe).Y));
var at5 = Collide.RockRotationAt(spawn, axis, speed, 5f);
Check("rockrotationat: advances with time", !Near(at5.Rotate(probe).X, spawn.Rotate(probe).X) || !Near(at5.Rotate(probe).Y, spawn.Rotate(probe).Y));

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
            new Vec3(cx - 1, -1, -1), new Vec3(cx + 1, -1, -1), new Vec3(cx - 1, 1, -1), new Vec3(cx + 1, 1, -1),
            new Vec3(cx - 1, -1, 1), new Vec3(cx + 1, -1, 1), new Vec3(cx - 1, 1, 1), new Vec3(cx + 1, 1, 1),
        }
    );
var cubeA = CubeAt(-3f);
var cubeB = CubeAt(3f);
// Merged shrink-wrap spans the whole x ∈ [-4,4] slab (its concavity is exactly the gap).
var mergedAB = ConvexHull.Build(
    new[]
    {
        new Vec3(-4, -1, -1), new Vec3(-2, -1, -1), new Vec3(-4, 1, -1), new Vec3(-2, 1, -1),
        new Vec3(-4, -1, 1), new Vec3(-2, -1, 1), new Vec3(-4, 1, 1), new Vec3(-2, 1, 1),
        new Vec3(2, -1, -1), new Vec3(4, -1, -1), new Vec3(2, 1, -1), new Vec3(4, 1, -1),
        new Vec3(2, -1, 1), new Vec3(4, -1, 1), new Vec3(2, 1, 1), new Vec3(4, 1, 1),
    }
);
var compound = Collide.StaticBody.BaseHull(mergedAB, new[] { cubeA, cubeB }, center: default, team: 0, faces: System.Array.Empty<DockFace>());

// Sphere contacting cube A (just off A's -X face at x=-4.4) resolves with A's normal (−X), not B's.
bool hitA = Collide.SphereVsBody(new Vec3(-4.4f, 0, 0), shipR, compound, out var na, out var pa);
Check("compound: contacts cube A with A's −X normal", hitA && Near(na.X, -1f) && Near(na.Y, 0f) && Near(na.Z, 0f) && Near(pa, 0.1f));

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
        new Vec3(-2.2f, -1, -1), new Vec3(-0.2f, -1, -1), new Vec3(-2.2f, 1, -1), new Vec3(-0.2f, 1, -1),
        new Vec3(-2.2f, -1, 1), new Vec3(-0.2f, -1, 1), new Vec3(-2.2f, 1, 1), new Vec3(-0.2f, 1, 1),
        new Vec3(0.6f, -1, -1), new Vec3(2.6f, -1, -1), new Vec3(0.6f, 1, -1), new Vec3(2.6f, 1, -1),
        new Vec3(0.6f, -1, 1), new Vec3(2.6f, -1, 1), new Vec3(0.6f, 1, 1), new Vec3(2.6f, 1, 1),
    }
);
var compound2 = Collide.StaticBody.BaseHull(mergedBig, new[] { bigCubeA, bigCubeB }, default, 0, System.Array.Empty<DockFace>());
// Sphere radius 0.8 at x=0: overlaps A' (right face x=−0.2, sphere reaches x=−0.8 ⇒ pen 0.8−0.2=0.6)
// and B' (left face x=0.6, sphere reaches x=0.8 ⇒ pen 0.8−0.6=0.2). Deeper = A' ⇒ normal +X (out of A').
bool bothHit = Collide.SphereVsBody(new Vec3(0, 0, 0), 0.8f, compound2, out var nBoth, out var pBoth);
Check("compound: deepest contact wins (cube A' over B')", bothHit && Near(nBoth.X, 1f) && Near(pBoth, 0.6f));

// Dock-door skip bypasses the WHOLE compound body (all sub-hulls), same as the single-hull carve-out.
// Door at cube A's −X face, inward normal +X (the way a docker at x=−4.4 moving +X would enter).
var discBody = Collide.StaticBody.BaseHull(mergedAB, new[] { cubeA, cubeB }, default, team: 0, faces: new[] { SquareDoor(new Vec3(-4, 0, 0), new Vec3(1, 0, 0), 7f) });
var docker = new ShipState { Pos = new Vec3(-4.4f, 0, 0), Vel = new Vec3(1, 0, 0) };
Collide.ResolveStatics(ref docker, shipR, new[] { discBody }, localTeam: 0, rest, CollisionConfig.DockFaceDepth, out _);
Check("compound: own dock door skips the whole body (no bounce)", Near(docker.Pos.X, -4.4f) && Near(docker.Vel.X, 1f));
Check("compound: dock door still bypasses when Touches probes", !Collide.Touches(new Vec3(-4.4f, 0, 0), shipR, new[] { discBody }, localTeam: 0, CollisionConfig.DockFaceDepth));

// Regression guard: a SINGLE-hull StaticBody routed through SphereVsBody === the old SphereVsHull.
Collide.SphereVsHull(new Vec3(1.4f, 0, 0), shipR, cube, default, Quat.Identity, 1f, out var nRef, out var pRef);
var single = Collide.StaticBody.BaseHull(cube, default, team: 0, System.Array.Empty<DockFace>());
bool sHit = Collide.SphereVsBody(new Vec3(1.4f, 0, 0), shipR, single, out var nS, out var pS);
Check("single-hull via SphereVsBody === SphereVsHull", sHit && Near(nS.X, nRef.X) && Near(nS.Y, nRef.Y) && Near(nS.Z, nRef.Z) && Near(pS, pRef));

// ---------------------------------------------------------------------------------------------
// REAL BAKED base.glb: SimModel.FromGlb must expose the generated sub-hulls (tools/collision-hull --kind base:
// a voxel solid-fill greedy-merged into ~90 boxes) with the merged metrics UNCHANGED — the boxes are
// each strictly interior to the visual convex hull, so they never enlarge the merged hull.
// Locate the repo GLB by probing up from the test binary (mirrors server/Assets/SimAssets.cs).
// ---------------------------------------------------------------------------------------------
string? FindBaseGlb()
{
    foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var d = new DirectoryInfo(start);
        for (int up = 0; up < 8 && d is not null; up++, d = d.Parent)
        {
            string cand = Path.Combine(d.FullName, "client", "assets", "bases", "base.glb");
            if (File.Exists(cand))
                return cand;
        }
    }
    return null;
}

string? glbPath = FindBaseGlb();
if (glbPath is null)
{
    Console.WriteLine("  [SKIP] base.glb not found — skipping baked compound-hull check");
}
else
{
    var baseModel = SimModel.FromGlb(File.ReadAllBytes(glbPath), glbPath);
    Check($"baked base: generated sub-hulls (COL_ parts) present (got {baseModel.Hulls.Count}, expect 8..512)", baseModel.Hulls.Count is >= 8 and <= 512);
    Check("baked base: merged LongestAxis unchanged (~32.2436)", Near(baseModel.LongestAxis, 32.243610f, 1e-2f));
    Check("baked base: merged BoundingRadius unchanged (~16.5435)", Near(baseModel.BoundingRadius, 16.543488f, 1e-2f));
    Check("baked base: merged Hull has 172 planes", baseModel.Hull.Planes.Length == 172);
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
    // GROUPED DOCKING DOORS on the real base.glb: HP_DockingEntrance_0..4 form ONE rectangular door
    // (1 face marker + 4 boundary markers). The regression this rework fixes: the exit tube (at a Z
    // OUTSIDE the door's Z range) must NOT trigger a dock, while the door centre must.
    // ---------------------------------------------------------------------------------------------
    float ws = CollisionConfig.BaseRadius * 2f / MathF.Max(1e-3f, baseModel.LongestAxis); // ≈ 5.582
    DockFace[] baseFaces = DockFaceParser.Build(baseModel.Hardpoints, ws);
    Check($"base.glb: exactly one docking door parsed (got {baseFaces.Length})", baseFaces.Length == 1);
    if (baseFaces.Length == 1)
    {
        DockFace door = baseFaces[0];
        const float shipR3 = CollisionConfig.ShipRadius; // 3
        const float depth = CollisionConfig.DockFaceDepth; // 9

        // (a) A point at the OLD exit-blob spot (local (0,−9.5,−4.6), inside the exit tube, world-
        //     scaled) must NOT intersect the door — this is the whole point of the rework.
        Vec3 exitBlob = new Vec3(0f, -9.5f, -4.6f) * ws;
        Check("base.glb door: exit-tube point does NOT dock", !Collide.IntersectsDockFace(exitBlob, baseFaces, depth, shipR3));

        // (b) The door face centre DOES dock.
        Check("base.glb door: door-centre point DOES dock", Collide.IntersectsDockFace(door.Center, baseFaces, depth, shipR3));

        // (c) Just past a rectangle edge (half-extent + shipRadius + margin along U) does NOT dock.
        Vec3 pastEdgeU = door.Center + door.U * (door.Eu + shipR3 + 1f);
        Check("base.glb door: point past the U edge does NOT dock", !Collide.IntersectsDockFace(pastEdgeU, baseFaces, depth, shipR3));
        Vec3 pastEdgeV = door.Center + door.V * (door.Ev + shipR3 + 1f);
        Check("base.glb door: point past the V edge does NOT dock", !Collide.IntersectsDockFace(pastEdgeV, baseFaces, depth, shipR3));

        // Sanity on the derived rectangle: base.glb's door is ~7.7 × 4.4 local ⇒ half-extents
        // {3.85, 2.21} × ws in world units (order-agnostic — check the sorted pair).
        float loU = MathF.Min(door.Eu, door.Ev) / ws;
        float hiU = MathF.Max(door.Eu, door.Ev) / ws;
        Check($"base.glb door: half-extents ≈ {{3.85, 2.21}} local (got {{{hiU:0.##}, {loU:0.##}}})", Near(hiU, 3.85f, 0.1f) && Near(loU, 2.21f, 0.1f));
        Check("base.glb door: inward normal is +Y (bay on the base bottom)", Near(door.Normal.Y, 1f, 1e-2f));
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
        Check("multi-door A: centre at origin, inward normal +Z", Near(a.Center.X, 0) && Near(a.Center.Y, 0) && Near(a.Center.Z, 0) && Near(a.Normal.Z, 1f, 1e-3f));
        Check("multi-door A: half-extents {4,3} (order-agnostic)", Near(MathF.Max(a.Eu, a.Ev), 4f, 1e-3f) && Near(MathF.Min(a.Eu, a.Ev), 3f, 1e-3f));
        Check("multi-door B: centre at (100,0,0), inward normal +X", Near(b.Center.X, 100f) && Near(b.Normal.X, 1f, 1e-3f));
        Check("multi-door B: half-extents {5,2} (order-agnostic)", Near(MathF.Max(b.Eu, b.Ev), 5f, 1e-3f) && Near(MathF.Min(b.Eu, b.Ev), 2f, 1e-3f));

        const float shipR3 = CollisionConfig.ShipRadius;
        const float depth = CollisionConfig.DockFaceDepth;
        // A ship at door A's centre docks; a ship out at (50,0,0) — between the doors — docks at neither.
        Check("multi-door: ship at door A centre docks", Collide.IntersectsDockFace(new Vec3(0, 0, 0), faces2, depth, shipR3));
        Check("multi-door: ship at door B centre docks", Collide.IntersectsDockFace(new Vec3(100, 0, 0), faces2, depth, shipR3));
        Check("multi-door: ship midway between doors docks at neither", !Collide.IntersectsDockFace(new Vec3(50, 0, 0), faces2, depth, shipR3));
    }

    // Leftover-marker fallback: 6 entrance markers (not a multiple of 5) ⇒ one door + one legacy disc.
    var hps2 = new List<(string Name, Vec3 Pos, Vec3 Forward)>(hps.GetRange(0, 5))
    {
        ("HP_DockingEntrance_5", new Vec3(0, 50, 0), new Vec3(0, 1, 0)),
    };
    DockFace[] faces3 = DockFaceParser.Build(hps2, 1f);
    Check($"multi-door: 6 markers ⇒ 1 door + 1 legacy disc (got {faces3.Length} faces)", faces3.Length == 2);
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
