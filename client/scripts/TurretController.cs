using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// The GUNNER's half of a crewed ship (v42 crews slice 2): free mouse aim from a teammate's turret
// station and the trigger that fires it. The pilot's ShipController equivalent — same 20 Hz sample
// cadence, same send-on-change + keepalive, same local fire prediction — but it steers an ORIENTATION,
// not a ship, and the server clamps that orientation into the station's arc.
//
// FREE LOOK, no gimbal (user steer 2026-09-13: the gun camera "should just carry through as if there
// were no difference at all until it reaches the other side"). The mouse turns a ship-local look
// basis about its OWN axes — yaw about its up, pitch about its right — exactly the way a pilot's nose
// turns about the hull. There is no azimuth and no elevation limit: look up past the station's zenith
// and the view carries over the top and continues down the far side, the up going with it, until the
// firing ARC stops it. All of that lives in the Godot-free TurretLook.
//
// The gun IS the look, and the CLIENT owns it (user steer 2026-09-13: a gun that traversed toward the
// sight under its own slew/accel was "very laggy and difficult to control"). There is exactly ONE aim —
// the look basis' forward — and it goes on the wire as the real thing; the server only fences it into
// the station's arc. The single piece of turret physics left is the SLEW limit in SampleAim, which
// holds the MOUSE's own SUSTAINED turn rate to the station's authored TurretSlewRad (user steer, same
// day: "let's not let the camera look faster than the aim") through TurretAim.SlewLimit's token bucket
// — small motions are never limited on any mount (user steer 2026-09-19); it lives here, on the
// client, and nothing anywhere re-derives the aim from it. The CAMERA is that same look: it moves with the mouse, the ship's pose and
// the arc fence and with nothing else (user steer 2026-09-13: the turret cam is a free look,
// "constrained only by its position, the ship's orientation and its arc fence, not other physics of the
// turret"). So the ONE reticle — the pilot's own aim reticle, drawn by TargetMarkers off the firing line
// (a gunner is a pilot who cannot steer, so they get the pilot's HUD) — sits on the centre of the view,
// which is exactly where the barrel and the predicted bolts point.
//
// Three mirrors of TurretAim must agree or the gunner shoots somewhere nobody else sees
// (server TryFireTurrets / this / ShipRenderer.ApplyTurrets) — the arc/rest maths therefore lives in
// shared TurretAim, never here. Turret input is HELD input, latest wins: the server re-fires the
// last aim+trigger on its own cadence, so a dropped frame costs nothing.
//
// Mouse capture is mostly NOT owned here: ShipController's _Input is the one place the cursor is
// released, Esc-gated and click-recaptured, and it treats "riding with a seat" exactly like
// "flying". This node only takes the cursor on the activation edge (a seat is the ride's launch) and
// hands it back when the ride ends with no hull to fly.
public partial class TurretController : Node
{
    private const int TargetLead = 3; // ticks ahead of authority, like ShipController's default
    private const float SlewGain = 0.08f;
    private const float MaxSlew = 0.30f;
    private const int MaxStepsPerFrame = 5;
    private const uint KeepaliveTicks = 10; // ~0.5 s — the server holds the last aim between sends
    private const float AimEpsilon = 1e-3f; // smallest aim move worth a frame on the wire

    // Read by CameraRig (the gun cam), the HUD subject seam and the barrel view, in the cross-system
    // static idiom (ZoomView.Active / SectorOverview.Active).
    public static bool Active { get; private set; }

    // True while the gunner is pushing the look past the station's firing arc — the mount cannot go
    // there, so the aim reticle warns rather than letting the input silently vanish.
    public static bool Clamped { get; private set; }

    // The live ship-local aim — where the look points, where the gun points and therefore where the
    // bolts go — and the station's zenith (its outward normal). `Aim` and `DesiredAim` are now the SAME
    // vector (there is no second, lagging aim any more); both statics are kept so every existing reader
    // compiles and reads the one truth. Both are in the captain's hull frame; the camera and the HUD
    // rotate them into world space through the ridden node's pose.
    public static Vector3 Aim { get; private set; } = Vector3.Forward;
    public static Vector3 DesiredAim { get; private set; } = Vector3.Forward;
    public static Vector3 Zenith { get; private set; } = Vector3.Up;

    // The gun cam's full ship-local basis: Z = the look (which IS the aim), Y = the gunner's up,
    // X = Y × Z — the TurretLook basis verbatim, carried frame to frame so the horizon is one
    // continuous thing all the way over the zenith, never a projection re-derived per frame (which is
    // what used to spin the view at the pole).
    public static Basis CamBasis { get; private set; } = Basis.Identity;

    // The station's ship-local mount offset — where the gun cam sits and where the bolts leave.
    public static Vector3 Station { get; private set; } = Vector3.Zero;

    // This station's own fire cadence, in the same prediction-tick space the pilot's predictor uses,
    // so the weapons panel can draw the gunner's CYCLE bar off the gate the server will actually apply.
    public static uint LastFireTick { get; private set; }
    public static uint PredTick { get; private set; }

    // The station we man — hardpoint geometry, the gun it fires (null when the seat's weapon isn't a
    // bolt weapon) and the ridden ship id — or null when we aren't seated. One resolution, shared with
    // TargetMarkers so the gunner's firing solution reads exactly the gun this controller predicts.
    public static (HardpointDef Hp, WeaponDef? Gun, ulong ShipId)? Seat { get; private set; }

    // Where the gunner's aim reticle is ranged: the seat gun's effective reach (its bolts die there),
    // or a plain anchor distance for a station whose gun hasn't streamed. Single source for the HUD
    // reticle, the system ring's centre and the Tab-target ranking point (all via HudSubject).
    public const float DefaultAimRange = 500f;

    public static float AimRange { get; private set; } = DefaultAimRange;

    // --crew-demo harness: while set, the look sweeps on its own and the trigger is held, so a
    // scripted gunner proves the aim/fire round trip without a hand on the mouse. Describe() is what
    // the harness prints beside each shot.
    public static bool DemoDrive;

    // --turret-test harness (scripts/turret-test.ps1). StatsEnabled turns on the once-a-second
    // [turret-stats] log line + StatsLine (the on-screen readout Hud draws); InjectMouse feeds
    // scripted cursor motion through the SAME path a real InputEventMouseMotion takes, so the auto
    // role measures the real mapping. Response is the running requested/applied angle since the last
    // ResetResponse — the auto role's linearity probe.
    public static bool StatsEnabled;
    public static string StatsLine { get; private set; } = "";
    private static Vector2 _injected;

    public static void InjectMouse(Vector2 pixels)
    {
        if (OS.IsDebugBuild()) // scripted input is a harness seam, never live in an exported build
            _injected += pixels;
    }

    public static (float WantDeg, float GotDeg) Response { get; private set; }

    public static void ResetResponse() => Response = (0f, 0f);

    private double _statT;
    private float _statPx,
        _statWant,
        _statGot,
        _statDropped;
    private int _statFrames,
        _statLimited;

    private int _sentFrames,
        _predictedShots;

    public static string Describe(TurretController? tc)
    {
        if (tc is null)
            return "no controller";
        Vector3 up = CamBasis.Y;
        return $"active={Active} clamped={Clamped} aim=({Aim.X:0.00},{Aim.Y:0.00},{Aim.Z:0.00}) want=({DesiredAim.X:0.00},{DesiredAim.Y:0.00},{DesiredAim.Z:0.00}) up=({up.X:0.00},{up.Y:0.00},{up.Z:0.00}) firing={tc._firing} sent={tc._sentFrames} predicted={tc._predictedShots}";
    }

    private WorldRenderer _world = null!;
    private GameNetClient? _net;
    private DefRegistry? _defs;

    // The free-look basis the mouse turns (TurretLook): its Z IS the aim. Carried frame to frame and
    // NEVER re-derived from the aim — carrying it is exactly what makes the horizon survive a pass over
    // the zenith. Seeded from the shared rest pose on every (re)activation.
    private TurretLook _look = TurretLook.Seed(new Vec3(0f, 1f, 0f));

    // Which seat the look belongs to, so a seat change (or a relaunch of the same captain) re-seeds
    // instead of carrying the previous station's orientation onto a differently-oriented mount.
    private ulong _seatShipId;
    private byte _seatIndex;
    private bool _seated;

    private Vector2 _mouseDelta; // captured-cursor motion since the last frame
    private float _mouseSens = DefaultMouseSens;
    private bool _mouseInvert;
    private bool _sensFromEnv,
        _invertFromEnv;

    // Same px→deflection gain the pilot's virtual stick uses; TurretStations turns it into radians so
    // one sensitivity setting drives both seats.
    private const float DefaultMouseSens = 0.01f;

    // The slew token bucket's carried allowance (TurretAim.SlewLimit), refilled on taking a seat.
    private float _slewBudget;

    // Feel-tuning overrides (env, harness only, ignored outside a debug build): TURRET_GAIN = rad per stick unit, TURRET_SLEW_DEG =
    // every station's slew (0 = uncapped), TURRET_SLEW_WINDOW = the bucket window in seconds.
    private float _radPerStick = TurretStations.RadPerStickUnit;
    private float _slewOverrideRad = -1f;
    private float _slewWindow = TurretAim.SlewWindowSec;

    private double _acc;
    private uint _predTick;
    private uint _lastFire; // this station's own cadence stamp, in prediction-tick space
    private uint _lastSentTick;
    private Vector3 _lastSentAim = Vector3.Zero;
    private bool _lastSentFiring;
    private bool _firing;

    // Armed on the activation edge, cleared the moment the cursor is actually taken. The frame a ride
    // starts is also the frame the Hud frees the spawn hangar, and a QueueFree'd overlay still counts
    // as owning the screen until it leaves the tree — so the grab has to be retried, not fired once.
    private bool _wantCapture;

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _net = GetNodeOrNull<GameNetClient>("../GameNetClient");
        _defs = GetNodeOrNull<DefRegistry>("../DefRegistry");

        // The same testing overrides ShipController honours, so a harness pins one feel for both seats.
        if (float.TryParse(OS.GetEnvironment("STDB_MOUSE_SENS"), out var sens) && sens > 0f)
        {
            _mouseSens = sens;
            _sensFromEnv = true;
        }
        string invertEnv = OS.GetEnvironment("STDB_MOUSE_INVERT");
        if (!string.IsNullOrEmpty(invertEnv))
        {
            _mouseInvert = invertEnv is "1" or "true";
            _invertFromEnv = true;
        }
        // DEBUG BUILDS ONLY (editor / run-from-source, which is all scripts/turret-test.ps1 needs):
        // the slew limit is enforced on this client, so in an exported build TURRET_SLEW_DEG=0 would
        // be a one-line unlimited-traverse cheat. STDB_MOUSE_SENS above stays open — a preference,
        // not an advantage.
        if (OS.IsDebugBuild())
        {
            if (EnvFloat("TURRET_GAIN") is float gain && gain > 0f)
                _radPerStick = gain;
            if (EnvFloat("TURRET_SLEW_DEG") is float slewDeg && slewDeg >= 0f)
                _slewOverrideRad = Mathf.DegToRad(slewDeg);
            if (EnvFloat("TURRET_SLEW_WINDOW") is float window && window >= 0f)
                _slewWindow = window;
        }
        RefreshMousePrefs();
        UserPrefs.Changed += RefreshMousePrefs;
    }

    private static float? EnvFloat(string name) =>
        float.TryParse(
            OS.GetEnvironment(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v
        )
            ? v
            : null;

    public override void _ExitTree()
    {
        UserPrefs.Changed -= RefreshMousePrefs; // static event — would leak this node otherwise
        Active = false;
        Clamped = false;
        Seat = null;
    }

    private void RefreshMousePrefs()
    {
        if (!_sensFromEnv)
            _mouseSens = DefaultMouseSens * UserPrefs.MouseSensMultiplier;
        if (!_invertFromEnv)
            _mouseInvert = UserPrefs.MouseInvertY;
    }

    // Raw motion only while the cursor is captured — a visible cursor means a menu owns it, and a
    // gunner nudging a button must not swing the gun.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            _mouseDelta += mm.Relative;
    }

    public override void _Process(double delta)
    {
        if (ResolveSeat() is not { } seat)
        {
            Deactivate();
            return;
        }
        var (hp, gun, shipId) = seat;

        if (!_seated || _seatShipId != shipId || _seatIndex != hp.Index)
            Activate(shipId, hp);

        Zenith = new Vector3(hp.DirX, hp.DirY, hp.DirZ);
        Station = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
        Seat = seat;
        AimRange = GunRange(gun);
        var zenith = ShipMath.ToShared(Zenith);

        TakeCursor();
        SampleAim(zenith, _slewOverrideRad >= 0f ? _slewOverrideRad : hp.TurretSlewRad, (float)delta);

        // The look's forward IS the aim: where the mouse has just put the sight is where the gun points,
        // where the bolts leave and what goes on the wire. SampleAim has already capped this frame's
        // turn at the station's slew speed and fenced it into the arc, so there is nothing left to lag
        // behind (user steer 2026-09-13: the traversing gun was "very laggy and difficult to control").
        Aim = DesiredAim = ShipMath.ToGodot(_look.Z);

        // The camera IS that same look — the mouse, the ship's pose and the arc fence are the only
        // things that move it, and the reticle therefore sits on the centre of the view (user steer
        // 2026-09-13: a camera that rode a traversing gun felt "clunky"; nothing but the speed cap and
        // the fence may hold the view back).
        CamBasis = ToBasis(_look);

        // The gunner's own barrel follows the LIVE aim: ApplyTurrets deliberately ignores our seat's
        // echo, so this is the only thing that moves it.
        _world.Ships.SetLocalTurretAim(shipId, hp.Index, Aim);
        // Inside the turret the ridden hull is hidden (you are in the gun, not above the deck); the F3
        // overview un-hides it the same way it does the pilot's own hull.
        _world.Ships.SetRiddenHullHidden(CameraRig.TurretFirstPerson && !SectorOverview.Active);

        StepSend(shipId, hp, gun, delta);
    }

    // A TurretLook's three axes as a Godot basis (columns X/Y/Z) — the form CameraRig multiplies the
    // ridden hull's pose by. Both spaces are the same right-handed axes, so this is a re-type.
    private static Basis ToBasis(TurretLook l) =>
        new Basis(ShipMath.ToGodot(l.X), ShipMath.ToGodot(l.Y), ShipMath.ToGodot(l.Z));

    // A bolt weapon's effective reach — where its shots die, and therefore where the aim reticle is
    // ranged. Mirrors DefRegistry.BoltAimRange for a gun resolved per STATION rather than per hull.
    private static float GunRange(WeaponDef? gun)
    {
        if (gun is null)
            return DefaultAimRange;
        float r = gun.ProjectileSpeed * gun.ProjectileLifeTicks * FlightModel.Dt;
        return r > 1f ? r : DefaultAimRange;
    }

    // The station we man, or null when we aren't riding one (the common case). Resolves the gun the
    // same way every other turret seam does: the captain's crew-roster assignment, falling back to the
    // authored hardpoint gun; a station whose gun isn't a bolt weapon simply never predicts a shot.
    private (HardpointDef Hp, WeaponDef? Gun, ulong ShipId)? ResolveSeat()
    {
        if (_net is null || _defs is null || !_world.Ships.Riding || _world.Ships.RidingNode is null)
            return null;
        if (_world.Crew.SeatOf(_net.LocalClientId) is not { } seat || seat.ShipId != _world.Ships.RidingShipId)
            return null;
        if (TurretStations.Find(_defs.GetHardpoints(seat.ClassId), seat.SeatIndex) is not { } hp)
            return null;
        var gun = _defs.GetWeapon(seat.WeaponId) ?? _defs.GetWeapon(hp.WeaponId);
        return (hp, gun is { Kind: WeaponKind.Bolt } ? gun : null, seat.ShipId);
    }

    private void Activate(ulong shipId, HardpointDef hp)
    {
        _seated = true;
        _seatShipId = shipId;
        _seatIndex = hp.Index;
        Active = true;
        Clamped = false;

        var zenith = new Vec3(hp.DirX, hp.DirY, hp.DirZ);
        _look = TurretLook.Seed(zenith);
        // The aim starts ON the rest pose — carrying the previous station's aim would seat the gunner
        // looking wherever the last seat pointed.
        Aim = DesiredAim = ShipMath.ToGodot(_look.Z);
        CamBasis = ToBasis(_look);

        _predTick = PredTick = _world.ServerTick;
        _acc = 0;
        _lastFire = LastFireTick = 0;
        _lastSentTick = 0;
        _lastSentAim = Vector3.Zero; // force the first sample onto the wire
        _lastSentFiring = false;
        _mouseDelta = Vector2.Zero;
        _injected = Vector2.Zero;
        // A fresh seat starts with a full bucket: the first motion is never the limited one.
        _slewBudget = float.MaxValue; // SlewLimit clamps it to the station's capacity on first use

        // Taking a seat is the ride's launch: lock the cursor straight to the gun so the gunner aims
        // without a click first — the same courtesy ShipController's AnchorFreshShip does on spawn.
        _wantCapture = true;
    }

    private void Deactivate()
    {
        if (!_seated)
            return;
        _seated = false;
        _world.Ships.SetRiddenHullHidden(false);
        Active = false;
        Clamped = false;
        Seat = null; // the gunner's firing solution (TargetMarkers) gates on this
        _firing = false;
        _wantCapture = false;
        _mouseDelta = Vector2.Zero;
        StatsLine = "";
        // Hand the cursor back only when nothing else is about to want it: launching our own hull
        // (the usual way a ride ends) leaves flight holding the capture it just took — and so does
        // being PROMOTED to captain of the hull we were riding, where the YouAre ends the ride a frame
        // or two before the hull's snapshot makes LocalShip real (AwaitingLocalShip covers that gap;
        // releasing there would pop the cursor onto the screen just as the pilot takes the stick).
        if (
            _world.Ships.LocalShip == null
            && !_world.Ships.AwaitingLocalShip
            && Input.MouseMode == Input.MouseModeEnum.Captured
        )
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // Claim the cursor for the gun once nothing else owns the screen — never yanking it out from
    // under a modal, and never a second time (Esc frees it deliberately; a click recaptures through
    // ShipController's normal in-flight gate).
    private void TakeCursor()
    {
        if (!_wantCapture || !InputGate.FlightInputFree || Scoreboard.PostMatchActive)
            return;
        _wantCapture = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _mouseDelta = Vector2.Zero;
    }

    // Fold this frame's cursor motion into the look basis. Unlike the pilot's self-centering virtual
    // stick, the turret HOLDS where it was pointed — the mouse moves the gun, it doesn't deflect a
    // spring — so the delta turns the basis rather than being eased back to zero.
    private void SampleAim(Vec3 zenith, float slewRad, float dt)
    {
        Vector2 m = _mouseDelta + _injected;
        _mouseDelta = Vector2.Zero;
        _injected = Vector2.Zero;
        bool look = Input.MouseMode == Input.MouseModeEnum.Captured && InputGate.FlightInputFree;
        _firing =
            look
            && (Input.IsActionPressed("fire_primary") || Input.IsMouseButtonPressed(MouseButton.Left))
            && !Scoreboard.PostMatchActive;
        if (DemoDrive)
        {
            m += new Vector2(3f, -0.9f); // a slow sweep right and UP (over the top of the mount), every frame
            _firing = true;
            look = true;
        }
        if (!look)
            return;

        // Mouse-right sweeps the view right; mouse-UP looks up (first-person look, the convention
        // every gun cam uses) unless the invert-Y pref flips it. This deliberately differs from the
        // pilot's stick (mouse-down = nose up): the pilot pushes a spring that commands a turn RATE,
        // the gunner drags a sight — a direct look, so it follows the direct-look sign.
        //
        // Both turns are about the look's OWN axes, so there is no reference orientation to be spun
        // around and no elevation to run out of: pitching up past the zenith carries over the top and
        // keeps going down the far side. The ARC is the only limit, and it moves the whole basis.
        Vector2 md = m / ZoomView.Magnification;
        float yaw = -TurretStations.AimDeltaRad(md.X, _mouseSens, _radPerStick);
        float pitch = TurretStations.AimDeltaRad(_mouseInvert ? -md.Y : md.Y, _mouseSens, _radPerStick);

        // …but never SUSTAINED faster than the mount can traverse (user steer 2026-09-13: "let's not
        // let the camera look faster than the aim"). This limit is the WHOLE of a turret's physics: a
        // heavy mount reads as a heavy view, never as a reticle drifting off the centre. It is a token
        // bucket (TurretAim.SlewLimit), NOT a per-frame cap: a motion smaller than the bucket passes
        // 1:1 on every mount (user steer 2026-09-19: "don't limit small motions for any type of
        // turret"), and only a held spin is brought down to the slew rate. Motion past the allowance
        // is dropped, not banked: a banked turn would keep the view moving after the hand stopped. A
        // station with no authored traverse (slew 0) is unlimited.
        float turn = Mathf.Sqrt(yaw * yaw + pitch * pitch);
        float k = TurretAim.SlewLimit(ref _slewBudget, slewRad, dt, turn, _slewWindow);
        yaw *= k;
        pitch *= k;
        if (StatsEnabled)
            RecordStats(m.Length(), turn, turn * k, slewRad, dt);
        _look.Yaw(yaw);
        _look.Pitch(pitch);
        Clamped = _look.ClampToArc(zenith);
    }

    // --turret-test readout: what the hand asked for against what the mount gave, once a second.
    // "limited" is the share of frames the slew bucket scaled; a healthy feel keeps it near zero
    // outside a deliberate hard spin.
    private void RecordStats(float pixels, float wantRad, float gotRad, float slewRad, float dt)
    {
        Response = (Response.WantDeg + Mathf.RadToDeg(wantRad), Response.GotDeg + Mathf.RadToDeg(gotRad));
        _statT += dt;
        _statPx += pixels;
        _statWant += wantRad;
        _statGot += gotRad;
        _statDropped += wantRad - gotRad;
        _statFrames++;
        if (gotRad < wantRad - 1e-6f)
            _statLimited++;
        if (_statT < 1.0)
            return;
        float t = (float)_statT;
        float capacity = TurretAim.SlewCapacity(slewRad, dt, _slewWindow);
        float fill = slewRad > 0f && capacity > 0f ? Mathf.Clamp(_slewBudget / capacity, 0f, 1f) : 1f;
        StatsLine =
            $"hand {_statPx / t:0} px/s  want {Mathf.RadToDeg(_statWant) / t:0.0}°/s  got {Mathf.RadToDeg(_statGot) / t:0.0}°/s  "
            + $"limited {100f * _statLimited / Mathf.Max(_statFrames, 1):0}%  dropped {Mathf.RadToDeg(_statDropped):0.0}°  "
            + $"budget {fill:0.00}  slew {Mathf.RadToDeg(slewRad):0}°/s  gain {Mathf.RadToDeg(_mouseSens * _radPerStick):0.000}°/px  fps {_statFrames / t:0}";
        GD.Print($"[turret-stats] {StatsLine}");
        _statT = 0;
        _statPx = _statWant = _statGot = _statDropped = 0f;
        _statFrames = _statLimited = 0;
    }

    // The 20 Hz half: send the held aim/trigger on change (or on the keepalive) and predict our own
    // bolts, both in the same prediction-tick space the pilot's input uses so the cadence gate agrees
    // with the one the server will apply.
    private void StepSend(ulong shipId, HardpointDef hp, WeaponDef? gun, double delta)
    {
        int lead = (int)_predTick - (int)_world.ServerTick;
        float slew = Mathf.Clamp((TargetLead - lead) * SlewGain, -MaxSlew, MaxSlew);
        _acc += delta * (1f + slew);

        int budget = MaxStepsPerFrame;
        while (_acc >= FlightModel.Dt && budget > 0)
        {
            _acc -= FlightModel.Dt;
            budget--;
            _predTick++;
            PredTick = _predTick;

            // The wire carries the ACTUAL aim, and the client is authoritative over it: the server
            // stores what arrives and only arc-clamps it, so what the gunner sees and what the server
            // fires are the same vector with no second rule to drift out of step.
            if (
                _firing != _lastSentFiring
                || _lastSentAim.DistanceSquaredTo(Aim) > AimEpsilon * AimEpsilon
                || _predTick - _lastSentTick >= KeepaliveTicks
            )
            {
                _net?.SendTurretInput(_predTick, Aim, _firing);
                _sentFrames++;
                _lastSentAim = Aim;
                _lastSentFiring = _firing;
                _lastSentTick = _predTick;
            }

            if (_firing && gun is not null && FireCadence.MountFires(_predTick, _lastFire, gun.FireIntervalTicks))
            {
                _lastFire = LastFireTick = _predTick;
                _predictedShots++;
                PredictShot(shipId, hp, gun);
            }
        }
    }

    // Our own bolt, spawned from the ridden hull's RENDERED pose (what is actually on screen this
    // frame — the interpolated remote pose, the same reasoning PredictionController's muzzles use)
    // with the server's spread seed, so the shot we watch leave the barrel is the shot the server
    // resolves.
    private void PredictShot(ulong shipId, HardpointDef hp, WeaponDef gun)
    {
        if (_world.Ships.RidingNode is not { } ridden)
            return;
        Transform3D t = ridden.GlobalTransform;
        Vector3 fwdG = t.Basis * Aim;
        Vec3 shotDir = FlightModel.SpreadDirection(
            new Vec3(fwdG.X, fwdG.Y, fwdG.Z),
            gun.SpreadRad,
            shipId,
            _predTick,
            TurretAim.SpreadBarrel(hp.Index)
        );
        Vector3 dir = ShipMath.ToGodot(shotDir);
        // From the END of the barrel (pivot + aim × barrel length), not the pivot inside the mount —
        // the same start every other client's rebuild of this shot uses (BoltRenderer.SpawnTurretBolt).
        Vector3 muzzle =
            t.Origin
            + t.Basis * new Vector3(hp.OffX, hp.OffY, hp.OffZ)
            + fwdG.Normalized() * _world.Ships.TurretBarrelLength(shipId);
        Vector3 vel = dir * gun.ProjectileSpeed + ShipRenderer.ShipVelocityOf(ridden);
        _world.Bolts.SpawnLocalTurretBolt(
            muzzle,
            vel,
            dir,
            gun.ProjectileLifeTicks * FlightModel.Dt,
            gun.BoltRadius,
            gun.BoltLength,
            gun.IsHealing,
            shipId
        );
    }
}
