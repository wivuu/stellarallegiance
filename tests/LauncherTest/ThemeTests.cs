using System.Text.RegularExpressions;
using Godot;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;

// GodotColorShim + TextStyleTable are the launcher's half of "one design system, two builds" (see
// launcher/Core/Theme/GodotColorShim.cs). Beyond unit-testing the shim itself, this suite reads the
// REAL client/scripts/ui/DesignTokens.cs and UiKit.cs off disk and checks the launcher's copies
// against them, so a change on the game side that the launcher forgot to mirror fails a test
// instead of silently drifting.
static class ThemeTests
{
    private static readonly Regex FromHtmlCall = new(@"Color\.FromHtml\(""([0-9A-Fa-f]+)""\)", RegexOptions.Compiled);

    // The UiKit.MakeLabel switch, e.g. `TextStyle.Display => (UiFonts.SairaBold, DesignTokens.DisplaySize, DesignTokens.TextHi),`
    private static readonly Regex NamedArm = new(
        @"TextStyle\.(\w+)\s*=>\s*\(UiFonts\.(\w+),\s*DesignTokens\.(\w+),\s*DesignTokens\.(\w+)\),",
        RegexOptions.Compiled
    );

    // The switch's default arm, e.g. `_ => (UiFonts.Saira, DesignTokens.BodySize, DesignTokens.TextHi),` — this is what TextStyle.Body actually gets.
    private static readonly Regex DefaultArm = new(
        @"_\s*=>\s*\(UiFonts\.(\w+),\s*DesignTokens\.(\w+),\s*DesignTokens\.(\w+)\),",
        RegexOptions.Compiled
    );

    public static void Run()
    {
        T.Section("GodotColorShim");

        T.Check(DesignTokens.Void is { R8: 5, G8: 7, B8: 15, A8: 255 }, "Void is 05070F, opaque, in 8-bit channels");
        T.Check(Approx(DesignTokens.PanelFill.A, 0.60f), "PanelFill alpha ≈ 0.60");
        T.Eq("37e0ff", DesignTokens.TeamAccent.ToHtml(false), "TeamAccent.ToHtml(false) is lowercase, no alpha");

        T.Check(Color.FromHtml("05070F") == Color.FromHtml("#05070F"), "FromHtml: RRGGBB with/without leading #");
        T.Check(
            Color.FromHtml("05070Fcc") == Color.FromHtml("#05070FCC"),
            "FromHtml: RRGGBBAA with/without #, case-insensitive"
        );
        T.Check(Color.FromHtml("abc") == Color.FromHtml("aabbcc"), "FromHtml: RGB shorthand doubles each digit");
        T.Check(
            Color.FromHtml("#abcd") == Color.FromHtml("aabbccdd"),
            "FromHtml: RGBA shorthand with #, doubles each digit"
        );
        T.Check(Color.FromHtml("ABC") == Color.FromHtml("abc"), "FromHtml is case-insensitive");

        foreach (string bad in new[] { "", "#", "12", "12345", "1234567", "zzzzzz", "#gggggg", "12 456" })
        {
            bool threw = false;
            try
            {
                Color.FromHtml(bad);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            T.Check(threw, $"FromHtml(\"{bad}\") rejects a bad token");
        }

        var replaced = new Color(DesignTokens.BorderLo, 0.4f);
        T.Check(
            Approx(replaced.A, 0.4f),
            "Color(from, alpha) REPLACES alpha (0.4), it does not multiply BorderLo's own 0.16"
        );
        T.Check(
            Approx(replaced.R, DesignTokens.BorderLo.R)
                && Approx(replaced.G, DesignTokens.BorderLo.G)
                && Approx(replaced.B, DesignTokens.BorderLo.B),
            "…and keeps the source RGB"
        );

        var black = new Color(0f, 0f, 0f, 0f);
        var white = new Color(1f, 1f, 1f, 1f);
        T.Check(black.Lerp(white, 0f) == black, "Lerp at weight 0 is the start colour");
        T.Check(black.Lerp(white, 1f) == white, "Lerp at weight 1 is the end colour");
        var mid = black.Lerp(white, 0.5f);
        T.Check(
            Approx(mid.R, 0.5f) && Approx(mid.G, 0.5f) && Approx(mid.B, 0.5f) && Approx(mid.A, 0.5f),
            "Lerp at weight 0.5 is the midpoint, alpha included"
        );

        T.Eq(0xFFFF0000u, new Color(1f, 0f, 0f, 1f).ToArgb32(), "ToArgb32 is 0xAARRGGBB (opaque red)");
        T.Eq(
            0x80102030u,
            new Color(0x10 / 255f, 0x20 / 255f, 0x30 / 255f, 0x80 / 255f).ToArgb32(),
            "ToArgb32 packs each channel in place"
        );

        T.Check(new Color(0.1f, 0.2f, 0.3f, 0.4f) == new Color(0.1f, 0.2f, 0.3f, 0.4f), "value equality via ==");
        T.Check(new Color(0.1f, 0.2f, 0.3f, 0.4f) != new Color(0.1f, 0.2f, 0.3f, 0.9f), "value inequality via !=");
        T.Eq(
            new Color(0.1f, 0.2f, 0.3f, 0.4f).GetHashCode(),
            new Color(0.1f, 0.2f, 0.3f, 0.4f).GetHashCode(),
            "equal colours hash the same"
        );
        T.Check(new Color(0.1f, 0.2f, 0.3f, 0.4f).ToString().Contains("0.1"), "ToString is readable");

        string repoRoot = FindRepoRoot();

        T.Section("DesignTokens.cs source guard (proves the shim parses every token the real file uses)");
        string designTokensPath = Path.Combine(repoRoot, "client", "scripts", "ui", "DesignTokens.cs");
        string designTokensText = File.ReadAllText(designTokensPath);

        var fromHtmlTokens = FromHtmlCall.Matches(designTokensText).Select(m => m.Groups[1].Value).ToList();
        T.Check(fromHtmlTokens.Count > 0, $"found Color.FromHtml(\"…\") literals in {designTokensPath}");
        foreach (string token in fromHtmlTokens)
            T.Eq(
                token.ToLowerInvariant(),
                Color.FromHtml(token).ToHtml(false),
                $"FromHtml(\"{token}\") round-trips via ToHtml(false)"
            );

        string[] deniedGodotApis = ["Colors.", "Mathf.", "GD.", "Vector2", "StyleBox"];
        foreach (string denied in deniedGodotApis)
            T.Check(
                !designTokensText.Contains(denied, StringComparison.Ordinal),
                $"DesignTokens.cs does not use Godot's {denied} (only Color)"
            );

        T.Section("UiKit.cs TextStyle switch guard (drift guard for the duplicated mapping)");
        string uiKitPath = Path.Combine(repoRoot, "client", "scripts", "ui", "UiKit.cs");
        string uiKitText = File.ReadAllText(uiKitPath);

        var namedArms = NamedArm.Matches(uiKitText);
        var defaultArms = DefaultArm.Matches(uiKitText);
        T.Check(
            namedArms.Count > 0 && defaultArms.Count == 1,
            $"found the TextStyle switch arms in {uiKitPath} (fails loudly if the switch's shape changed)"
        );

        var arms = new List<(string StyleName, string FontMember, string SizeToken, string ColorToken)>();
        foreach (Match m in namedArms)
            arms.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));
        foreach (Match m in defaultArms) // the `_` arm is what TextStyle.Body actually gets
            arms.Add(("Body", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));

        T.Eq(
            TextStyleTable.All.Count,
            arms.Count,
            "one switch arm parsed per TextStyle value (catches a style UiKit.cs added that TextStyleTable didn't)"
        );

        foreach (var (styleName, fontMember, sizeToken, colorToken) in arms)
        {
            var style = Enum.Parse<TextStyle>(styleName);
            TextStyleSpec spec = TextStyleTable.Get(style);
            (FontFamilyKind family, int weight, int letterSpacing) = DescribeFont(fontMember);

            T.Check(
                spec.Family == family && spec.Weight == weight,
                $"TextStyle.{styleName}: font family/weight matches UiFonts.{fontMember}"
            );
            T.Eq(letterSpacing, spec.LetterSpacing, $"TextStyle.{styleName}: letter-spacing matches UiFonts.{fontMember}");
            T.Eq(SizeTokenValue(sizeToken), spec.SizePx, $"TextStyle.{styleName}: size matches DesignTokens.{sizeToken}");
            T.Check(
                ColorTokenValue(colorToken) == spec.Color,
                $"TextStyle.{styleName}: colour matches DesignTokens.{colorToken}"
            );
        }

        // Font family/weight/letter-spacing implied by the UiFonts.cs member name (see
        // client/scripts/ui/UiFonts.cs's EnsureLoaded: the fixed set of Weight(...) calls that
        // build every UiFonts.X). UiKit.cs referencing a UiFonts member this doesn't know about
        // fails the guard loudly instead of silently passing.
        static (FontFamilyKind Family, int Weight, int LetterSpacing) DescribeFont(string uiFontsMember) =>
            uiFontsMember switch
            {
                "Saira" => (FontFamilyKind.Saira, 400, 0),
                "SairaSemi" => (FontFamilyKind.Saira, 600, 0),
                "SairaBold" => (FontFamilyKind.Saira, 700, 0),
                "SairaLabel" => (FontFamilyKind.Saira, 600, DesignTokens.LabelLetterSpacing),
                "Mono" => (FontFamilyKind.Mono, 400, 0),
                "MonoMedium" => (FontFamilyKind.Mono, 500, 0),
                _ => throw new InvalidOperationException(
                    $"UiKit.cs now references UiFonts.{uiFontsMember}, which this guard doesn't know how to map — extend DescribeFont in ThemeTests.cs"
                ),
            };

        static int SizeTokenValue(string designTokensMember) =>
            designTokensMember switch
            {
                "DisplaySize" => DesignTokens.DisplaySize,
                "HeroSize" => DesignTokens.HeroSize,
                "TitleSize" => DesignTokens.TitleSize,
                "BodySize" => DesignTokens.BodySize,
                "DataSize" => DesignTokens.DataSize,
                "LabelSize" => DesignTokens.LabelSize,
                "CaptionSize" => DesignTokens.CaptionSize,
                "MicroSize" => DesignTokens.MicroSize,
                _ => throw new InvalidOperationException(
                    $"UiKit.cs now references DesignTokens.{designTokensMember}, which this guard doesn't know — extend SizeTokenValue in ThemeTests.cs"
                ),
            };

        static Color ColorTokenValue(string designTokensMember) =>
            designTokensMember switch
            {
                "TextHi" => DesignTokens.TextHi,
                "Text2" => DesignTokens.Text2,
                "TextDim" => DesignTokens.TextDim,
                "Data" => DesignTokens.Data,
                "Void" => DesignTokens.Void,
                "TeamAccent" => DesignTokens.TeamAccent,
                _ => throw new InvalidOperationException(
                    $"UiKit.cs now references DesignTokens.{designTokensMember}, which this guard doesn't know — extend ColorTokenValue in ThemeTests.cs"
                ),
            };
    }

    private static bool Approx(float a, float b) => MathF.Abs(a - b) < 0.0005f;

    // Walk up from the test binary to find the repo root. The real repo has wivuullegiance.slnx;
    // the private dev tree (scratchpad/private-tree.sh) has no .slnx, so also accept a directory
    // shaped like the repo (has both launcher/Core and client/scripts/ui/DesignTokens.cs).
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            bool hasSlnx = File.Exists(Path.Combine(dir.FullName, "wivuullegiance.slnx"));
            bool looksLikeRepo =
                Directory.Exists(Path.Combine(dir.FullName, "launcher", "Core"))
                && File.Exists(Path.Combine(dir.FullName, "client", "scripts", "ui", "DesignTokens.cs"));
            if (hasSlnx || looksLikeRepo)
                return dir.FullName;
        }
        throw new InvalidOperationException($"could not locate the repo root by walking up from {AppContext.BaseDirectory}");
    }
}
