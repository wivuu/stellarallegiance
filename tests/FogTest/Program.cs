// Fog-of-war vision tests (plan WP2 / tests/FogTest). Console PASS/FAIL in the repo's test idiom
// (mirrors MineTest/MissileTest): exits non-zero on any failure so CI / a manual run can gate.
//
// Boots the real Simulation from the live content bundle (server/content/factions, copied next to the
// test binary — same seam as MineTest) with PIGs off and drives it tick-by-tick with Step(). Fog is
// forced ON and VisionSynchronous=true so the 2 Hz vision pass computes inline at each boundary and
// the applied timeline is fully deterministic. All ships park in the sentinel empty sector 999 (no
// rocks, no boundary) so geometry is exact; the occlusion + base-vision scenarios use a hand-placed
// rock / a real base sector.
//
// Detection ranges are all scaled by the TARGET's radar signature; the test reads the authored
// numbers from the loaded ContentSet (scout/fighter/bomber vision + signatures, base sphere/signature,
// eyeball multiplier) so retuning the YAML never breaks the assertions — boundaries are computed from
// the defs, and behavior is asserted at ×sig ± epsilon.

using System.Text;
using SimServer.Content;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared;

int failures = 0;
void Check(bool cond, string pass, string fail)
{
    if (cond)
        Console.WriteLine($"PASS: {pass}");
    else
    {
        Console.WriteLine($"FAIL: {fail}");
        failures++;
    }
}

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "factions", "core.manifest.yaml");
const uint EmptySector = 999;
const int Settle = 30; // ticks to hold a configuration so the 2 Hz apply reflects it (>2 boundaries)

Simulation BootSim(ulong seed, bool sync = true)
{
    var content = ContentLoader.Load(stockPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.FogEnabled = true;
    sim.VisionSynchronous = sync;
    sim.StartMatch();
    return sim;
}

ShipClassDef Def(Simulation sim, byte cls) => sim.Content.Ships.First(d => d.ClassId == cls);

Simulation.ShipSim Join(Simulation sim, int clientId, byte team, byte cls)
{
    sim.EnqueueJoin(clientId, team, cls);
    sim.Step();
    return sim.Ships.First(s => s.OwnerClientId == clientId);
}

void Park(Simulation.ShipSim s, uint sector, Vec3 pos)
{
    s.SectorId = sector;
    s.State.Pos = pos;
    s.State.Vel = new Vec3(0f, 0f, 0f);
    s.State.Rot = Quat.Identity; // forward = +Z
    s.State.AngVel = new Vec3(0f, 0f, 0f);
}

// Hold a configuration for `ticks` steps, re-applying `hold` each tick so parked ships don't drift,
// and return every (team, shipId) lost-contact transition observed across the window.
HashSet<(byte, ulong)> Run(Simulation sim, Action hold, int ticks)
{
    var lost = new HashSet<(byte, ulong)>();
    for (int i = 0; i < ticks; i++)
    {
        hold();
        sim.Step();
        foreach (var l in sim.LostContactsThisStep)
            lost.Add(l);
    }
    return lost;
}

Simulation.TeamVision Vision(Simulation sim, byte team) => sim.VisionFor(team)!;

// Parse the (base, rock, aleph) static counts out of a MsgWelcome frame, asserting count == body.
(int s, int b, int r, int a) WelcomeCounts(byte[] frame)
{
    using var ms = new MemoryStream(frame);
    using var br = new BinaryReader(ms);
    br.ReadByte(); br.ReadByte(); br.ReadInt32(); br.ReadByte(); br.ReadUInt32(); br.ReadSingle();
    int tl = br.ReadByte(); br.ReadBytes(tl);
    int ns = br.ReadUInt16();
    for (int i = 0; i < ns; i++) { br.ReadUInt32(); br.ReadSingle(); }
    int nb = br.ReadUInt16(); br.ReadBytes(nb * 33);
    long nr = br.ReadUInt32(); br.ReadBytes((int)nr * 41);
    int na = br.ReadUInt16(); br.ReadBytes(na * 28);
    if (ms.Position != frame.Length) throw new Exception("Welcome count != body");
    return (ns, nb, (int)nr, na);
}

// A point at `dist` along a viewer's forward (+Z), tilted `angleDeg` off-axis in the XZ plane.
Vec3 AtAngle(float dist, float angleDeg)
{
    float r = angleDeg * MathF.PI / 180f;
    return new Vec3(dist * MathF.Sin(r), 0f, dist * MathF.Cos(r));
}

// ================================================================================================
// 1. Cone — length + angle edges (scout viewer facing +Z; fighter target, sig 1.0, beyond the
//    sphere so ONLY the cone can detect it).
// ================================================================================================
{
    var sim = BootSim(1);
    var scout = Def(sim, FlightModel.ClassScout);
    float coneLen = scout.VisionConeLength;   // 2400
    float coneAng = scout.VisionConeAngleDeg; // 30
    float sphere = scout.VisionSphereRadius;  // 900

    var viewer = Join(sim, 1, 0, FlightModel.ClassScout);
    var target = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sig = Def(sim, FlightModel.ClassFighter).RadarSignature; // 1.0
    float beyondSphere = sphere / sig + 200f; // outside the omni sphere so the cone is the only sensor

    bool InCone(float dist, float ang)
    {
        Park(viewer, EmptySector, new Vec3(0, 0, 0));
        Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, AtAngle(dist, ang)); }, Settle);
        return Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId);
    }

    Check(InCone(beyondSphere, coneAng - 5f), $"target on-axis within cone length ({beyondSphere:F0} ≤ {coneLen}×{sig}) and inside the half-angle is detected", "cone failed to detect an in-length, in-angle target");
    Check(!InCone(coneLen * sig + 300f, 0f), $"target past cone length ({coneLen}×{sig}) on-axis is NOT detected", "cone detected a target beyond its length");
    Check(InCone(beyondSphere, coneAng - 3f), $"target just inside the {coneAng}° half-angle is detected", "cone missed a target just inside the half-angle");
    Check(!InCone(beyondSphere, coneAng + 5f), $"target just outside the {coneAng}° half-angle is NOT detected", "cone detected a target outside its half-angle");
}

// ================================================================================================
// 2. Sphere — omnidirectional + unoccluded (a rock directly between still leaves an in-sphere target
//    detected; a target behind the viewer, well inside the sphere, is still seen).
// ================================================================================================
{
    var sim = BootSim(2);
    var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);
    var target = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius; // 450
    float sig = Def(sim, FlightModel.ClassFighter).RadarSignature;        // 1.0

    // Behind the viewer (−Z), well inside the sphere → omnidirectional detection.
    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, new Vec3(0, 0, -sphere * 0.4f)); }, Settle);
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId), "the proximity sphere is omnidirectional (target behind the viewer is detected)", "sphere missed a target behind the viewer");

    // A rock straddling the line of sight does NOT block the (unoccluded) sphere.
    var sim2 = BootSim(22);
    sim2.World.AddRockForTest(EmptySector, new Vec3(0, 0, 200f), 120f);
    var v2 = Join(sim2, 1, 0, FlightModel.ClassFighter);
    var t2 = Join(sim2, 2, 1, FlightModel.ClassFighter);
    Run(sim2, () => { Park(v2, EmptySector, new Vec3(0, 0, 0)); Park(t2, EmptySector, new Vec3(0, 0, sphere * 0.9f)); }, Settle);
    Check(Vision(sim2, 0).VisibleEnemyShips.Contains(t2.ShipId), "the sphere is unoccluded (a rock between viewer and target still yields detection inside the sphere)", "a rock occluded the omnidirectional sphere");
}

// ================================================================================================
// 3. Cone occlusion — a rock on the line of sight blocks the cone; the same target with the rock
//    moved off-axis is detected (target beyond the sphere so only the cone applies).
// ================================================================================================
{
    float coneLen, sphere, sig;
    {
        var probe = BootSim(3);
        coneLen = Def(probe, FlightModel.ClassScout).VisionConeLength;
        sphere = Def(probe, FlightModel.ClassScout).VisionSphereRadius;
        sig = Def(probe, FlightModel.ClassFighter).RadarSignature;
    }
    float targetDist = sphere / sig + 600f; // outside the sphere, inside the cone

    bool DetectedWithRock(Vec3 rockPos)
    {
        var sim = BootSim(30);
        sim.World.AddRockForTest(EmptySector, rockPos, 250f);
        var viewer = Join(sim, 1, 0, FlightModel.ClassScout);
        var target = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, new Vec3(0, 0, targetDist)); }, Settle);
        return Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId);
    }

    Check(!DetectedWithRock(new Vec3(0, 0, targetDist * 0.5f)), "a rock on the line of sight occludes the cone (target NOT detected)", "cone saw through an occluding rock");
    Check(DetectedWithRock(new Vec3(700, 0, targetDist * 0.5f)), "the same target with the rock moved off-axis is detected (clear LoS)", "cone missed a target with an unobstructed line of sight");
}

// ================================================================================================
// 4. Radar signature — exact ×sig sphere boundary. A fighter viewer's sphere against a bomber
//    (sig 1.75) vs a scout (sig 0.5) at the same spot; target placed perpendicular to forward so the
//    cone never fires and the sphere×sig boundary is isolated.
// ================================================================================================
{
    var sim = BootSim(4);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius; // 450
    float bomberSig = Def(sim, FlightModel.ClassBomber).RadarSignature;   // 1.75
    float scoutSig = Def(sim, FlightModel.ClassScout).RadarSignature;     // 0.5

    var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);

    bool DetectedAt(byte targetCls, float dist)
    {
        var s = BootSim(4);
        var v = Join(s, 1, 0, FlightModel.ClassFighter);
        var t = Join(s, 2, 1, targetCls);
        Run(s, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(dist, 0, 0)); }, Settle); // +X = perpendicular to forward
        return Vision(s, 0).VisibleEnemyShips.Contains(t.ShipId);
    }

    float midRange = 400f; // between scout boundary (450×0.5=225) and bomber boundary (450×1.75=787.5)
    Check(DetectedAt(FlightModel.ClassBomber, midRange), $"a bomber (sig {bomberSig}) is detected at {midRange} (≤ {sphere}×{bomberSig})", "bomber not detected inside its signature-scaled sphere");
    Check(!DetectedAt(FlightModel.ClassScout, midRange), $"a scout (sig {scoutSig}) at the SAME spot is NOT detected ({midRange} > {sphere}×{scoutSig})", "scout detected beyond its signature-scaled sphere");

    // Exact boundary: scout is seen just inside 450×0.5 and unseen just outside.
    float scoutBoundary = sphere * scoutSig; // 225
    Check(DetectedAt(FlightModel.ClassScout, scoutBoundary - 2f), $"scout just inside the ×sig boundary ({scoutBoundary:F0}) is detected", "scout not detected just inside the ×sig boundary");
    Check(!DetectedAt(FlightModel.ClassScout, scoutBoundary + 2f), $"scout just outside the ×sig boundary ({scoutBoundary:F0}) is NOT detected", "scout detected just outside the ×sig boundary");
}

// ================================================================================================
// 5. Eyeball band — an enemy between sphere×sig and sphere×eyeMult×sig is streamed (EyeballShips)
//    but NOT radar-detected. Leaving after radar contact ghosts; an eyeball-only glimpse leaves none.
// ================================================================================================
{
    float sphere, eyeMult, sig;
    {
        var probe = BootSim(5);
        sphere = Def(probe, FlightModel.ClassFighter).VisionSphereRadius;
        sig = Def(probe, FlightModel.ClassFighter).RadarSignature;
        eyeMult = probe.Content.World.FogEyeballMultiplier;
    }
    float bandDist = (sphere * sig + sphere * eyeMult * sig) * 0.5f; // mid eyeball band, +X (out of cone)
    float farDist = sphere * eyeMult * sig + 2000f;

    // Eyeball-only classification.
    {
        var sim = BootSim(5);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(bandDist, 0, 0)); }, Settle);
        var tv = Vision(sim, 0);
        Check(tv.EyeballShips.Contains(t.ShipId) && !tv.VisibleEnemyShips.Contains(t.ShipId),
            $"an enemy in the eyeball band ({bandDist:F0}, between {sphere * sig:F0} and {sphere * eyeMult * sig:F0}) is streamed but NOT radar-detected",
            "eyeball-band enemy was misclassified (radar vs eyeball)");
    }

    // Radar → the VIEWER flies away (so the last-seen spot is no longer in vision) → ghost + lost.
    {
        var sim = BootSim(55);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Vec3 spot = new Vec3(sphere * sig * 0.5f, 0, 0);
        Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, spot); }, Settle);
        Check(Vision(sim, 0).VisibleEnemyShips.Contains(t.ShipId), "enemy inside radar range is radar-detected (pre-condition for a ghost)", "enemy not radar-detected before leaving");
        var lost = Run(sim, () => { Park(v, EmptySector, new Vec3(60000f, 0, 0)); Park(t, EmptySector, spot); }, Settle);
        var tv = Vision(sim, 0);
        bool ghosted = tv.Ghosts.TryGetValue(t.ShipId, out var g);
        Check(ghosted && !tv.VisibleEnemyShips.Contains(t.ShipId) && !tv.EyeballShips.Contains(t.ShipId), "a ship leaving the streamed union after radar contact becomes a ghost", "radar-then-lost ship did not ghost");
        Check(ghosted && (g.Pos - spot).Length() < 60f, $"the ghost sits at the last streamed position (~{spot.X:F0})", "the ghost was placed away from the last streamed position");
        Check(lost.Contains(((byte)0, t.ShipId)), "leaving the streamed union emits a LostContactsThisStep entry", "no lost-contact was recorded on leaving the streamed union");
    }

    // Eyeball-only (never radar) → leave → NO ghost (but still a lost-contact).
    {
        var sim = BootSim(555);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(bandDist, 0, 0)); }, Settle);
        Check(Vision(sim, 0).EyeballShips.Contains(t.ShipId) && !Vision(sim, 0).VisibleEnemyShips.Contains(t.ShipId), "enemy held in the eyeball band never gains radar (pre-condition)", "eyeball-only enemy unexpectedly radar-detected");
        Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(farDist, 0, 0)); }, Settle);
        var tv = Vision(sim, 0);
        Check(!tv.Ghosts.ContainsKey(t.ShipId), "an eyeball-only glimpse (never radar) leaves NO ghost on loss", "an eyeball-only contact left a ghost");
    }
}

// ================================================================================================
// 6. Base vision sphere — a garrison base detects an enemy with no ship viewer; zeroing base health
//    stops the base from seeing.
// ================================================================================================
{
    var sim = BootSim(6);
    var baseSite = sim.World.Bases[0];      // team 0 base in sector 0
    float baseSphere = sim.Content.Bases[0].VisionSphereRadius; // 1500
    float sig = Def(sim, FlightModel.ClassFighter).RadarSignature;

    // Enemy (team 1) parked near the team-0 base, well inside baseSphere×sig. No team-0 ship exists.
    var enemy = Join(sim, 2, 1, FlightModel.ClassFighter);
    Vec3 nearBase = baseSite.Pos + new Vec3(baseSphere * sig * 0.5f, 0, 0);
    Run(sim, () => Park(enemy, baseSite.SectorId, nearBase), Settle);
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(enemy.ShipId), "a garrison base detects an enemy in its vision sphere with no ship viewer present", "base vision sphere failed to detect a nearby enemy");

    // Destroy the base (directly zero its health — does NOT end the match, that only fires via
    // ApplyBaseDamage) → it stops contributing vision.
    sim.World.BaseHealth[0] = 0f;
    Run(sim, () => Park(enemy, baseSite.SectorId, nearBase), Settle);
    Check(!Vision(sim, 0).VisibleEnemyShips.Contains(enemy.ShipId), "a destroyed base (health 0) stops seeing", "a destroyed base still provided vision");
}

// ================================================================================================
// 7. Statics discovery persistence — a scout discovers a rock, then flies far away; the rock stays
//    discovered (fog memory is sticky) and a reveal was queued.
// ================================================================================================
{
    var sim = BootSim(7);
    var rock = sim.World.AddRockForTest(EmptySector, new Vec3(0, 0, 300f), 60f);
    var scout = Join(sim, 1, 0, FlightModel.ClassScout);
    Run(sim, () => Park(scout, EmptySector, new Vec3(0, 0, 0)), Settle);
    var tv = Vision(sim, 0);
    Check(tv.DiscoveredRocks.Contains(rock.Id), "a scouted rock is discovered", "scout failed to discover a nearby rock");
    Check(tv.RevealLogRocks.Contains(rock.Id), "the newly-discovered rock is appended to the (append-only) reveal log", "the new discovery was not logged for reveal");

    Run(sim, () => Park(scout, EmptySector, new Vec3(50000f, 0, 0)), Settle);
    Check(Vision(sim, 0).DiscoveredRocks.Contains(rock.Id), "the rock stays discovered after the scout leaves (persistent fog memory)", "a discovered rock was forgotten after the scout left");
}

// ================================================================================================
// 8. Stale base memory — discover the enemy base, leave, damage it unseen (LastKnownBaseHealth
//    unchanged), re-scout (refreshes to the true, lower value).
// ================================================================================================
{
    var sim = BootSim(8);
    var enemyBase = sim.World.Bases[1]; // team 1 base in sector 1
    ulong baseId = enemyBase.Id;
    var scout = Join(sim, 1, 0, FlightModel.ClassScout);
    Vec3 nearEnemyBase = enemyBase.Pos + new Vec3(300f, 0, 0);

    Run(sim, () => Park(scout, enemyBase.SectorId, nearEnemyBase), Settle);
    var tv = Vision(sim, 0);
    Check(tv.DiscoveredBases.Contains(baseId), "team 0 scouts and discovers the enemy base", "enemy base not discovered by a scout in its sector");
    float remembered = tv.LastKnownBaseHealth.GetValueOrDefault(baseId, -1f);
    Check(remembered > 0f, $"LastKnownBaseHealth is recorded while in vision ({remembered:F0})", "no remembered base health while the base was in vision");

    // Fly away, then damage the base while it is unseen.
    int baseIdx = 1;
    Run(sim, () => Park(scout, EmptySector, new Vec3(0, 0, 0)), Settle);
    sim.World.BaseHealth[baseIdx] = remembered * 0.5f;
    Run(sim, () => Park(scout, EmptySector, new Vec3(0, 0, 0)), Settle);
    Check(Math.Abs(Vision(sim, 0).LastKnownBaseHealth[baseId] - remembered) < 1e-3f, "LastKnownBaseHealth is UNCHANGED while the base is damaged out of vision (stale memory)", "remembered base health changed while the base was unseen");

    // Re-scout → refreshes to the true, lower value.
    Run(sim, () => Park(scout, enemyBase.SectorId, nearEnemyBase), Settle);
    Check(Math.Abs(Vision(sim, 0).LastKnownBaseHealth[baseId] - remembered * 0.5f) < 1f, "re-scouting refreshes LastKnownBaseHealth to the true current value", "re-scout did not refresh the remembered base health");
}

// ================================================================================================
// 9. Ghost lifecycle — create on loss, clear on area re-scout (ship absent), replace on re-spot
//    elsewhere (exactly one contact), dies-unseen persists until re-scout.
// ================================================================================================
{
    float sphere, sig;
    {
        var probe = BootSim(9);
        sphere = Def(probe, FlightModel.ClassFighter).VisionSphereRadius;
        sig = Def(probe, FlightModel.ClassFighter).RadarSignature;
    }
    float radarDist = sphere * sig * 0.5f;
    Vec3 origin = new Vec3(0, 0, 0);
    Vec3 spotS = new Vec3(radarDist, 0, 0);   // where the target is first spotted
    Vec3 viewerFar = new Vec3(60000f, 0, 0);  // viewer flees so the last-seen spot leaves its vision

    // create → clear-on-rescout-empty. Establish radar, viewer flees (ghost persists), then the viewer
    // returns to the ghost spot with the target now moved far away → the empty re-scout clears it.
    {
        var sim = BootSim(9);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotS); }, Settle); // radar
        Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotS); }, Settle); // viewer flees → ghost at spotS
        Check(Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "ghost created on loss of a radar contact", "no ghost created on contact loss");
        // Viewer returns to the ghost spot, target has moved far away → empty re-scout clears the ghost.
        Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, new Vec3(90000f, 0, 0)); }, Settle);
        Check(!Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "the ghost is cleared when the team re-scouts its location empty", "a re-scouted-empty ghost was not cleared");
    }

    // replace-on-respot-elsewhere = exactly one contact (radar, no ghost).
    {
        var sim = BootSim(99);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotS); }, Settle);
        Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotS); }, Settle); // ghost at spotS
        Check(Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "ghost exists before re-spotting (pre-condition)", "no ghost before re-spot");
        // Re-spot the SAME ship at a fresh, far location — radar re-detection removes the old ghost.
        Run(sim, () => { Park(v, EmptySector, new Vec3(40000f, 0, 0)); Park(t, EmptySector, new Vec3(40000f + radarDist, 0, 0)); }, Settle);
        var tv = Vision(sim, 0);
        Check(tv.VisibleEnemyShips.Contains(t.ShipId) && !tv.Ghosts.ContainsKey(t.ShipId), "re-spotting the ship elsewhere replaces the ghost with a live radar contact (exactly one contact)", "re-spot left both a ghost and a live contact");
    }

    // dies-unseen persists (a ghosted ship destroyed while unseen keeps its ghost until re-scout).
    {
        var sim = BootSim(999);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotS); }, Settle);
        Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotS); }, Settle); // ghost at spotS
        Check(Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "ghost exists before an unseen death (pre-condition)", "no ghost before unseen death");
        ulong deadId = t.ShipId;
        t.Health = -1f; // kill it while the viewer is far away (unseen death)
        Run(sim, () => Park(v, EmptySector, viewerFar), Settle);
        Check(Vision(sim, 0).Ghosts.ContainsKey(deadId), "a ship destroyed while unseen keeps its (wrong-memory) ghost", "an unseen death cleared the ghost");
    }

    // witnessed death — a ship destroyed WHILE radar-visible leaves no ghost.
    {
        var sim = BootSim(9009);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(radarDist, 0, 0)); }, Settle);
        Check(Vision(sim, 0).VisibleEnemyShips.Contains(t.ShipId), "target radar-visible before its death (pre-condition)", "target not radar-visible before death");
        ulong deadId = t.ShipId;
        t.Health = -1f; // dies while still radar-visible
        Run(sim, () => Park(v, EmptySector, new Vec3(0, 0, 0)), Settle);
        Check(!Vision(sim, 0).Ghosts.ContainsKey(deadId), "a witnessed death (radar-visible when it died) leaves NO ghost", "a witnessed death produced a ghost");
    }
}

// ================================================================================================
// 10. Determinism — two sync sims on the same seed+script produce bit-identical vision timelines,
//     and an async run (worker thread) produces the SAME timeline (fixed-boundary apply is
//     worker-speed independent).
// ================================================================================================
{
    string Sig(Simulation sim)
    {
        var sb = new StringBuilder();
        foreach (var team in sim.TeamVisions.Keys.OrderBy(x => x))
        {
            var tv = sim.VisionFor(team)!;
            sb.Append('T').Append(team);
            sb.Append("|R:").Append(string.Join(',', tv.VisibleEnemyShips.OrderBy(x => x)));
            sb.Append("|E:").Append(string.Join(',', tv.EyeballShips.OrderBy(x => x)));
            sb.Append("|G:").Append(string.Join(',', tv.Ghosts.Keys.OrderBy(x => x)));
            sb.Append("|DB:").Append(string.Join(',', tv.DiscoveredBases.OrderBy(x => x)));
            sb.Append("|DR:").Append(string.Join(',', tv.DiscoveredRocks.OrderBy(x => x)));
            sb.Append("|DA:").Append(string.Join(',', tv.DiscoveredAlephs.OrderBy(x => x)));
            sb.Append(';');
        }
        return sb.ToString();
    }

    // A scripted run: scout viewer + fighter target, cycled radar → far(ghost) → radar, sampling the
    // vision signature every 10 ticks. Identical for sync and async modes.
    List<string> Script(bool sync)
    {
        var sim = BootSim(42424242, sync);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius;
        float sig = Def(sim, FlightModel.ClassFighter).RadarSignature;
        var samples = new List<string>();
        Vec3 spot = new Vec3(sphere * sig * 0.5f, 0, 0);

        // Deploy a probe (WP5) off to the side, far from `spot`/the flee point below, so it exercises
        // the new probe-viewer vision code path (Simulation.Probes.cs -> CaptureVisionInput) every
        // tick for the rest of the run WITHOUT granting any extra detection — the existing
        // radar/ghost timeline below stays byte-for-byte what it was pre-probes. Determinism of the
        // probe's own contribution is what's under test here, not its detection radius.
        var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8); // probe-dispenser
        v.ProbeAmmo = 1;
        v.ProbeWeaponId = probeW.WeaponId;
        Vec3 probeSpot = new Vec3(20000f, 0, 0);
        Park(v, EmptySector, probeSpot);
        Park(t, EmptySector, spot);
        v.HeldInput = new ShipInputState { DropProbe = true };
        sim.Step();
        v.HeldInput = new ShipInputState();

        void Phase(Vec3 viewerPos, Vec3 targetPos, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Park(v, EmptySector, viewerPos);
                Park(t, EmptySector, targetPos);
                sim.Step();
                if (sim.Tick % 10 == 0)
                    samples.Add(Sig(sim));
            }
        }
        Phase(new Vec3(0, 0, 0), spot, 40); // radar
        Phase(new Vec3(60000f, 0, 0), spot, 50); // viewer flees → persistent ghost + lost
        Phase(new Vec3(0, 0, 0), spot, 40); // viewer returns → radar re-detect, ghost cleared
        sim.StopVision();
        return samples;
    }

    var a = Script(true);
    var b = Script(true);
    Check(a.Count == b.Count && a.SequenceEqual(b), $"two sync sims produce bit-identical vision timelines ({a.Count} samples)", "sync vision timeline diverged between two runs");

    var c = Script(false); // async worker thread
    Check(a.Count == c.Count && a.SequenceEqual(c), $"the async (worker-thread) run reproduces the SAME timeline ({c.Count} samples)", "async vision timeline differed from sync (fixed-boundary apply not worker-speed independent)");

    // The timeline must actually exercise radar + ghost states (guard against a vacuous all-empty pass).
    bool NonEmptyAfter(string s, string tag)
    {
        int i = s.IndexOf(tag, StringComparison.Ordinal);
        return i >= 0 && char.IsDigit(s[i + tag.Length]);
    }
    Check(a.Any(s => NonEmptyAfter(s, "|R:")) && a.Any(s => NonEmptyAfter(s, "|G:")), "the determinism script actually reached radar and ghost states", "the determinism script never populated radar/ghost state (vacuous)");
}

// ================================================================================================
// 11. Recon probes (WP5) — deploy via the real fire path (held DropProbe input, cadence-gated,
//     ammo-decrementing — mirrors MineTest's Deploy helper). A deployed probe grants its team radar
//     detection of an enemy inside ProbeSightRadius×sig with NO ship viewer nearby; it stops
//     contributing once it expires past its authored lifespan.
// ================================================================================================
{
    var sim = BootSim(11);
    var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8); // probe-dispenser
    float sig = Def(sim, FlightModel.ClassFighter).RadarSignature; // 1.0

    var layer = Join(sim, 1, 0, FlightModel.ClassFighter);
    var enemy = Join(sim, 2, 1, FlightModel.ClassFighter);
    // Force the dispenser ammo directly (bypasses the hangar payload budget — the sim path under
    // test is deploy/expire/vision, not the hangar validator; mirrors MineTest.SetupLayer).
    layer.ProbeAmmo = 1;
    layer.ProbeWeaponId = probeW.WeaponId;
    Park(enemy, EmptySector, new Vec3(90000f, 0, 0)); // parked well outside everything for now

    // Hold DropProbe for a few ticks: the cadence gate (FireIntervalTicks) must still yield ONE probe.
    int probesBefore = sim.Probes.Count;
    for (int i = 0; i < 3; i++)
    {
        Park(layer, EmptySector, new Vec3(0, 0, 0));
        layer.HeldInput = new ShipInputState { DropProbe = true };
        sim.Step();
    }
    layer.HeldInput = new ShipInputState();
    Check(sim.Probes.Count == probesBefore + 1, $"a held DropProbe deploys exactly one probe (cadence gate holds)", $"expected {probesBefore + 1} probe(s), found {sim.Probes.Count}");
    Check(layer.ProbeAmmo == 0, "one deploy consumes exactly one probe-cargo unit", $"ProbeAmmo wrong ({layer.ProbeAmmo}, expected 0)");
    var probe = sim.Probes[sim.Probes.Count - 1];

    // Fly the deploying ship far away — it is NOT itself a viewer of the enemy from here on, so any
    // detection can only come from the stationary probe. Put the enemy just inside the probe's
    // signature-scaled sight radius.
    Vec3 nearProbe = probe.Pos + new Vec3(probeW.ProbeSightRadius * sig * 0.5f, 0, 0);
    var lost = Run(sim, () => { Park(layer, EmptySector, new Vec3(90000f, 0, 0)); Park(enemy, EmptySector, nearProbe); }, Settle);
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(enemy.ShipId), "a deployed probe detects an enemy in its sight radius with no ship viewer nearby", "probe failed to grant vision with the deploying ship far away");

    // Run to the probe's expiry with the enemy still parked in its (former) sight radius.
    uint expireAt = probe.ExpireAtTick;
    while (sim.Tick < expireAt)
    {
        Park(layer, EmptySector, new Vec3(90000f, 0, 0));
        Park(enemy, EmptySector, nearProbe);
        sim.Step();
    }
    Check(!sim.Probes.Any(p => p.ProbeId == probe.ProbeId), $"the probe despawns at/after its ExpireAtTick ({expireAt})", $"the probe did not expire by tick {sim.Tick} (expire {expireAt})");
    Run(sim, () => { Park(layer, EmptySector, new Vec3(90000f, 0, 0)); Park(enemy, EmptySector, nearProbe); }, Settle);
    Check(!Vision(sim, 0).VisibleEnemyShips.Contains(enemy.ShipId), "vision contribution stops once the granting probe expires", "the enemy stayed visible after the granting probe expired");
}

// ================================================================================================
// 12. Probes are cleared on a match reseed (ReturnToLobby tears down every live probe).
// ================================================================================================
{
    var sim = BootSim(12);
    var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8);
    var layer = Join(sim, 1, 0, FlightModel.ClassFighter);
    layer.ProbeAmmo = 1;
    layer.ProbeWeaponId = probeW.WeaponId;
    Park(layer, EmptySector, new Vec3(0, 0, 0));
    layer.HeldInput = new ShipInputState { DropProbe = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    Check(sim.Probes.Count == 1, "a probe is live before the reseed (pre-condition)", "no probe deployed before the reseed");

    sim.ReturnToLobby();
    Check(sim.Probes.Count == 0, "a match reseed (ReturnToLobby) clears every live probe", $"probes survived the reseed ({sim.Probes.Count} left)");

    // F7: a match clear emits a ProbeGone (reason 1 = silent cleanup) for every live probe, so the
    // client (which never drops probes on MsgProbes omission) doesn't keep phantom probes.
    Check(sim.ProbeGoneThisStep.Any(g => g.reason == 1),
        "ReturnToLobby emits a ProbeGone (reason 1) for the live probe so clients drop it (F7)",
        "no ProbeGone was queued for the probe torn down at match clear");
}

// ================================================================================================
// 13. F6 — IsPointVisibleToTeam honors probe viewers (a point covered ONLY by a team probe, with the
//     deploying ship flown far away, is visible; a point beyond the probe's sight radius is not).
// ================================================================================================
{
    var sim = BootSim(66);
    var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8);
    var layer = Join(sim, 1, 0, FlightModel.ClassFighter);
    layer.ProbeAmmo = 1;
    layer.ProbeWeaponId = probeW.WeaponId;
    Park(layer, EmptySector, new Vec3(0, 0, 0));
    layer.HeldInput = new ShipInputState { DropProbe = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    var probe = sim.Probes[^1];
    Park(layer, EmptySector, new Vec3(90000f, 0, 0)); // deploying ship no longer a viewer of the test point

    Vec3 near = probe.Pos + new Vec3(probeW.ProbeSightRadius * 0.5f, 0, 0);
    Vec3 far = probe.Pos + new Vec3(probeW.ProbeSightRadius + 2000f, 0, 0);
    Check(sim.IsPointVisibleToTeam(0, EmptySector, near), "IsPointVisibleToTeam sees a point under probe-only coverage (F6)", "a probe-covered point was reported not visible");
    Check(!sim.IsPointVisibleToTeam(0, EmptySector, far), "a point beyond the probe sight radius is NOT visible", "a point outside the probe radius was reported visible");
}

// ================================================================================================
// 13b. Enemy-probe visibility: a deployed probe is a radar TARGET for the OTHER team — an enemy in
//      sensor range sees it (so it can shoot it), and it fogs out again when the enemy leaves range.
// ================================================================================================
{
    var sim = BootSim(661);
    var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8);
    var layer = Join(sim, 1, 0, FlightModel.ClassFighter);
    layer.ProbeAmmo = 1;
    layer.ProbeWeaponId = probeW.WeaponId;
    Park(layer, EmptySector, new Vec3(0, 0, 0));
    layer.HeldInput = new ShipInputState { DropProbe = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    var probe = sim.Probes[^1];
    Park(layer, EmptySector, new Vec3(90000f, 0, 0)); // deployer far away — only the enemy's own sensors matter

    var enemy = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius; // 450
    Run(sim, () => Park(enemy, EmptySector, probe.Pos + new Vec3(sphere * 0.5f, 0, 0)), Settle);
    Check(sim.VisionFor(1)!.VisibleEnemyProbes.Contains(probe.ProbeId),
        "an enemy within sensor range detects a deployed probe (it can see what it may shoot)", "an enemy in range did not see the probe");

    Run(sim, () => Park(enemy, EmptySector, probe.Pos + new Vec3(90000f, 0, 0)), Settle);
    Check(!sim.VisionFor(1)!.VisibleEnemyProbes.Contains(probe.ProbeId),
        "the probe fogs out of an enemy's view once out of sensor range", "the probe stayed visible to a far enemy");
}

// ================================================================================================
// 13c. Probe destruction: an enemy bolt through a deployed probe destroys it (gone reason 2), and it
//      is removed from the live set. Probe health is forced to 1 so a single hit resolves the kill.
// ================================================================================================
{
    var sim = BootSim(662);
    var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8);
    var layer = Join(sim, 1, 0, FlightModel.ClassFighter);
    layer.ProbeAmmo = 1;
    layer.ProbeWeaponId = probeW.WeaponId;
    Park(layer, EmptySector, new Vec3(0, 0, 0));
    layer.HeldInput = new ShipInputState { DropProbe = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    Park(layer, EmptySector, new Vec3(90000f, 0, 0)); // deployer out of the line of fire
    var probe = sim.Probes[^1];
    probe.Health = 1f; // one bolt kills it (avoids multi-hit cadence timing in the test)

    var enemy = Join(sim, 2, 1, FlightModel.ClassFighter);
    bool gone = false;
    for (uint i = 0; i < 60 && !gone; i++)
    {
        Park(enemy, EmptySector, probe.Pos - new Vec3(0, 0, 120f)); // behind the probe, facing +Z straight at it
        enemy.HeldInput = new ShipInputState { Firing = true };
        sim.Step();
        if (sim.ProbeGoneThisStep.Any(g => g.id == probe.ProbeId && g.reason == 2))
            gone = true;
    }
    Check(gone, "an enemy bolt destroys a deployed probe (gone reason 2)", "the enemy never destroyed the probe");
    Check(!sim.Probes.Any(p => p.ProbeId == probe.ProbeId), "the destroyed probe is removed from the live set", "the probe survived in the live set after destruction");
}

// ================================================================================================
// 14. F8 — warp discovery: a ship warping through an aleph immediately scouts the rocks around its
//     arrival point (reveal log THIS tick; persisted to DiscoveredRocks by the next vision boundary).
// ================================================================================================
{
    var sim = BootSim(88);
    var g = sim.World.Alephs[0];
    // A rock at the exit mouth of the destination sector — undiscovered until the warp reveals it.
    var exitRock = sim.World.AddRockForTest(g.DestSectorId, g.PartnerPos, 40f);
    var scout = Join(sim, 1, 0, FlightModel.ClassScout);
    var tv = Vision(sim, 0);
    Check(!tv.RevealLogRocks.Contains(exitRock.Id) && !tv.DiscoveredRocks.Contains(exitRock.Id), "the exit-mouth rock is unknown before the warp (pre-condition)", "the exit rock was already known before warping");

    // Park the scout on the aleph so Pass A's TryWarp fires this step, then verify the arrival rock
    // was scouted synchronously (streamed via the reveal log the same tick).
    Park(scout, g.SectorId, g.Pos);
    sim.Step();
    Check(scout.SectorId == g.DestSectorId, "the scout warped to the destination sector (pre-condition)", "the scout did not warp");
    Check(Vision(sim, 0).RevealLogRocks.Contains(exitRock.Id), "warping scouts the arrival-point rocks the SAME tick (reveal log) (F8)", "the arrival rock was not revealed on warp");

    // Hold at the exit a couple of vision boundaries: the warp-staged rock is merged into the
    // persistent DiscoveredRocks (so a late joiner's Welcome and fog memory carry it).
    Run(sim, () => Park(scout, g.DestSectorId, g.PartnerPos), Settle);
    Check(Vision(sim, 0).DiscoveredRocks.Contains(exitRock.Id), "the warp-revealed rock persists into DiscoveredRocks (F8)", "the warp-revealed rock was never persisted");
}

// ================================================================================================
// 15. F1 — BuildWelcome fog semantics: fog off = full world; fog on + NULL vision (NoTeam) = ZERO
//     statics (the leak fix); fog on + team vision = exactly the discovered set.
// ================================================================================================
{
    var sim = BootSim(15);
    // Scout a rock so team 0's discovered set is non-trivial (base(s) + one rock).
    var rock = sim.World.AddRockForTest(EmptySector, new Vec3(0, 0, 300f), 60f);
    var scout = Join(sim, 1, 0, FlightModel.ClassScout);
    Run(sim, () => Park(scout, EmptySector, new Vec3(0, 0, 0)), Settle);
    var tv0 = sim.VisionFor(0)!;

    var full = WelcomeCounts(Protocol.BuildWelcome(1, 0, sim.World, sim.Tick, Array.Empty<byte>(), fog: false, vision: null));
    Check(full.s == sim.World.Sectors.Count && full.b == sim.World.Bases.Count && full.r == sim.World.Asteroids.Count && full.a == sim.World.Alephs.Count,
        "fog-off Welcome dumps the full world incl. all sectors (byte-compatible with pre-fog)", "fog-off Welcome did not carry the full world");

    var noTeam = WelcomeCounts(Protocol.BuildWelcome(1, Protocol.NoTeam, sim.World, sim.Tick, Array.Empty<byte>(), fog: true, vision: null));
    Check(noTeam.s == 0 && noTeam.b == 0 && noTeam.r == 0 && noTeam.a == 0,
        "fog-on Welcome for a NoTeam join (null vision) contains ZERO statics AND zero sectors — the full-world leak is fixed (F1)",
        $"a fog NoTeam Welcome leaked statics ({noTeam.s} sectors, {noTeam.b} bases, {noTeam.r} rocks, {noTeam.a} alephs)");

    var team = WelcomeCounts(Protocol.BuildWelcome(1, 0, sim.World, sim.Tick, Array.Empty<byte>(), fog: true, vision: tv0));
    Check(team.b == tv0.DiscoveredBases.Count && team.r == tv0.DiscoveredRocks.Count && team.a == tv0.DiscoveredAlephs.Count,
        $"fog-on team Welcome carries exactly the discovered set ({team.b}B/{team.r}R/{team.a}A)",
        "fog-on team Welcome did not match the discovered set");
    Check(tv0.DiscoveredRocks.Contains(rock.Id) && team.r >= 1, "the discovered set (and its Welcome) includes the scouted rock", "the scouted rock was missing from the team Welcome");
    // Sector gating: without discovering the aleph to sector 1, team 0 knows only its home sector.
    Check(team.s == tv0.DiscoveredSectors.Count && team.s >= 1 && team.s < sim.World.Sectors.Count,
        $"fog-on team Welcome carries only discovered sectors ({team.s} of {sim.World.Sectors.Count}) — undiscovered sectors are hidden",
        $"fog-on team Welcome leaked sectors ({team.s} sent, {tv0.DiscoveredSectors.Count} discovered, {sim.World.Sectors.Count} total)");
}

// ================================================================================================
// 15b. Sector discovery: a team knows only its home sector until it discovers an aleph, which
//      reveals BOTH endpoint sectors, logs them, and streams them in a MsgReveal sector slice.
// ================================================================================================
{
    var sim = BootSim(1515);
    var g = sim.World.Alephs[0]; // aleph in team 0's home sector, leading to g.DestSectorId
    var tv = sim.VisionFor(0)!;
    uint home = g.SectorId, dest = g.DestSectorId;

    Check(tv.DiscoveredSectors.Contains(home) && !tv.DiscoveredSectors.Contains(dest),
        "before scouting the aleph, the team knows its home sector but NOT the destination",
        $"initial discovered sectors wrong (home={tv.DiscoveredSectors.Contains(home)}, dest={tv.DiscoveredSectors.Contains(dest)})");

    // Park a scout on top of the aleph so it discovers it; hold a couple of vision boundaries so the
    // discovery applies (aleph → both endpoint sectors), then verify the destination is now known.
    var scout = Join(sim, 1, 0, FlightModel.ClassScout);
    Run(sim, () => Park(scout, home, g.Pos), Settle);
    var tv2 = sim.VisionFor(0)!;
    Check(tv2.DiscoveredAlephs.Contains(g.Id), "the scout discovers the aleph (pre-condition)", "the aleph was not discovered");
    Check(tv2.DiscoveredSectors.Contains(dest) && tv2.RevealLogSectors.Contains(dest),
        "discovering the aleph reveals the destination sector AND logs it for streaming",
        "the destination sector was not revealed/logged after aleph discovery");

    // A reveal slice from a fresh (zero) sector cursor must carry the destination sector record.
    var rockIndex = new Dictionary<ulong, int>();
    for (int i = 0; i < sim.World.Asteroids.Count; i++) rockIndex[sim.World.Asteroids[i].Id] = i;
    var rf = Protocol.BuildRevealSlice(sim.World, tv2, rockIndex, 0, 0, 0, 0,
        out _, out _, out _, out int nextSector);
    Check(rf is not null && nextSector == tv2.RevealLogSectors.Count && nextSector >= 1,
        "BuildRevealSlice emits the newly-revealed sector(s) and advances the sector cursor to the log end",
        "the reveal slice did not carry the revealed sector / advance the cursor");
}

// ================================================================================================
// 15c. Fire-signature boost: a ship parked just OUTSIDE a viewer's detection range is undetected at
//      rest, becomes a radar contact while it is firing (signature multiplied), and fades again once
//      the boost window elapses. Distances derive from the loaded content so retuning never breaks it.
// ================================================================================================
{
    var sim = BootSim(1516);
    float boost = sim.Content.World.FireSignatureBoost; // 2.5 stock
    float window = sim.Content.World.FireSignatureWindow; // 4.0 s stock
    Check(boost > 1f && window > 0f, "fire-signature boost/window are authored positive (pre-condition)", "fire-signature knobs did not load positive");

    var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);
    var target = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius; // 450
    float eyeMult = sim.Content.World.FogEyeballMultiplier; // 1.5 stock
    // Place the target BEHIND the viewer (−Z) so the forward cone never applies — only the
    // omnidirectional sphere/eyeball. Distance sits between the resting reach (sphere × eyeball) and
    // the boosted reach (sphere × boost): undetected at rest, radar-detected only while firing.
    float dist = sphere * (eyeMult + boost) / 2f;
    Vec3 behind = new Vec3(0, 0, -dist);

    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, behind); }, Settle);
    Check(!Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
        "a ship beyond the resting detection range is NOT a contact at rest", "the resting ship was detected without firing");

    // Keep the target "just fired" every tick so the boost stays maxed while the 2 Hz apply catches up.
    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, behind); target.LastFireTick = sim.Tick + 1; }, Settle);
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
        "firing multiplies the ship's radar signature — it becomes a contact while shooting", "a firing ship at boosted range was not detected");

    // Stop firing and hold longer than the boost window: the signature decays and the contact fades.
    int decayTicks = (int)(window * FlightModel.TickRate) + Settle + 10;
    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, behind); }, decayTicks);
    Check(!Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
        "after the fire-signature window elapses, the boosted contact fades back out", "the fire-boost contact never decayed");
}

// ================================================================================================
// 16. F3/F4 — BuildRevealSlice is bounded (≤512 rocks/frame), count == body, and its per-cursor
//     slicing streams the whole log across successive frames then reports caught-up (null).
// ================================================================================================
{
    var sim = BootSim(16);
    var world = sim.World;
    var tv = sim.VisionFor(0)!;
    // Append 600 fresh rocks to the (append-only) reveal log — more than one frame's cap.
    const int total = 600;
    for (int i = 0; i < total; i++)
        tv.RevealLogRocks.Add(world.AddRockForTest(EmptySector, new Vec3(i, 0, 0), 10f).Id);
    var rockIndex = new Dictionary<ulong, int>();
    for (int i = 0; i < world.Asteroids.Count; i++)
        rockIndex[world.Asteroids[i].Id] = i;

    (int b, int r, int a, int s) RevealCounts(byte[] frame)
    {
        using var ms = new MemoryStream(frame);
        using var br = new BinaryReader(ms);
        br.ReadByte(); // MsgReveal
        int nb = br.ReadByte(); br.ReadBytes(nb * 33);
        int nr = br.ReadUInt16(); br.ReadBytes(nr * 41);
        int na = br.ReadByte(); br.ReadBytes(na * 28);
        int ns = br.ReadByte(); br.ReadBytes(ns * 8); // sector slice: u32 id + f32 radius
        if (ms.Position != frame.Length) throw new Exception("Reveal count != body");
        return (nb, nr, na, ns);
    }

    var f1 = Protocol.BuildRevealSlice(world, tv, rockIndex, 0, 0, 0, 0, out int nb1, out int nr1, out int na1, out int nsx1);
    var c1 = RevealCounts(f1!);
    Check(c1.r == Protocol.RevealMaxRocks && nr1 == Protocol.RevealMaxRocks, $"the first reveal slice is capped at {Protocol.RevealMaxRocks} rocks (count == body)", "the first reveal slice was not capped / count != body");

    var f2 = Protocol.BuildRevealSlice(world, tv, rockIndex, nb1, nr1, na1, nsx1, out int nb2, out int nr2, out int na2, out int nsx2);
    var c2 = RevealCounts(f2!);
    Check(c2.r == total - Protocol.RevealMaxRocks && nr2 == total, $"the remainder ({total - Protocol.RevealMaxRocks}) streams in the next slice (cursor advances)", "the reveal remainder did not stream correctly");

    var f3 = Protocol.BuildRevealSlice(world, tv, rockIndex, nb2, nr2, na2, nsx2, out _, out _, out _, out _);
    Check(f3 is null, "once the cursor reaches the log end, BuildRevealSlice returns null (caught up)", "BuildRevealSlice kept emitting frames past the log end");
}

// ================================================================================================
// 17. F5 — the vision worker reads CAPTURED base health, not live World.BaseHealth: sync and async
//     runs produce the SAME remembered-health timeline even when a base is damaged at a non-boundary
//     tick (a live read would let the worker's timing race the mid-interval mutation).
// ================================================================================================
{
    List<float> Script(bool sync)
    {
        var sim = BootSim(17171717, sync);
        var enemyBase = sim.World.Bases[1];
        int baseIdx = 1;
        var scout = Join(sim, 1, 0, FlightModel.ClassScout);
        Vec3 near = enemyBase.Pos + new Vec3(300f, 0, 0);
        var samples = new List<float>();
        for (int i = 0; i < 120; i++)
        {
            Park(scout, enemyBase.SectorId, near); // keep the base continuously in vision
            // Damage the base at a NON-boundary tick (tick % 10 == 3), stepping down each time.
            if (sim.Tick % 10 == 3 && sim.World.BaseHealth[baseIdx] > 1f)
                sim.World.BaseHealth[baseIdx] -= sim.World.BaseHealth[baseIdx] * 0.1f;
            sim.Step();
            if (sim.Tick % 10 == 0)
                samples.Add(sim.VisionFor(0)!.LastKnownBaseHealth.GetValueOrDefault(enemyBase.Id, -1f));
        }
        sim.StopVision();
        return samples;
    }
    var sync1 = Script(true);
    var async1 = Script(false);
    Check(sync1.Count == async1.Count && sync1.SequenceEqual(async1),
        "captured base health makes the remembered-health timeline worker-speed independent across a mid-interval damage (F5)",
        "sync and async remembered-health timelines diverged (a live World.BaseHealth read raced the mutation)");
    var recorded = sync1.Where(h => h > 0f).ToList(); // early samples are -1 until the base is discovered
    Check(recorded.Count >= 2 && recorded.Last() < recorded.First(), "the F5 script actually damaged an in-vision base (non-vacuous)", "the F5 base-damage script never recorded a falling remembered health");
}

// ================================================================================================
// 18. F1 hub-level — drive the REAL ClientHub over an in-memory transport: a NoTeam join under fog
//     gets a ZERO-static Welcome (no full-world leak), and picking a team re-Welcomes it with that
//     team's discovered world. Exercises the NoTeam -> team-pick Welcome flow end to end (SendWelcome
//     on Hello + MsgSetTeam), the flow the real Godot client walks on join.
// ================================================================================================
{
    var sim = BootSim(18);
    var hub = new ClientHub(sim, new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(), new SimServer.Backend.ReadyUpMatchmaker(false));

    var ft = new FakeHubTransport();
    var cts = new CancellationTokenSource();
    var conn = hub.HandleConnection(ft, cts.Token);

    // Hello v9: [MsgHello][secretLen 0][nameLen 5]['smoke'][tokenLen 0] (fresh join, no reconnect).
    var name = Encoding.UTF8.GetBytes("smoke");
    var hello = new List<byte> { Protocol.MsgHello, 0, (byte)name.Length };
    hello.AddRange(name);
    hello.Add(0);
    ft.Feed(hello.ToArray());

    byte[]? WaitWelcome(int afterCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var welcomes = ft.Sent.Where(f => f.Length > 0 && f[0] == Protocol.MsgWelcome).ToList();
            if (welcomes.Count > afterCount)
                return welcomes[afterCount];
            Thread.Sleep(10);
        }
        return null;
    }

    var w1 = WaitWelcome(0);
    Check(w1 is not null && WelcomeCounts(w1).b == 0 && WelcomeCounts(w1).r == 0 && WelcomeCounts(w1).a == 0,
        "a fog-on NoTeam join receives a ZERO-static Welcome through the real ClientHub (no world leak) (F1)",
        "the NoTeam join's Welcome leaked statics (or never arrived)");

    // Pick team 0 → the hub re-Welcomes with team 0's discovered world (its garrison base(s)).
    ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
    var w2 = WaitWelcome(1);
    var tv0 = sim.VisionFor(0)!;
    Check(w2 is not null && WelcomeCounts(w2).b == tv0.DiscoveredBases.Count && WelcomeCounts(w2).b > 0,
        "picking a team re-Welcomes the client with that team's discovered world (NoTeam -> team-pick flow) (F1)",
        "the team-pick did not re-send a Welcome carrying the team's discovered bases");

    cts.Cancel();
    try { conn.Wait(2000); } catch { /* teardown */ }
}

// ================================================================================================
// 19. Integration smoke (autofly substitute — no Godot here): drive the REAL ClientHub + the ASYNC
//     vision worker fog-on end to end — join, pick a team, auto-start the match, spawn, and run 300
//     ticks (Step + AfterStep, the real sim-loop pair). Asserts no exception is thrown anywhere
//     (worker thread, reveal cursors, warp merge, per-team coarse/snapshot fan-out) and that the
//     client actually received its ship (YouAre) and streaming snapshots.
// ================================================================================================
{
    var content = ContentLoader.Load(stockPath);
    var world = new World(19, content.World, content.Bases[0].MaxHealth, content.Start);
    var sim = new Simulation(world, content) { PigsEnabled = false, FogEnabled = true, VisionSynchronous = false };
    var hub = new ClientHub(sim, new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(), new SimServer.Backend.ReadyUpMatchmaker(true));
    sim.ShouldStartMatch = hub.ShouldStartMatch;
    sim.OnReturnToLobby = hub.OnReturnToLobby;

    var ft = new FakeHubTransport();
    var cts = new CancellationTokenSource();
    var conn = hub.HandleConnection(ft, cts.Token);

    var name = Encoding.UTF8.GetBytes("smoke");
    var hello = new List<byte> { Protocol.MsgHello, 0, (byte)name.Length };
    hello.AddRange(name);
    hello.Add(0);
    ft.Feed(hello.ToArray());
    Thread.Sleep(50);
    ft.Feed(new byte[] { Protocol.MsgSetTeam, 0 });
    Thread.Sleep(50);

    Exception? crash = null;
    void Pump(int n)
    {
        for (int i = 0; i < n && crash is null; i++)
        {
            try { sim.Step(); hub.AfterStep(); }
            catch (Exception e) { crash = e; }
            Thread.Sleep(2);
        }
    }
    Pump(20); // matchmaker auto-starts the match while in lobby
    Check(crash is null && sim.IsActive, "the hub-driven match auto-starts fog-on without exceptions", $"the match did not start cleanly ({crash?.GetType().Name}: {crash?.Message})");

    ft.Feed(new byte[] { Protocol.MsgSpawn, FlightModel.ClassScout });
    Thread.Sleep(50);
    Pump(300); // fly the async vision worker across ~30 boundaries with a live ship

    Check(crash is null, "300 ticks of the real hub + async vision worker run fog-on with NO exception (autofly substitute)", $"an exception was thrown during the fog-on integration run: {crash}");
    Check(ft.Sent.Any(f => f.Length > 0 && f[0] == Protocol.MsgYouAre), "the client received its spawned ship (MsgYouAre)", "the client never received a YouAre for its spawn");
    Check(ft.Sent.Any(f => f.Length > 0 && f[0] == Protocol.MsgSnapshot), "the client received streaming snapshots under fog", "no snapshots reached the fog-on client");

    cts.Cancel();
    sim.StopVision();
    try { conn.Wait(2000); } catch { /* teardown */ }
}

Console.WriteLine(failures == 0 ? "\nALL FOG TESTS PASSED" : $"\n{failures} FOG TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// In-memory IClientTransport for the hub-level test: feed client->server frames, capture server->client.
sealed class FakeHubTransport : SimServer.Net.IClientTransport
{
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _in = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Sent = new();

    public void Feed(byte[] frame) => _in.Add(frame);

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            byte[] f = await Task.Run(() => _in.Take(ct), ct);
            Array.Copy(f, buffer, f.Length);
            return f.Length;
        }
        catch (OperationCanceledException)
        {
            return -1; // transport closed
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Sent.Enqueue(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct) => ValueTask.CompletedTask;
}
