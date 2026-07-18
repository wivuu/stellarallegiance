using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  LoadoutPreview.cs — HANGAR 3D SHIP VIEWER (SubViewport-in-UI)
//
//  A self-contained 3D viewer for the loadout screen: its own SubViewport WORLD (the
//  main scene's environment/lights don't reach it and its contents never leak into the
//  game view), a camera orbiting the ship built by ShipModelLoader, and click-picking
//  of the ship's weapon hardpoints.
//
//  Interaction contract:
//    - left-drag orbits, wheel / pinch zooms; idle for a moment -> slow auto-spin
//    - a left CLICK (negligible drag) raycasts into the sub-world against small
//      Area3D spheres placed on the HP_ markers and raises HardpointClicked
//    - the camera moves, the model doesn't — markers keep stable world transforms so
//      MarkerOverlay can unproject them cheaply every frame
//
//  Physics note: space-state queries are deferred to _PhysicsProcess (querying from an
//  input callback can hit a locked space), which also gives freshly-added areas the one
//  physics frame they need to become queryable.
// =====================================================================
public partial class LoadoutPreview : SubViewportContainer
{
    // One pickable/markable mount on the current model. Assignable = Weapon-kind (the
    // only kind the loadout edits); the rest render as dim inert dots in the overlay.
    public readonly record struct Mount(HardpointDef Hp, Vector3 LocalPos, bool Assignable);

    private const float Fov = 40f;
    private const float OrbitPerPixel = 0.35f; // deg/px, same feel as the F3 sector map
    private const float PitchMin = -80f;
    private const float PitchMax = 80f;
    private const float ZoomStep = 1.12f;
    private const float IdleSpinDelay = 2.5f; // s without a drag before auto-spin
    private const float IdleSpinRate = 8f; // deg/s
    private const float ClickSlop = 6f; // px of drag under which a release is a click

    private SubViewport _viewport = null!;
    private Camera3D _cam = null!;
    private Node3D? _model;

    private readonly List<Mount> _mounts = new();
    public IReadOnlyList<Mount> Mounts => _mounts;

    // Selected/hovered assignable mount (HardpointDef.Index), mirrored by the slot list.
    public byte? SelectedIndex { get; set; }
    public byte? HoverIndex { get; private set; }

    public event Action<byte>? HardpointClicked;

    private float _yawDeg = 205f; // start on a 3/4 rear-quarter view
    private float _pitchDeg = 18f;
    private float _dist = 10f;
    private float _minDist = 4f,
        _maxDist = 30f;
    private float _idleTime;
    private bool _dragging;
    private Vector2 _dragStart;
    private Vector2 _mousePos;
    private Vector2? _pendingPick; // click awaiting the physics-frame raycast

    public override void _Ready()
    {
        Stretch = true;
        StretchShrink = 1;
        MouseFilter = MouseFilterEnum.Stop;

        // Own world + transparent background: the hangar's hatch/scanline backdrop shows
        // through around the hull, and nothing from the live game scene renders here.
        // Explicit World3D (not just OwnWorld3D): the property is the reliable handle for
        // DirectSpaceState raycasts — the flag's auto-created world isn't reachable from C#.
        _viewport = new SubViewport
        {
            OwnWorld3D = true,
            World3D = new World3D(),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Msaa2X,
            GuiDisableInput = true,
        };
        AddChild(_viewport);

        // The sub-world needs its own lighting: cool ambient so the dark side stays
        // readable, a warm key from camera-left, and a cyan rim from behind for the
        // hologram-bay read.
        _cam = new Camera3D
        {
            Fov = Fov,
            Near = 0.1f,
            Far = 200f,
            Environment = new Godot.Environment
            {
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.35f, 0.45f, 0.55f),
                AmbientLightEnergy = 0.9f,
            },
        };
        _viewport.AddChild(_cam);

        var key = new DirectionalLight3D { LightColor = new Color(1f, 0.92f, 0.8f), LightEnergy = 1.2f };
        key.RotationDegrees = new Vector3(-35f, 40f, 0f);
        _viewport.AddChild(key);

        var rim = new DirectionalLight3D { LightColor = DesignTokens.TeamAccentBase, LightEnergy = 0.5f };
        rim.RotationDegrees = new Vector3(20f, 200f, 0f);
        _viewport.AddChild(rim);

        PlaceCamera();
    }

    // Swap the previewed hull. Frees the old model, builds the new one at the origin via
    // the same loader the game uses (so the preview IS the ship, hardpoints included),
    // and frames the camera off the class's silhouette length.
    public void ShowShip(DefRegistry defs, byte classId)
    {
        _model?.QueueFree();
        _mounts.Clear();
        SelectedIndex = null;
        HoverIndex = null;

        // Fallback material only matters when a GLB is missing (placeholder silhouette).
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.65f, 0.75f), Metallic = 0.4f, Roughness = 0.5f };
        _model = ShipModelLoader.Build(defs, (ShipClass)classId, isPod: false, mat);
        _viewport.AddChild(_model);

        // Frame the orbit camera off the hull's authored silhouette length (the same
        // ShipClassDef.ModelLength the model loader normalizes the GLB to); 4.5 guards a hull
        // that authored none.
        float len = defs.TryGetShipDef(classId, out ShipClassDef def) && def.ModelLength > 0f ? def.ModelLength : 4.5f;
        _dist = len * 1.9f;
        _minDist = len * 0.9f;
        _maxDist = len * 5f;
        _idleTime = IdleSpinDelay; // fresh hull greets you already slowly spinning
        PlaceCamera();

        BuildHardpointMounts(defs, classId);
    }

    // Record every hardpoint on the class and hang a pickable Area3D sphere on each
    // WEAPON mount. The model sits unrotated at the origin, so a marker's local transform
    // is its world transform; areas parent under the model so a hull swap frees them.
    private void BuildHardpointMounts(DefRegistry defs, byte classId)
    {
        List<HardpointDef>? hardpoints = defs.GetHardpoints(classId);
        if (hardpoints == null || _model == null)
            return;

        float pickRadius = MathF.Max(0.35f, _dist * 0.045f);
        foreach (HardpointDef hp in hardpoints)
        {
            // The loader guarantees a marker per def hardpoint (def-seeded or GLB-authored);
            // fall back to the def offset if an authored GLB hid it somewhere unexpected.
            // A NonMountable weapon mount isn't a loadout slot (an unauthored mesh HP_Weapon node):
            // skip it entirely so no marker, dot, or pick area renders — it's HIDDEN in the hangar.
            if (hp.Kind == HardpointKind.Weapon && hp.Mount == WeaponMountKind.NonMountable)
                continue;
            Node3D? marker = _model.GetNodeOrNull<Node3D>($"HP_{hp.Kind}_{hp.Index}");
            Vector3 pos = marker?.Position ?? new Vector3(hp.OffX, hp.OffY, hp.OffZ);
            bool assignable = hp.Kind == HardpointKind.Weapon;
            _mounts.Add(new Mount(hp, pos, assignable));

            if (!assignable)
                continue;
            var area = new Area3D { Position = pos };
            area.SetMeta("hp_index", hp.Index);
            area.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = pickRadius } });
            _model.AddChild(area);
        }
    }

    // Screen-space position of a mount in THIS control's local coords (1:1 with viewport
    // coords under Stretch). Null when the point is behind the camera.
    public Vector2? MountScreenPos(in Mount m)
    {
        if (_cam.IsPositionBehind(m.LocalPos))
            return null;
        return _cam.UnprojectPosition(m.LocalPos);
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    _dragging = true;
                    _dragStart = mb.Position;
                }
                else
                {
                    if (_dragging && (mb.Position - _dragStart).Length() <= ClickSlop)
                        _pendingPick = mb.Position; // resolved next physics frame
                    _dragging = false;
                }
                AcceptEvent();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp }:
                _dist = Mathf.Max(_minDist, _dist / ZoomStep);
                PlaceCamera();
                AcceptEvent();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown }:
                _dist = Mathf.Min(_maxDist, _dist * ZoomStep);
                PlaceCamera();
                AcceptEvent();
                break;

            case InputEventMagnifyGesture mg:
                _dist = Mathf.Clamp(_dist / mg.Factor, _minDist, _maxDist);
                PlaceCamera();
                AcceptEvent();
                break;

            case InputEventMouseMotion mm:
                _mousePos = mm.Position;
                if (_dragging)
                {
                    _yawDeg -= mm.Relative.X * OrbitPerPixel;
                    _pitchDeg = Mathf.Clamp(_pitchDeg + mm.Relative.Y * OrbitPerPixel, PitchMin, PitchMax);
                    _idleTime = 0f;
                    PlaceCamera();
                }
                break;
        }
    }

    public override void _Process(double delta)
    {
        // Slow turntable once the user lets go — the "spinnable" hologram idle.
        if (!_dragging)
        {
            _idleTime += (float)delta;
            if (_idleTime >= IdleSpinDelay)
            {
                _yawDeg += IdleSpinRate * (float)delta;
                PlaceCamera();
            }
        }

        // Hover = nearest assignable mount within a comfortable screen distance. 2D
        // proximity (not the ray) so the affordance is forgiving on small mounts.
        HoverIndex = NearestAssignable(_mousePos, 20f);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pendingPick is not Vector2 p)
            return;
        _pendingPick = null;

        Vector3 from = _cam.ProjectRayOrigin(p);
        Vector3 dir = _cam.ProjectRayNormal(p);
        var query = PhysicsRayQueryParameters3D.Create(from, from + dir * 200f);
        query.CollideWithAreas = true;
        query.CollideWithBodies = false;
        Godot.Collections.Dictionary hit = _viewport.World3D.DirectSpaceState.IntersectRay(query);

        byte? picked = null;
        if (hit.Count > 0 && hit["collider"].As<GodotObject>() is Area3D area && area.HasMeta("hp_index"))
            picked = (byte)(int)area.GetMeta("hp_index");
        // The ray misses when the click lands near-but-off a small sphere; the overlay's
        // 2D proximity doubles as the fallback so the dot and the hitbox always agree.
        picked ??= NearestAssignable(p, 14f);

        if (picked is byte idx)
        {
            _idleTime = 0f;
            HardpointClicked?.Invoke(idx);
        }
    }

    private byte? NearestAssignable(Vector2 screenPos, float maxDistPx)
    {
        byte? best = null;
        float bestD = maxDistPx;
        foreach (Mount m in _mounts)
        {
            if (!m.Assignable)
                continue;
            if (MountScreenPos(m) is not Vector2 sp)
                continue;
            float d = (sp - screenPos).Length();
            if (d < bestD)
            {
                bestD = d;
                best = m.Hp.Index;
            }
        }
        return best;
    }

    // Orbit-sphere placement around the origin (the model), same math as SectorOverview.
    private void PlaceCamera()
    {
        float yaw = Mathf.DegToRad(_yawDeg);
        float pitch = Mathf.DegToRad(_pitchDeg);
        var dir = new Vector3(Mathf.Cos(pitch) * Mathf.Sin(yaw), Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Cos(yaw));
        _cam.Position = dir * _dist;
        _cam.LookAt(Vector3.Zero, Vector3.Up);
    }
}
