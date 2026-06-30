using Godot;

namespace StellarAllegiance.Ui;

// Font registry for the design system. Two variable typefaces, imported as FontFile:
//   • Saira          — UI, headings, labels (weights 400 / 600 / 700)
//   • JetBrains Mono — telemetry, numbers, coordinates (weights 400 / 500)
//
// Weights are realised as FontVariation instances over the variable base fonts (one
// .ttf per family) so we don't ship a file per weight. The caps "Label" style also
// bakes in per-glyph letter-spacing, which Godot's Label has no native control for.
//
// Following the client's no-fallback-but-never-crash discipline: if the imported
// resources aren't present yet (cold import cache on a fresh headless/CI run before
// `godot --headless --import`), every accessor degrades to ThemeDB's fallback font
// instead of throwing. Custom-draw nodes read fonts from here rather than the Theme so
// they render correctly even outside a themed subtree.
public static class UiFonts
{
    private const string SairaPath = "res://assets/fonts/saira.ttf";
    private const string MonoPath = "res://assets/fonts/jetbrains-mono.ttf";

    public static Font Saira { get; private set; } = null!; // 400
    public static Font SairaSemi { get; private set; } = null!; // 600
    public static Font SairaBold { get; private set; } = null!; // 700
    public static Font SairaLabel { get; private set; } = null!; // 600 + letter-spacing (caps labels)
    public static Font Mono { get; private set; } = null!; // 400
    public static Font MonoMedium { get; private set; } = null!; // 500

    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        Font fallback = ThemeDB.Singleton.FallbackFont;
        Font sairaBase = LoadOrFallback(SairaPath, fallback);
        Font monoBase = LoadOrFallback(MonoPath, fallback);

        Saira = Weight(sairaBase, 400);
        SairaSemi = Weight(sairaBase, 600);
        SairaBold = Weight(sairaBase, 700);
        SairaLabel = Weight(sairaBase, 600, DesignTokens.LabelLetterSpacing);
        Mono = Weight(monoBase, 400);
        MonoMedium = Weight(monoBase, 500);
    }

    private static Font LoadOrFallback(string resPath, Font fallback)
    {
        if (ResourceLoader.Exists(resPath) && GD.Load(resPath) is FontFile f)
            return f;
        GD.PushWarning($"[UiFonts] font not imported yet: {resPath} (run `godot --headless --import`)");
        return fallback;
    }

    // A FontVariation over a variable base font at a given OpenType weight, with optional
    // extra per-glyph spacing (caps labels). If the base is already the plain fallback we
    // still return it variation-wrapped so weight axes apply when the real font lands.
    private static Font Weight(Font baseFont, int wght, int glyphSpacing = 0)
    {
        var v = new FontVariation { BaseFont = baseFont };
        v.SetVariationOpentype(new Godot.Collections.Dictionary { { WghtTag, wght } });
        if (glyphSpacing != 0)
            v.SetSpacing(TextServer.SpacingType.Glyph, glyphSpacing);
        return v;
    }

    // OpenType "wght" axis tag packed big-endian ('w'<<24 | 'g'<<16 | 'h'<<8 | 't').
    // Computed directly because TextServer.NameToTag is an instance (server) method.
    private const long WghtTag = ('w' << 24) | ('g' << 16) | ('h' << 8) | 't';
}
