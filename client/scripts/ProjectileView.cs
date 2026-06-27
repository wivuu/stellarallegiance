using Godot;

// A single visual projectile bolt. Bolts are dumb, constant-velocity shots: their whole
// trajectory is determined by the spawn pos+vel, so we FIRE AND FORGET — extrapolate that
// one straight line for the bolt's life and never correct.
//
// There is no authoritative Projectile row anymore: the server resolves hits analytically
// at fire time, and every client SYNTHESIZES the bolt locally — the local ship from its
// own fire prediction (PredictionController.Step), remote ships from their row's
// LastFireTick advancing (WorldRenderer.SpawnBoltFor, which mirrors the server's muzzle +
// deterministic-spread math). A shot costs zero replication.
//
// Lifetime: the bolt self-expires after its TTL — the weapon's flight life, clipped at
// spawn to the first static obstruction (asteroid/base) along its line so it visually
// stops at rocks the way the old server row-delete used to read. WorldRenderer's
// hit-spark pass may consume it earlier when it visually meets a (moving) ship.
public partial class ProjectileView : Node3D
{
    private Vector3 _pos;
    private Vector3 _vel;
    private Vector3 _aimDir; // normalized muzzle/shot direction, used only to orient the tracer
    private double _t0; // seconds, when _pos/_vel were sampled (spawn)
    private float _ttlSec; // flight life (already obstruction-clipped by the spawner)

    // Enemy-shot masking: remote shots are observed ~one-way latency after they were
    // actually fired, so the spawn sample would otherwise pop in at the (already stale)
    // muzzle. We render this many seconds AHEAD of the spawn line so the bolt appears
    // where it really is now. A one-time spawn offset, not an ongoing correction — it's
    // baked into the single extrapolation, so motion stays smooth. Zero for the local
    // ship's own predicted shots and on localhost (no measurable latency).
    private float _renderLeadSec;

    // Constant flight velocity (world u/s), read by WorldRenderer's client-side hit-spark
    // pass — it sweeps the bolt across each frame (so a fast shot can't tunnel through a
    // ship). OwnerShipId is the ship that fired this bolt; the hit-spark pass skips it
    // outright so a shot never sparks on its own hull — a static muzzle-distance gate can't
    // do that once the firing ship flies forward with the bolt. Team is otherwise not
    // tracked: friendly fire sparks like any other.
    public Vector3 Velocity => _vel;
    public ulong OwnerShipId { get; private set; }

    public void Initialize(
        Vector3 pos,
        Vector3 vel,
        Vector3 aimDir,
        float ttlSec,
        ulong ownerShipId,
        float renderLeadSec = 0f
    )
    {
        _pos = pos;
        _vel = vel;
        // Orient the tracer along the fired direction (velocity in the firing ship's frame),
        // not the absolute velocity. Absolute velocity inherits the ship's strafe, so a bolt
        // fired while flying sideways would yaw the cylinder off-axis — fall back to it only
        // if no aim direction was supplied.
        _aimDir = aimDir.LengthSquared() > 1e-6f ? aimDir.Normalized() : vel.Normalized();
        _ttlSec = ttlSec;
        _renderLeadSec = renderLeadSec;
        OwnerShipId = ownerShipId;
        _t0 = Time.GetTicksMsec() / 1000.0;
        OrientAlongAim();
        Position = pos + vel * renderLeadSec; // honour the lead immediately (no 1-frame muzzle pop)
    }

    // The render lead counts against the TTL: a remote bolt drawn `lead` ahead was fired
    // that much earlier, so it also expires that much sooner on our screen.
    public bool Expired => Time.GetTicksMsec() / 1000.0 - _t0 + _renderLeadSec >= _ttlSec;

    // Aim the bolt's local +Z down the fired direction so the cylinder tracer points where
    // the gun aimed (not skewed by the ship's strafe). Set once at spawn, never per frame.
    private void OrientAlongAim()
    {
        if (_aimDir.LengthSquared() > 1e-6f)
            Quaternion = new Quaternion(Vector3.Back, _aimDir);
    }

    public override void _Process(double delta)
    {
        float e = (float)(Time.GetTicksMsec() / 1000.0 - _t0);
        Position = _pos + _vel * (e + _renderLeadSec);
    }
}
