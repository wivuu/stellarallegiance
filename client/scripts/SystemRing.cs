using Godot;
using StellarAllegiance.Ui;

// HUD system ring: the HULL + SHIELD + ENRG/FUEL + AMMO/BOOST gauges from the "Stellar Allegiance"
// Game-HUD design. Segmented arc gauges framing the aim reticle (centre of screen space) — on the
// right span, HULL as segmented blocks (inner) with the regenerating SHIELD as a solid arc wrapping
// it (outer) and the AMMO magazine tag underneath; on the left span, FUEL/BOOST (inner) with the
// draining ENRG pool as a solid arc wrapping it (outer). Top and bottom are left open so vertical aim
// stays clear. Each gauge only draws when the hull actually carries the thing it reads: SHLD needs a
// shield part, FUEL needs an EQUIPPED afterburner (not just a tank), ENRG/AMMO need something that
// actually spends them (a cloak, or an energy-/ammo-costing gun), CLK needs a cloak part.
//
// A crew GUNNER gets the same ring, reading the hull they RIDE and centred on their turret's aim
// (v42 crews slice 2c) — the captain's hull is the one that can kill them, so it is the one their
// gauges have to show. Both seats resolve through HudSubject; the own-hull-only extras (the fuel-pod
// reserve/LOAD sweep, the ammo-pack LOAD sweep) are gated on actually being the pilot — a gunner has
// no way to time either loader on a hull that isn't theirs to fly.
//
// Pure overlay: reads the subject's authoritative-derived state and the active camera, and draws.
// Never touches authoritative state. Factored onto a `RingReadout` snapshot (`FromSubject` in the
// live path, `SetMock` for the gallery) so `_Draw` never has to know which world it came from.
// Created and wired up by the Hud, like the other combat overlays.
public partial class SystemRing : Control
{
    private const float Radius = 82f; // arc radius (px) — frames the reticle/lead circle
    private const float ArcWidth = 7f; // lit/track block thickness (px)
    private const int Blocks = 10; // segments per gauge
    private const float SpanDeg = 130f; // angular sweep of each gauge
    private const float GapDeg = 2.4f; // dead space between blocks
    private const float ShieldRadius = Radius + 8f; // SHLD/ENRG solid arc wraps just outside the HULL/FUEL blocks
    private const float ShieldWidth = 5f; // thinner than the hull blocks (design: solid outer band)
    private const float InnerRadius = Radius - 9f; // fuel-pod / ammo-pack LOAD sweeps: an inner accent ring, clear of the HULL/FUEL blocks
    private const float EnergyStarvedFrac = 0.10f; // at/under this fraction the ENRG arc + CLK tag read Danger, not Data

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;
    private DefRegistry _defs = null!; // resolves the subject's bolt-weapon range for the reticle centre

    // Whoever the HUD is about this frame (pilot or crew gunner), sampled in _Process so the gate and
    // the draw can never disagree about which hull they are reading.
    private HudSubject? _subject;

    // Showcase fixture: when set, _Draw reads this fixed snapshot and never consults a world.
    private RingReadout? _mock;

    // The gauges' audible side: shield down / back up and the hull-critical alarm, edge-detected off the
    // same subject the arcs draw (so a gunner hears the hull they ride). Runs whether or not the ring
    // is drawn — the F3 map or the zoom scope hiding the gauges doesn't make the hull any safer.
    private readonly HullCues _cues = new();

    // Match TargetMarkers: project through the F3 overview camera while the sector map is
    // open, otherwise the flight chase camera. Resolved per-access so it follows the toggle.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    // A fully-resolved snapshot of everything the ring draws — the live path derives one from the HUD
    // subject each frame (FromSubject); the gallery hands one straight in via SetMock, so the drawing
    // code never has to know whether a live world exists.
    public readonly record struct RingReadout(
        float Health,
        float MaxHealth,
        float Shield,
        float MaxShield,
        float Fuel,
        float MaxFuel,
        bool HasAfterburner,
        bool FuelLoading,
        float FuelLoadFrac,
        int FuelPods,
        bool ShowEnergy,
        float Energy,
        float MaxEnergy,
        bool ShowAmmo,
        int Ammo,
        int MaxAmmo,
        int AmmoPacks,
        bool AmmoLoading,
        float AmmoLoadFrac,
        bool HasCloak,
        float CloakLevel,
        bool CloakEngaged
    );

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera, DefRegistry defs)
    {
        _world = world;
        _camera = camera;
        _defs = defs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme
    }

    // Render standalone from a fixed readout (UiShowcase). No world, no camera, no defs — Init is
    // never called on this path, so load fonts here instead (this custom-draw node reads them
    // directly, not via a Theme).
    public void SetMock(RingReadout readout)
    {
        UiFonts.EnsureLoaded();
        MouseFilter = MouseFilterEnum.Ignore;
        _mock = readout;
        Visible = true;
        // Unlike the live path (which re-queues every _Process frame), a mock never ticks — so a
        // later resize (the gallery's container settling into its final layout, a window resize)
        // needs its own trigger to redraw at the new centre.
        Resized += QueueRedraw;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_mock is not null)
            return; // showcase fixture: fixed readout, always visible
        _subject = HudSubject.Resolve(_world, _defs);
        PlayHullCues();
        Visible = _subject is not null && !ZoomView.Active && !SectorOverview.Active; // scope circle or F3 map replaces these gauges
        if (Visible)
            QueueRedraw();
    }

    private void PlayHullCues()
    {
        var s = _subject;
        var cue = _cues.Observe(s?.Node, s?.Health ?? 0f, s?.MaxHealth ?? 0f, s?.Shield ?? 0f, s?.MaxShield ?? 0f);
        if (cue == HullCues.Cue.None || SfxManager.Instance is not { } sfx)
            return;
        if ((cue & HullCues.Cue.ShieldDown) != 0)
            sfx.PlayCockpit(SfxManager.SfxId.ShieldDown);
        if ((cue & HullCues.Cue.ShieldUp) != 0)
            sfx.PlayCockpit(SfxManager.SfxId.ShieldUp);
        if ((cue & HullCues.Cue.HullCritical) != 0)
            sfx.PlayUi(SfxManager.SfxId.HullCritical);
    }

    // The live path's HudSubject -> RingReadout projection. Own-hull-only fields (fuel-pod reserve/
    // LOAD, ammo-pack LOAD) come straight off HudSubject, which already zeroes them for a gunner.
    private static RingReadout FromSubject(in HudSubject s) =>
        new(
            s.Health,
            s.MaxHealth,
            s.Shield,
            s.MaxShield,
            s.Fuel,
            s.MaxFuel,
            s.HasAfterburner,
            s.Pilot?.FuelLoading ?? false,
            s.Pilot?.FuelLoadFrac ?? 0f,
            s.Pilot?.FuelPods ?? 0,
            s.ShowEnergy,
            s.Energy,
            s.MaxEnergy,
            s.ShowAmmo,
            s.Ammo,
            s.MaxAmmo,
            s.AmmoPacks,
            s.AmmoLoading,
            s.AmmoLoadFrac,
            s.HasCloak,
            s.CloakLevel,
            s.CloakEngaged
        );

    public override void _Draw()
    {
        RingReadout local;
        if (_mock is { } mock)
            local = mock;
        else if (_subject is { } s)
            local = FromSubject(s);
        else
            return;

        // Centre on the aim reticle so the ring hugs the crosshair the player is already looking at —
        // HudSubject.AimPoint is the very point TargetMarkers draws that reticle on, in either seat;
        // fall back to this control's own centre when it is behind the camera, or there is no live
        // subject at all (the showcase mock has no aim point to project — it just centres on its own
        // box). Size, not GetViewportRect(): in flight this control fills the viewport (FullRect), so
        // the two agree, but the gallery embeds it as a small boxed component, not a full overlay.
        Vector2 c = Size * 0.5f;
        if (_mock is null && _subject is { } sub)
        {
            Camera3D cam = Cam;
            if (!cam.IsPositionBehind(sub.AimPoint))
                c = cam.UnprojectPosition(sub.AimPoint);
        }

        Color track = DesignTokens.BorderLo;

        // HULL — right span (centre 0° = +X). Lit from the bottom (high-angle end) and
        // tinted by health tier (green → amber → red) so a failing hull reads at a glance.
        float hullFrac = local.MaxHealth > 0f ? Mathf.Clamp(local.Health / local.MaxHealth, 0f, 1f) : 0f;
        SegmentedArc(c, 0f, hullFrac, HealthColor(hullFrac), track, litFromEnd: true);

        // SHIELD — a solid cyan arc wrapping just OUTSIDE the hull blocks on the same right span,
        // filled from the bottom to match. Only on hulls that carry a shield (MaxShield > 0), so a
        // shieldless class (scout/pod) shows no arc. Cyan = the chrome gauge-arc convention.
        bool hasShield = local.MaxShield > 0f;
        float shieldFrac = hasShield ? Mathf.Clamp(local.Shield / local.MaxShield, 0f, 1f) : 0f;
        if (hasShield)
            SolidArc(c, ShieldRadius, 0f, shieldFrac, DesignTokens.TeamAccent, track, ShieldWidth);

        // ENRG — the left span's outer arc, mirroring SHLD, only when something aboard actually spends
        // energy (HudSubject.ShowEnergy: a cloak or an energy-costing gun). Data normally, Danger once
        // the pool is critically low (EnergyStarvedFrac) — CLK below the ring shares the same tint.
        bool showEnergy = local.ShowEnergy && local.MaxEnergy > 0f;
        float energyFrac = showEnergy ? Mathf.Clamp(local.Energy / local.MaxEnergy, 0f, 1f) : 0f;
        bool energyStarved = showEnergy && energyFrac <= EnergyStarvedFrac;
        Color energyColor = energyStarved ? DesignTokens.Danger : DesignTokens.Data;
        if (showEnergy)
            SolidArc(c, ShieldRadius, 180f, energyFrac, energyColor, track, ShieldWidth);

        // FUEL — left span's inner (block) arc, on hulls with an EQUIPPED afterburner AND a modeled
        // tank. A hull with neither shows nothing at all on this side (no legacy BOOST-ramp fallback —
        // every equipped afterburner pairs with a tank in this content, so there is nothing left to
        // read when HasAfterburner is false).
        bool hasFuel = local.HasAfterburner && local.MaxFuel > 0f;
        float fuelFrac = hasFuel ? Mathf.Clamp(local.Fuel / local.MaxFuel, 0f, 1f) : 0f;
        if (hasFuel)
            SegmentedArc(c, 180f, fuelFrac, FuelColor(fuelFrac), track, litFromEnd: false);

        // Mono labels just outside each gauge, top-to-bottom mirrored on both sides: the shield-like
        // outer arc's tag (SHLD right / ENRG left), the primary block arc's tag (HULL right / FUEL
        // left), then a bottom-row status tag (AMMO right / FUEL-POD or LOAD left) that only earns its
        // slot when there is something to say.
        if (hasShield)
            DrawTagValue(
                c + new Vector2(Radius + 12f, -14f),
                "SHLD",
                $"{local.Shield:0}",
                DesignTokens.TeamAccent,
                rightAlign: false
            );
        if (showEnergy)
            DrawTagValue(c + new Vector2(-(Radius + 12f), -14f), "ENRG", $"{local.Energy:0}", energyColor, rightAlign: true);
        DrawTagValue(
            c + new Vector2(Radius + 12f, 4f),
            "HULL",
            $"{local.Health:0}",
            HealthColor(hullFrac),
            rightAlign: false
        );
        if (hasFuel)
            DrawTagValue(
                c + new Vector2(-(Radius + 12f), 4f),
                "FUEL",
                $"{fuelFrac * 100f:0}",
                FuelColor(fuelFrac),
                rightAlign: true
            );

        // AMMO (right, under HULL): the shared magazine — NOT gated on hasFuel/hasShield, since the
        // ammo pool is independent of either. LOADING replaces it in the same slot while a pack is
        // inbound, mirroring the FUEL side's LOAD/POD swap below. Danger when the mag is dry, Warn
        // while a pack is loading it back up.
        if (local.ShowAmmo && local.MaxAmmo > 0)
        {
            if (local.AmmoLoading)
            {
                SolidArc(c, InnerRadius, 0f, local.AmmoLoadFrac, DesignTokens.Warn, track, ShieldWidth);
                DrawTagValue(
                    c + new Vector2(Radius + 12f, 22f),
                    "LOAD",
                    $"{local.AmmoLoadFrac * 100f:0}%",
                    DesignTokens.Warn,
                    rightAlign: false
                );
            }
            else
            {
                bool ammoEmpty = local.Ammo <= 0;
                string val = local.AmmoPacks > 0 ? $"{local.Ammo} +{local.AmmoPacks}" : $"{local.Ammo}";
                DrawTagValue(
                    c + new Vector2(Radius + 12f, 22f),
                    "AMMO",
                    val,
                    ammoEmpty ? DesignTokens.Danger : DesignTokens.Data,
                    rightAlign: false
                );
            }
        }

        // CLK, centred below the ring (the open bottom gap between the two spans) — only on a hull
        // that carries a cloak part. Pulses while the level is doing anything (engaged, or still
        // ramping down after release); Danger whenever the energy pool that feeds it is starved.
        if (local.HasCloak)
        {
            bool ramping = local.CloakEngaged || local.CloakLevel > 0f;
            Color clkColor = energyStarved ? DesignTokens.Danger : DesignTokens.TeamAccent;
            DrawCentered(
                c + new Vector2(0f, ShieldRadius + 26f),
                $"CLK {Mathf.RoundToInt(local.CloakLevel * 100f)}%",
                ramping ? Pulsed(clkColor) : clkColor
            );
        }

        // Fuel-pod reserve under the FUEL tag (predicted count — drops the instant one is committed
        // to the loader). Hidden at zero so the legacy layout is untouched without pods. While a pod
        // is LOADING the tank is dead, so the line becomes a load readout in the danger tone with a
        // sweep arc on the inner ring (mirroring the ammo-pack sweep on the right) — the pilot can see
        // how long the afterburner stays out. PREDICTED own-hull state, so gated on HasAfterburner
        // already covering the pilot-only fields (a gunner's FuelLoading/FuelPods read false/0).
        if (!hasFuel)
            return;
        if (local.FuelLoading)
        {
            SolidArc(c, InnerRadius, 180f, local.FuelLoadFrac, DesignTokens.Danger, track, ShieldWidth);
            DrawTagValue(
                c + new Vector2(-(Radius + 12f), 22f),
                "LOAD",
                $"{local.FuelLoadFrac * 100f:0}%",
                DesignTokens.Danger,
                rightAlign: true
            );
        }
        else if (local.FuelPods > 0)
            DrawTagValue(
                c + new Vector2(-(Radius + 12f), 22f),
                "POD",
                $"+{local.FuelPods}",
                DesignTokens.Warn,
                rightAlign: true
            );
    }

    // One segmented arc gauge centred at `centerDeg`, sweeping `SpanDeg`. Each block is a
    // short DrawArc; lit blocks use `lit`, the rest `track`. `litFromEnd` lights from the
    // high-angle (bottom) end so the hull drains downward like the design's HP arc.
    private void SegmentedArc(Vector2 c, float centerDeg, float value, Color lit, Color track, bool litFromEnd)
    {
        float start = centerDeg - SpanDeg * 0.5f;
        float cell = SpanDeg / Blocks;
        int filled = Mathf.Clamp(Mathf.RoundToInt(value * Blocks), 0, Blocks);
        for (int i = 0; i < Blocks; i++)
        {
            bool isLit = litFromEnd ? i >= Blocks - filled : i < filled;
            float a1 = Mathf.DegToRad(start + i * cell + GapDeg * 0.5f);
            float a2 = Mathf.DegToRad(start + (i + 1) * cell - GapDeg * 0.5f);
            DrawArc(c, Radius, a1, a2, 6, isLit ? lit : track, ArcWidth, true);
        }
    }

    // One SOLID arc gauge (not segmented) at `radius`, centred at `centerDeg`. Its ends are inset by
    // half the block gap so the arc caps exactly at the hull blocks' outer edges (the first block
    // starts at start+GapDeg/2, the last ends at start+SpanDeg-GapDeg/2) instead of overflowing past
    // them. The dim track spans that capped range; the lit fill grows from the high-angle (bottom)
    // end so the shield drains the same direction the hull blocks do. Used for the SHLD/ENRG wrap arcs
    // and, at InnerRadius, for the fuel-pod/ammo-pack LOAD sweeps.
    private void SolidArc(Vector2 c, float radius, float centerDeg, float value, Color lit, Color track, float width)
    {
        float half = SpanDeg * 0.5f - GapDeg * 0.5f; // cap to the hull's lit extent, not the full span
        float lo = centerDeg - half; // top end (aligned to the first hull block's start)
        float hi = centerDeg + half; // bottom end (aligned to the last hull block's end)
        DrawArc(c, radius, Mathf.DegToRad(lo), Mathf.DegToRad(hi), 48, track, width, true);
        float v = Mathf.Clamp(value, 0f, 1f);
        if (v > 0f)
            DrawArc(c, radius, Mathf.DegToRad(hi - (hi - lo) * v), Mathf.DegToRad(hi), 48, lit, width, true);
    }

    // Draw "TAG value" in JetBrains Mono — tag in `tagColor`, value in TextHi. When
    // rightAlign, the pair ends at `anchor` (used for the left/BST gauge).
    private void DrawTagValue(Vector2 anchor, string tag, string value, Color tagColor, bool rightAlign)
    {
        const int size = 12;
        Font font = UiFonts.Mono;
        string combined = tag + " " + value;
        float tagW = font.GetStringSize(tag + " ", HorizontalAlignment.Left, -1, size).X;
        Vector2 pos = anchor;
        if (rightAlign)
        {
            float totalW = font.GetStringSize(combined, HorizontalAlignment.Left, -1, size).X;
            pos.X -= totalW;
        }
        DrawString(font, pos, tag, HorizontalAlignment.Left, -1, size, tagColor);
        DrawString(font, pos + new Vector2(tagW, 0f), value, HorizontalAlignment.Left, -1, size, DesignTokens.TextHi);
    }

    // Centre-aligned mono string (the CLK tag below the ring — no left/right anchor to hang off).
    private void DrawCentered(Vector2 center, string text, Color color)
    {
        const int size = 12;
        Font font = UiFonts.Mono;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(font, center - new Vector2(w * 0.5f, 0f), text, HorizontalAlignment.Left, -1, size, color);
    }

    // The design's saPulse: dip alpha on a ~0.7s cycle. Time-driven (not a per-frame accumulator) so
    // it needs no extra per-instance state — matches HardpointMarkerOverlay's Pulse().
    private static Color Pulsed(Color c)
    {
        float a = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin(Time.GetTicksMsec() / 220f));
        return new Color(c, a);
    }

    // Green at full hull, amber at half, red near death — matches the design's HP arc ramp
    // and the existing base-health bar in TargetMarkers.
    private static Color HealthColor(float frac) =>
        frac > 0.5f ? DesignTokens.Ok
        : frac > 0.25f ? DesignTokens.Warn
        : DesignTokens.Danger;

    // Amber tank, red when critically low (<=25%) — mirrors HealthColor's low-end tier so an
    // empty-tank warning reads the same way a failing hull does.
    private static Color FuelColor(float frac) => frac > 0.25f ? DesignTokens.Warn : DesignTokens.Danger;
}
