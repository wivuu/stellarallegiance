using Godot;

namespace StellarAllegiance.Ui;

// Single source of truth for the "Stellar Allegiance" design system — the bracket /
// retro-futurism look imported from the Claude Design "System" component library.
//
// Everything chrome-related (panels, buttons, gauges, dividers, text tiers) pulls its
// colour and sizing from here so the UI has one place to retune. Gameplay TEAM identity
// (blue vs red ships, blips, rosters, trails) is deliberately kept separate as
// Faction0/Faction1 — those existing colours are readability-critical and must NOT be
// collapsed into the cyan structural accent.
public static class DesignTokens
{
    // ---- Surfaces ------------------------------------------------------------
    public static readonly Color Void = Color.FromHtml("05070F"); // bg / base
    public static readonly Color Panel = Color.FromHtml("0B1320"); // opaque surface
    public static readonly Color PanelHi = Color.FromHtml("16243A"); // raised surface
    public static readonly Color PanelFill = new(8f / 255f, 14f / 255f, 24f / 255f, 0.60f); // translucent panel body
    public static readonly Color Well = new(0.02f, 0.027f, 0.06f, 1f); // recessed data well (≈ Void, opaque)
    public static readonly Color PanelDeep = Color.FromHtml("070B14"); // modal body — between Void and Panel
    public static readonly Color Scrim = new(3f / 255f, 5f / 255f, 11f / 255f, 0.78f); // modal backdrop dim
    public static readonly Color BorderHi = new(120f / 255f, 190f / 255f, 255f / 255f, 0.25f); // strong hairline
    public static readonly Color BorderLo = new(120f / 255f, 190f / 255f, 255f / 255f, 0.16f); // faint hairline

    // ---- Accents -------------------------------------------------------------
    // Structural/chrome accent (token diamonds, brackets, primary buttons, gauge arcs).
    // Faction-tintable so a player's chrome can lean toward their team without becoming
    // the literal team-identity colour. Mutable: SetTeamAccentTint swaps it at runtime.
    public static Color TeamAccent = Color.FromHtml("37E0FF");
    public static readonly Color TeamAccentBase = Color.FromHtml("37E0FF");
    public static readonly Color Secondary = Color.FromHtml("FF9D4D"); // highlight / credits
    public static readonly Color CmdrGold = Color.FromHtml("FFD24D"); // commander authority: CMDR badge, order directives, F3 command selection

    // ---- Text tiers ----------------------------------------------------------
    public static readonly Color TextHi = Color.FromHtml("CFE6F5"); // primary
    public static readonly Color Text2 = Color.FromHtml("7FA6C8"); // secondary
    public static readonly Color TextDim = Color.FromHtml("5A7390"); // dim / captions / disabled

    // ---- Status --------------------------------------------------------------
    public static readonly Color Ok = Color.FromHtml("4DFFA6");
    public static readonly Color Warn = Color.FromHtml("FFB347");
    public static readonly Color Danger = Color.FromHtml("FF5A6A");
    public static readonly Color DangerText = Color.FromHtml("FF8A96"); // danger on dark, for legibility
    public static readonly Color Data = Color.FromHtml("9FD6FF"); // mono telemetry / numbers

    // ---- Gameplay team identity (preserved from existing code; NOT the accent) ----
    public static readonly Color Faction0 = new(0.30f, 0.55f, 1.00f); // BLUE
    public static readonly Color Faction1 = new(1.00f, 0.40f, 0.34f); // RED

    public static Color Faction(int team) => team == 0 ? Faction0 : Faction1;

    // ---- Type scale (px) -----------------------------------------------------
    public const int DisplaySize = 34;
    public const int TitleSize = 22;
    public const int LabelSize = 13; // caps + letter-spacing
    public const int BodySize = 15;
    public const int DataSize = 14; // mono

    // Extra per-glyph spacing applied to the caps "Label" style (Saira has no native
    // letter-spacing on Label; we bake it onto a FontVariation in UiFonts).
    public const int LabelLetterSpacing = 2;

    // ---- Geometry ------------------------------------------------------------
    public const float CornerChamfer = 9f; // chamfered button corner cut
    public const float BracketLength = 16f; // corner-bracket arm length on panels

    // Tint the structural accent toward a faction (subtle — keeps the cyan reading as
    // chrome, not as the team colour). Call once the local team is known.
    public static void SetTeamAccentTint(int team)
    {
        // 25% lean toward the faction colour; reset by passing team < 0.
        if (team < 0)
        {
            TeamAccent = TeamAccentBase;
            return;
        }
        TeamAccent = TeamAccentBase.Lerp(Faction(team), 0.25f);
    }
}
