// The launcher's UI-free core (launcher/Core) compiles the game's real
// client/scripts/ui/DesignTokens.cs via <Compile Include Link> (see
// StellarLauncher.Core.csproj) so the Avalonia launcher can never drift from the Godot client's
// palette/sizes — one file, two builds. DesignTokens.cs is written against Godot.Color, and
// launcher/Core deliberately has no reference to Godot itself (an engine dependency has no
// business in a console-testable, AOT-compatible core library), so this struct stands in for just
// enough of Godot.Color to let DesignTokens.cs compile unmodified, plus a handful of members
// ported draw code will need later (Lerp, ToHtml, the ARGB8888 helpers).
//
// GOTCHA ported straight from real Godot: the (Color from, float alpha) constructor REPLACES the
// alpha channel, it does not multiply it. `new Color(BorderLo, 0.4f)` is alpha 0.40 even though
// BorderLo's own alpha is 0.16. Any future port of Godot draw code that assumes "multiply" will be
// wrong here in exactly the way it would be wrong in Godot itself — that is the point.
//
// This is a shim, not a port of Godot.Color: it has exactly what DesignTokens.cs and the
// launcher's own theme code need, nothing more. If a future edit to DesignTokens.cs needs a real
// Godot API this shim doesn't have, the launcher build breaks loudly — the fix is to extend this
// file, never to work around it.
namespace Godot;

public readonly struct Color : IEquatable<Color>
{
    public float R { get; }
    public float G { get; }
    public float B { get; }
    public float A { get; }

    public Color(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    // Godot's "copy with a different alpha" constructor: REPLACES alpha, does not multiply it.
    public Color(Color from, float alpha)
    {
        R = from.R;
        G = from.G;
        B = from.B;
        A = alpha;
    }

    // 0-255 channel views, rounded like Godot rounds (MathF.Round, clamped) — what the Avalonia
    // side converts through to build its own brushes without this project referencing Avalonia.
    public byte R8 => RoundToByte(R);
    public byte G8 => RoundToByte(G);
    public byte B8 => RoundToByte(B);
    public byte A8 => RoundToByte(A);

    public uint ToArgb32() => ((uint)A8 << 24) | ((uint)R8 << 16) | ((uint)G8 << 8) | B8;

    // Accepts RRGGBB, RRGGBBAA, RGB or RGBA, each with or without a leading '#', case-insensitive
    // — the forms Godot's own Color.FromHtml accepts. A 3/4-digit code is expanded by doubling
    // each digit (like CSS `#rgb`). Anything else — wrong length, non-hex characters — throws
    // ArgumentException: a bad token in a design token must fail loudly, not silently go black.
    public static Color FromHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        string hex = html.Length > 0 && html[0] == '#' ? html[1..] : html;

        if (hex.Length == 3 || hex.Length == 4)
        {
            var doubled = new char[hex.Length * 2];
            for (int i = 0; i < hex.Length; i++)
                doubled[i * 2] = doubled[(i * 2) + 1] = hex[i];
            hex = new string(doubled);
        }

        if (hex.Length != 6 && hex.Length != 8)
            throw new ArgumentException(
                $"Invalid HTML color code '{html}': expected RGB, RGBA, RRGGBB or RRGGBBAA (optionally with a leading '#').",
                nameof(html)
            );

        float r = ParseChannel(0);
        float g = ParseChannel(2);
        float b = ParseChannel(4);
        float a = hex.Length == 8 ? ParseChannel(6) : 1f;
        return new Color(r, g, b, a);

        float ParseChannel(int index)
        {
            int hi = Nibble(hex[index]);
            int lo = Nibble(hex[index + 1]);
            if (hi < 0 || lo < 0)
                throw new ArgumentException($"Invalid HTML color code '{html}': not hexadecimal.", nameof(html));
            return ((hi * 16) + lo) / 255f;
        }

        static int Nibble(char c) =>
            c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
    }

    // Component-wise interpolation, alpha included — matches Godot's Color.Lerp.
    public Color Lerp(Color to, float weight) =>
        new(R + ((to.R - R) * weight), G + ((to.G - G) * weight), B + ((to.B - B) * weight), A + ((to.A - A) * weight));

    // Lowercase hex like Godot's Color.ToHtml — "rrggbb" or "rrggbbaa".
    public string ToHtml(bool includeAlpha = true)
    {
        string hex = $"{R8:x2}{G8:x2}{B8:x2}";
        return includeAlpha ? hex + $"{A8:x2}" : hex;
    }

    private static byte RoundToByte(float channel) => (byte)Math.Clamp(MathF.Round(channel * 255f), 0f, 255f);

    public bool Equals(Color other) => R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);

    public override bool Equals(object? obj) => obj is Color other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public static bool operator ==(Color left, Color right) => left.Equals(right);

    public static bool operator !=(Color left, Color right) => !left.Equals(right);

    // Invariant on purpose: this runs inside launcher/Core (InvariantGlobalization=true), and a
    // '.'-vs-',' decimal separator must not depend on the host machine's locale.
    public override string ToString() => FormattableString.Invariant($"({R}, {G}, {B}, {A})");
}
