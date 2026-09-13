using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// The GUNNER's half of a crewed ship (v42 crews slice 2): free mouse aim from a teammate's turret
// station and the trigger that fires it. The pilot's ShipController equivalent — same 20 Hz sample
// cadence, same send-on-change + keepalive, same local fire prediction — but it steers an ANGLE, not
// a ship: there is no flight model between the mouse and the muzzle, so the gimbal is integrated
// straight into a ship-local aim vector and the server clamps it into the station's arc.
//
// Three mirrors of TurretAim must agree or the gunner shoots somewhere nobody else sees
// (server TryFireTurrets / this / ShipRenderer.ApplyTurrets) — the gimbal→aim maths therefore lives
// in shared TurretAim, never here. Turret input is HELD input, latest wins: the server re-fires the
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

    // Read by CameraRig (the gun cam), TurretReticle (the crosshair) and the barrel view, in the
    // cross-system static idiom (ZoomView.Active / SectorOverview.Active).
    public static bool Active { get; private set; }

    // True while the gunner is pushing the aim BELOW the station's horizon — the hull is in the way,
    // so the reticle warns rather than silently eating the input.
    public static bool Clamped { get; private set; }

    // The live ship-local aim and the station's zenith (its outward normal), both in the captain's
    // hull frame. The camera rotates them into world space through the ridden node's pose.
    public static Vector3 Aim { get; private set; } = Vector3.Forward;
    public static Vector3 Zenith { get; private set; } = Vector3.Up;

    // The station's ship-local mount offset — where the gun cam sits and where the bolts leave.
    public static Vector3 Station { get; private set; } = Vector3.Zero;

    // --crew-demo harness: while set, the gimbal sweeps on its own and the trigger is held, so a
    // scripted gunner proves the aim/fire round trip without a hand on the mouse. Describe() is what
    // the harness prints beside each shot.
    public static bool DemoDrive;
    private int _sentFrames,
        _predictedShots;

    public static string Describe(TurretController? tc) =>
        tc is null
            ? "no controller"
            : $"active={Active} clamped={Clamped} az={tc._azimuth:0.00} el={tc._elevation:0.00} aim=({Aim.X:0.00},{Aim.Y:0.00},{Aim.Z:0.00}) firing={tc._firing} sent={tc._sentFrames} predicted={tc._predictedShots}";

    private WorldRenderer _world = null!;
    private GameNetClient? _net;
    private DefRegistry? _defs;

    // Gimbal state in the station frame (TurretAim.Frame): azimuth turns about the zenith, elevation
    // lifts from the horizon toward it. Seeded from the shared rest pose on every (re)activation.
    private float _azimuth,
        _elevation;

    // Which seat the gimbal belongs to, so a seat change (or a relaunch of the same captain) re-seeds
    // instead of carrying the previous station's angles onto a differently-oriented mount.
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
        RefreshMousePrefs();
        UserPrefs.Changed += RefreshMousePrefs;
    }

    public override void _ExitTree()
    {
        UserPrefs.Changed -= RefreshMousePrefs; // static event — would leak this node otherwise
        Active = false;
        Clamped = false;
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
        var (shipId, hp, gun) = seat;

        if (!_seated || _seatShipId != shipId || _seatIndex != hp.Index)
            Activate(shipId, hp);

        Zenith = new Vector3(hp.DirX, hp.DirY, hp.DirZ);
        Station = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
        var zenith = new Vec3(Zenith.X, Zenith.Y, Zenith.Z);

        TakeCursor();
        SampleAim(zenith);
        Aim = ShipMath.ToGodot(TurretAim.FromGimbal(zenith, _azimuth, _elevation));

        // The gunner's own barrel follows the LIVE gimbal: ApplyTurrets deliberately ignores our
        // seat's echo, so this is the only thing that moves it.
        _world.Ships.SetLocalTurretAim(shipId, hp.Index, Aim);

        StepSend(shipId, hp, gun, delta);
    }

    // The station we man, or null when we aren't riding one (the common case). Resolves the gun the
    // same way every other turret seam does: the captain's crew-roster assignment, falling back to the
    // authored hardpoint gun; a station whose gun isn't a bolt weapon simply never predicts a shot.
    private (ulong ShipId, HardpointDef Hp, WeaponDef? Gun)? ResolveSeat()
    {
        if (_net is null || _defs is null || !_world.Ships.Riding || _world.Ships.RidingNode is null)
            return null;
        if (_world.Crew.SeatOf(_net.LocalClientId) is not { } seat || seat.ShipId != _world.Ships.RidingShipId)
            return null;
        if (TurretStations.Find(_defs.GetHardpoints(seat.ClassId), seat.SeatIndex) is not { } hp)
            return null;
        var gun = _defs.GetWeapon(seat.WeaponId) ?? _defs.GetWeapon(hp.WeaponId);
        return (seat.ShipId, hp, gun is { Kind: WeaponKind.Bolt } ? gun : null);
    }

    private void Activate(ulong shipId, HardpointDef hp)
    {
        _seated = true;
        _seatShipId = shipId;
        _seatIndex = hp.Index;
        Active = true;
        Clamped = false;

        var zenith = new Vec3(hp.DirX, hp.DirY, hp.DirZ);
        (_azimuth, _elevation) = TurretAim.ToGimbal(zenith, TurretAim.Rest(zenith));

        _predTick = _world.ServerTick;
        _acc = 0;
        _lastFire = 0;
        _lastSentTick = 0;
        _lastSentAim = Vector3.Zero; // force the first sample onto the wire
        _lastSentFiring = false;
        _mouseDelta = Vector2.Zero;

        // Taking a seat is the ride's launch: lock the cursor straight to the gun so the gunner aims
        // without a click first — the same courtesy ShipController's AnchorFreshShip does on spawn.
        _wantCapture = true;
    }

    private void Deactivate()
    {
        if (!_seated)
            return;
        _seated = false;
        Active = false;
        Clamped = false;
        _firing = false;
        _wantCapture = false;
        _mouseDelta = Vector2.Zero;
        // Hand the cursor back only when nothing else is about to want it: launching our own hull
        // (the usual way a ride ends) leaves flight holding the capture it just took.
        if (_world.Ships.LocalShip == null && Input.MouseMode == Input.MouseModeEnum.Captured)
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

    // Fold this frame's cursor motion into the gimbal. Unlike the pilot's self-centering virtual
    // stick, the turret HOLDS where it was pointed — the mouse moves the gun, it doesn't deflect a
    // spring — so the delta is integrated rather than eased back to zero.
    private void SampleAim(Vec3 zenith)
    {
        Vector2 m = _mouseDelta;
        _mouseDelta = Vector2.Zero;
        bool look = Input.MouseMode == Input.MouseModeEnum.Captured && InputGate.FlightInputFree;
        _firing =
            look
            && (Input.IsActionPressed("fire_primary") || Input.IsMouseButtonPressed(MouseButton.Left))
            && !Scoreboard.PostMatchActive;
        if (DemoDrive)
        {
            m += new Vector2(4f, -1.2f); // a slow sweep right and up, every frame
            _firing = true;
            look = true;
        }
        if (!look)
            return;

        // Mouse-right sweeps the aim right; mouse-UP raises the elevation (first-person look, the
        // convention every gun cam uses) unless the invert-Y pref flips it. This deliberately differs
        // from the pilot's stick (mouse-down = nose up): the pilot pushes a spring that commands a
        // turn RATE, the gunner drags a sight — a direct look, so it follows the direct-look sign.
        Vector2 md = m / ZoomView.Magnification;
        _azimuth -= TurretStations.AimDeltaRad(md.X, _mouseSens);
        _elevation += TurretStations.AimDeltaRad(_mouseInvert ? md.Y : -md.Y, _mouseSens);

        // Keep the azimuth in (−π, π] so a long sweep can't grind away float precision.
        _azimuth = Mathf.Wrap(_azimuth, -Mathf.Pi, Mathf.Pi);
        // The hemisphere IS the arc (TurretAim.ArcHalfAngleRad): below the horizon is the captain's
        // hull. Flag the low edge so the reticle warns; the zenith end is a pole, not a limit.
        Clamped = _elevation < 0f;
        _elevation = Mathf.Clamp(_elevation, 0f, TurretAim.ArcHalfAngleRad);
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
                _lastFire = _predTick;
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
        Vector3 muzzle = t.Origin + t.Basis * new Vector3(hp.OffX, hp.OffY, hp.OffZ);
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
