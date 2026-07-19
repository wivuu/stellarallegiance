using System.Collections.Generic;
using Godot;

// The quick discover/warp dissolve for static geometry (asteroids + bases). Instead of popping a node in/
// out the instant its sector becomes the view sector — a fog reveal or an aleph warp used to blink the
// whole rock field / stations into existence — each node's per-instance transparency ramps over FadeDur so
// it dissolves in (and out). Curr is a 0..1 "shown" factor; applied transparency lerps from 1 (invisible)
// at Curr=0 to the node's RESTING transparency at Curr=1 (0 for a live rock/base, StaleBaseTransparency for
// a dead-but-remembered station — so a fade-in never un-dims a wreck). Node.Visible stays true through the
// fade and only drops to false once a fade-out completes, so the Visible-gated queries (Bases.Visible/
// Alephs.Visible/collision) keep matching what's actually on screen. Sector transitions are hard cuts
// (ShowNodeInstant, no fade); this only covers same-sector fog reveals + the stale-base ghost dim.
//
// The resting transparency is carried on the node itself via the "restTransparency" meta (stamped by the
// base renderer when a station goes stale-dead), so this controller depends on no other subsystem.
public sealed class FadeController
{
    public const float FadeDur = 0.55f; // seconds for a full in/out ramp

    private struct Fade
    {
        public float Curr;
        public float Target;
    }

    private readonly Dictionary<Node3D, Fade> _fades = new();
    private readonly List<Node3D> _fadeScratch = new();

    // Resting (fully-shown) transparency for a world node: 0 opaque, or the node's stamped
    // "restTransparency" meta (StaleBaseTransparency for a destroyed-but-remembered base, so a re-scout
    // fade settles at the ghostly dim rather than solid).
    public static float RestTransparencyFor(Node3D node) =>
        node.HasMeta("restTransparency") ? (float)node.GetMeta("restTransparency") : 0f;

    // Begin (or reverse) a fade toward shown/hidden for one static node. A node not yet mid-fade only
    // starts one if it actually needs to change — an already-shown node staying shown is a no-op, so
    // steady frames cost nothing. A fade-in forces Visible=true up front (its transparency carries the
    // reveal); a fade already running just retargets, so a warp-in-then-out mid-ramp reverses cleanly.
    public void FadeNode(Node3D n, bool show)
    {
        float target = show ? 1f : 0f;
        if (_fades.TryGetValue(n, out var f))
        {
            f.Target = target;
            _fades[n] = f;
        }
        else if (show && !n.Visible)
        {
            NodeFx.DimNode(n, 1f); // start invisible so the ramp dissolves it in
            n.Visible = true;
            _fades[n] = new Fade { Curr = 0f, Target = 1f };
        }
        else if (!show && n.Visible)
        {
            _fades[n] = new Fade { Curr = 1f, Target = 0f };
        }
    }

    // Drop any in-flight fade for a node — a hard cut (ShowNodeInstant) cancels the dissolve.
    public void Remove(Node3D n) => _fades.Remove(n);

    // Smoothstep: eases a 0..1 linear progress into a gentle in/out curve (no hard start/stop).
    private static float Ease(float t) => t * t * (3f - 2f * t);

    // Advance every in-flight fade one frame, applying transparency and retiring finished ramps.
    public void AdvanceFades(double delta)
    {
        if (_fades.Count == 0)
            return;
        float step = (float)delta / FadeDur;
        _fadeScratch.Clear();
        _fadeScratch.AddRange(_fades.Keys);
        foreach (var n in _fadeScratch)
        {
            if (!GodotObject.IsInstanceValid(n))
            {
                _fades.Remove(n);
                continue;
            }
            var f = _fades[n];
            f.Curr = Mathf.MoveToward(f.Curr, f.Target, step);
            // f.Curr is the LINEAR progress (drives MoveToward + the retire check below unchanged); only
            // the applied transparency is smoothstep-eased so the dissolve has no hard start/stop.
            NodeFx.DimNode(n, Mathf.Lerp(1f, RestTransparencyFor(n), Ease(f.Curr)));
            if (Mathf.IsEqualApprox(f.Curr, f.Target))
            {
                if (f.Target <= 0f)
                    n.Visible = false; // fully faded out — drop out of the Visible-gated queries
                _fades.Remove(n);
            }
            else
                _fades[n] = f;
        }
    }

    public void Clear() => _fades.Clear();
}
