// Fog-of-war vision tests (plan WP2 / tests/FogTest). Console PASS/FAIL in the repo's test idiom
// (mirrors MineTest/MissileTest): exits non-zero on any failure so CI / a manual run can gate.
//
// Boots the real Simulation from the live content bundle (server/content/core, copied next to the
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

string stockPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "core.manifest.yaml");
string worldPath = Path.Combine(AppContext.BaseDirectory, "content", "core", "world.yaml");
const uint EmptySector = 999;
const int Settle = 30; // ticks to hold a configuration so the 2 Hz apply reflects it (>2 boundaries)

Simulation BootSim(ulong seed, bool sync = true)
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(seed, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content);
    sim.PigsEnabled = false;
    sim.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
    sim.FogEnabled = true;
    sim.VisionSynchronous = sync;
    sim.StartMatch();
    return sim;
}

ShipClassDef Def(Simulation sim, byte cls) => sim.Content.Ships.First(d => d.ClassId == cls);

// The loaded world's signature-pipeline knobs (SignatureModel), for defs-derived boundaries.
SignatureKnobs Knobs(ContentSet c) =>
    new(
        c.World.FireSignatureBoost,
        c.World.FireSignatureWindow * FlightModel.TickRate,
        c.World.BoostSignatureMult,
        c.World.ShieldSignatureMult,
        c.World.DustSignatureMult,
        c.World.SignatureMinMult,
        c.World.SignatureMaxMult
    );

// Effective AT-REST radar signature of a hull under the loaded knobs — the composed pipeline with
// the dynamic terms quiet (no fire, no afterburner, no dust): (base + bias) × shield-mult, clamped.
// Detection-range assertions scale by THIS rather than raw RadarSignature, so retuning the YAML —
// including the signature knobs — never breaks them (the file's standing idiom).
float EffSig(ContentSet c, byte cls)
{
    var d = c.Ships.First(x => x.ClassId == cls);
    return SignatureModel.Compute(
        new SignatureInputs(d.RadarSignature, d.SignatureBias, 0, 0, 0, 0f, d.ShieldCapacity > 0f, 0f),
        Knobs(c)
    );
}

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
    for (int i = 0; i < ns; i++)
    {
        br.ReadUInt32(); br.ReadSingle(); br.ReadString(); // id, radius, name
        if (br.ReadByte() != 0) br.ReadBytes(8); // map-pos: presence byte then x,y (2 floats)
        // Per-sector environment (mirror Protocol.WriteSectorEnv): 3 presence bytes always present.
        if (br.ReadByte() != 0) br.ReadBytes(40); // sun: godRays + dir(3) + color(3) + energy + ambient + size
        if (br.ReadByte() != 0) { br.ReadBytes(28); if (br.ReadByte() != 0) br.ReadUInt32(); } // nebula: colorA+colorB+intensity (+seed)
        if (br.ReadByte() != 0) { br.ReadBytes(16); int nc = br.ReadUInt16(); br.ReadBytes(nc * 20); } // dust: color(3) + opacity(1) + clouds
    }
    int nb = br.ReadUInt16(); br.ReadBytes(nb * 33);
    // RockStatic v32: 41-byte prefix + mining block (u8 class + f32 currentRadius + u8 orePct + f32 oreCapacity) = 51.
    long nr = br.ReadUInt32(); br.ReadBytes((int)nr * 51);
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
// 0. SignatureModel unit tests — the pure signature pipeline, no sim. Neutral knobs must reproduce
//    the fire-boost-only behavior byte-identically; each term applies exactly its multiplier; the
//    clamp rails bound extreme stacking.
// ================================================================================================
{
    bool Close(float a, float b) => MathF.Abs(a - b) < 1e-4f;
    var neutral = new SignatureKnobs(FireBoost: 2.5f, FireWindowTicks: 80f, BoostMult: 1f, ShieldMult: 1f, DustMult: 1f, MinMult: 0.1f, MaxMult: 8f);
    SignatureInputs At(float bias = 0f, uint fire = 0, uint missile = 0, float ab = 0f, bool shield = false, float dust = 0f) =>
        new(BaseSig: 2f, Bias: bias, Tick: 1000, LastFireTick: fire, LastMissileTick: missile, AbPower: ab, HasShield: shield, DustCoverage: dust);

    Check(Close(SignatureModel.Compute(At(), neutral), 2f),
        "all-neutral knobs + bias 0 == base (the byte-identical guard)", "neutral pipeline did not return the base signature");
    Check(Close(SignatureModel.Compute(At(bias: 0.5f), neutral), 2.5f),
        "SigBias adds to the base signature", "SigBias was not additive");

    // Fire term: full boost at age 0, linear decay inside the window, expired at the window end;
    // a missile launch boosts exactly like a gun shot (max of the two stamps).
    float fired = SignatureModel.Compute(At(fire: 1000), neutral);
    float mid = SignatureModel.Compute(At(fire: 960), neutral); // age 40 of 80 → half-decayed
    Check(Close(fired, 2f * 2.5f), "a just-fired ship reads base × FireBoost", "fire boost at age 0 wrong");
    Check(Close(mid, 2f * 1.75f), "the fire boost decays linearly inside the window", "mid-window fire decay wrong");
    Check(Close(SignatureModel.Compute(At(fire: 920), neutral), 2f), "at the window end the signature is back to base", "fire boost outlived its window");
    Check(Close(SignatureModel.Compute(At(missile: 1000), neutral), fired), "a missile launch boosts like a gun shot", "missile stamp did not boost");

    // The new terms, each isolated under live-style knobs.
    var live = new SignatureKnobs(FireBoost: 2.5f, FireWindowTicks: 80f, BoostMult: 1.4f, ShieldMult: 1.15f, DustMult: 0.5f, MinMult: 0.1f, MaxMult: 8f);
    Check(Close(SignatureModel.Compute(At(ab: 1f), live), 2f * 1.4f), "AbPower 1 applies the full BoostMult", "full-afterburner term wrong");
    Check(Close(SignatureModel.Compute(At(ab: 0.5f), live), 2f * 1.2f), "the boost term ramps linearly with AbPower", "half-afterburner term wrong");
    Check(Close(SignatureModel.Compute(At(shield: true), live), 2f * 1.15f), "an equipped shield applies ShieldMult", "shield term wrong");
    Check(Close(SignatureModel.Compute(At(dust: 1f), live), 2f * 0.5f), "full dust coverage applies DustMult (quieter than base)", "dust term wrong");
    Check(Close(SignatureModel.Compute(At(dust: 0.5f), live), 2f * 0.75f), "the dust term ramps linearly with coverage", "half-coverage dust term wrong");

    // Clamp rails: extreme loud stacking caps at base × MaxMult; extreme quieting floors at × MinMult.
    var rails = new SignatureKnobs(FireBoost: 10f, FireWindowTicks: 80f, BoostMult: 3f, ShieldMult: 2f, DustMult: 0.02f, MinMult: 0.5f, MaxMult: 4f);
    Check(Close(SignatureModel.Compute(At(fire: 1000, ab: 1f, shield: true), rails), 2f * 4f),
        "extreme loud stacking clamps at base × MaxMult", "the max clamp rail did not hold");
    Check(Close(SignatureModel.Compute(At(dust: 1f), rails), 2f * 0.5f),
        "extreme quieting clamps at base × MinMult", "the min clamp rail did not hold");
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
    float sig = EffSig(sim.Content, FlightModel.ClassFighter); // at-rest effective signature
    float beyondSphere = sphere * sig + 200f; // outside the omni sphere so the cone is the only sensor

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
// 2. Sphere — omnidirectional but rock-occluded (with a clear line of sight the sphere sees in every
//    direction, incl. behind the viewer; a rock straddling the line of sight casts a radar shadow —
//    move the same rock off the line and the in-sphere target is detected again).
// ================================================================================================
{
    var sim = BootSim(2);
    var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);
    var target = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius; // 450

    // Behind the viewer (−Z), well inside the sphere, clear LoS → omnidirectional detection.
    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, new Vec3(0, 0, -sphere * 0.4f)); }, Settle);
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId), "the proximity sphere is omnidirectional (target behind the viewer, clear LoS, is detected)", "sphere missed a target behind the viewer");

    // A rock straddling the line of sight casts a radar shadow — an in-sphere target is NOT detected.
    var sim2 = BootSim(22);
    sim2.World.AddRockForTest(EmptySector, new Vec3(0, 0, 200f), 120f);
    var v2 = Join(sim2, 1, 0, FlightModel.ClassFighter);
    var t2 = Join(sim2, 2, 1, FlightModel.ClassFighter);
    Run(sim2, () => { Park(v2, EmptySector, new Vec3(0, 0, 0)); Park(t2, EmptySector, new Vec3(0, 0, sphere * 0.9f)); }, Settle);
    Check(!Vision(sim2, 0).VisibleEnemyShips.Contains(t2.ShipId), "the proximity sphere is rock-occluded (a rock between viewer and target blocks the in-sphere radar return)", "a rock between viewer and target failed to occlude the sphere");

    // Same viewer/target/range with the rock moved off the line of sight → detected again (proves the
    // miss above is occlusion, not range).
    var sim3 = BootSim(22);
    sim3.World.AddRockForTest(EmptySector, new Vec3(400f, 0, 200f), 120f);
    var v3 = Join(sim3, 1, 0, FlightModel.ClassFighter);
    var t3 = Join(sim3, 2, 1, FlightModel.ClassFighter);
    Run(sim3, () => { Park(v3, EmptySector, new Vec3(0, 0, 0)); Park(t3, EmptySector, new Vec3(0, 0, sphere * 0.9f)); }, Settle);
    Check(Vision(sim3, 0).VisibleEnemyShips.Contains(t3.ShipId), "the same in-sphere target with the rock moved off-axis is detected (clear LoS)", "the sphere missed a target with an unobstructed line of sight");
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
        sig = EffSig(probe.Content, FlightModel.ClassFighter);
    }
    float targetDist = sphere * sig + 600f; // outside the sphere, inside the cone

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
    float bomberSig = EffSig(sim.Content, FlightModel.ClassBomber);       // 1.75 × shield-mult
    float scoutSig = EffSig(sim.Content, FlightModel.ClassScout);         // 0.5 × shield-mult

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
        sig = EffSig(probe.Content, FlightModel.ClassFighter);
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

    // Eyeball occlusion — a rock on the line of sight hides an eyeball-band enemy ENTIRELY (a ship
    // hiding behind an asteroid streams neither radar nor mesh); move the rock off-axis → streams again.
    {
        var sim = BootSim(56);
        sim.World.AddRockForTest(EmptySector, new Vec3(bandDist * 0.5f, 0, 0), 120f);
        var v = Join(sim, 1, 0, FlightModel.ClassFighter);
        var t = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(bandDist, 0, 0)); }, Settle);
        var tv = Vision(sim, 0);
        Check(!tv.EyeballShips.Contains(t.ShipId) && !tv.VisibleEnemyShips.Contains(t.ShipId),
            "a rock on the line of sight hides an eyeball-band enemy entirely (not streamed by radar OR eyeball)",
            "an eyeball-band enemy behind a rock was still streamed");

        var sim2 = BootSim(56);
        sim2.World.AddRockForTest(EmptySector, new Vec3(bandDist * 0.5f, 400f, 0), 120f);
        var v2 = Join(sim2, 1, 0, FlightModel.ClassFighter);
        var t2 = Join(sim2, 2, 1, FlightModel.ClassFighter);
        Run(sim2, () => { Park(v2, EmptySector, new Vec3(0, 0, 0)); Park(t2, EmptySector, new Vec3(bandDist, 0, 0)); }, Settle);
        Check(Vision(sim2, 0).EyeballShips.Contains(t2.ShipId),
            "the same eyeball-band enemy with the rock moved off-axis is streamed again (clear LoS)",
            "an eyeball-band enemy with clear LoS was not streamed");
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
    float sig = EffSig(sim.Content, FlightModel.ClassFighter);

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
// 7b. Mining live shrink (MsgRockUpdate) — fog filtering + the shared-helper guarantee. A fog-on
//     client gets rock-updates ONLY for rocks its team has DISCOVERED (an enemy mining an unscouted
//     rock must not leak); fog-off broadcasts every changed rock; a NoTeam (null vision) client gets
//     none. And the SAME WriteRockStatic feeds Welcome + MsgReveal, so a discovered rock's static
//     record is byte-identical in both paths.
// ================================================================================================
{
    var sim = BootSim(72);
    var seen = sim.World.AddRockForTest(EmptySector, new Vec3(0, 0, 300f), 60f);   // scout discovers it
    var unseen = sim.World.AddRockForTest(EmptySector, new Vec3(80000f, 0, 0), 60f); // never in range
    var scout = Join(sim, 1, 0, FlightModel.ClassScout);
    Run(sim, () => Park(scout, EmptySector, new Vec3(0, 0, 0)), Settle);
    var tv = Vision(sim, 0);
    Check(tv.DiscoveredRocks.Contains(seen.Id) && !tv.DiscoveredRocks.Contains(unseen.Id),
        "pre-condition: the scout discovered the near rock but not the far one", "rock discovery pre-condition failed");

    // Extract the ids from a set of MsgRockUpdate frames (count-prefixed 13-byte records).
    List<ulong> UpdateIds(List<byte[]> frames)
    {
        var ids = new List<ulong>();
        foreach (var f in frames)
        {
            using var ms = new MemoryStream(f);
            using var br = new BinaryReader(ms);
            if (br.ReadByte() != Protocol.MsgRockUpdate) throw new Exception("wrong id on a rock-update frame");
            int n = br.ReadByte();
            for (int i = 0; i < n; i++) { ids.Add(br.ReadUInt64()); br.ReadSingle(); br.ReadByte(); }
            if (ms.Position != f.Length) throw new Exception("rock-update count != body");
        }
        return ids;
    }

    var changed = new List<ulong> { seen.Id, unseen.Id };
    var fogOn = UpdateIds(Protocol.BuildRockUpdatesFor(sim.World, tv, changed));
    Check(fogOn.Contains(seen.Id) && !fogOn.Contains(unseen.Id),
        "fog-on rock-updates carry ONLY the team's discovered rock (an unscouted rock does not leak)",
        "an undiscovered rock leaked into a fog-on client's rock-updates");
    var fogOff = UpdateIds(Protocol.BuildRockUpdates(sim.World, changed));
    Check(fogOff.Contains(seen.Id) && fogOff.Contains(unseen.Id),
        "fog-off rock-updates broadcast every changed rock", "the fog-off broadcast dropped a changed rock");
    Check(Protocol.BuildRockUpdatesFor(sim.World, null, changed).Count == 0,
        "a NoTeam (null-vision) client receives no rock-updates", "a null-vision client got rock-updates");

    // Shared-helper guarantee: pull the discovered rock's 51-byte static record out of a fog-off
    // Welcome and a MsgReveal slice and assert byte-equality (both go through WriteRockStatic).
    byte[]? WelcomeRock(byte[] frame, ulong id)
    {
        using var ms = new MemoryStream(frame);
        using var br = new BinaryReader(ms);
        br.ReadByte(); br.ReadByte(); br.ReadInt32(); br.ReadByte(); br.ReadUInt32(); br.ReadSingle();
        int tl = br.ReadByte(); br.ReadBytes(tl);
        int ns = br.ReadUInt16();
        for (int i = 0; i < ns; i++)
        {
            br.ReadUInt32(); br.ReadSingle(); br.ReadString();
            if (br.ReadByte() != 0) br.ReadBytes(8);
            if (br.ReadByte() != 0) br.ReadBytes(40);
            if (br.ReadByte() != 0) { br.ReadBytes(28); if (br.ReadByte() != 0) br.ReadUInt32(); }
            if (br.ReadByte() != 0) { br.ReadBytes(16); int nc = br.ReadUInt16(); br.ReadBytes(nc * 20); }
        }
        int nb = br.ReadUInt16(); br.ReadBytes(nb * 33);
        long nr = br.ReadUInt32();
        for (long i = 0; i < nr; i++) { var rec = br.ReadBytes(51); if (BitConverter.ToUInt64(rec, 0) == id) return rec; }
        return null;
    }
    byte[]? RevealRock(byte[] frame, ulong id)
    {
        using var ms = new MemoryStream(frame);
        using var br = new BinaryReader(ms);
        br.ReadByte();
        int nb = br.ReadByte(); br.ReadBytes(nb * 33);
        int nr = br.ReadUInt16();
        for (int i = 0; i < nr; i++) { var rec = br.ReadBytes(51); if (BitConverter.ToUInt64(rec, 0) == id) return rec; }
        return null;
    }

    var rockIndex = new Dictionary<ulong, int>();
    for (int i = 0; i < sim.World.Asteroids.Count; i++) rockIndex[sim.World.Asteroids[i].Id] = i;
    var welcome = Protocol.BuildWelcome(1, 0, sim.World, sim.Tick, Array.Empty<byte>(), fog: false, vision: null);
    var reveal = Protocol.BuildRevealSlice(sim.World, tv, rockIndex, 0, 0, 0, 0, out _, out _, out _, out _);
    var wRec = WelcomeRock(welcome, seen.Id);
    var rRec = reveal is null ? null : RevealRock(reveal, seen.Id);
    Check(wRec is not null && rRec is not null && wRec.AsSpan().SequenceEqual(rRec),
        "a discovered rock's static record is byte-identical in a fog-off Welcome and a MsgReveal slice (shared WriteRockStatic)",
        "the Welcome and Reveal rock static records diverged");
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
        sig = EffSig(probe.Content, FlightModel.ClassFighter);
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
// 9b. Eyeball soft-track — a ghost of a ship we can still SEE (eyeball tier, not radar) is NOT
//     scouted-empty away; it is refreshed to the ship's live pose so the blip follows the mesh and
//     then firms into a live radar contact when it closes — no vanish-then-reappear gap.
// ================================================================================================
{
    float sphere, sig, eyeMult;
    {
        var probe = BootSim(96);
        sphere = Def(probe, FlightModel.ClassFighter).VisionSphereRadius;
        sig = EffSig(probe.Content, FlightModel.ClassFighter);
        eyeMult = probe.Content.World.FogEyeballMultiplier;
    }
    float radarDist = sphere * sig * 0.5f;            // well inside the radar sphere
    float eyeDist = sphere * sig * (1f + eyeMult) * 0.5f; // mid eyeball band: > sphere, < sphere×eyeMult
    Vec3 origin = new Vec3(0, 0, 0);
    Vec3 spotA = new Vec3(radarDist, 0, 0);  // first radar fix (also the ghost's frozen spot)
    Vec3 spotB = new Vec3(eyeDist, 0, 0);    // later eyeball-only position, a DIFFERENT point than A
    Vec3 viewerFar = new Vec3(60000f, 0, 0);

    var sim = BootSim(96);
    var v = Join(sim, 1, 0, FlightModel.ClassFighter);
    var t = Join(sim, 2, 1, FlightModel.ClassFighter);
    Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotA); }, Settle); // radar at A
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(t.ShipId), "target radar-detected at spot A (pre-condition)", "target not radar-detected at A");
    Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotA); }, Settle); // flee → ghost at A
    Check(Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "ghost created at A on contact loss (pre-condition)", "no ghost created at A");

    // Return the viewer while the target sits at B in the EYEBALL band (not radar). The old ghost spot
    // A is now inside the viewer's radar sphere — pre-fix that "empty re-scout" would delete the ghost.
    Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotB); }, Settle);
    var tvE = Vision(sim, 0);
    Check(tvE.EyeballShips.Contains(t.ShipId) && !tvE.VisibleEnemyShips.Contains(t.ShipId), "the target sits in the eyeball tier (streamed, not radar) at B", "target was not eyeball-only at B");
    bool tracked = tvE.Ghosts.TryGetValue(t.ShipId, out var gE);
    Check(tracked, "an eyeball glimpse KEEPS the ghost instead of scouting the stale spot empty", "the ghost was cleared while the ship was still visible (eyeball)");
    Check(tracked && (gE.Pos - spotB).Length() < 60f, $"the ghost is soft-tracked to the ship's live eyeball pose (~{spotB.X:F0}), not left at A (~{spotA.X:F0})", "the eyeball-tracked ghost was not repositioned to the live pose");

    // Closing to radar range firms the blip into a live contact (ghost gone, exactly one contact).
    Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotA); }, Settle);
    var tvR = Vision(sim, 0);
    Check(tvR.VisibleEnemyShips.Contains(t.ShipId) && !tvR.Ghosts.ContainsKey(t.ShipId), "the eyeball-tracked ghost firms into a live radar contact on close (no lingering ghost)", "closing to radar left both a ghost and a live contact");
}

// ================================================================================================
// 9c. Ghost timeout — a lost-contact ghost that is never re-scouted self-expires after
//     FogGhostTimeout, but persists up to that point.
// ================================================================================================
{
    float sphere, sig;
    {
        var probe = BootSim(97);
        sphere = Def(probe, FlightModel.ClassFighter).VisionSphereRadius;
        sig = EffSig(probe.Content, FlightModel.ClassFighter);
    }
    float radarDist = sphere * sig * 0.5f;
    Vec3 origin = new Vec3(0, 0, 0);
    Vec3 spotS = new Vec3(radarDist, 0, 0);
    Vec3 viewerFar = new Vec3(60000f, 0, 0);

    var sim = BootSim(97);
    var v = Join(sim, 1, 0, FlightModel.ClassFighter);
    var t = Join(sim, 2, 1, FlightModel.ClassFighter);
    Run(sim, () => { Park(v, EmptySector, origin); Park(t, EmptySector, spotS); }, Settle); // radar
    Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotS); }, Settle); // flee → ghost
    Check(Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "ghost created on contact loss (pre-condition for timeout)", "no ghost created before timeout test");

    int timeoutTicks = (int)MathF.Round(sim.Content.World.FogGhostTimeout * FlightModel.TickRate); // 120 s × 20 Hz = 2400
    // Hold the viewer away (no re-scout, no re-detect) well past ghost creation but before the timeout.
    Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotS); }, timeoutTicks - 400);
    Check(Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "a never-re-scouted ghost persists until FogGhostTimeout elapses", "a ghost expired before its timeout");
    // Cross the timeout — the stale ghost self-expires even though the area was never re-scouted.
    Run(sim, () => { Park(v, EmptySector, viewerFar); Park(t, EmptySector, spotS); }, 800);
    Check(!Vision(sim, 0).Ghosts.ContainsKey(t.ShipId), "a lost-contact ghost self-expires after FogGhostTimeout", "a ghost outlived its FogGhostTimeout");
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
        float sig = EffSig(sim.Content, FlightModel.ClassFighter);
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
    float sig = EffSig(sim.Content, FlightModel.ClassFighter);

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
// 13d. Minefield radar discoverability — an ARMED enemy field is a radar target (VisibleEnemyMines,
//      signature-scaled ×MineSignature, mirroring probes 13b): silent while still arming, detected
//      in sensor range once armed, fogged out again when the enemy leaves range, and rock-occluded.
// ================================================================================================
{
    var sim = BootSim(663);
    var mineW = sim.Content.Weapons.First(w => w.WeaponId == 7); // mine-dispenser
    var layer = Join(sim, 1, 0, FlightModel.ClassBomber);
    layer.MineAmmo = 1;
    layer.MineWeaponId = mineW.WeaponId;
    Park(layer, EmptySector, new Vec3(0, 0, 0));
    layer.HeldInput = new ShipInputState { DropMine = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    var field = sim.Minefields[^1];
    Park(layer, EmptySector, new Vec3(90000f, 0, 0)); // deployer far away — only the enemy's own sensors matter

    var enemy = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius; // 450
    float mineSig = mineW.MineSignature;
    // In radar range of the center but well outside the lethal cloud sphere (parked = ~0 damage anyway).
    Vec3 nearField = field.Center + new Vec3(sphere * mineSig * 0.6f, 0, 0);

    // Still arming: every capture inside the arm window skips the field, and an apply lags its capture
    // by a boundary — so with a margin before ArmAtTick no applied result can contain it yet.
    int preArm = (int)(field.ArmAtTick - sim.Tick) - 5;
    if (preArm > 0)
    {
        Run(sim, () => Park(enemy, EmptySector, nearField), preArm);
        Check(!Vision(sim, 1).VisibleEnemyMines.Contains(field.FieldId),
            "a still-arming field is radar-silent (armed-only capture)", "the enemy radar-detected a field before it armed");
    }

    Run(sim, () => Park(enemy, EmptySector, nearField), Settle);
    Check(Vision(sim, 1).VisibleEnemyMines.Contains(field.FieldId),
        $"an enemy within sphere×MineSignature ({sphere:F0}×{mineSig}) radar-detects an armed field without direct LOS gating",
        "an enemy in sensor range did not detect the armed field");

    Run(sim, () => Park(enemy, EmptySector, field.Center + new Vec3(90000f, 0, 0)), Settle);
    Check(!Vision(sim, 1).VisibleEnemyMines.Contains(field.FieldId),
        "the field fogs back out of the enemy's radar once out of sensor range", "the field stayed visible to a far enemy");

    // Rock occlusion: identical armed-and-in-range geometry, but a rock straddles the enemy→center
    // sightline — the field's radar return is shadowed (ClassifyTarget's shared occlusion scan).
    var sim2 = BootSim(664);
    var layer2 = Join(sim2, 1, 0, FlightModel.ClassBomber);
    layer2.MineAmmo = 1;
    layer2.MineWeaponId = mineW.WeaponId;
    Park(layer2, EmptySector, new Vec3(0, 0, 0));
    layer2.HeldInput = new ShipInputState { DropMine = true };
    sim2.Step();
    layer2.HeldInput = new ShipInputState();
    var field2 = sim2.Minefields[^1];
    Park(layer2, EmptySector, new Vec3(90000f, 0, 0));
    Vec3 near2 = field2.Center + new Vec3(sphere * mineSig * 0.6f, 0, 0);
    sim2.World.AddRockForTest(EmptySector, (field2.Center + near2) * 0.5f, 120f); // midpoint of the sightline
    var enemy2 = Join(sim2, 2, 1, FlightModel.ClassFighter);
    Run(sim2, () => Park(enemy2, EmptySector, near2), Settle);
    Check(!Vision(sim2, 1).VisibleEnemyMines.Contains(field2.FieldId),
        "a rock between the enemy and the armed field's center occludes its radar return", "the field was detected through a rock");
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
        int nr = br.ReadUInt16(); br.ReadBytes(nr * 51); // RockStatic v32: 41 + mining block (class + currentRadius + orePct + oreCapacity)
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
        new SimServer.Backend.InMemoryPlayerDirectory(), new SimServer.Backend.ReadyUpMatchmaker(false),
        "Test Arena", System.Array.Empty<SimServer.Content.MapCatalogEntry>());

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
    var content = ContentLoader.Load(stockPath, worldPath);
    var world = new World(19, content.World, content.Bases[0].MaxHealth, content.Start, content.Ships);
    var sim = new Simulation(world, content) { PigsEnabled = false, MinersEnabled = false, FogEnabled = true, VisionSynchronous = false };
    var hub = new ClientHub(sim, new SimServer.Backend.OpenAuthenticator(),
        new SimServer.Backend.InMemoryPlayerDirectory(), new SimServer.Backend.ReadyUpMatchmaker(true),
        "Test Arena", System.Array.Empty<SimServer.Content.MapCatalogEntry>());
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

// ================================================================================================
// 20. Probe collision — a deployed probe is a SOLID, low-HP body: deploying it must not kick the
//     deploying ship; a ship rammed into it is pushed out (no penetration) and takes collision
//     damage (like a base); and enough ramming destroys the probe (reason-2 gone) with no
//     base-damage system. Shields off so the ship's collision damage is observable on the hull.
// ================================================================================================
{
    var sim = BootSim(20);
    sim.ShieldsEnabled = false;
    var probeW = sim.Content.Weapons.First(w => w.WeaponId == 8);
    float hitR = probeW.ProbeHitRadius;
    float minD = hitR + CollisionConfig.ShipRadius; // ship-center distance at the probe's surface

    var layer = Join(sim, 1, 0, FlightModel.ClassFighter);
    layer.ProbeAmmo = 1;
    layer.ProbeWeaponId = probeW.WeaponId;

    // Deploy one probe (real fire path), facing +Z so it ejects behind toward −Z.
    Park(layer, EmptySector, new Vec3(0, 0, 0));
    layer.HeldInput = new ShipInputState { DropProbe = true };
    sim.Step();
    layer.HeldInput = new ShipInputState();
    Check(sim.Probes.Count == 1, "probe deployed (pre-condition)", $"found {sim.Probes.Count}");
    var probe = sim.Probes[0];
    Check(probe.Health > 0f, "the deployed probe is destructible (HP > 0, not invulnerable)", $"probe HP {probe.Health}");

    // Self-deploy must clear the deploying ship: let physics run (no Park) — no shove, no damage.
    float shipHp0 = layer.Health;
    Vec3 shipPos0 = layer.State.Pos;
    for (int i = 0; i < 3; i++) sim.Step();
    Check(layer.Health >= shipHp0 - 0.001f, "deploying a probe does not damage the deploying ship", $"ship lost {shipHp0 - layer.Health} HP on self-deploy");
    Check((layer.State.Pos - shipPos0).Length() < 1f, "deploying a probe does not shove the deploying ship", $"ship moved {(layer.State.Pos - shipPos0).Length():0.0}u on self-deploy");

    // Ram the probe head-on (+X side, moving −X) until the low-HP probe dies. Each contact must push
    // the ship out to the probe surface (no penetration) and dent both ship and probe.
    bool noPenetration = false, shipHurt = false, probeHurt = false, destroyed = false;
    for (int attempt = 0; attempt < 20 && !destroyed; attempt++)
    {
        layer.SectorId = probe.SectorId;
        layer.State.Pos = probe.Pos + new Vec3(minD * 0.85f, 0, 0); // start penetrating on +X
        layer.State.Vel = new Vec3(-40f, 0, 0); // slam inward (closing speed 40 >> min 4)
        float shipBefore = layer.Health;
        float probeBefore = probe.Health;
        sim.Step();

        if ((layer.State.Pos - probe.Pos).Length() >= minD - 0.5f) noPenetration = true;
        if (layer.Health < shipBefore - 0.001f) shipHurt = true;
        bool alive = sim.Probes.Any(p => p.ProbeId == probe.ProbeId);
        if (probe.Health < probeBefore - 0.001f) probeHurt = true;
        if (!alive)
        {
            destroyed = sim.ProbeGoneThisStep.Any(g => g.id == probe.ProbeId && g.reason == 2);
            break;
        }
    }
    Check(noPenetration, "a rammed ship is pushed out to the probe surface (solid — no penetration)", "the ship penetrated the probe without a bounce");
    Check(shipHurt, "ramming a probe damages the ship (collision damage, like a base)", "the ramming ship took no collision damage");
    Check(probeHurt, "ramming a probe damages the probe", "the rammed probe took no damage from the ram");
    Check(destroyed, "enough ramming destroys the low-HP probe (gone reason 2, no base-damage system)", "the probe never died from ramming");
}

// ---- Per-sector environment: map parse, dust-cloud seeding determinism, dust radar attenuation ----

// (env-1) The map `environment:` block round-trips through MapLoader → WorldConfig.Sectors[].Env, and
// a sector with no block projects to a null Env.
{
    string dir = Path.Combine(Path.GetTempPath(), "envmap_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    File.WriteAllText(
        Path.Combine(dir, "envmap.yaml"),
        "name: EnvMap\n"
            + "sectors:\n"
            + "  - id: 0\n"
            + "    radius: 3000\n"
            + "    garrison: { team: 0 }\n"
            + "    asteroids: belt\n"
            + "    map-pos: [-1.0, 0.5]\n"
            + "    environment:\n"
            + "      sun:\n"
            + "        azimuth: 30\n"
            + "        elevation: 15\n"
            + "        color: [1.0, 0.8, 0.6]\n"
            + "        ambient: 0.7\n"
            + "        god-rays: 0.5\n"
            + "      nebula:\n"
            + "        color-a: [0.4, 0.2, 0.6]\n"
            + "        intensity: 0.09\n"
            + "      dust:\n"
            + "        amount: 0.4\n"
            + "        color: [0.3, 0.2, 0.1]\n"
            + "  - id: 1\n"
            + "    radius: null\n"
            + "    garrison: { team: 1 }\n"
    );
    var maps = MapLoader.LoadAvailable(dir, null);
    var map = MapLoader.Resolve(maps, "EnvMap");
    var wc = new WorldConfig();
    MapLoader.ApplyTo(map, wc);
    var s0 = wc.Sectors.First(s => s.Id == 0);
    var s1 = wc.Sectors.First(s => s.Id == 1);
    Check(
        s0.Env?.Sun is { GodRays: 0.5f, Azimuth: 30f },
        "map env: sun god-rays + azimuth round-trip through ApplyTo",
        "sun env did not round-trip"
    );
    Check(
        s0.Env?.Sun is { Ambient: 0.7f },
        "map env: sun ambient round-trips through ApplyTo",
        "sun ambient did not round-trip"
    );
    Check(
        s0.Env?.Dust is { } d && MathF.Abs(d.Amount - 0.4f) < 1e-4f,
        "map env: dust amount round-trips through ApplyTo",
        "dust amount did not round-trip"
    );
    Check(
        s0.Asteroids == AsteroidKind.Belt && s1.Asteroids == AsteroidKind.Field,
        "map: asteroid kind (belt/field default) round-trips",
        "asteroid kind did not round-trip"
    );
    Check(
        s0.Garrison?.Team == 0 && s1.Garrison?.Team == 1,
        "map: garrisons round-trip (team per sector)",
        "garrison round-trip failed"
    );
    Check(
        s0.MapPosX is { } mx && MathF.Abs(mx + 1.0f) < 1e-4f && s0.MapPosY.HasValue,
        "map: map-pos round-trips through ApplyTo",
        "map-pos did not round-trip"
    );
    Check(
        s0.Env?.Nebula?.ColorA is { } ca && MathF.Abs(ca.X - 0.4f) < 1e-4f,
        "map env: nebula color-a round-trip",
        "nebula env did not round-trip"
    );
    Check(
        s1.Env == null,
        "a sector with no environment block projects to a null Env",
        "an omitted environment block did not yield a null Env"
    );
    Directory.Delete(dir, true);
}

// (env-2) Dust clouds are deterministic for a fixed world seed, AND authoring dust leaves the asteroid
// field byte-identical (dust runs on its own RNG stream — it must never perturb rock/aleph placement).
{
    var content = ContentLoader.Load(stockPath, worldPath);
    float mh = content.Bases[0].MaxHealth;

    WorldConfig DustCfg() =>
        new()
        {
            SectorScale = 1f,
            AsteroidDensity = 1f,
            Sectors = new List<WorldSectorConfig>
            {
                new()
                {
                    Id = 0,
                    Env = new SectorEnvironment
                    {
                        Dust = new SectorDust { Amount = 0.6f },
                    },
                },
                new() { Id = 1 },
            },
        };

    var wA = new World(12345, DustCfg(), mh, content.Start, content.Ships);
    var wB = new World(12345, DustCfg(), mh, content.Start, content.Ships);
    bool cloudsMatch =
        wA.DustClouds.Count == wB.DustClouds.Count
        && wA.DustClouds.Count > 0
        && wA.DustClouds.Zip(wB.DustClouds)
            .All(p =>
                p.First.SectorId == p.Second.SectorId
                && p.First.Pos.X == p.Second.Pos.X
                && p.First.Pos.Y == p.Second.Pos.Y
                && p.First.Pos.Z == p.Second.Pos.Z
                && p.First.Radius == p.Second.Radius
            );
    Check(cloudsMatch, "dust clouds are deterministic for a fixed world seed", "dust clouds differed across two same-seed Worlds");

    var noDust = new World(
        12345,
        new WorldConfig
        {
            SectorScale = 1f,
            AsteroidDensity = 1f,
            Sectors = new List<WorldSectorConfig> { new() { Id = 0 }, new() { Id = 1 } },
        },
        mh,
        content.Start,
        content.Ships
    );
    bool rocksUnchanged =
        wA.Asteroids.Count == noDust.Asteroids.Count
        && wA.Asteroids.Count > 0
        && wA.Asteroids.Zip(noDust.Asteroids)
            .All(p =>
                p.First.Pos.X == p.Second.Pos.X
                && p.First.Pos.Y == p.Second.Pos.Y
                && p.First.Pos.Z == p.Second.Pos.Z
                && p.First.Radius == p.Second.Radius
            );
    Check(rocksUnchanged, "authoring dust leaves the asteroid field byte-identical (separate RNG stream)", "dust seeding perturbed the asteroid field");
    Check(noDust.DustClouds.Count == 0, "a world with no dust config seeds zero dust clouds", "a no-dust world produced dust clouds");
}

// (seed) The whole static layout is seed-driven. Two Worlds built with the SAME seed are byte-identical
// in base/rock/aleph placement (so --seed / SIM_SEED reproduces an exact arena for tests / benchmarks /
// bug repro), while two built with DIFFERENT seeds place them differently (so the per-match reroll in
// Program.cs actually reshuffles every match — players must re-scout). Uses the live world config, which
// populates all three static kinds via its default arena/ring.
{
    var content = ContentLoader.Load(stockPath, worldPath);
    float mh = content.Bases[0].MaxHealth;

    World Build(ulong s) => new World(s, content.World, mh, content.Start, content.Ships);

    // Compare only the seeded positions of the three static kinds (counts + XYZ), which is all the
    // reroll touches — GLB hulls / grids are identical across seeds.
    bool SamePositions(World a, World b) =>
        a.Bases.Count == b.Bases.Count
        && a.Asteroids.Count == b.Asteroids.Count
        && a.Alephs.Count == b.Alephs.Count
        && a.Bases.Zip(b.Bases).All(p =>
            p.First.Pos.X == p.Second.Pos.X && p.First.Pos.Y == p.Second.Pos.Y && p.First.Pos.Z == p.Second.Pos.Z)
        && a.Asteroids.Zip(b.Asteroids).All(p =>
            p.First.Pos.X == p.Second.Pos.X && p.First.Pos.Y == p.Second.Pos.Y && p.First.Pos.Z == p.Second.Pos.Z)
        && a.Alephs.Zip(b.Alephs).All(p =>
            p.First.Pos.X == p.Second.Pos.X && p.First.Pos.Y == p.Second.Pos.Y && p.First.Pos.Z == p.Second.Pos.Z);

    var same1 = Build(777);
    var same2 = Build(777);
    Check(
        same1.Bases.Count > 0 && same1.Asteroids.Count > 0 && same1.Alephs.Count > 0 && SamePositions(same1, same2),
        "same seed → identical base/rock/aleph layout (a pinned --seed reproduces an exact arena)",
        "two same-seed Worlds produced different static layouts"
    );

    var diff = Build(778);
    Check(
        !SamePositions(same1, diff),
        "different seed → different base/rock/aleph layout (per-match reroll reshuffles the arena)",
        "two different-seed Worlds produced an identical static layout"
    );
}

// (env-3) A dust cloud on the sightline shrinks radar range: an enemy detected in clear air at 0.6× the
// sphere radius drops OFF radar once a full-density cloud sits between viewer and target. Geometry is
// sized from the defs so it holds regardless of tuning; base vision + rocks are removed to isolate dust.
{
    var probe = ContentLoader.Load(stockPath, worldPath);
    var fighter = probe.Ships.First(d => d.ClassId == FlightModel.ClassFighter);
    float baseR = fighter.VisionSphereRadius * EffSig(probe, FlightModel.ClassFighter); // effective clear-air sphere
    float cloudR = baseR * 0.3f; // viewer & target sit on opposite edges → D = 2·cloudR = 0.6·baseR

    // Dust is a "feel" knob now: amount drives BOTH coverage and the radar/vision floor, all relative
    // to sector size. Size sector 0 to baseR so its dust fills a ~0.9·baseR disc around the origin;
    // viewer & target then straddle that dusty center. amount 0 = clear air (no dust); high amount =
    // thick dust that should attenuate the sightline.
    Simulation MkDustSim(float amount, float opacity, out World w)
    {
        var c = ContentLoader.Load(stockPath, worldPath);
        c.World.AsteroidDensity = 0f; // no rocks — isolate dust from rock occlusion
        c.World.SectorScale = 1f;
        c.World.SectorRadius = baseR; // sector 0 (no explicit radius) → radius baseR, dust ~0.9·baseR
        foreach (var bd in c.Bases)
            bd.VisionSphereRadius = 0f; // silence the home base so only the ship viewer detects
        c.World.Sectors = new List<WorldSectorConfig>
        {
            new()
            {
                Id = 0,
                Env = amount > 0f
                    ? new SectorEnvironment { Dust = new SectorDust { Amount = amount, Opacity = opacity } }
                    : null,
            },
            new() { Id = 1 },
        };
        w = new World(77, c.World, c.Bases[0].MaxHealth, c.Start, c.Ships);
        var s = new Simulation(w, c);
        s.PigsEnabled = false;
        s.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
        s.FogEnabled = true;
        s.VisionSynchronous = true;
        s.StartMatch();
        return s;
    }

    bool RadarSeesAcrossDust(float amount, float opacity = 1f)
    {
        var sim = MkDustSim(amount, opacity, out var w);
        // Straddle the dusty sector center along +X (perpendicular to the viewer's +Z forward) so the
        // omnidirectional sphere — not the cone — is under test; the segment passes through the dust.
        var vpos = new Vec3(-cloudR, 0f, 0f);
        var tpos = new Vec3(cloudR, 0f, 0f);
        var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);
        var target = Join(sim, 2, 1, FlightModel.ClassFighter);
        Run(
            sim,
            () =>
            {
                Park(viewer, 0, vpos);
                Park(target, 0, tpos);
            },
            Settle
        );
        return Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId);
    }

    Check(RadarSeesAcrossDust(0f), "clear air: the enemy at 0.6× sphere range is on radar", "baseline radar detection failed (env-3 geometry is off)");
    Check(!RadarSeesAcrossDust(0.9f), "thick dust on the sightline drops the enemy off radar (range attenuated)", "dust did NOT attenuate radar — the enemy was still detected through the cloud");
    // Opacity decouples radar impact from the VISUAL amount: the SAME thick dust with opacity 0 leaves
    // radar untouched (floor forced back to 1), so the enemy stays detected through the visually-dense cloud.
    Check(RadarSeesAcrossDust(0.9f, 0f), "thick dust with opacity 0 does NOT attenuate radar (radar impact decoupled from visual amount)", "opacity 0 still cut radar range — the opacity knob is not scaling the vision floor");
}

// (env-4) A Welcome built from a world with a FULL environment (sun + nebula + dust clouds) round-trips
// byte-exact through the env-aware WelcomeCounts parser — exercising every non-zero presence branch of
// Protocol.WriteSectorEnv and proving the appended payload keeps the frame self-consistent.
{
    var content = ContentLoader.Load(stockPath, worldPath);
    var cfg = new WorldConfig
    {
        SectorScale = 1f,
        AsteroidDensity = 0.2f,
        Sectors = new List<WorldSectorConfig>
        {
            new()
            {
                Id = 0,
                Env = new SectorEnvironment
                {
                    Sun = new SectorSun { Azimuth = 30f, Elevation = 15f, Color = new Vec3(1f, 0.8f, 0.6f), Energy = 1.3f, GodRays = 0.5f },
                    Nebula = new SectorNebula { ColorA = new Vec3(0.4f, 0.2f, 0.6f), Intensity = 0.09f, Seed = 42u },
                    Dust = new SectorDust { Amount = 0.6f, Color = new Vec3(0.4f, 0.4f, 0.5f) },
                },
            },
            new() { Id = 1 },
        },
    };
    var w = new World(9, cfg, content.Bases[0].MaxHealth, content.Start, content.Ships);
    byte[] frame = Protocol.BuildWelcome(1, 0, w, 0, Array.Empty<byte>(), fog: false);
    var (ns, _, _, _) = WelcomeCounts(frame); // throws if the appended env desyncs the frame length
    Check(ns == 2 && w.DustClouds.Count > 0, "a full-environment Welcome (sun+nebula+dust) round-trips byte-exact", "the environment payload desynced the Welcome frame");
}

// ================================================================================================
// 21. Signature pipeline in the LIVE sim (dynamic-signature WP): afterburner makes a ship visible
//     farther, an equipped shield does too, hiding inside a dust cloud makes it quieter (asymmetric
//     — the hidden ship still sees OUT), and the per-ship SigBias seam shifts detection live. Each
//     case guards on its knob actually being authored non-neutral (retuning to 1.0 skips, not fails).
// ================================================================================================

// (21a) Afterburner: a fighter parked between its coasting reach and its full-boost reach is off
// radar while coasting and becomes a contact under a held afterburner.
{
    var sim = BootSim(210);
    float boostMult = sim.Content.World.BoostSignatureMult;
    if (boostMult <= 1.05f)
        Console.WriteLine("SKIP: boost-signature-mult is neutral — afterburner signature test skipped");
    else
    {
        var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);
        var target = Join(sim, 2, 1, FlightModel.ClassFighter);
        float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius;
        float eff = EffSig(sim.Content, FlightModel.ClassFighter);
        // Mid between the at-rest radar reach and the full-afterburner reach, placed +X
        // (perpendicular to forward) so the cone never applies. Radar tier only — the eyeball
        // band may stream the mesh either way, VisibleEnemyShips is what's asserted.
        float dist = sphere * eff * (1f + boostMult) / 2f;
        Vec3 spot = new Vec3(dist, 0, 0);

        Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, spot); }, Settle);
        Check(!Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
            "a COASTING ship beyond its at-rest reach is not a radar contact", "the coasting ship was already detected (boost geometry is off)");

        // Hold the afterburner: AbPower ramps to 1 (fuel is full from spawn), the capture reads it
        // live, and the boosted signature lands at the next applies.
        target.HeldInput = new ShipInputState { Boost = true };
        Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, spot); }, Settle);
        Check(Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
            "the SAME ship under full afterburner is picked up farther (boost-signature-mult)", "a boosting ship was not detected inside its boosted reach");
        target.HeldInput = new ShipInputState();
    }
}

// (21b) Equipped shield: at a range between the bare-hull reach and the shielded reach, the stock
// (shield-fitted) fighter is detected; stripping the shield from the loaded def (capacity 0 =
// nothing equipped) drops the same geometry off radar. Pool level is irrelevant by design.
{
    float shieldMult, sphere, effShielded, effBare;
    {
        var probe = BootSim(211);
        shieldMult = probe.Content.World.ShieldSignatureMult;
        sphere = Def(probe, FlightModel.ClassFighter).VisionSphereRadius;
        effShielded = EffSig(probe.Content, FlightModel.ClassFighter);
        var d = Def(probe, FlightModel.ClassFighter);
        effBare = SignatureModel.Compute(
            new SignatureInputs(d.RadarSignature, d.SignatureBias, 0, 0, 0, 0f, false, 0f), Knobs(probe.Content));
    }
    if (shieldMult <= 1.02f)
        Console.WriteLine("SKIP: shield-signature-mult is neutral — shield signature test skipped");
    else
    {
        float dist = sphere * (effBare + effShielded) / 2f;
        bool DetectedAt(bool stripShield)
        {
            var sim = BootSim(211);
            if (stripShield)
            {
                // Simulation.ShipDefs shares these def instances, so zeroing capacity BEFORE any
                // spawn makes the hull genuinely shieldless (equipment AND pool).
                var fd = sim.Content.Ships.First(x => x.ClassId == FlightModel.ClassFighter);
                fd.ShieldCapacity = 0f;
                fd.ShieldRecharge = 0f;
            }
            var v = Join(sim, 1, 0, FlightModel.ClassFighter);
            var t = Join(sim, 2, 1, FlightModel.ClassFighter);
            Run(sim, () => { Park(v, EmptySector, new Vec3(0, 0, 0)); Park(t, EmptySector, new Vec3(dist, 0, 0)); }, Settle);
            return Vision(sim, 0).VisibleEnemyShips.Contains(t.ShipId);
        }

        Check(DetectedAt(stripShield: false),
            "a shield-EQUIPPED hull is detected between the bare and shielded reaches (shield-signature-mult)", "the shielded fighter was not detected inside its shielded reach");
        Check(!DetectedAt(stripShield: true),
            "the identical hull with the shield stripped is NOT detected at the same range", "the bare fighter was still detected — the shield term leaked");
    }
}

// (21c) Dust cover is asymmetric: one hand-placed cloud (the seeded ones are cleared for exact
// geometry), one ship parked at its center, one in clear space. The sightline — hence the dust
// RANGE attenuation — is identical both ways, so at a range between the two reaches the buried
// ship sees OUT while remaining unseen itself: dust-signature-mult quiets targets, not viewers.
{
    var c = ContentLoader.Load(stockPath, worldPath);
    float dustMult = c.World.DustSignatureMult;
    if (dustMult >= 0.95f)
        Console.WriteLine("SKIP: dust-signature-mult is neutral — dust signature test skipped");
    else
    {
        const float amount = 0.9f, opacity = 1f;
        c.World.AsteroidDensity = 0f; // no rocks — isolate dust from occlusion (env-3 idiom)
        c.World.SectorScale = 1f;
        foreach (var bd in c.Bases)
            bd.VisionSphereRadius = 0f; // silence base vision so only the two ships classify
        c.World.Sectors = new List<WorldSectorConfig>
        {
            new() { Id = 0, Env = new SectorEnvironment { Dust = new SectorDust { Amount = amount, Opacity = opacity } } },
            new() { Id = 1 },
        };
        var w = new World(2100, c.World, c.Bases[0].MaxHealth, c.Start, c.Ships);
        const float cloudR = 150f;
        w.DustClouds.Clear(); // replace the procedural clouds with ONE exactly-known cloud
        w.DustClouds.Add(new World.DustCloud(0, new Vec3(0, 0, 0), cloudR, 1f)); // full density
        var sim = new Simulation(w, c);
        sim.PigsEnabled = false;
        sim.MinersEnabled = false; // isolate from the auto-seeded team miner (mirrors PigsEnabled)
        sim.FogEnabled = true;
        sim.VisionSynchronous = true;
        sim.StartMatch();

        var buried = Join(sim, 1, 0, FlightModel.ClassFighter); // parks at the cloud center
        var clear = Join(sim, 2, 1, FlightModel.ClassFighter); // parks in clear space at `dist`
        float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius;
        float eff = EffSig(sim.Content, FlightModel.ClassFighter);
        // Sightline attenuation is symmetric: the center→outside segment crosses half the cloud
        // (chord = cloudR), so τ = density·cloudR/(2·cloudR) = 0.5 and the range multiplier is
        // s = 1 − τ·(1 − floor) for the sector's authored floor. Seeing OUT reaches sphere·eff·s;
        // seeing IN reaches only sphere·eff·s·dustMult (the buried target is also quieter).
        float floor = World.DustVisionFloor(amount, opacity);
        float s = 1f - 0.5f * (1f - floor);
        float dist = sphere * eff * s * (dustMult + 1f) / 2f; // between the two reaches, outside the cloud
        Check(dist > cloudR, "the test range clears the cloud (geometry pre-condition)", $"dist {dist:F0} inside the cloud — retune the test geometry");

        Run(sim, () => { Park(buried, 0, new Vec3(0, 0, 0)); Park(clear, 0, new Vec3(dist, 0, 0)); }, Settle);
        Check(Vision(sim, 0).VisibleEnemyShips.Contains(clear.ShipId),
            "the ship buried in the cloud still sees OUT to the clear-space ship", "the buried ship failed to see out of the cloud");
        Check(!Vision(sim, 1).VisibleEnemyShips.Contains(buried.ShipId),
            "at the SAME range along the SAME sightline, the ship buried in dust stays hidden (dust-signature-mult)", "the buried ship was detected — the dust signature term is not applying");
    }
}

// (21d) SigBias is the live equipment/ability seam: a ship parked just outside its at-rest radar
// reach becomes a contact when its per-ship bias is raised at runtime (no def or respawn involved).
{
    var sim = BootSim(213);
    var viewer = Join(sim, 1, 0, FlightModel.ClassFighter);
    var target = Join(sim, 2, 1, FlightModel.ClassFighter);
    float sphere = Def(sim, FlightModel.ClassFighter).VisionSphereRadius;
    float eff = EffSig(sim.Content, FlightModel.ClassFighter);
    float dist = sphere * eff * 1.2f; // outside the at-rest radar reach, +X (out of cone)
    Vec3 spot = new Vec3(dist, 0, 0);

    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, spot); }, Settle);
    Check(!Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
        "at stock bias the ship outside its at-rest reach is not a contact", "the pre-bias ship was already detected");

    target.SigBias += Def(sim, FlightModel.ClassFighter).RadarSignature; // double the effective base, live
    Run(sim, () => { Park(viewer, EmptySector, new Vec3(0, 0, 0)); Park(target, EmptySector, spot); }, Settle);
    Check(Vision(sim, 0).VisibleEnemyShips.Contains(target.ShipId),
        "raising the ship's SigBias at runtime pulls it onto radar (the live loadout/ability seam)", "the biased ship was not detected — SigBias is not reaching the capture");
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
