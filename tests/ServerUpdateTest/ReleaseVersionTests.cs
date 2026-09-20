using StellarAllegiance.Shared;

// shared/ReleaseVersion.cs: the ONE "is that newer than me?" used by the lobby (max of baked and
// confirmed), the game server (is the Release Advert newer than this build?) and the client (should the
// server browser offer an update?). A wrong answer either nags everyone or hides a release.
static class ReleaseVersionTests
{
    public static void Run()
    {
        T.Section("ReleaseVersion: parsing");
        T.Check(ReleaseVersion.TryParse("0.0.14", out var plain) && plain.ToString() == "0.0.14", "plain x.y.z");
        T.Check(
            ReleaseVersion.TryParse("v0.0.14", out var tagged) && tagged.ToString() == "0.0.14",
            "a git tag's leading v is dropped"
        );
        T.Check(
            ReleaseVersion.TryParse(" V1.2.3 ", out var padded) && padded.ToString() == "1.2.3",
            "whitespace and an upper-case V"
        );
        T.Check(ReleaseVersion.TryParse("1.2", out var two) && two.ToString() == "1.2.0", "x.y reads as x.y.0");
        T.Check(
            ReleaseVersion.TryParse("0.0.14-ci.2", out var pre) && pre.IsPrerelease && pre.ToString() == "0.0.14-ci.2",
            "a rehearsal pre-release keeps its tail"
        );
        T.Check(
            ReleaseVersion.TryParse("1.2.3+abc123", out var meta) && meta.ToString() == "1.2.3" && !meta.IsPrerelease,
            "build metadata is ignored"
        );
        foreach (
            var bad in new[]
            {
                null,
                "",
                "   ",
                "latest",
                "1",
                "1.2.3.4",
                "1..2",
                "1.2.x",
                "1.2.3-",
                "1.2.3-ci..1",
                "-1.2.3",
                "v",
            }
        )
            T.Check(!ReleaseVersion.TryParse(bad, out _), $"not a version: '{bad ?? "<null>"}'");

        T.Section("ReleaseVersion: ordering");
        string[] ascending =
        [
            "0.0.13",
            "0.0.14-alpha",
            "0.0.14-ci.1",
            "0.0.14-ci.2",
            "0.0.14-ci.10",
            "0.0.14-ci.10.1",
            "0.0.14",
            "0.0.15-ci.1",
            "0.1.0",
            "1.0.0",
            "10.0.0",
        ];
        for (int i = 0; i < ascending.Length; i++)
        for (int j = 0; j < ascending.Length; j++)
        {
            ReleaseVersion.TryParse(ascending[i], out var a);
            ReleaseVersion.TryParse(ascending[j], out var b);
            if (Math.Sign(a.CompareTo(b)) != Math.Sign(i.CompareTo(j)))
                T.Check(false, $"{ascending[i]} vs {ascending[j]} orders like its position");
        }
        T.Check(
            true,
            $"all {ascending.Length * ascending.Length} pairs order by semantic version (numeric ci.10 > ci.2, final > every pre-release)"
        );
        T.Check(
            ReleaseVersion.TryParse("v1.2", out var x) && ReleaseVersion.TryParse("1.2.0", out var y) && x.Equals(y),
            "v1.2 equals 1.2.0"
        );

        T.Section("ReleaseVersion: IsNewer / Max");
        T.Check(ReleaseVersion.IsNewer("0.0.13", "v0.0.14"), "a higher release is newer");
        T.Check(!ReleaseVersion.IsNewer("0.0.14", "0.0.14"), "the same release is not newer");
        T.Check(!ReleaseVersion.IsNewer("0.0.14", "0.0.13"), "an older release is not newer");
        T.Check(ReleaseVersion.IsNewer("0.0.14-ci.2", "0.0.14"), "the final release is newer than its own rehearsal build");
        T.Check(!ReleaseVersion.IsNewer("0.0.14", "0.0.14-ci.2"), "a rehearsal build is not newer than the final release");
        T.Check(!ReleaseVersion.IsNewer("0.0.0-dev", "garbage"), "garbage is never newer");
        T.Check(
            !ReleaseVersion.IsNewer("garbage", "0.0.14"),
            "nothing is newer than an unknown version (never nag a build we cannot place)"
        );
        T.Check(!ReleaseVersion.IsNewer(null, null), "null on both sides");
        T.Eq("0.0.14", ReleaseVersion.Max("v0.0.13", "0.0.14"), "Max picks the higher and normalizes");
        T.Eq("0.0.14", ReleaseVersion.Max("0.0.14", "0.0.14-ci.9"), "Max: final over pre-release");
        T.Eq("0.0.13", ReleaseVersion.Max("0.0.13", "not-a-version"), "Max ignores a side that is not a version");
        T.Eq("0.0.13", ReleaseVersion.Max(null, "v0.0.13"), "Max with one null");
        T.Eq<string?>(null, ReleaseVersion.Max("", "nope"), "Max of nothing is null");
    }
}
