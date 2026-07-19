using Godot;
using StellarAllegiance.Ui;

// Chase / cockpit camera. A Camera3D rigidly attached to the local ship each frame (ships fly
// along local +Z, so the third-person "behind" shot sits at local −Z and looks ahead). The camera
// runs a two-mode state machine:
//   • THIRD PERSON — the chase shot, dollied in/out along the framing with the scroll wheel.
//   • FIRST PERSON — the pilot's eye, parked at the hull's cockpit hardpoint.
// The two framings share EXACTLY the same basis (the ship's orientation, faced forward), so
// switching modes is a purely positional dolly between the chase offset and the cockpit offset —
// a brief eased blend, never a hard cut, so the pilot keeps their bearings. First person is the
// default (persisted per player), toggled with V, and reachable by zooming past the closest chase
// shot. When there is no local ship the camera parks at a wide overview of the sector.
public partial class CameraRig : Camera3D
{
    private static readonly Vector3 ChaseOffset = new Vector3(0f, 2.5f, -10f); // ship-local
    private static readonly Vector3 OverviewPos = new Vector3(600f, 750f, 1600f);

    // Fallback cockpit eye offset (ship-local) for a hull whose model carries no HP_Cockpit_0
    // marker — so first person never breaks on custom server content. Slightly above and ahead of
    // the origin, matching the authored scout eye point.
    private static readonly Vector3 CockpitFallback = new Vector3(0f, 0.5f, 1f);

    // A Camera3D looks down its local -Z, but the ship flies along +Z, so rotate the ship's basis
    // 180° about its OWN up to aim the camera along the ship's forward. Inheriting the ship's full
    // orientation (its up included) is the whole point: there is no world "up" in space, so
    // referencing one (as LookAt does) makes the view flip when you pitch toward vertical.
    private static readonly Basis FaceForward = new Basis(Vector3.Up, Mathf.Pi);

    // Scroll-wheel zoom: multiply ChaseOffset to dolly the camera straight back along its current
    // framing. 1.0 is the tightest shot (the baseline offset above); the wheel only ever widens out
    // from there. Zooming IN past the tightest shot dives into first person instead (see HandleWheel).
    private const float MinZoom = 1f; // closest (default ChaseOffset)
    private const float MaxZoom = 24f; // widest pull-back
    private const float ZoomStep = 1.15f; // per wheel notch (matches SectorOverview feel)
    private float _zoom = MinZoom;

    // View-mode state. `_fpDesired` is the mode the player picked (persisted); `_blend` is the
    // animated position between chase (0) and cockpit (1). The two are decoupled so a mode change
    // mid-transition simply re-aims the blend toward the new target from wherever it currently sits.
    private const float TransitionSec = 0.3f; // edge-to-edge dolly time
    private bool _fpDesired;
    private float _blend; // 0 = third person, 1 = first person (linear param; eased when applied)

    // Launch cinematic: a fresh base spawn/respawn or pod-eject (flagged "Launched" on the ship node)
    // gets a brief external establishing shot parked ahead of the nose looking BACK at it, rigidly
    // tracking the ship, then eased into the player's chosen chase/cockpit framing — you watch the
    // ship punch out rather than blinking into your seat. A reconnect reclaim is NOT flagged.
    private const float LaunchCamHoldSec = 1.5f; // rigid external hold before the blend-out
    private const float LaunchAheadLengthMult = 4f; // camera ahead of the nose, in model lengths
    private const float LaunchRightWidthMult = 2f; // and off to the camera's right, in model widths
    private const float LaunchUpLengthMult = 0.5f; // mild vertical lift (art-tuning knob; ~ChaseOffset's ratio)
    private const float LaunchBlendOutSec = 0.6f; // softer/longer than the 0.3s mode-toggle dolly — a cinematic beat

    // Defensive fallbacks only — ShipModelLoader.Build always stashes the real extents. Length
    // reads ShipModelLoader.DefaultModelLength directly (single source, same CockpitFallback
    // philosophy: never break on odd content); width has no ShipModelLoader equivalent, so it
    // stays a local constant.
    private const float DefaultModelWidth = 3f;
    private float _launchCamT; // seconds remaining in the rigid hold; 0 = inactive
    private float _launchBlendT; // 0..1 progress of the blend-out into the normal framing
    private Transform3D? _launchFromPose; // frozen launch pose to blend FROM (null once blend done)
    private float _launchLen = ShipModelLoader.DefaultModelLength,
        _launchWidth = DefaultModelWidth;

    // True only once the dolly INTO first person has (nearly) finished — so the own hull stays
    // rendered while the camera moves in/out and hides only when actually in the cockpit. The
    // cross-system idiom (mirrors ZoomView.Active / SectorOverview.Active); false with no local ship.
    public static bool FirstPersonActive { get; private set; }

    // Surfaced for the transient HUD view-mode chip: which mode the player last selected and when
    // (ms), so the readout can flash briefly on a change and fade out.
    public static bool ViewIsFirstPerson { get; private set; }
    public static ulong ViewChangedMsec { get; private set; }

    // Cockpit eye offset, resolved from the hull's HP_Cockpit_0 marker and cached per ship node
    // instance (a respawn/pod-eject makes a new node, invalidating the cache naturally).
    private Node? _ship;
    private Vector3 _cockpitOffset = CockpitFallback;

    // Afterburner rumble: a subtle high-frequency rattle layered onto the framing while the burn is
    // lit, scaled by the ship's afterburner ramp (AbPower 0..1) so it fades in/out with the burn
    // rather than snapping. FIRST PERSON ONLY — you feel the shudder at the pilot's eye point, not on
    // the detached chase cam — so the amplitude scales with the FP blend and is zero in third person.
    // `_shakeTime` free-runs only while shaking so the layered sines stay smooth (no phase jump).
    private const float ShakePosAmp = 0.11f; // metres of positional jitter at full burn
    private const float ShakeRotAmp = 0.006f; // radians of angular shudder at full burn
    private float _shakeTime;

    private WorldRenderer _world = null!;

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        Far = 6000f;

        // Track this listener's own velocity so positional loops (the asteroid hum) pitch-bend into a
        // near-miss woosh as the ship streaks past. The emitter side sets the matching flag; both are
        // required for Godot to compute the doppler shift.
        DopplerTracking = DopplerTrackingEnum.IdleStep;

        // Default = first person, restoring the last mode the player toggled to.
        _fpDesired = UserPrefs.FirstPersonView;
        ViewIsFirstPerson = _fpDesired;
        _blend = _fpDesired ? 1f : 0f;

        foreach (string a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--view-demo="))
                _demoDir = a["--view-demo=".Length..];
    }

    public override void _Input(InputEvent @event)
    {
        // Same inputFree idiom the rest of the client gates keys on (ZoomView), plus a live local
        // ship: no view-mode changes while a full-screen overlay owns the screen (F3 overview reads
        // the wheel itself; chat/menus capture keys).
        bool inputFree = InputGate.FlightInputFree;
        if (!inputFree || _world.Ships.LocalShip == null)
            return;

        // toggle_view flips modes WITHOUT touching _zoom, so toggling round-trips to the prior
        // framing. Rebindable via the InputMap (InputBindings), so it accepts a key or a pad button.
        if (@event.IsActionPressed("toggle_view"))
        {
            SetDesiredMode(!_fpDesired);
            GetViewport().SetInputAsHandled();
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                HandleWheel(down: true);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                HandleWheel(down: false);
                break;
        }
    }

    // WheelDown narrows the shot; WheelUp widens it. The two ends of the zoom range hand off to the
    // view mode: winding IN past the closest chase shot dives into the cockpit, winding OUT of the
    // cockpit pulls back to the tightest chase framing.
    private void HandleWheel(bool down)
    {
        if (down) // narrower
        {
            if (_fpDesired)
                return; // already in the cockpit — WheelDown has nowhere closer to go
            if (_zoom <= MinZoom + 1e-4f)
                SetDesiredMode(true); // at the closest chase shot, one more notch dives to first person
            else
                _zoom = Mathf.Max(MinZoom, _zoom / ZoomStep);
        }
        else // wider
        {
            if (_fpDesired)
            {
                SetDesiredMode(false);
                _zoom = MinZoom; // pull back out of the cockpit to the tightest chase framing
            }
            else
                _zoom = Mathf.Min(MaxZoom, _zoom * ZoomStep);
        }
    }

    // Change (and persist) the view mode. The blend animates toward it from its current value, so a
    // change mid-transition just reverses the dolly. A no-op if the mode is unchanged.
    private void SetDesiredMode(bool fp)
    {
        if (_fpDesired == fp)
            return;
        _fpDesired = fp;
        UserPrefs.SetFirstPersonView(fp);
        ViewIsFirstPerson = fp;
        ViewChangedMsec = Time.GetTicksMsec();
    }

    public override void _Process(double delta)
    {
        var ship = _world.Ships.LocalShip;
        if (ship == null)
        {
            FirstPersonActive = false; // no ship ⇒ never "in the cockpit" (hull-hide seam reads this)
            // Just died: hold the last chase framing on the death point for a beat so the player
            // sees their own blast up close (see WorldRenderer death-cam) before the view pulls back
            // to the wide overview. The death cam always frames the wreck from OUTSIDE (third person).
            if (_world.Ships.DeathCamActive)
            {
                Transform3D d = _world.Ships.DeathCamShipTransform;
                GlobalTransform = new Transform3D(d.Basis * FaceForward, d.Origin + d.Basis * (ChaseOffset * _zoom));
                return;
            }
            GlobalPosition = OverviewPos;
            LookAt(Vector3.Zero, Vector3.Up);
            return;
        }

        // A fresh ship node (spawn / respawn / pod-eject): re-resolve its cockpit eye point and snap
        // straight to the preferred mode with no dolly — you spawn already framed, you don't watch a
        // transition play out on birth.
        if (!ReferenceEquals(ship, _ship))
        {
            _ship = ship;
            _cockpitOffset = ResolveCockpit(ship);
            _blend = _fpDesired ? 1f : 0f; // seeds the blend-out target = the player's chosen framing
            _launchBlendT = 0f;
            _launchFromPose = null;
            // A launch/eject plays the cinematic; anything else (reconnect reclaim) spawns already framed.
            if (ship.HasMeta("Launched"))
            {
                (_launchLen, _launchWidth) = ResolveModelExtents(ship);
                _launchCamT = LaunchCamHoldSec;
            }
            else
                _launchCamT = 0f;
        }

        // Advance the blend toward the desired mode over ~TransitionSec, edge to edge.
        float target = _fpDesired ? 1f : 0f;
        if (_blend != target)
        {
            float step = (float)delta / TransitionSec;
            _blend = _blend < target ? Mathf.Min(target, _blend + step) : Mathf.Max(target, _blend - step);
        }
        // Hide the own hull only once we're (essentially) all the way into the cockpit, so it stays
        // rendered throughout the dolly both directions — but NEVER during the launch cinematic, whose
        // whole point is an external shot of the hull (and _blend snaps to 1 instantly for an FP player).
        bool launchActive = _launchCamT > 0f || _launchFromPose.HasValue;
        FirstPersonActive = !launchActive && _blend >= 0.98f;

        // Rigidly attach to the ship's (smoothly interpolated) transform so the camera moves AND
        // rotates at EXACTLY the ship's rate — no smoothing lag, no world-up reference. Both framings
        // share the basis, so the transition is a pure ship-local positional lerp (smoothstep-eased)
        // between the chase offset and the cockpit offset; the ship's attitude stays locked the whole
        // time. CameraRig processes after the ship's node in tree order, so this reads the transform
        // the ship rendered this frame.
        float e = _blend * _blend * (3f - 2f * _blend); // smoothstep ease
        Vector3 offset = (ChaseOffset * _zoom).Lerp(_cockpitOffset, e);
        Transform3D t = ship.GlobalTransform;
        Basis basis = t.Basis * FaceForward;

        // Afterburner rumble (first person only). Fade the rattle in with the burn ramp AND the FP
        // blend (e), so third person (e≈0) stays rock-steady and the shudder grows as you dolly into
        // the cockpit. Layered incommensurate sines give a smooth, non-repeating jitter per axis —
        // jitters the ship-local framing offset for translation and tacks a faint roll/pitch onto the
        // shared basis for a felt "shudder".
        float amp = ship.AbPower * e;
        if (amp > 0.001f)
        {
            _shakeTime += (float)delta;
            float f1 = _shakeTime * 37f,
                f2 = _shakeTime * 53f,
                f3 = _shakeTime * 71f;
            offset +=
                new Vector3(
                    Mathf.Sin(f1) + 0.5f * Mathf.Sin(f2 * 1.7f),
                    Mathf.Sin(f2 + 1.3f) + 0.5f * Mathf.Sin(f3 * 1.3f),
                    Mathf.Sin(f3 + 2.1f) + 0.5f * Mathf.Sin(f1 * 1.9f)
                ) * (ShakePosAmp * amp / 1.5f);
            float pitch = Mathf.Sin(f1 * 1.1f + 1.9f) * ShakeRotAmp * amp;
            float roll = Mathf.Sin(f2 * 0.9f + 0.7f) * ShakeRotAmp * amp;
            basis = basis * new Basis(Vector3.Right, pitch) * new Basis(Vector3.Forward, roll);
        }

        Transform3D normalPose = new Transform3D(basis, t.Origin + t.Basis * offset);

        // Launch cinematic overrides the normal framing: an external shot rigidly ahead of the nose for
        // the hold, then an eased release INTO normalPose (never a hard cut). Inactive ⇒ pose = normalPose.
        Transform3D pose = normalPose;
        if (_launchCamT > 0f)
        {
            _launchCamT -= (float)delta;
            Transform3D launchPose = ComputeLaunchPose(t, _launchLen, _launchWidth);
            if (_launchCamT > 0f)
                pose = launchPose; // still holding rigidly ahead of the nose
            else
            {
                _launchFromPose = launchPose; // hold just expired — freeze it; the release begins below this frame
                _launchBlendT = 0f;
            }
        }
        if (_launchFromPose is { } from)
        {
            _launchBlendT = Mathf.Min(1f, _launchBlendT + (float)delta / LaunchBlendOutSec);
            float be = _launchBlendT * _launchBlendT * (3f - 2f * _launchBlendT); // smoothstep — same ease as the mode dolly
            pose = from.InterpolateWith(normalPose, be);
            if (_launchBlendT >= 1f)
                _launchFromPose = null;
        }

        GlobalTransform = pose;

        if (_demoDir != null)
            RunDemo(delta);
    }

    // Marker-first cockpit lookup: convert the hull's HP_Cockpit_0 node into ship-local space (so it
    // honors both the def-seeded marker AND any future GLB-authored override, and survives hull
    // scaling). Falls back to CockpitFallback when the model carries no cockpit node.
    private static Vector3 ResolveCockpit(Node3D ship)
    {
        var shipModel = ship.GetNodeOrNull<Node3D>("ShipModel");
        if (shipModel?.FindChild("HP_Cockpit_0", recursive: true, owned: false) is Node3D cockpit)
            return ship.GlobalTransform.AffineInverse() * cockpit.GlobalTransform.Origin;
        return CockpitFallback;
    }

    // Model length/width for the launch-cam framing, off the "ShipModel" child's meta (stashed by
    // ShipModelLoader.Build). Mirrors ResolveCockpit's node lookup; falls back to sane defaults when
    // the meta is absent or degenerate (odd server content), so the cinematic never frames to zero.
    private static (float Length, float Width) ResolveModelExtents(Node3D ship)
    {
        var shipModel = ship.GetNodeOrNull<Node3D>("ShipModel");
        float len = shipModel?.GetMeta("ModelLength", 0f).AsSingle() ?? 0f;
        float wid = shipModel?.GetMeta("ModelWidth", 0f).AsSingle() ?? 0f;
        return (len > 0.01f ? len : ShipModelLoader.DefaultModelLength, wid > 0.01f ? wid : DefaultModelWidth);
    }

    // The rigid launch-cam pose from the ship's transform `t`: parked ahead of the nose (+Z) and off to
    // the camera's right, looking BACK at the nose. Uses the ship's OWN up (no world up in space) and
    // deliberately skips FaceForward — the cam faces opposite the ship, so LookingAt(dir, shipUp) with
    // `dir` pointing from the cam back toward the nose is already the correct facing.
    private static Transform3D ComputeLaunchPose(Transform3D t, float len, float width)
    {
        Vector3 offset = new Vector3(width * LaunchRightWidthMult, len * LaunchUpLengthMult, len * LaunchAheadLengthMult);
        Vector3 camPos = t.Origin + t.Basis * offset;
        Vector3 nose = t.Origin + t.Basis * new Vector3(0f, 0f, len * 0.5f);
        Vector3 dir = (nose - camPos).Normalized();
        Basis basis = Basis.LookingAt(dir, t.Basis.Y);
        return new Transform3D(basis, camPos);
    }

    // ---- --view-demo=<dir>: scripted self-drive for screenshot verification --------
    // Synthesizes real V-key + wheel events through Input.ParseInputEvent (the normal input
    // pipeline — the same _Input handler a player reaches), snapshotting after each step INCLUDING a
    // couple of mid-transition frames that prove the animated dolly keeps the hull on screen while
    // moving. Pair with --autofly (needs a live local ship); quits when done.

    private string? _demoDir;
    private int _demoStep;
    private double _demoWait = 2.0; // let the autofly spawn + first snapshots settle

    private void RunDemo(double delta)
    {
        _demoWait -= delta;
        if (_demoWait > 0)
            return;
        _demoWait = 0.8;
        switch (_demoStep++)
        {
            case 0:
                Snap("01-fp-default");
                break; // spawns in first person (default)
            case 1:
                Tap(Key.V);
                break; // FP -> 3P
            case 2:
                Snap("02-third");
                break;
            case 3:
                Tap(Key.V);
                _demoWait = 0.15;
                break; // 3P -> FP; short wait to catch the dolly
            case 4:
                Snap("03-fp-mid-transition");
                break; // hull still visible mid-blend
            case 5:
                Snap("04-fp");
                break; // settled first person
            case 6:
                Wheel(MouseButton.WheelUp);
                break; // FP -> 3P (zoom snaps to MinZoom)
            case 7:
                Snap("05-wheel-out-third");
                break;
            case 8:
                Wheel(MouseButton.WheelDown);
                _demoWait = 0.15;
                break; // at MinZoom -> auto FP
            case 9:
                Snap("06-wheel-in-fp-mid");
                break; // mid dolly again
            case 10:
                Snap("07-wheel-in-fp");
                GetTree().Quit();
                break;
        }
    }

    private static void Tap(Key k)
    {
        Input.ParseInputEvent(
            new InputEventKey
            {
                Keycode = k,
                PhysicalKeycode = k,
                Pressed = true,
            }
        );
        Input.ParseInputEvent(
            new InputEventKey
            {
                Keycode = k,
                PhysicalKeycode = k,
                Pressed = false,
            }
        );
    }

    private static void Wheel(MouseButton b)
    {
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = true });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = false });
    }

    private void Snap(string name)
    {
        GetViewport().GetTexture().GetImage().SavePng($"{_demoDir}/{name}.png");
        GD.Print($"VIEW_DEMO_SHOT:{name}");
    }
}
