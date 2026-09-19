using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Launcher.Instance;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.Update;
using Velopack.Locators;

// Everything around the flow: the renderer policy, the single-instance lock + doorbell, the real Velopack
// adapter against a synthetic feed, and a few GUARDS that read repo files — they fail when something
// elsewhere in the repo changes in a way that would quietly break the launcher.
static class InfraTests
{
    public static void Run()
    {
        RenderPolicyTests();
        SingleInstanceTests();
        VelopackAdapterTests();
        RepoGuards();
    }

    static void RenderPolicyTests()
    {
        T.Section("RenderPolicy");
        T.Eq(
            RenderChoice.Default,
            RenderPolicy.Decide(null, HostOs.Windows, isX64: true, sentinelPresent: false),
            "normal start → Avalonia's own GPU-first order"
        );
        T.Eq(
            RenderChoice.Default,
            RenderPolicy.Decide("auto", HostOs.MacOs, isX64: false, sentinelPresent: false),
            "Apple silicon keeps Metal"
        );
        T.Eq(
            RenderChoice.Compatible,
            RenderPolicy.Decide("auto", HostOs.MacOs, isX64: true, sentinelPresent: false),
            "Intel Macs skip Metal (NativeAOT + Metal instability reports)"
        );
        T.Eq(
            RenderChoice.Software,
            RenderPolicy.Decide(null, HostOs.Linux, isX64: true, sentinelPresent: true),
            "boot sentinel still there = the last start never drew a frame → software"
        );
        T.Eq(
            RenderChoice.Software,
            RenderPolicy.Decide(" Software ", HostOs.Windows, isX64: true, sentinelPresent: false),
            "--launcher-render=software wins (trimmed, case-insensitive)"
        );
        T.Eq(
            RenderChoice.Default,
            RenderPolicy.Decide("gpu", HostOs.MacOs, isX64: true, sentinelPresent: true),
            "--launcher-render=gpu overrides both the Intel rule and the sentinel"
        );
    }

    static void SingleInstanceTests()
    {
        T.Section("SingleInstance");
        string dir = T.TempDir("instance");
        try
        {
            string lockFile = Path.Combine(dir, "launcher.lock");
            using var first = new SingleInstance(lockFile);
            T.Check(first.TryAcquire(attempts: 1), "the first launcher takes the lock");

            using var rang = new ManualResetEventSlim();
            first.Activated += rang.Set;

            using (var second = new SingleInstance(lockFile))
            {
                T.Check(
                    !second.TryAcquire(attempts: 2, delayMs: 50),
                    "a second launcher does not get it (after its retries)"
                );
                T.Check(!second.IsOwner, "…and knows it is not the owner");
                second.RingOwner();
            }
            T.Check(rang.Wait(TimeSpan.FromSeconds(5)), "ringing the doorbell raises Activated in the owner");

            first.Dispose();
            using var third = new SingleInstance(lockFile);
            T.Check(
                third.TryAcquire(attempts: 1),
                "once the owner is gone the lock is free again (a crashed launcher cannot wedge the next start)"
            );
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The REAL VelopackUpdateService, pointed at a hand-written folder feed through Velopack's own test
    // locator: proves the adapter maps Velopack's UpdateInfo into UpdateOffer correctly without needing an
    // installed app or a single .nupkg.
    static void VelopackAdapterTests()
    {
        T.Section("VelopackUpdateService (real adapter, synthetic feed)");
        string dir = T.TempDir("feed");
        try
        {
            string feed = Path.Combine(dir, "feed");
            string packages = Path.Combine(dir, "packages");
            Directory.CreateDirectory(feed);
            Directory.CreateDirectory(packages);
            string channel =
                OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : "linux";

            void WriteFeed(params (string Version, string Type, long Size, string? Notes)[] assets)
            {
                var json = new StringBuilder("{\"Assets\":[");
                for (int i = 0; i < assets.Length; i++)
                {
                    var a = assets[i];
                    string suffix = a.Type == "Full" ? "full" : "delta";
                    json.Append(i == 0 ? "" : ",")
                        .Append($"{{\"PackageId\":\"StellarAllegiance\",\"Version\":\"{a.Version}\",\"Type\":\"{a.Type}\",")
                        .Append(
                            $"\"FileName\":\"StellarAllegiance-{a.Version}-{channel}-{suffix}.nupkg\",\"SHA1\":\"{new string('A', 40)}\",\"SHA256\":\"{new string('B', 64)}\",\"Size\":{a.Size}"
                        )
                        .Append(a.Notes is null ? "" : $",\"NotesMarkdown\":\"{a.Notes}\"")
                        .Append('}');
                }
                File.WriteAllText(Path.Combine(feed, $"releases.{channel}.json"), json.Append("]}").ToString());
            }

            var log = new NullLog();
            VelopackUpdateService Service(string installed) =>
                new(feed, log, new TestVelopackLocator("StellarAllegiance", installed, packages));

            WriteFeed(("1.0.0", "Full", 300_000_000, null));
            var current = Service("1.0.0");
            T.Check(current.IsInstalled, "the test locator reads as an installed app");
            T.Eq("1.0.0", current.CurrentVersion, "…at the version we gave it");
            T.Eq(
                null,
                current.CheckAsync(UpdateChannel.Stable, CancellationToken.None).GetAwaiter().GetResult(),
                "feed offers nothing newer → null"
            );

            WriteFeed(
                ("1.0.0", "Full", 300_000_000, null),
                ("1.1.0", "Full", 310_000_000, "* what changed in 1.1.0"),
                ("1.1.0", "Delta", 2_000_000, null)
            );
            var offer = Service("1.0.0").CheckAsync(UpdateChannel.Stable, CancellationToken.None).GetAwaiter().GetResult();
            T.Check(
                offer is { Version: "1.1.0", FullBytes: 310_000_000, IsDowngrade: false },
                "a newer full release → an offer for it"
            );
            // No base package is cached locally (fresh macOS/Linux install), so Velopack disables deltas — the
            // offer must say "full download", because that is what the player will actually wait for.
            T.Check(
                offer is { IsDelta: false, DownloadBytes: 310_000_000 },
                "without a cached base package the offer is honest about being a FULL download"
            );
            T.Check(
                offer?.Notes is [{ Version: "1.1.0", Markdown: "* what changed in 1.1.0" }],
                "release notes ride along from the feed"
            );

            File.WriteAllText(Path.Combine(feed, $"releases.{channel}.json"), "{ not json");
            bool threw = false;
            try
            {
                Service("1.0.0").CheckAsync(UpdateChannel.Stable, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                threw = true;
            }
            T.Check(threw, "a broken feed throws (the flow turns that into CheckFailed — PLAY stays available)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static void RepoGuards()
    {
        T.Section("Repo guards");
        string? root = FindRepoRoot();
        if (root is null)
        {
            T.Check(false, "repo root (wivuullegiance.slnx) found by walking up from the test binary");
            return;
        }

        // GodotPaths points the player at the game's log folder; that path is derived from project.godot.
        string project = File.ReadAllText(Path.Combine(root, "client", "project.godot"));
        T.Check(
            project.Contains($"config/name=\"{GodotPaths.ProjectName}\""),
            "client/project.godot still names the project 'stellarallegiance' (GodotPaths + GameLocator depend on it)"
        );
        T.Check(!project.Contains("use_custom_user_dir=true"), "…and does not switch to a custom user:// dir");
        T.Check(
            GodotPaths.LogDir(HostOs.MacOs).EndsWith(Path.Combine("Godot", "app_userdata", "stellarallegiance", "logs")),
            "macOS game log dir = …/Godot/app_userdata/stellarallegiance/logs"
        );
        T.Check(
            GodotPaths.LogDir(HostOs.Linux).Contains(Path.Combine("godot", "app_userdata")),
            "Linux uses the lower-case 'godot' data dir"
        );

        // The packaging script and the launcher must agree on where the game sits inside the package.
        string package = File.ReadAllText(Path.Combine(root, "scripts", "package-clients.ps1"));
        T.Check(
            package.Contains($"$MacGameBundle = '{GameLocator.MacGameBundle}'"),
            "package-clients.ps1 nests the game bundle under the name GameLocator expects"
        );
        T.Check(
            package.Contains($"$GameExeBase = '{GameLocator.GameExeBase}'"),
            "…and uses the same game executable base name"
        );

        // Static font instances (tools/font-instancer): Avalonia cannot drive variable axes, and an instance
        // that kept its `fvar` table would silently render at the variable font's DEFAULT weight (Saira: Thin).
        string fonts = Path.Combine(root, "launcher", "App", "Assets", "Fonts");
        foreach (
            var (file, weight) in new[]
            {
                ("Saira-Regular.ttf", 400),
                ("Saira-SemiBold.ttf", 600),
                ("Saira-Bold.ttf", 700),
                ("JetBrainsMono-Regular.ttf", 400),
                ("JetBrainsMono-Medium.ttf", 500),
            }
        )
        {
            var (hasFvar, usWeightClass) = ReadFont(Path.Combine(fonts, file));
            T.Check(
                !hasFvar && usWeightClass == weight,
                $"{file}: static (no fvar table), usWeightClass {weight} (was {usWeightClass}, fvar={hasFvar})"
            );
        }
        T.Check(
            File.Exists(Path.Combine(fonts, "OFL-Saira.txt")) && File.Exists(Path.Combine(fonts, "OFL-JetBrainsMono.txt")),
            "the OFL license texts ship next to the fonts"
        );

        // DESIGN.md's rule — never re-hardcode colours or sizes — enforced for the launcher's UI code: every
        // colour must come from DesignTokens through Theme/Sa.cs.
        var literal = new Regex(
            """(?<![\w&])#[0-9A-Fa-f]{6}\b|Color\.(Parse|FromRgb|FromArgb)\(|Brushes\.(?!Transparent)\w+|Colors\.\w+"""
        );
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "launcher", "App"), "*.*", SearchOption.AllDirectories)
            .Where(f =>
                (f.EndsWith(".cs") || f.EndsWith(".axaml"))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            )
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && literal.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}")
            .ToList();
        T.Check(
            offenders.Count == 0,
            offenders.Count == 0
                ? "no colour literals in launcher/App — everything goes through DesignTokens"
                : "colour literals found in launcher/App: " + string.Join(", ", offenders)
        );
    }

    static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "wivuullegiance.slnx")))
                return dir.FullName;
        return null;
    }

    // Just enough of the sfnt table directory to answer: is there an `fvar` table, and what is OS/2.usWeightClass?
    static (bool HasFvar, int UsWeightClass) ReadFont(string path)
    {
        if (!File.Exists(path))
            return (true, -1);
        byte[] data = File.ReadAllBytes(path);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        bool fvar = false;
        int weight = -1;
        for (int i = 0; i < tables; i++)
        {
            var record = data.AsSpan(12 + i * 16, 16);
            string tag = Encoding.ASCII.GetString(record[..4]);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            if (tag == "fvar")
                fvar = true;
            else if (tag == "OS/2")
                weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
        }
        return (fvar, weight);
    }
}
