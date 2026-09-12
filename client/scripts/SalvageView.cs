using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  SalvageView.cs — CLIENT DROPPED-SALVAGE ITEM VISUAL
//
//  One node per live server salvage item (WorldRenderer._salvageRenderer). A wreck scatters what it
//  carried; each piece drifts, bounces off the world's geometry and finally parks until someone flies
//  over it. That makes an item a hybrid of the two visuals that already exist: it loads its mesh the
//  way ProbeView does (an authored GLB by def ModelName, the team-tinted puff when there is none) and
//  it MOVES the way MissileView does (dead-reckon the last authoritative position along its velocity,
//  ease the rendered node toward that so a corrected frame nudges instead of popping).
//
//  Everything here is cosmetic. The server owns the item's real position, its collisions and who may
//  collect it; this node never reports anything back.
// =====================================================================
public partial class SalvageView : Node3D
{
    // Longest local axis (world units) a loaded item GLB is uniform-scaled to. Between MissileView's
    // 1.5 dart and a 4-5.5 hull: a gun or a pack should read as a pickup floating near a ship, not as
    // another ship.
    private const float TargetLength = 2.0f;

    // How fast the rendered node eases toward the dead-reckoned authoritative position (MissileView's
    // rate — a correction resolves in a frame or two at the 20 Hz stream cadence without a pop).
    private const float EaseRate = 18f;

    // Below this speed² the item is treated as PARKED: the dead-reckoning baseline stops advancing so
    // a resting item cannot creep between frames. Matches the server's rest threshold in spirit; the
    // exact number only decides when the visual stops drifting, never where the item is.
    private const float RestSpeedSq = 0.04f;

    // How much lifespan is left when the item starts blinking (10 s at 20 Hz) — the pilot's cue that
    // this piece is about to expire.
    private const ushort BlinkTicks = 200;

    // Blink rate while expiring: 2 Hz, i.e. a visible flash every half second.
    private const float BlinkHz = 2f;

    // Cosmetic tumble ceiling (rad/s). Collide.RockSpin's speeds are authored for asteroid-sized
    // tumble; a hand-sized item spinning that fast reads as a glitch, so clamp it.
    private const float MaxSpinRate = 0.6f;

    // The scale ChaffFx.FallbackPuff is authored at, so a fallback puff can be re-scaled to the item
    // silhouette the same way ProbeView does it.
    private const float PuffBaseSize = 1.2f;

    // The WRECK's team — the HUD tint (and the pickup FX tint), never a pickup gate.
    public byte Team { get; private set; }

    // One-line HUD name for this item ("PW Gat Gun 1", "Counter-missile ×8"), resolved from the
    // streamed defs so the marker/banner passes never re-look-up per frame.
    public string Label { get; private set; } = "Salvage";

    // What this item IS, kept so the label + mesh can be re-resolved once its def streams (see
    // _defsPending). Never changes for the life of the view — the server never re-kinds an item.
    private byte _kind;
    private uint _itemId;
    private byte _count;

    // True while the item's def had NOT streamed when the view was built. A client joining a world
    // that already holds salvage can drain the first MsgSalvage before MsgDefs (they ride different
    // reliability tiers), which would otherwise strand the item on the placeholder puff and the
    // generic "Salvage" caption for its whole life. RefreshDefs clears it on the next frame.
    private bool _defsPending;

    // Dead-reckoned authoritative position (advanced by _vel each frame) + the last known velocity.
    // The node's own Position eases toward _targetPos.
    private Vector3 _targetPos;
    private Vector3 _vel;

    // Cosmetic tumble (fixed per item id, so every client tumbles the same piece identically).
    private Vector3 _spinAxis = Vector3.Up;
    private float _spinRate;

    // Remaining lifespan in ticks, seeded at Initialize, refreshed by every authoritative frame and
    // decremented locally between them so the expiry blink keeps its phase at the stream cadence.
    private float _ticksLeft;

    // The loaded mesh. The blink toggles THIS node's visibility — never the view node's, which
    // belongs to the sector gate (SectorView.SetNodeSector writes it).
    private Node3D? _hull;

    // Build the visual from the streamed defs: model + label by item kind. `id` only seeds the
    // cosmetic tumble. A def that hasn't streamed (shouldn't happen — defs precede any ship) or a
    // model that won't load falls back to the team-tinted puff, so an item is never invisible.
    public void Initialize(
        ulong id,
        Vector3 pos,
        Vector3 vel,
        ushort ticksLeft,
        byte kind,
        uint itemId,
        byte count,
        byte team,
        DefRegistry defs
    )
    {
        Team = team;
        Position = pos;
        _targetPos = pos;
        _vel = vel;
        _ticksLeft = ticksLeft;
        _kind = kind;
        _itemId = itemId;
        _count = count;
        _defsPending = !DefsKnown(kind, itemId, defs);
        Label = LabelFor(kind, itemId, count, defs);

        var (axis, speed) = Collide.RockSpin(id);
        _spinAxis = new Vector3(axis.X, axis.Y, axis.Z).Normalized();
        _spinRate = Mathf.Min(speed, MaxSpinRate);

        _hull = LoadHull(kind, itemId, team, defs);
        AddChild(_hull);
    }

    // Re-resolve the caption and the mesh for an item that was built before its def streamed. Called
    // from the renderer on every authoritative frame; a no-op once the def is in (the common case) and
    // a no-op while it still isn't. An UNAUTHORED model (def present, ModelName empty) is NOT pending —
    // that puff is the intended visual, not a race.
    public void RefreshDefs(DefRegistry defs)
    {
        if (!_defsPending || !DefsKnown(_kind, _itemId, defs))
            return;
        _defsPending = false;
        Label = LabelFor(_kind, _itemId, _count, defs);
        _hull?.QueueFree();
        _hull = LoadHull(_kind, _itemId, Team, defs);
        AddChild(_hull);
    }

    // Has the def this item's caption/mesh reads streamed yet? Kind 1 is a cargo pack, everything else
    // resolves through a WeaponDef (the gun itself, or the rack a loose missile stack belongs to).
    private static bool DefsKnown(byte kind, uint itemId, DefRegistry defs) =>
        kind == 1 ? defs.GetCargoItem(itemId) is not null : defs.GetWeapon(itemId) is not null;

    // Latest server truth for this item: reset the dead-reckoning baseline, velocity and lifespan.
    // Called on EVERY stream frame (the per-sector frame re-sends every item it holds), so it must
    // stay side-effect free — no sound, no FX, no re-parenting.
    public void OnAuthoritative(Vector3 pos, Vector3 vel, ushort ticksLeft)
    {
        _targetPos = pos;
        _vel = vel;
        _ticksLeft = ticksLeft;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Dead-reckon only while the item is actually moving — a parked item's target pins to its
        // last authoritative position, so it sits perfectly still instead of creeping on f16 noise.
        if (_vel.LengthSquared() > RestSpeedSq)
            _targetPos += _vel * dt;
        Position = Position.Lerp(_targetPos, 1f - Mathf.Exp(-EaseRate * dt));

        if (_spinRate > 0f)
            Rotate(_spinAxis, _spinRate * dt);

        // Expiry blink. The lifespan runs down locally at the sim's tick rate between frames so the
        // flash keeps its phase even on a quiet tick (the stream only re-sends on change/keepalive).
        // The CHILD hull is what toggles — the view node's own Visible belongs to the sector gate.
        _ticksLeft -= dt * FlightModel.TickRate;
        if (_hull is not null)
        {
            float phase = Mathf.PosMod(_ticksLeft / FlightModel.TickRate * BlinkHz, 1f);
            _hull.Visible = _ticksLeft >= BlinkTicks || phase < 0.5f;
        }
    }

    // The GLB for one item, by kind — each kind's model lives in the directory its def family already
    // uses, so nothing new has to be authored for missiles/mines/chaff/probes:
    //   kind 0 (part)     the gun's own WeaponDef      -> assets/parts/<ModelName>.glb
    //   kind 2 (missiles) the RACK's WeaponDef, which already carries its missile's model
    //                                                  -> assets/missiles/<ModelName>.glb
    //   kind 1 (cargo)    the dispenser that fires it  -> assets/{mines,chaff,probes}/<ModelName>.glb
    //                     no dispenser (the fuel pod)  -> the cargo def's own model in assets/parts/
    // Anything missing, unauthored or unloadable falls back to the team-tinted puff (ProbeView's
    // idiom) — expected for guns and fuel until their meshes land.
    private static Node3D LoadHull(byte kind, uint itemId, byte team, DefRegistry defs)
    {
        string? path = ModelPath(kind, itemId, defs);
        if (!string.IsNullOrEmpty(path) && GlbLoader.Load(path) is { } hull)
        {
            GlbLoader.NormalizeLongestAxis(hull, TargetLength);
            return hull;
        }
        var puff = ChaffFx.FallbackPuff(team);
        puff.Scale = Vector3.One * (TargetLength / PuffBaseSize);
        return puff;
    }

    private static string? ModelPath(byte kind, uint itemId, DefRegistry defs)
    {
        switch (kind)
        {
            case 0:
            {
                string? name = defs.GetWeapon(itemId)?.ModelName;
                return string.IsNullOrEmpty(name) ? null : $"res://assets/parts/{name}.glb";
            }
            case 2:
            {
                string? name = defs.GetWeapon(itemId)?.ModelName;
                return string.IsNullOrEmpty(name) ? null : $"res://assets/missiles/{name}.glb";
            }
            default:
            {
                // A dispenser claims the cargo id for chaff/mine/probe packs; the fuel pod has none
                // and carries its own model on the cargo def.
                if (defs.DispenserForCargo(itemId) is { } disp && !string.IsNullOrEmpty(disp.ModelName))
                {
                    string dir = disp.Kind switch
                    {
                        WeaponKind.Mine => "mines",
                        WeaponKind.Chaff => "chaff",
                        _ => "probes",
                    };
                    return $"res://assets/{dir}/{disp.ModelName}.glb";
                }
                string? name = defs.GetCargoItem(itemId)?.ModelName;
                return string.IsNullOrEmpty(name) ? null : $"res://assets/parts/{name}.glb";
            }
        }
    }

    // HUD name, resolved once. A gun is just its name; a stack carries its count.
    private static string LabelFor(byte kind, uint itemId, byte count, DefRegistry defs) =>
        kind switch
        {
            0 => defs.GetWeapon(itemId)?.Name is { Length: > 0 } gun ? gun : "Salvage",
            2 => defs.GetWeapon(itemId)?.Name is { Length: > 0 } rack ? $"{rack} ×{count}" : "Salvage",
            _ => defs.GetCargoItem(itemId)?.Name is { Length: > 0 } cargo ? $"{cargo} ×{count}" : "Salvage",
        };
}
