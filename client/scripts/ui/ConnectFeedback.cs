using Godot;

namespace StellarAllegiance.Ui;

// ── 06 CONNECT FEEDBACK ──────────────────────────────────────────────────────
// Animated link-status components from the "Connecting" design spec: a rotating
// radar ring with a centred percentage, and a continuous progress bar with a
// sweeping highlight. Both freeze (and recolor) once the link settles.

// Rotating radar: two static range rings, an outer dashed ring carrying four tick
// marks, a counter-rotating inner ring headed by an orange diamond, and a centred
// mono percentage over a tiny "LINK" caption.
public partial class LinkRadar : Control
{
    public enum LinkState
    {
        Busy, // spinning, accent-coloured
        Ok, // frozen, green
        Failed, // frozen, red
    }

    private const float OuterRevSec = 3.4f;
    private const float InnerRevSec = 2.2f;

    private LinkState _state = LinkState.Busy;
    private float _progress; // 0..1
    private float _outer; // radians
    private float _inner; // radians (counter-rotates)

    public override void _Ready()
    {
        UiFonts.EnsureLoaded();
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(104, 104);
        Resized += QueueRedraw;
    }

    public void SetProgress(float v01)
    {
        v01 = Mathf.Clamp(v01, 0f, 1f);
        if (Mathf.IsEqualApprox(v01, _progress))
            return;
        _progress = v01;
        QueueRedraw();
    }

    public void SetLinkState(LinkState state)
    {
        if (state == _state)
            return;
        _state = state;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        // Only animate while actively linking and on screen — redraw discipline.
        if (_state != LinkState.Busy || !IsVisibleInTree())
            return;
        _outer += (float)(delta / OuterRevSec) * Mathf.Tau;
        _inner -= (float)(delta / InnerRevSec) * Mathf.Tau;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = Size * 0.5f;
        float outerR = Mathf.Min(Size.X, Size.Y) * 0.5f - 2f;
        float innerR = outerR - 16f;
        Color ring = _state switch
        {
            LinkState.Ok => new Color(DesignTokens.Ok, 0.4f),
            LinkState.Failed => new Color(DesignTokens.Danger, 0.4f),
            _ => new Color(DesignTokens.TeamAccent, 0.35f),
        };
        Color tick = _state switch
        {
            LinkState.Ok => DesignTokens.Ok,
            LinkState.Failed => DesignTokens.Danger,
            _ => DesignTokens.TeamAccent,
        };

        // Static range rings.
        DrawArc(center, outerR, 0, Mathf.Tau, 64, DesignTokens.BorderLo, 1f, true);
        DrawArc(center, innerR, 0, Mathf.Tau, 48, new Color(DesignTokens.BorderLo, 0.12f), 1f, true);

        // Outer dashed ring, rotated by the accumulator (24 dashes, half duty cycle).
        const int dashes = 24;
        float dashArc = Mathf.Tau / dashes;
        for (int i = 0; i < dashes; i++)
        {
            float a = _outer + i * dashArc;
            DrawArc(center, outerR, a, a + dashArc * 0.5f, 4, ring, 1f, true);
        }

        // Four tick marks riding the outer ring at 90° intervals (soft glow behind each).
        for (int k = 0; k < 4; k++)
        {
            float a = _outer + k * Mathf.Pi * 0.5f;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var pos = center + dir * outerR;
            DrawSetTransform(pos, a + Mathf.Pi * 0.5f, Vector2.One);
            DrawRect(new Rect2(-10, -3.5f, 20, 7), new Color(tick, 0.18f), filled: true);
            DrawRect(new Rect2(-7, -1.5f, 14, 3), tick, filled: true);
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }

        // Counter-rotating inner diamond (the design's orange marker).
        var head = center + new Vector2(Mathf.Cos(_inner), Mathf.Sin(_inner)) * innerR;
        UiDraw.Diamond(this, head, 6f, new Color(DesignTokens.Secondary, 0.25f));
        UiDraw.Diamond(this, head, 4f, DesignTokens.Secondary);

        // Centre readout: percentage + caption.
        Color pctColor = _state switch
        {
            LinkState.Ok => DesignTokens.Ok,
            LinkState.Failed => DesignTokens.DangerText,
            _ => DesignTokens.TextHi,
        };
        string pct = $"{Mathf.RoundToInt(_progress * 100)}%";
        var sz = UiFonts.Mono.GetStringSize(pct, HorizontalAlignment.Left, -1, 24);
        DrawString(UiFonts.Mono, center + new Vector2(-sz.X * 0.5f, 4), pct, HorizontalAlignment.Left, -1, 24, pctColor);
        const string caption = "LINK";
        var cs = UiFonts.SairaLabel.GetStringSize(caption, HorizontalAlignment.Left, -1, 8);
        DrawString(UiFonts.SairaLabel, center + new Vector2(-cs.X * 0.5f, 18), caption, HorizontalAlignment.Left, -1, 8, DesignTokens.TextDim);
    }
}

// Continuous progress bar: faint track, coloured fill, and (while Sweep) a bright
// band drifting across the track — the design's indeterminate-motion cue.
public partial class ProgressSweepBar : Control
{
    private const float SweepSec = 1.6f;

    private float _fill; // 0..1
    private Color _color = DesignTokens.TeamAccent;
    private bool _sweep;
    private float _phase; // 0..1 sweep position

    public bool Sweep
    {
        get => _sweep;
        set
        {
            if (value == _sweep)
                return;
            _sweep = value;
            QueueRedraw();
        }
    }

    public override void _Ready()
    {
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(0, 6);
        Resized += QueueRedraw;
    }

    public void Set(float fill01, Color fillColor)
    {
        fill01 = Mathf.Clamp(fill01, 0f, 1f);
        if (Mathf.IsEqualApprox(fill01, _fill) && fillColor == _color)
            return;
        _fill = fill01;
        _color = fillColor;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!_sweep || !IsVisibleInTree())
            return;
        _phase = Mathf.PosMod(_phase + (float)(delta / SweepSec), 1f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), DesignTokens.BorderLo, filled: true);
        if (_fill > 0f)
            DrawRect(new Rect2(0, 0, Size.X * _fill, Size.Y), _color, filled: true);
        if (!_sweep)
            return;
        // Sweeping highlight: stepped-alpha slices approximating a soft gradient band
        // (same trick as HoloBackdrop's scanline), travelling from off-left to off-right.
        float band = Size.X * 0.4f;
        float x0 = -band + (Size.X + band) * _phase;
        const int steps = 6;
        float slice = band / steps;
        for (int i = 0; i < steps; i++)
        {
            // Alpha peaks mid-band, fades to the edges.
            float t = (i + 0.5f) / steps;
            float a = 0.5f * (1f - Mathf.Abs(t * 2f - 1f));
            var r = new Rect2(x0 + i * slice, 0, slice, Size.Y);
            r = r.Intersection(new Rect2(Vector2.Zero, Size));
            if (r.Size.X > 0)
                DrawRect(r, new Color(DesignTokens.TextHi, a), filled: true);
        }
    }
}
