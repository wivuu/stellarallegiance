namespace StellarAllegiance.Shared;

// A release version as it appears in a git tag / GitHub release / Velopack package: "v0.0.14",
// "0.0.14", "0.0.14-ci.2". ONE comparison for everyone who has to answer "is that newer than me?":
// the public lobby (max of its baked-in and polled version), the game server (is the lobby's Release
// Advert newer than this build?) and the game client (should the server browser offer an update?).
//
// Semantic-version ordering, because rehearsal pre-releases are real here (docs/RELEASING.md):
// 0.0.14-ci.1 < 0.0.14-ci.2 < 0.0.14 < 0.0.15-ci.1. Build metadata ("+sha") is ignored.
//
// Dependency-free on purpose (no Godot, no Velopack): this file compiles into the client, the server,
// the lobby and their tests through Shared.csproj.
public readonly struct ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly int _major;
    private readonly int _minor;
    private readonly int _patch;

    // Dot-separated pre-release identifiers ("ci", "2"); empty = a final release.
    private readonly string[]? _pre;

    private ReleaseVersion(int major, int minor, int patch, string[] pre)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _pre = pre;
    }

    public bool IsPrerelease => _pre is { Length: > 0 };

    // Accepts an optional leading "v", 2 or 3 numeric components ("1.2" = "1.2.0"), an optional
    // "-pre.release" tail and optional "+build" metadata. Anything else is not a version.
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.AsSpan().Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        int plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        ReadOnlySpan<char> pre = default;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.IsEmpty)
                return false;
        }

        Span<int> core = stackalloc int[3];
        int count = 0;
        foreach (var range in s.Split('.'))
        {
            if (count == 3)
                return false;
            var part = s[range];
            if (part.IsEmpty || !IsDigits(part) || !int.TryParse(part, out core[count]))
                return false;
            count++;
        }
        if (count < 2)
            return false;

        string[] ids = [];
        if (!pre.IsEmpty)
        {
            ids = pre.ToString().Split('.');
            foreach (var id in ids)
                if (id.Length == 0)
                    return false;
        }

        version = new ReleaseVersion(core[0], core[1], core[2], ids);
        return true;
    }

    // True only when BOTH sides are versions and `candidate` is strictly higher. An unparseable side
    // is never "newer" — a garbled tag must not nag anyone to update.
    public static bool IsNewer(string? current, string? candidate) =>
        TryParse(current, out var have) && TryParse(candidate, out var offered) && offered.CompareTo(have) > 0;

    // The higher of two version strings, normalized (no "v"); a side that is not a version is ignored.
    // Null when neither parses.
    public static string? Max(string? a, string? b)
    {
        bool hasA = TryParse(a, out var va);
        bool hasB = TryParse(b, out var vb);
        if (hasA && hasB)
            return (va.CompareTo(vb) >= 0 ? va : vb).ToString();
        if (hasA)
            return va.ToString();
        return hasB ? vb.ToString() : null;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int c = _major.CompareTo(other._major);
        if (c != 0)
            return c;
        c = _minor.CompareTo(other._minor);
        if (c != 0)
            return c;
        c = _patch.CompareTo(other._patch);
        if (c != 0)
            return c;

        // Same core: a final release outranks every pre-release of it.
        var mine = _pre ?? [];
        var theirs = other._pre ?? [];
        if (mine.Length == 0 || theirs.Length == 0)
        {
            if (mine.Length == theirs.Length)
                return 0;
            return mine.Length == 0 ? 1 : -1;
        }

        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            c = CompareIdentifier(mine[i], theirs[i]);
            if (c != 0)
                return c;
        }
        // Equal prefix: the longer identifier list is the later pre-release (semver §11.4.4).
        return mine.Length.CompareTo(theirs.Length);
    }

    // Numeric identifiers compare as numbers and rank below alphanumeric ones; the rest is ordinal.
    private static int CompareIdentifier(string a, string b)
    {
        bool numA = IsDigits(a) && long.TryParse(a, out _);
        bool numB = IsDigits(b) && long.TryParse(b, out _);
        if (numA && numB)
            return long.Parse(a).CompareTo(long.Parse(b));
        if (numA != numB)
            return numA ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }

    private static bool IsDigits(ReadOnlySpan<char> s)
    {
        foreach (char ch in s)
            if (ch is < '0' or > '9')
                return false;
        return !s.IsEmpty;
    }

    public bool Equals(ReleaseVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_major, _minor, _patch, string.Join('.', _pre ?? []));

    public override string ToString() =>
        IsPrerelease ? $"{_major}.{_minor}.{_patch}-{string.Join('.', _pre!)}" : $"{_major}.{_minor}.{_patch}";
}
