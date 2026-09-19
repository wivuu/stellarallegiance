using StellarAllegiance.Ui;

namespace StellarAllegiance.Launcher.Theme;

// Mirrors the (Font, size, colour) switch in client/scripts/ui/UiKit.cs (UiKit.MakeLabel) so the
// Avalonia launcher can build text styles that look identical without referencing Godot's Font
// type. Sizes/colours are read from StellarAllegiance.Ui.DesignTokens (never literals) — the same
// linked DesignTokens.cs the game itself uses (see GodotColorShim.cs / StellarLauncher.Core.csproj)
// — so this table can only drift from the game in the (Font member -> family/weight) mapping,
// which tests/LauncherTest/ThemeTests.cs guards by re-parsing UiKit.cs's actual switch arms.
//
// FINDING (see the task report): UiKit.MakeLabel never calls ToUpper/ToUpperInvariant — every
// call site that wants the caps "Label" look upper-cases its own string before calling MakeLabel.
// The switch itself only ever changes font/size/colour. So Caps is false for every style below:
// that is what the mirrored switch really does, not a claim that Label text renders lowercase in
// the game.
public enum TextStyle
{
    Display, // 34 / bold
    Title, // 22 / bold
    Label, // 13 / caps (caller-applied) + letter-spacing
    Body, // 15 / regular — UiKit's `_` (default) switch arm
    Data, // 14 / mono
    Hero, // 26 / bold — between Title and Display
    Caption, // 11 / regular — the sub-Label tier
    Micro, // 9 / regular — smallest legible tier
}

public enum FontFamilyKind
{
    Saira,
    Mono,
}

public sealed record TextStyleSpec(
    TextStyle Style,
    FontFamilyKind Family,
    int Weight,
    int SizePx,
    Godot.Color Color,
    int LetterSpacing,
    bool Caps
);

public static class TextStyleTable
{
    // Font weights/letter-spacing mirror client/scripts/ui/UiFonts.cs's Weight(...) calls:
    // Saira 400, SairaSemi 600, SairaBold 700, SairaLabel 600 + DesignTokens.LabelLetterSpacing,
    // Mono 400, MonoMedium 500. UiKit.MakeLabel only ever selects Saira/SairaBold/SairaLabel/Mono.
    private static readonly TextStyleSpec DisplaySpec = new(
        TextStyle.Display,
        FontFamilyKind.Saira,
        700,
        DesignTokens.DisplaySize,
        DesignTokens.TextHi,
        0,
        false
    );
    private static readonly TextStyleSpec TitleSpec = new(
        TextStyle.Title,
        FontFamilyKind.Saira,
        700,
        DesignTokens.TitleSize,
        DesignTokens.TextHi,
        0,
        false
    );
    private static readonly TextStyleSpec LabelSpec = new(
        TextStyle.Label,
        FontFamilyKind.Saira,
        600,
        DesignTokens.LabelSize,
        DesignTokens.Text2,
        DesignTokens.LabelLetterSpacing,
        false
    );
    private static readonly TextStyleSpec BodySpec = new(
        TextStyle.Body,
        FontFamilyKind.Saira,
        400,
        DesignTokens.BodySize,
        DesignTokens.TextHi,
        0,
        false
    );
    private static readonly TextStyleSpec DataSpec = new(
        TextStyle.Data,
        FontFamilyKind.Mono,
        400,
        DesignTokens.DataSize,
        DesignTokens.Data,
        0,
        false
    );
    private static readonly TextStyleSpec HeroSpec = new(
        TextStyle.Hero,
        FontFamilyKind.Saira,
        700,
        DesignTokens.HeroSize,
        DesignTokens.TextHi,
        0,
        false
    );
    private static readonly TextStyleSpec CaptionSpec = new(
        TextStyle.Caption,
        FontFamilyKind.Saira,
        400,
        DesignTokens.CaptionSize,
        DesignTokens.Text2,
        0,
        false
    );
    private static readonly TextStyleSpec MicroSpec = new(
        TextStyle.Micro,
        FontFamilyKind.Saira,
        400,
        DesignTokens.MicroSize,
        DesignTokens.TextDim,
        0,
        false
    );

    public static IReadOnlyList<TextStyleSpec> All { get; } =
    [DisplaySpec, TitleSpec, LabelSpec, BodySpec, DataSpec, HeroSpec, CaptionSpec, MicroSpec];

    // Same shape as UiKit.MakeLabel's switch, arm for arm (including the `_ => Body` default).
    public static TextStyleSpec Get(TextStyle style) =>
        style switch
        {
            TextStyle.Display => DisplaySpec,
            TextStyle.Hero => HeroSpec,
            TextStyle.Title => TitleSpec,
            TextStyle.Label => LabelSpec,
            TextStyle.Data => DataSpec,
            TextStyle.Caption => CaptionSpec,
            TextStyle.Micro => MicroSpec,
            _ => BodySpec,
        };
}
