using Godot;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// The DRAWING half of the flight-HUD contact markers, split out of TargetMarkers: every
// immediate-mode primitive a marker is made of — class glyphs, bracket reticles, lock/health arcs,
// damage bars, edge-clamped off-screen arrows, captions and warning banners — plus the world→screen
// projection each one is anchored by.
//
// TargetMarkers keeps the per-frame half (which contacts to show, the Tab focus, fog gating, the
// marker cap) and calls in here to render them. Nothing in here reads simulation, net or selection
// state: each method is a pure function of a screen point (or a world point plus the camera that
// projects it) and a style. Marker GEOMETRY sizes and the palette that is pure decoration live here
// with the shapes they define; an entity's IDENTITY colour (team tint, focus tint, aleph/rock
// chrome) is chosen by the caller and passed in.
internal sealed class MarkerDraw
{
    private const float FocusHalf = 16f; // focused lock-bracket half-extent (px)
    private const float ArrowSize = 13f; // off-screen arrow half-extent (px)
    private const float EdgeMargin = 34f; // off-screen arrow inset from viewport edge (px)
    private const float LeadRadius = 13f; // lead-indicator circle radius (px)
    private const float AimRadius = 8f; // aim-reticle gunsight radius (px)

    // Class-glyph radius (px). Internal: the rock-caption pass in TargetMarkers hangs its label off
    // the same offset, so the two must read one constant.
    internal const float GlyphSize = 8f;

    // Fog last-known ghost contact opacity — dim enough to read as memory, not a live marker.
    private const float GhostAlpha = 0.32f;

    // Screen-space base damage bar (px). Drawn directly over each damaged base's projected
    // position so it can never clip behind the base geometry the way a world-space quad did.
    private const float BaseBarWidth = 64f;
    private const float BaseBarHeight = 6f;
    private const float BaseBarYOffset = 22f; // bar centre this many px above the base centre

    // Chrome pulls from the shared design tokens. Focus = the amber "selection" highlight
    // (Secondary); the lead indicator shares that amber so it reads as belonging to the
    // focused target (the design colours the lead to the target's chrome). Aim reticle = the
    // cyan structural accent.
    private static readonly Color FocusColor = DesignTokens.Secondary;
    private static readonly Color AimColor = DesignTokens.TeamAccent;

    // Rock-class glyph tints: gameplay IDENTITY read off streamed rock data, echoing the 3D
    // material families — deliberately NOT chrome tokens (same rule as Faction0/Faction1).
    private static readonly Color RockHelium3 = new(0.45f, 0.85f, 0.95f); // bright cyan — the valuable one
    private static readonly Color RockUranium = new(0.95f, 0.45f, 0.25f); // orange-red — hazard
    private static readonly Color RockSilicon = new(0.65f, 0.85f, 0.60f); // pale green
    private static readonly Color RockCarbonaceous = new(0.55f, 0.68f, 0.90f); // cool blue

    // The He3 glyph's specular sparkle. A near-white highlight, not a text tier.
    private static readonly Color GlyphHighlight = new(1f, 1f, 1f, 0.85f);

    // Hull-health traffic light: green → yellow → red. A gameplay readability ramp, kept separate
    // from the Ok/Warn/Danger chrome tokens (those are a mint/amber/pink family that would wash the
    // bar out against the sector). HealthColor lerps between these.
    private static readonly Color HealthFull = new(0.15f, 0.85f, 0.15f);
    private static readonly Color HealthHalf = new(0.9f, 0.85f, 0.15f);
    private static readonly Color HealthLow = new(0.9f, 0.15f, 0.15f);

    // The waypoint diamond uses the cyan structural accent (chrome), distinct from the enemy-red
    // brackets so a nav destination never reads as a threat.
    private static readonly Color WaypointColor = DesignTokens.TeamAccent;

    // The per-class symbol drawn at each marker. A pod overrides the hull class; Aleph is a
    // world landmark (warp gate) rather than a ship/base; Probe is a deployed recon beacon.
    public enum Kind
    {
        Base,
        Scout,
        Fighter,
        Bomber,
        Miner,
        Pod,
        Aleph,
        Probe,
        Mine,
        Asteroid,
    }

    // The overlay every primitive draws onto (the TargetMarkers Control).
    private readonly CanvasItem _ci;

    // Reusable scratch arrays for DrawColoredPolygon — Godot copies on call so sequential
    // reuse is safe. Eliminates per-draw allocation for every entity marker drawn.
    private readonly Vector2[] _poly3 = new Vector2[3]; // Scout tri
    private readonly Vector2[] _poly4 = new Vector2[4]; // Fighter chevron
    private readonly Vector2[] _poly5 = new Vector2[5]; // Miner pentagon
    private readonly Vector2[] _poly6 = new Vector2[6]; // Bomber hexagon

    public MarkerDraw(CanvasItem ci) => _ci = ci;

    // ---- projection ---------------------------------------------------------------------------

    // Project a world position for a marker that only draws while its anchor is ON SCREEN (the
    // captions, bars and arcs below): false when the point is behind the camera or its projection
    // falls outside the viewport — exactly the gate each of those draws applied inline before.
    private static bool TryProject(Camera3D cam, Vector3 worldPos, Vector2 view, out Vector2 sp)
    {
        if (cam.IsPositionBehind(worldPos))
        {
            sp = Vector2.Zero;
            return false;
        }
        sp = cam.UnprojectPosition(worldPos);
        return new Rect2(Vector2.Zero, view).HasPoint(sp);
    }

    // The EdgeMargin-inset viewport rect: inside it a marker draws in place, outside it clamps to
    // the edge as an arrow.
    private static Rect2 OnScreenRect(Vector2 view) => new Rect2(Vector2.Zero, view).Grow(-EdgeMargin);

    // Clamp an off-screen (or behind-camera) marker to the inset viewport edge: given the
    // marker's projected screen point `sp` (already un-mirrored for behind-camera points via
    // center*2 - sp) and the viewport size, return the point on the EdgeMargin-inset rectangle
    // edge along the ray from center, and the outward unit direction along that ray. Shared by
    // every edge indicator — live entities, the incoming-missile threat arrow, and fog ghosts —
    // so they all pin to the same border. Thin wrapper over UiDraw.ClampToEdge (the canonical
    // math, also used by SectorOverview's rock-order glyph) that binds EdgeMargin and keeps the
    // `out dir` shape the callers here are written against.
    private static Vector2 ClampToEdge(Vector2 sp, Vector2 view, out Vector2 dir)
    {
        (Vector2 edge, dir) = UiDraw.ClampToEdge(sp, view, EdgeMargin);
        return edge;
    }

    // ---- entity markers -----------------------------------------------------------------------

    // Draw one entity marker. On screen: enemies get a corner bracket + class glyph (focus =
    // larger/brighter); friendlies/bases get a subtle, dimmer class glyph. Off screen or
    // behind the camera: an edge-clamped class glyph + an arrow pointing the way to turn — unless
    // hideOffScreen is set, in which case an off-screen entity draws nothing (used to keep distant
    // friendly probes from crowding the screen edges while still marking them when in view).
    public void Entity(
        Camera3D cam,
        Vector2 size,
        Vector3 worldPos,
        Kind kind,
        Color color,
        bool focused,
        bool friendly,
        string glyph = "",
        string label = "",
        bool hideOffScreen = false
    )
    {
        Vector2 center = size * 0.5f;

        bool behind = cam.IsPositionBehind(worldPos);
        Vector2 sp = cam.UnprojectPosition(worldPos);
        // A point behind the camera unprojects mirrored about the center; flip it back
        // so the edge arrow points to the correct side.
        if (behind)
            sp = center * 2f - sp;

        bool onScreen = !behind && OnScreenRect(size).HasPoint(sp);

        if (onScreen)
        {
            if (friendly)
            {
                // Subtle teammate / base marker: dimmer and small so it never competes with
                // the enemy reticles or clutters the view.
                ClassGlyph(sp, kind, new Color(color, 0.55f), GlyphSize * 0.85f, glyph);
                if (label.Length > 0)
                    EntityLabel(sp, GlyphSize * 0.85f, color, label);
            }
            else
            {
                // Enemy on screen: the same class glyph as the off-screen indicator so the
                // marker reads identically whether it's at the edge or in view. The focused
                // target is enlarged, recolored, and wrapped in a lock bracket (there's no
                // edge arrow on screen to set it apart otherwise).
                ClassGlyph(sp, kind, color, focused ? GlyphSize * 1.15f : GlyphSize, glyph);
                if (focused)
                    Bracket(sp, FocusHalf, color, 2.5f);
            }
            return;
        }

        if (hideOffScreen)
            return; // off screen and suppressed (e.g. a distant friendly probe) — no edge marker

        // Off screen: clamp the marker to the inset viewport edge along the ray from center,
        // draw the class glyph there and an arrow just outside it pointing outward.
        Vector2 edge = ClampToEdge(sp, size, out Vector2 dir);
        float glyphScale = focused ? GlyphSize * 1.15f : GlyphSize;
        Vector2 glyphPos = edge - dir * (ArrowSize + 2f);
        ClassGlyph(glyphPos, kind, color, glyphScale, glyph);
        Arrow(edge, dir, color);
        if (label.Length > 0)
            EntityLabel(glyphPos, glyphScale, color, label);
    }

    // One fog last-known ghost contact: a dim, low-alpha class glyph at the remembered position,
    // wrapped in a faint hollow ring so it reads as a stale contact rather than a live enemy
    // marker. On screen it sits at the remembered position; off screen (or behind the camera) it
    // clamps to the viewport edge with an arrow pointing the way to the last-known contact — the
    // same edge treatment as a live entity, but dimmed to read as memory (never a bracket or lead —
    // a ghost isn't something to chase or lock).
    public void GhostMarker(Camera3D cam, Vector2 view, Vector3 worldPos, Kind kind, Color teamColor, string glyph)
    {
        Vector2 center = view * 0.5f;
        bool behind = cam.IsPositionBehind(worldPos);
        Vector2 sp = cam.UnprojectPosition(worldPos);
        // A point behind the camera unprojects mirrored about the center; flip it back so the
        // edge marker pins to the correct side.
        if (behind)
            sp = center * 2f - sp;
        Color c = new(teamColor, GhostAlpha);

        if (!behind && OnScreenRect(view).HasPoint(sp))
        {
            ClassGlyph(sp, kind, c, GlyphSize * 0.85f, glyph);
            // Faint hollow ring: the "last-known contact" cue that sets a ghost apart from a live
            // (but dim) friendly/base glyph.
            _ci.DrawArc(sp, GlyphSize * 1.5f, 0f, Mathf.Tau, 16, new Color(teamColor, GhostAlpha * 0.7f), 1f, true);
            return;
        }

        // Off screen: clamp to the inset viewport edge, draw the dim class glyph there and an
        // arrow pointing outward — the reduced alpha (GhostAlpha) keeps it reading as a
        // remembered contact, not a live threat.
        Vector2 edge = ClampToEdge(sp, view, out Vector2 dir);
        ClassGlyph(edge - dir * (ArrowSize + 2f), kind, c, GlyphSize * 0.85f, glyph);
        Arrow(edge, dir, c);
    }

    // A fog stale-memory base marker: a dim, desaturated hollow square with a small cross, so a
    // destroyed-but-remembered station reads as wreckage rather than a live base (which draws a
    // filled square). On-screen only — a wreck needs no chase arrow. Skipped if behind the camera.
    public void StaleBase(Camera3D cam, Vector2 view, Vector3 worldPos, Color teamColor)
    {
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;

        // Desaturate the team colour toward the dim text token and drop the alpha — a faded memory.
        Color c = new(teamColor.Lerp(DesignTokens.TextDim, 0.5f), 0.5f);
        float r = GlyphSize;
        _ci.DrawRect(new Rect2(sp - new Vector2(r, r), new Vector2(r * 2f, r * 2f)), c, filled: false, width: 1.5f);
        // Small cross through the centre: the "destroyed" cue.
        _ci.DrawLine(sp + new Vector2(-r * 0.5f, -r * 0.5f), sp + new Vector2(r * 0.5f, r * 0.5f), c, 1f, true);
        _ci.DrawLine(sp + new Vector2(-r * 0.5f, r * 0.5f), sp + new Vector2(r * 0.5f, -r * 0.5f), c, 1f, true);
    }

    // Screen-space damage bar over a base: project the base centre, then draw a fixed-size
    // pixel bar a little above it (a dark backdrop + a left-anchored fill that depletes
    // rightward and ramps green->red). Being a 2D overlay it always draws on top, so unlike
    // the old world-space quad it never clips behind the base from a low angle. Skipped when
    // the base is behind the camera or its centre projects off screen.
    public void BaseHealthBar(Camera3D cam, Vector2 view, Vector3 worldPos, float frac)
    {
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;

        Vector2 topLeft = sp + new Vector2(-BaseBarWidth * 0.5f, -BaseBarYOffset);
        // Dark backdrop with a 1px border so the bar reads against bright or dark scenery.
        _ci.DrawRect(
            new Rect2(topLeft - Vector2.One, new Vector2(BaseBarWidth + 2f, BaseBarHeight + 2f)),
            new Color(DesignTokens.Void, 0.75f)
        );
        // Left-anchored fill, width scaled by the health fraction.
        _ci.DrawRect(new Rect2(topLeft, new Vector2(BaseBarWidth * frac, BaseBarHeight)), HealthColor(frac));
    }

    // The missile lock-progress arc wrapping the focused target's bracket, driven by the local
    // ship's own LockState (`locked` + `progress` 0..100, decoded by the caller). A partial cyan arc
    // grows clockwise from the top while the lock timer runs; once locked it snaps to a full steady
    // ring with a LOCK tag in `lockColor` (a shade of the target's team color). Skipped when the
    // target is behind us.
    public void LockRing(Camera3D cam, Vector3 worldPos, bool locked, int progress, Color lockColor)
    {
        if (cam.IsPositionBehind(worldPos))
            return;
        Vector2 sp = cam.UnprojectPosition(worldPos);
        float r = FocusHalf + 7f;
        if (locked)
        {
            _ci.DrawArc(sp, r, 0f, Mathf.Tau, 32, lockColor, 2.5f, true);
            CenteredText(sp, -r - 3f, "LOCK", 9, lockColor);
        }
        else
        {
            float start = -Mathf.Pi * 0.5f; // 12 o'clock
            float sweep = Mathf.Clamp(progress / 100f, 0f, 1f) * Mathf.Tau;
            _ci.DrawArc(sp, r, start, start + sweep, 32, AimColor, 2f, true);
        }
    }

    // The focused target's condition indicator: a bottom-left quarter arc wrapping the bracket that
    // drains and shifts green→amber→red as its hull falls, with a thin cyan shield band just outside
    // (shielded hulls only) — the design's target HP arc. Uses the same tiered colours as the local
    // SystemRing gauge so the target and own-ship readouts agree. Only drawn when the target is on
    // screen and has taken damage, so a pristine target stays uncluttered.
    public void TargetHealthArc(
        Camera3D cam,
        Vector2 view,
        Vector3 worldPos,
        float hullFrac,
        bool hasShield,
        float shieldFrac
    )
    {
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;

        // Nothing to say about a pristine target — keep the marker clean until it's actually hurt.
        if (hullFrac >= 1f && (!hasShield || shieldFrac >= 1f))
            return;

        // Bottom-left quarter: 6 o'clock (Godot 90°) → 9 o'clock (180°), the fill growing from the
        // 6 o'clock end so the arc drains toward 9 o'clock like the design's HP arc and the bottom-lit
        // SystemRing gauges. (Design degrees are 0=top clockwise; Godot's DrawArc is 0=+X clockwise,
        // so a design degree maps to Godot angle = deg − 90.)
        const float lo = Mathf.Pi * 0.5f; // 6 o'clock
        const float hi = Mathf.Pi; // 9 o'clock
        Color track = DesignTokens.BorderLo;

        // HULL arc — just outside the bracket, inside the lock ring (FocusHalf + 7f) so the
        // bottom-left quarter reads distinctly against the full lock ring.
        float hullR = FocusHalf + 6f;
        _ci.DrawArc(sp, hullR, lo, hi, 24, track, 3f, true);
        _ci.DrawArc(sp, hullR, lo, lo + (hi - lo) * hullFrac, 24, HullColor(hullFrac), 3f, true);

        // SHIELD band — a thinner cyan (chrome) arc one band outside the hull, on shielded hulls,
        // filled from the same 6 o'clock end. Mirrors SystemRing's solid outer SHLD band.
        if (hasShield)
        {
            float shieldR = FocusHalf + 11f;
            _ci.DrawArc(sp, shieldR, lo, hi, 24, track, 2f, true);
            if (shieldFrac > 0f)
                _ci.DrawArc(sp, shieldR, lo, lo + (hi - lo) * shieldFrac, 24, DesignTokens.TeamAccent, 2f, true);
        }
    }

    // The navigation waypoint: a hollow cyan (chrome) diamond with a center dot at the dropped
    // point. On screen it sits at the point; off screen (or behind the camera) it clamps to the
    // viewport edge with an arrow pointing the way — the same edge treatment as live entities.
    // Distinct from the enemy-red brackets and the amber focus chrome so a nav destination never
    // reads as a threat.
    public void WaypointMarker(Camera3D cam, Vector2 view, Vector3 worldPos)
    {
        Vector2 center = view * 0.5f;
        bool behind = cam.IsPositionBehind(worldPos);
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (behind)
            sp = center * 2f - sp;

        if (!behind && OnScreenRect(view).HasPoint(sp))
        {
            float r = GlyphSize * 1.15f;
            UiDraw.HollowDiamondMarker(_ci, sp, r, WaypointColor, "NAV", UiFonts.Mono, 9);
            return;
        }

        Vector2 edge = ClampToEdge(sp, view, out Vector2 dir);
        Arrow(edge, dir, WaypointColor);
    }

    // An edge-clamped arrow pointing at a world point, reusing the off-screen clamp path — it points
    // the way to turn even when the point is on screen (a threat indicator, not just an off-screen
    // marker). Used by the incoming-missile warning.
    public void EdgeArrowTo(Camera3D cam, Vector2 view, Vector3 worldPos, Color color)
    {
        Vector2 center = view * 0.5f;
        bool behind = cam.IsPositionBehind(worldPos);
        Vector2 sp = cam.UnprojectPosition(worldPos);
        if (behind)
            sp = center * 2f - sp;
        Vector2 edge = ClampToEdge(sp, view, out Vector2 dir);
        Arrow(edge, dir, color);
    }

    // ---- tags, captions, banners --------------------------------------------------------------

    // The focused target's "▣ TARGET" tag above its marker and range below, in mono. Only
    // drawn when the focus is on screen; skipped when behind the camera or off-screen (the
    // edge arrow already points the way). `rangeFrom` is the point to measure the range from
    // (the local ship) — null skips the range line. Range is in world units, matching the HUD's u/s.
    public void FocusTag(Camera3D cam, Vector2 view, Vector3 worldPos, Color tint, Vector3? rangeFrom)
    {
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;

        CenteredText(sp, -FocusHalf - 9f, "▣ TARGET", 11, tint);
        if (rangeFrom is Vector3 from)
            CenteredText(sp, FocusHalf + 17f, $"{(worldPos - from).Length():0} u", 10, DesignTokens.Text2);
    }

    // A small role tag (e.g. "MINER") under a focused ship's bracket, in neutral data chrome.
    public void RoleTag(Camera3D cam, Vector2 view, Vector3 worldPos, string tag)
    {
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;
        CenteredText(sp, FocusHalf + 29f, tag, 10, DesignTokens.Data);
    }

    // A small mono type caption centred just below a ship's glyph on the F3 map. Team-coloured and
    // slightly dimmed so it annotates without competing with the glyph. Skipped when the text is
    // empty (pods) or the ship is behind the camera / projects off screen.
    public void TypeLabel(Camera3D cam, Vector2 view, Vector3 worldPos, string text, Color color)
    {
        if (text.Length == 0)
            return;
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;
        CenteredText(sp, GlyphSize + 12f, text, 9, new Color(color, 0.85f));
    }

    // A focused rock's resource caption under its TARGET tag, with the special-class glyph echoed
    // beside it so it matches the near/F3 rock labels. Neutral data chrome, minimal text — no new
    // panel. Drawn a line below FocusTag's range readout.
    public void RockDetail(Camera3D cam, Vector2 view, Vector3 worldPos, string label, byte rockClass, Color color)
    {
        if (!TryProject(cam, worldPos, view, out Vector2 sp))
            return;
        float w = CenteredText(sp, FocusHalf + 29f, label, 10, color);
        const float rg = 5.5f;
        RockGlyph(sp + new Vector2(-w * 0.5f - rg - 4f, FocusHalf + 29f - 3f), rockClass, rg, RockGlyphColor(rockClass));
    }

    // A short mono caption centred horizontally on `sp` and offset `dy` px vertically — the shape
    // every marker tag shares (TARGET / LOCK / role tags / rock + type captions). Returns the
    // measured text width so a caller can hang a glyph off its left edge.
    public float CenteredText(Vector2 sp, float dy, string text, int fontSize, Color color)
    {
        Font font = UiFonts.Mono;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        _ci.DrawString(font, sp + new Vector2(-w * 0.5f, dy), text, HorizontalAlignment.Left, -1, fontSize, color);
        return w;
    }

    // A mono banner centred horizontally and sat `yFrac` down the viewport — the flight-HUD status
    // line (incoming missile, missile lock, contact lost, autopilot). The throb/fade lives in the
    // caller's colour: it owns the state driving it.
    public void CenterBanner(Vector2 view, float yFrac, string text, int fontSize, Color color)
    {
        Font font = UiFonts.Mono;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        _ci.DrawString(
            font,
            new Vector2(view.X * 0.5f - w * 0.5f, view.Y * yFrac),
            text,
            HorizontalAlignment.Left,
            -1,
            fontSize,
            color
        );
    }

    // A small mono caption drawn just to the right of an entity glyph (e.g. the destination
    // sector name beside a warp gate). Dimmer than the glyph so it annotates without competing.
    private void EntityLabel(Vector2 p, float r, Color color, string label)
    {
        Font font = UiFonts.Mono;
        const int fs = 10;
        var pos = new Vector2(p.X + r + 5f, p.Y + (font.GetAscent(fs) - font.GetDescent(fs)) * 0.5f);
        _ci.DrawString(font, pos, label, HorizontalAlignment.Left, -1, fs, new Color(color, 0.8f));
    }

    // ---- glyphs and reticles ------------------------------------------------------------------

    // A small symbol encoding the entity class, centered on p. Ship hulls render their authored
    // glyph (ShipClassDef.Glyph, e.g. ▲/◆/⬢) as mono text so a new hull's marker is data-driven;
    // the non-ship landmarks (base square, warp-gate rings) and any glyph-less hull fall back to
    // the distinct drawn silhouettes so class still reads at a glance even tiny.
    private void ClassGlyph(Vector2 p, Kind kind, Color color, float r, string glyph = "")
    {
        // Only take the text path when the mono font can actually render every char of the authored
        // glyph — JetBrains Mono has no ⬟ (miner) or ⬢ (bomber), so those would draw as invisible tofu.
        // When a glyph is unsupported (or empty), fall through to the drawn silhouette below so the
        // class still reads. (Keeps the marker data-driven for hulls whose glyph the font DOES carry.)
        if (glyph.Length > 0 && FontHasGlyph(UiFonts.Mono, glyph))
        {
            Font font = UiFonts.Mono;
            int fs = Mathf.RoundToInt(r * 2.6f);
            Vector2 sz = font.GetStringSize(glyph, HorizontalAlignment.Left, -1, fs);
            // Center both axes: x off the measured width, y off the baseline (ascent/descent).
            var pos = new Vector2(p.X - sz.X * 0.5f, p.Y + (font.GetAscent(fs) - font.GetDescent(fs)) * 0.5f);
            _ci.DrawString(font, pos, glyph, HorizontalAlignment.Left, -1, fs, color);
            return;
        }
        switch (kind)
        {
            case Kind.Base:
                // Station: filled square with a punched-out center dot.
                _ci.DrawRect(new Rect2(p - new Vector2(r, r), new Vector2(r * 2f, r * 2f)), color);
                _ci.DrawCircle(p, r * 0.4f, new Color(DesignTokens.Void, 0.85f));
                break;
            case Kind.Scout:
                // Slim upward triangle.
                _poly3[0] = p + new Vector2(0f, -r);
                _poly3[1] = p + new Vector2(r * 0.8f, r * 0.7f);
                _poly3[2] = p + new Vector2(-r * 0.8f, r * 0.7f);
                _ci.DrawColoredPolygon(_poly3, color);
                break;
            case Kind.Fighter:
                // Chevron / arrowhead (tip up, notched base).
                _poly4[0] = p + new Vector2(0f, -r);
                _poly4[1] = p + new Vector2(r, r * 0.7f);
                _poly4[2] = p + new Vector2(0f, r * 0.25f);
                _poly4[3] = p + new Vector2(-r, r * 0.7f);
                _ci.DrawColoredPolygon(_poly4, color);
                break;
            case Kind.Bomber:
                // Heavy hexagon.
                for (int i = 0; i < 6; i++)
                {
                    float a = Mathf.Pi / 6f + i * Mathf.Tau / 6f;
                    _poly6[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                }
                _ci.DrawColoredPolygon(_poly6, color);
                break;
            case Kind.Miner:
                // Industrial ore hull: an upright filled pentagon (echoes the authored ⬟ glyph),
                // distinct from the fighter chevron and bomber hexagon so a miner reads at a glance.
                for (int i = 0; i < 5; i++)
                {
                    float a = -Mathf.Pi / 2f + i * Mathf.Tau / 5f;
                    _poly5[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                }
                _ci.DrawColoredPolygon(_poly5, color);
                break;
            case Kind.Pod:
                // Small circle.
                _ci.DrawCircle(p, r * 0.85f, color);
                break;
            case Kind.Aleph:
                // Warp gate: concentric hollow rings (a portal/vortex), distinct from the
                // solid pod circle and never team-colored.
                _ci.DrawArc(p, r, 0f, Mathf.Tau, 20, color, 1.6f, true);
                _ci.DrawArc(p, r * 0.5f, 0f, Mathf.Tau, 16, color, 1.4f, true);
                break;
            case Kind.Probe:
                // Recon probe: a hollow diamond (sensor beacon) with a bright center dot — distinct
                // from the filled pod circle and the aleph's concentric rings. Drawn as four line
                // segments off the reused _poly4 scratch so the glyph allocates nothing.
                _poly4[0] = p + new Vector2(0f, -r);
                _poly4[1] = p + new Vector2(r, 0f);
                _poly4[2] = p + new Vector2(0f, r);
                _poly4[3] = p + new Vector2(-r, 0f);
                _ci.DrawLine(_poly4[0], _poly4[1], color, 1.5f, true);
                _ci.DrawLine(_poly4[1], _poly4[2], color, 1.5f, true);
                _ci.DrawLine(_poly4[2], _poly4[3], color, 1.5f, true);
                _ci.DrawLine(_poly4[3], _poly4[0], color, 1.5f, true);
                _ci.DrawCircle(p, r * 0.32f, color);
                break;
            case Kind.Mine:
                // Deployed ordnance: a spiked hazard burst — a filled core with six radiating
                // spikes off the reused _poly6 scratch. Distinct from the pod's plain circle, the
                // probe's diamond, and the aleph's concentric rings.
                for (int i = 0; i < 6; i++)
                {
                    float a = i * Mathf.Tau / 6f;
                    _poly6[i] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    _ci.DrawLine(p, _poly6[i], color, 1.5f, true);
                }
                _ci.DrawCircle(p, r * 0.45f, color);
                break;
            case Kind.Asteroid:
                // Navigation rock: a hollow ring with a small center dot — a neutral, non-threat
                // marker distinct from the pod's filled circle and the aleph's concentric rings.
                _ci.DrawArc(p, r, 0f, Mathf.Tau, 16, color, 1.5f, true);
                _ci.DrawCircle(p, r * 0.3f, color);
                break;
        }
    }

    // True when the font carries a glyph for every char of s (BMP codepoints — the authored hull
    // symbols are all single BMP chars). Guards the text path in ClassGlyph so an authored symbol
    // the mono font lacks (⬟/⬢) falls back to a drawn silhouette instead of rendering invisible tofu.
    private static bool FontHasGlyph(Font font, string s)
    {
        foreach (char c in s)
            if (!font.HasChar(c))
                return false;
        return true;
    }

    // A small distinctive vector icon for each of the four "special" resource classes, drawn beside a
    // rock's HUD label so the valuable rocks read at a glance. Shapes are chosen to stay distinct from
    // each other AND from the ship glyphs in ClassGlyph; commons (Regolith) draw nothing. Tinted
    // to echo each rock's material family (RockGlyphColor). Reuses the preallocated _poly* scratch —
    // allocates nothing per frame.
    public void RockGlyph(Vector2 center, byte rockClass, float r, Color color)
    {
        switch ((RockClass)rockClass)
        {
            case RockClass.Helium3:
                // THE valuable one: a bright filled crystalline diamond (rotated, slightly narrow) with a
                // tiny sparkle dot — solid, so it never reads as the probe's hollow diamond.
                _poly4[0] = center + new Vector2(0f, -r);
                _poly4[1] = center + new Vector2(r * 0.72f, 0f);
                _poly4[2] = center + new Vector2(0f, r);
                _poly4[3] = center + new Vector2(-r * 0.72f, 0f);
                _ci.DrawColoredPolygon(_poly4, color);
                _ci.DrawCircle(center + new Vector2(0f, -r * 0.28f), r * 0.22f, GlyphHighlight);
                break;
            case RockClass.Uranium:
                // Radiation trefoil: three filled blades at 120° around a hot center dot — a hazard read,
                // distinct from the scout's single upright triangle.
                for (int i = 0; i < 3; i++)
                {
                    float a = -Mathf.Pi / 2f + i * Mathf.Tau / 3f;
                    _ci.DrawCircle(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.68f, r * 0.42f, color);
                }
                _ci.DrawCircle(center, r * 0.3f, color);
                break;
            case RockClass.Silicon:
                // Faceted crystal: a tall pointy-top hexagon (a standing gem), distinct from the bomber's
                // flat regular hexagon by both proportion and its pale tint.
                for (int i = 0; i < 6; i++)
                {
                    float a = -Mathf.Pi / 2f + i * Mathf.Tau / 6f;
                    _poly6[i] = center + new Vector2(Mathf.Cos(a) * r * 0.68f, Mathf.Sin(a) * r);
                }
                _ci.DrawColoredPolygon(_poly6, color);
                break;
            case RockClass.Carbonaceous:
                // Rubble pile: a lumpy blob — a main disc with two smaller overlapping lobes — distinct
                // from the pod's clean single circle and the asteroid glyph's hollow ring.
                _ci.DrawCircle(center, r * 0.72f, color);
                _ci.DrawCircle(center + new Vector2(-r * 0.55f, r * 0.28f), r * 0.4f, color);
                _ci.DrawCircle(center + new Vector2(r * 0.5f, -r * 0.35f), r * 0.34f, color);
                break;
        }
    }

    // Material-family tint for each special rock's HUD glyph so the icon echoes the 3D material look.
    public static Color RockGlyphColor(byte cls) =>
        (RockClass)cls switch
        {
            RockClass.Helium3 => RockHelium3,
            RockClass.Uranium => RockUranium,
            RockClass.Silicon => RockSilicon,
            RockClass.Carbonaceous => RockCarbonaceous,
            _ => DesignTokens.Text2,
        };

    // A rounded four-corner bracket reticle centered on p: four short arcs at the diagonal corners
    // (with gaps at the cardinal directions, where the ticks/lead/tag sit). Drawn on a circle of
    // radius h so the reticle is curved and concentric with the target's health arc — the rounded
    // corners echo the gauge arcs instead of clashing with a square four-corner bracket.
    private void Bracket(Vector2 p, float h, Color color, float width)
    {
        const float span = 26f * (Mathf.Pi / 180f); // half-angle each corner arc extends around its diagonal
        // Godot angles: 0° = +X (right), 90° = down. The corners sit on the four diagonals.
        for (int i = 0; i < 4; i++)
        {
            float mid = Mathf.Pi * 0.25f + i * Mathf.Pi * 0.5f; // 45°, 135°, 225°, 315°
            _ci.DrawArc(p, h, mid - span, mid + span, 10, color, width, true);
        }
    }

    // A gunsight at p marking the firing line: a ring with four short spokes and a
    // center dot, so it reads clearly against ships and the lead circle.
    public void AimReticle(Vector2 p)
    {
        _ci.DrawArc(p, AimRadius, 0f, Mathf.Tau, 24, AimColor, 1.5f, true);
        float inner = AimRadius + 1f;
        float outer = AimRadius + 5f;
        _ci.DrawLine(p + new Vector2(-outer, 0f), p + new Vector2(-inner, 0f), AimColor, 1.5f, true);
        _ci.DrawLine(p + new Vector2(outer, 0f), p + new Vector2(inner, 0f), AimColor, 1.5f, true);
        _ci.DrawLine(p + new Vector2(0f, -outer), p + new Vector2(0f, -inner), AimColor, 1.5f, true);
        _ci.DrawLine(p + new Vector2(0f, outer), p + new Vector2(0f, inner), AimColor, 1.5f, true);
        _ci.DrawCircle(p, 1.5f, AimColor);
    }

    // The lead indicator for the focused target: a dashed connector from the target marker to
    // the firing-solution point, then a ringed crosshair at the lead point with a "LEAD" tag —
    // echoing the design's lead mark. Amber (FocusColor) so it reads as part of the focused
    // target's chrome. `target` is the target's screen point (null if it's behind the camera,
    // in which case the connector is skipped but the lead mark still draws).
    public void LeadIndicator(Vector2? target, Vector2 lp)
    {
        if (target is Vector2 tp)
            DashedLine(tp, lp, new Color(FocusColor, 0.55f), 1f, 5f, 4f);

        // Soft glow (a faint wider ring — _Draw has no box-shadow) under the crisp ring.
        _ci.DrawArc(lp, LeadRadius + 2f, 0f, Mathf.Tau, 28, new Color(FocusColor, 0.22f), 3f, true);
        _ci.DrawArc(lp, LeadRadius, 0f, Mathf.Tau, 28, FocusColor, 1.5f, true);

        // Crosshair through the centre, kept inside the ring (design's ±7 in a r≈11 ring).
        float c = LeadRadius * 0.6f;
        _ci.DrawLine(lp + new Vector2(-c, 0f), lp + new Vector2(c, 0f), FocusColor, 1f, true);
        _ci.DrawLine(lp + new Vector2(0f, -c), lp + new Vector2(0f, c), FocusColor, 1f, true);

        _ci.DrawString(
            UiFonts.Mono,
            lp + new Vector2(LeadRadius + 4f, 3f),
            "LEAD",
            HorizontalAlignment.Left,
            -1,
            9,
            new Color(FocusColor, 0.85f)
        );
    }

    // A filled triangle at p pointing along dir (unit). Thin wrapper over UiDraw.DrawEdgeArrow that
    // binds the marker arrow size.
    private void Arrow(Vector2 p, Vector2 dir, Color color) => UiDraw.DrawEdgeArrow(_ci, p, dir, ArrowSize, color);

    // A dashed line from a to b (Godot's own DrawDashedLine measures its dashes differently): march
    // the segment in `dash`-long strokes separated by `gap`, clipping the final stroke to the endpoint.
    private void DashedLine(Vector2 a, Vector2 b, Color color, float width, float dash, float gap)
    {
        Vector2 delta = b - a;
        float len = delta.Length();
        if (len < 0.01f)
            return;
        Vector2 dir = delta / len;
        float step = dash + gap;
        for (float t = 0f; t < len; t += step)
        {
            Vector2 s = a + dir * t;
            Vector2 e = a + dir * Mathf.Min(t + dash, len);
            _ci.DrawLine(s, e, color, width, true);
        }
    }

    // Green at full health, through yellow at half, to red when nearly destroyed.
    private static Color HealthColor(float frac) =>
        frac > 0.5f ? HealthHalf.Lerp(HealthFull, (frac - 0.5f) * 2f) : HealthLow.Lerp(HealthHalf, frac * 2f);

    // Tiered green→amber→red ramp matching the design's HP arc (#4dffa6 / #ffb347 / #ff5a6a) and
    // the local SystemRing gauge. Distinct from HealthColor's continuous lerp, which the base-health
    // bar deliberately keeps.
    private static Color HullColor(float frac) =>
        frac > 0.5f ? DesignTokens.Ok
        : frac > 0.25f ? DesignTokens.Warn
        : DesignTokens.Danger;
}
