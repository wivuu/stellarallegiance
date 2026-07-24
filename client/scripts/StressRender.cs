using Godot;

// =====================================================================
//  StressRender.cs — CLIENT RENDER STRESS / MEASUREMENT KNOBS
//
//  Opt-in switches for profiling scene-render cost against a static fleet (the
//  `--stress-fighters N` server harness). Every field defaults to plain game
//  behaviour, so a normal client run is unaffected — these do something only
//  when set from the CLI in ShipController (`--stress-fx=…`, `--render-stats`).
//
//  The point of the A/B: a MultiMesh could batch the identical ship HULLS into
//  a few draw calls, but never the per-ship SIDECAR fx (engine glow, team trail,
//  nav beacons — separate nodes, some with real-time OmniLights + particles).
//  These knobs strip the sidecars in stages so we can see how much of the frame
//  the instanceable hull vs the un-instanceable fx actually costs — i.e. whether
//  hull instancing is worth it, or the lights/particles are the real wall.
//
//  Read by: ShipModelLoader.AttachEngineGlow (skip/trim ship fx), EngineGlow.
//  SetThrottle (ForceGlow — light a PARKED ship's plume so the measure reflects
//  a moving fleet, not the throttle-gated-dark parked default), and Hud (the
//  on-screen + logged draw-call / primitive counters).
// =====================================================================
public static class StressRender
{
    // How much of a ship's per-instance fx to build. The hull is always drawn.
    public enum FxMode
    {
        Full, // engine glow + team trail + nav beacons (normal game dress)
        NoLights, // keep the glow/mote MESHES + particles, hide their real-time OmniLights
        NoFx, // hull only — skip glow/trail/beacons entirely (the instancing ceiling)
    }

    public static FxMode Fx = FxMode.Full;

    // Force every engine glow to a lit throttle even on a parked ship. The stress fleet sits inert
    // (throttle 0), and EngineGlow gates its whole node Visible on throttle, so WITHOUT this the
    // plume/particles/wash-light draw nothing and the measurement under-counts a real moving fleet.
    // Set alongside the dressed stress modes; irrelevant under NoFx (no glow at all).
    public static bool ForceGlow;

    // Draw the draw-call / primitive counters on the HUD and log them each interval. FPS alone says
    // frames dropped; these say WHY (draw calls vs geometry).
    public static bool ShowStats;

    // Hide every real-time OmniLight under a freshly-built ship subtree (NoLights mode). Called once
    // right after the fx children are added: nothing re-shows a light (EngineGlow/BaseBeacon drive
    // LightEnergy, never Visible), so a one-shot hide sticks. The additive glow/mote MESHES stay.
    public static void HideLightsUnder(Node node)
    {
        if (node is OmniLight3D light)
            light.Visible = false;
        foreach (Node child in node.GetChildren())
            HideLightsUnder(child);
    }
}
