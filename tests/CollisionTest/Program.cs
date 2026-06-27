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
bool any = Collide.ResolveStatics(ref st, shipR, new[] { rock }, localTeam: -1, rest, CollisionConfig.DockDiscRadius, out _);
Check("resolve-statics: asteroid bounces ship out", any && Near(st.Pos.X, 1.5f));

// 4) Own-base dock-disc carve-out: a disc at the +X face lets the OWN team's ship through (no
//    bounce), while an ENEMY ship still bounces off the same hull.
var discs = new[] { (Pos: new Vec3(1, 0, 0), Normal: new Vec3(1, 0, 0)) };
var baseBody = Collide.StaticBody.BaseHull(cube, default, team: 0, discs);

var own = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.ResolveStatics(ref own, shipR, new[] { baseBody }, localTeam: 0, rest, CollisionConfig.DockDiscRadius, out _);
Check("own base: dock disc lets ship through (no bounce)", Near(own.Pos.X, 1.4f) && Near(own.Vel.X, -1f));

var enemy = new ShipState { Pos = new Vec3(1.4f, 0, 0), Vel = new Vec3(-1, 0, 0) };
Collide.ResolveStatics(ref enemy, shipR, new[] { baseBody }, localTeam: 1, rest, CollisionConfig.DockDiscRadius, out _);
Check("enemy base: solid hull bounces ship out", Near(enemy.Pos.X, 1.5f));

// 5) Touches (audio probe) agrees with the geometry: overlapping = true, clear = false.
Check("touches: overlapping ship reports contact", Collide.Touches(new Vec3(1.4f, 0, 0), shipR, new[] { rock }, -1, CollisionConfig.DockDiscRadius));
Check("touches: clear ship reports none", !Collide.Touches(new Vec3(3f, 0, 0), shipR, new[] { rock }, -1, CollisionConfig.DockDiscRadius));

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
