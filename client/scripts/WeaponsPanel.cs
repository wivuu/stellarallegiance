using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// HUD weapons readout: the bottom-right "◀ WEAPONS" panel from the "Stellar Allegiance" Game-HUD
// design — the symmetric counterpart to the bottom-left Minimap (same bracket frame, same margin).
// It lists the local ship's actual armament and its live state:
//
//   • PRIMARY  — the hull's bolt gun (Space / LMB). Shown in the highlighted accent slot with a
//                fire-cadence bar that charges from just-fired back to READY. The cadence is read
//                from the predictor's own fire gate (ClientTick - LastFireTick vs FireInterval), so
//                the bar reads the same tick space the sim actually fires on.
//   • ORDNANCE — the missile / torpedo launcher (F / RMB), if the hull mounts one. Shown as a
//                secondary row: magazine as diamond pips + the authoritative lock state
//                (READY / LOCK nn% / LOCKED / EMPTY) decoded from the local ship's snapshot.
//   • any further gun mounts fall out as extra secondary rows (rare — most hulls carry one gun).
//
// Pure overlay, exactly like the Minimap/SystemRing siblings: it reads the local ship's
// authoritative-derived state (weapon mounts from the def registry, ammo/lock from GameNetClient,
// fire cadence from the predictor) and draws. It never touches authoritative state. Mouse-transparent
// so it never eats a click meant for the game. Created and wired up by the Hud.
public partial class WeaponsPanel : Control
{
    private const float Margin = 16f; // inset from the viewport's bottom-right corner (matches Minimap)
    private const float PanelW = 244f;
    private const float PadX = 12f;
    private const float PadTop = 10f;
    private const float PadBottom = 12f;
    private const float HeaderH = 18f;
    private const float GapAfterHeader = 8f;
    private const float PrimaryH = 42f; // highlighted primary slot
    private const float SecRowH = 26f; // each secondary weapon row
    private const float RowGap = 4f;

    private WorldRenderer _world = null!;
    private GameNetClient _net = null!;
    private DefRegistry _defs = null!;

    // Pulse phase for the LOCKED cues (mirrors the design's saPulse), advanced by real time.
    private double _t;

    // Distinct weapon mounts for the local ship, rebuilt each frame (a cheap cached lookup on the
    // registry). Deduped by WeaponId in hardpoint order so a twin-barrel gun is one row, not two.
    private readonly List<WeaponDef> _weapons = new();

    public void Init(WorldRenderer world, GameNetClient net, DefRegistry defs)
    {
        _world = world;
        _net = net;
        _defs = defs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme
    }

    public override void _Process(double delta)
    {
        _t += delta;
        var local = _world.LocalShip;
        // Only armed hulls in flight get the readout — a pod (no weapon hardpoint) shows nothing.
        BuildWeapons(local);
        Visible = local != null && !local.IsPod && _weapons.Count > 0 && !SectorOverview.Active;
        if (Visible)
            QueueRedraw();
    }

    // Distinct weapons on the local ship's class, in hardpoint order, deduped by WeaponId.
    private void BuildWeapons(PredictionController? local)
    {
        _weapons.Clear();
        if (local == null || local.IsPod)
            return;
        foreach (var (_, weapon) in _defs.WeaponMounts((byte)local.Class))
        {
            bool seen = false;
            foreach (var w in _weapons)
                if (w.WeaponId == weapon.WeaponId)
                {
                    seen = true;
                    break;
                }
            if (!seen)
                _weapons.Add(weapon);
        }
    }

    public override void _Draw()
    {
        var local = _world.LocalShip;
        if (local == null || _weapons.Count == 0)
            return;

        // Primary = the first bolt gun (the Space/LMB weapon); everything else is a secondary row.
        int primaryIdx = _weapons.FindIndex(w => w.Kind == WeaponKind.Bolt);
        if (primaryIdx < 0)
            primaryIdx = 0; // pathological all-missile hull: still show something as primary
        WeaponDef primary = _weapons[primaryIdx];

        // Dispenser rows (chaff / mine / probe) — NOT hardpoint-mounted, so absent from _weapons;
        // resolved from the class's default hold OR live ammo (the spawned hold can differ from the
        // default).
        byte cls = (byte)local.Class;
        WeaponDef? chaffDisp = DispenserFor(cls, WeaponKind.Chaff, _net.LocalChaffAmmo);
        WeaponDef? mineDisp = DispenserFor(cls, WeaponKind.Mine, _net.LocalMineAmmo);
        WeaponDef? probeDisp = DispenserFor(cls, WeaponKind.Probe, _net.LocalProbeAmmo);

        int secCount =
            _weapons.Count - 1
            + (chaffDisp != null ? 1 : 0)
            + (mineDisp != null ? 1 : 0)
            + (probeDisp != null ? 1 : 0);
        float panelH =
            PadTop
            + HeaderH
            + GapAfterHeader
            + PrimaryH
            + (secCount > 0 ? RowGap + secCount * SecRowH : 0f)
            + PadBottom;

        Vector2 view = GetViewportRect().Size;
        Vector2 panelPos = new(view.X - PanelW - Margin, view.Y - panelH - Margin);
        var panel = new Rect2(panelPos, new Vector2(PanelW, panelH));
        UiDraw.Hairline(this, panel, DesignTokens.PanelFill, DesignTokens.BorderLo);
        UiDraw.CornerBrackets(this, panel, DesignTokens.BracketLength, DesignTokens.TeamAccent);

        Font mono = UiFonts.Mono;
        float left = panelPos.X + PadX;
        float right = panelPos.X + PanelW - PadX;
        float y = panelPos.Y + PadTop;

        // ---- Header: "◀ WEAPONS" (chrome) + a right-aligned ordnance/guns status cue ----
        DrawString(mono, new Vector2(left, y + 12f), "◀ WEAPONS", HorizontalAlignment.Left, -1, 11, DesignTokens.Data);
        DrawHeaderStatus(new Vector2(right, y + 12f), mono);
        y += HeaderH + GapAfterHeader;

        // ---- Primary slot (highlighted accent box + inset left bar) ----
        var primRect = new Rect2(left, y, right - left, PrimaryH);
        DrawRect(primRect, new Color(DesignTokens.TeamAccent, 0.10f));
        DrawRect(primRect, new Color(DesignTokens.TeamAccent, 0.45f), false, 1f);
        DrawRect(new Rect2(primRect.Position, new Vector2(2f, primRect.Size.Y)), DesignTokens.TeamAccent); // inset accent bar

        float readyFrac = BoltReadyFrac(local, primary);
        bool ready = readyFrac >= 1f;
        string primState = ready ? "READY" : "CYCLING";
        float primStateW = MonoWidth(mono, primState, 10);
        DrawString(mono, new Vector2(left + 8f, y + 14f), "[1]", HorizontalAlignment.Left, -1, 10, DesignTokens.TeamAccent);
        DrawString(UiFonts.SairaBold, new Vector2(left + 30f, y + 15f), primary.Name.ToUpperInvariant(), HorizontalAlignment.Left, right - 6f - primStateW - 8f - (left + 30f), 14, DesignTokens.TextHi);
        DrawStringRight(mono, new Vector2(right - 6f, y + 14f), primState, 10, ready ? DesignTokens.Ok : DesignTokens.Warn);

        // Cadence bar: charges from just-fired (empty) back to full = READY. Green when ready, cyan while charging.
        DrawString(mono, new Vector2(left + 8f, y + 33f), "CYCLE", HorizontalAlignment.Left, -1, 9, DesignTokens.TextDim);
        var pbar = new Rect2(left + 44f, y + 27f, primRect.Size.X - 44f - 10f, 5f);
        DrawBar(pbar, readyFrac, ready ? DesignTokens.Ok : DesignTokens.TeamAccent);
        y += PrimaryH + RowGap;

        // ---- Secondary rows (launcher + any extra guns) ----
        int slot = 2;
        for (int i = 0; i < _weapons.Count; i++)
        {
            if (i == primaryIdx)
                continue;
            DrawSecondaryRow(_weapons[i], slot++, left, right, y, mono, local);
            y += SecRowH;
        }

        // ---- Dispenser rows (chaff [C] / mine [B] / probe [G]), keyed to their hotkeys ----
        if (chaffDisp != null)
        {
            DrawDispenserRow("C", chaffDisp, _net.LocalChaffAmmo, left, right, y, mono);
            y += SecRowH;
        }
        if (mineDisp != null)
        {
            DrawDispenserRow("B", mineDisp, _net.LocalMineAmmo, left, right, y, mono);
            y += SecRowH;
        }
        if (probeDisp != null)
        {
            DrawDispenserRow("G", probeDisp, _net.LocalProbeAmmo, left, right, y, mono);
            y += SecRowH;
        }
    }

    // The chaff/mine dispenser WeaponDef the local ship carries, or null if it carries none of that
    // kind. Dispensers aren't hardpoint-mounted (not in WeaponMounts): the class's default hold names
    // one (shown EMPTY once spent), but live ammo alone also earns the row — the spawned hold can
    // differ from the default (e.g. decoys added to a scout in the hangar), and the authoritative
    // per-kind count is all the wire carries.
    private WeaponDef? DispenserFor(byte classId, WeaponKind kind, int liveAmmo)
    {
        if (_defs.TryGetShipDef(classId, out var def) && def.DefaultCargo is not null)
            foreach (var load in def.DefaultCargo)
                foreach (var w in _defs.AllWeapons())
                    if (w.Kind == kind && w.CargoId == load.CargoId)
                        return w;
        if (liveAmmo > 0)
            foreach (var w in _defs.AllWeapons())
                if (w.Kind == kind)
                    return w;
        return null;
    }

    // One dispenser row: "[key]  NAME  <pack-pips>  NN  READY/EMPTY". `ammo` is the local ship's
    // authoritative TOTAL charge count (LocalChaffAmmo / LocalMineAmmo); dispensers are loaded in
    // packs of `ChargesPerPack`, so the pips show PACKS still holding a charge (bounded, readable)
    // and the NN number carries the exact remaining charges.
    private void DrawDispenserRow(string keyHint, WeaponDef disp, int ammo, float left, float right, float y, Font mono)
    {
        float mid = y + SecRowH * 0.5f;
        DrawString(mono, new Vector2(left, mid + 4f), $"[{keyHint}]", HorizontalAlignment.Left, -1, 10, DesignTokens.TextDim);

        int packSize = _defs.GetCargoItem(disp.CargoId)?.ChargesPerPack ?? 1;
        if (packSize < 1)
            packSize = 1;
        int packs = (ammo + packSize - 1) / packSize; // ceil — packs still holding at least one charge

        (string txt, Color col) = ammo == 0 ? ("EMPTY", DesignTokens.TextDim) : ("READY", DesignTokens.Ok);
        DrawStringRight(mono, new Vector2(right, mid + 4f), txt, 10, col);

        // Exact remaining charges, just left of the state tag.
        float countRight = right - MonoWidth(mono, txt, 10) - 10f;
        string countTxt = ammo.ToString();
        DrawStringRight(mono, new Vector2(countRight, mid + 4f), countTxt, 10, ammo == 0 ? DesignTokens.TextDim : DesignTokens.Data);

        float pipsRight = countRight - MonoWidth(mono, countTxt, 10) - 10f;
        float clusterLeft = DrawPips(pipsRight, mid, packs, System.Math.Max(packs, 1));

        float nameX = left + 26f;
        DrawString(UiFonts.Saira, new Vector2(nameX, mid + 4f), disp.Name.ToUpperInvariant(), HorizontalAlignment.Left, Mathf.Max(24f, clusterLeft - 8f - nameX), 12, DesignTokens.Text2);
    }

    // One secondary weapon row: "[n]  NAME  <pips|bar>  STATE".
    private void DrawSecondaryRow(WeaponDef w, int slot, float left, float right, float y, Font mono, PredictionController local)
    {
        float mid = y + SecRowH * 0.5f;
        DrawString(mono, new Vector2(left, mid + 4f), $"[{slot}]", HorizontalAlignment.Left, -1, 10, DesignTokens.TextDim);

        // Draw the right-hand cluster first (state tag + pips/bar) so the name can be clipped to
        // end just before whatever the cluster's leftmost element is — no overlap at any mag size.
        float clusterLeft;
        if (w.Kind == WeaponKind.Missile)
        {
            // Ammo (pips) + authoritative lock state, from the local ship's snapshot.
            byte ls = _net.LocalLockState;
            bool locked = (ls & 0x80) != 0;
            int prog = ls & 0x7F; // 0..100
            int ammo = _net.LocalMissileAmmo;
            int mag = System.Math.Max(w.MagazineSize, (byte)ammo);

            (string txt, Color col, bool pulse) =
                ammo == 0 ? ("EMPTY", DesignTokens.TextDim, false)
                : locked ? ("LOCKED", DesignTokens.Danger, true)
                : prog > 0 ? ($"LOCK {prog}%", DesignTokens.Warn, false)
                : ("READY", DesignTokens.Ok, false);
            DrawStringRight(mono, new Vector2(right, mid + 4f), txt, 10, pulse ? Pulsed(col) : col);

            float pipsRight = right - MonoWidth(mono, txt, 10) - 10f;
            clusterLeft = DrawPips(pipsRight, mid, ammo, mag);
        }
        else
        {
            // Extra gun mount: its own cadence bar (rare — most hulls carry a single gun).
            float frac = BoltReadyFrac(local, w);
            bool ready = frac >= 1f;
            string txt = ready ? "READY" : "CYCLING";
            DrawStringRight(mono, new Vector2(right, mid + 4f), txt, 10, ready ? DesignTokens.Ok : DesignTokens.Warn);
            float barLeft = right - MonoWidth(mono, txt, 10) - 8f - 56f;
            DrawBar(new Rect2(barLeft, mid - 2f, 56f, 4f), frac, ready ? DesignTokens.Ok : DesignTokens.TeamAccent);
            clusterLeft = barLeft;
        }

        float nameX = left + 26f;
        DrawString(UiFonts.Saira, new Vector2(nameX, mid + 4f), w.Name.ToUpperInvariant(), HorizontalAlignment.Left, Mathf.Max(24f, clusterLeft - 8f - nameX), 12, DesignTokens.Text2);
    }

    // Right-aligned header cue: the launcher's state if the hull carries one, else a guns-armed tag.
    private void DrawHeaderStatus(Vector2 rightAnchor, Font mono)
    {
        WeaponDef? launcher = _weapons.Find(w => w.Kind == WeaponKind.Missile);
        string txt;
        Color col;
        bool pulse = false;
        if (launcher != null)
        {
            byte ls = _net.LocalLockState;
            bool locked = (ls & 0x80) != 0;
            int prog = ls & 0x7F;
            int ammo = _net.LocalMissileAmmo;
            (txt, col, pulse) =
                ammo == 0 ? ("● EMPTY", DesignTokens.TextDim, false)
                : locked ? ("● LOCKED", DesignTokens.Danger, true)
                : prog > 0 ? ($"● LOCK {prog}%", DesignTokens.Warn, false)
                : ("● ARMED", DesignTokens.Ok, false);
        }
        else
        {
            txt = "● GUNS";
            col = DesignTokens.Ok;
        }
        DrawStringRight(mono, rightAnchor, txt, 10, pulse ? Pulsed(col) : col);
    }

    // Fire-cadence readiness for a bolt gun, 0..1 (1 = READY). Mirrors the predictor's fire gate:
    // charged = elapsed ticks since the last shot / the mount's fire interval. Before any def loads
    // (FireIntervalTicks 0) or before the first shot, it reads ready.
    private static float BoltReadyFrac(PredictionController local, WeaponDef gun)
    {
        if (gun.FireIntervalTicks == 0)
            return 1f;
        uint elapsed = local.ClientTick >= local.LastFireTick ? local.ClientTick - local.LastFireTick : 0u;
        return Mathf.Clamp((float)elapsed / gun.FireIntervalTicks, 0f, 1f);
    }

    // Magazine as a row of diamond pips: filled up to `ammo`, hollow to `mag`, capped at MaxPips so a
    // large torpedo magazine (or a big missile rack) never overruns the row — the state text carries
    // the exact count. Pips are laid out right-to-left ending at `rightX`.
    private const int MaxPips = 6;
    private float DrawPips(float rightX, float midY, int ammo, int mag)
    {
        int shown = System.Math.Min(mag, MaxPips);
        if (shown <= 0)
            return rightX;
        const float pip = 6f, gap = 4f;
        float x = rightX - shown * (pip + gap) + gap;
        for (int i = 0; i < shown; i++)
        {
            var c = new Vector2(x + i * (pip + gap) + pip * 0.5f, midY);
            if (i < ammo)
                UiDraw.Diamond(this, c, pip * 0.5f, DesignTokens.Danger);
            else
                DrawPipOutline(c, pip, DesignTokens.TextDim);
        }
        return x; // leftmost pip edge, so the name clips before it
    }

    // Hollow diamond outline (spent round), drawn as a 4-point polyline.
    private void DrawPipOutline(Vector2 c, float size, Color color)
    {
        float r = size * 0.5f;
        var pts = new Vector2[]
        {
            c + new Vector2(0f, -r),
            c + new Vector2(r, 0f),
            c + new Vector2(0f, r),
            c + new Vector2(-r, 0f),
            c + new Vector2(0f, -r),
        };
        DrawPolyline(pts, color, 1f, true);
    }

    private void DrawBar(Rect2 r, float frac, Color fill)
    {
        DrawRect(r, DesignTokens.BorderLo);
        if (frac > 0f)
            DrawRect(new Rect2(r.Position, new Vector2(r.Size.X * frac, r.Size.Y)), fill);
    }

    // DrawString that ends AT `rightAnchor.X` (right-aligned), used for state tags along the panel edge.
    private void DrawStringRight(Font font, Vector2 rightAnchor, string text, int size, Color color)
    {
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(font, new Vector2(rightAnchor.X - w, rightAnchor.Y), text, HorizontalAlignment.Left, -1, size, color);
    }

    private static float MonoWidth(Font font, string text, int size) =>
        font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;

    // The design's saPulse: dip alpha on a ~0.7s cycle for LOCKED cues.
    private Color Pulsed(Color c)
    {
        float a = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin((float)_t * 8f));
        return new Color(c, a);
    }
}
