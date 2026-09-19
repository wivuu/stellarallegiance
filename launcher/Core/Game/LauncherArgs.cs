namespace StellarAllegiance.Launcher.Game;

// Splits the launcher's command line into "ours" and "the game's".
//
// The launcher is the front door for every way the game used to be started, so ANY flag the client
// understands (`--host`, `--lobby`, `--anonymous`, `--join-listing=…`, the UI-harness flags after a
// bare `--`, …) must reach the game verbatim and in order. Only one namespace is ours:
//
//   --launcher-<name>[=<value>]   consumed here, never forwarded
//
// Rules (pinned by tests/LauncherTest):
//   * Only the `=` form carries a value — a launcher flag NEVER swallows the following token.
//   * Scanning stops at the first bare `--`: it and everything after it pass through untouched,
//     even a token that looks like `--launcher-…` (that region belongs to the game's UI harness).
//   * `-psn_*` (the process serial number macOS Finder used to append) is dropped.
//   * Unknown `--launcher-*` flags are consumed too (and surfaced in Unknown) so a typo can't leak
//     into the game's argument list.
public sealed class LauncherArgs
{
    public const string Prefix = "--launcher-";

    // Everything we were started with. Velopack restarts us with these (so `--launcher-feed=` and
    // friends survive an apply-and-restart), optionally plus `--launcher-resume=play`.
    public required IReadOnlyList<string> Original { get; init; }

    // What the game gets, verbatim and in order.
    public required IReadOnlyList<string> GameArgs { get; init; }

    // Launcher flags that are not recognised (consumed, logged by the caller).
    public required IReadOnlyList<string> Unknown { get; init; }

    // True when Velopack (Setup.exe / Update.exe, Windows only) is invoking us as an install/update/
    // uninstall hook. The process must do nothing but let VelopackApp handle it and exit fast.
    public bool IsVelopackHook { get; init; }

    public string? Feed { get; init; } // --launcher-feed=<dir|url>       update feed override (tests, CI)
    public string? GameOverride { get; init; } // --launcher-game=<path>          game binary override (dev)
    public string? DataDir { get; init; } // --launcher-data=<dir>           settings/log/lock dir (hermetic tests)
    public string? Render { get; init; } // --launcher-render=auto|gpu|software
    public string? Resume { get; init; } // --launcher-resume=play          continue into the game after a restart
    public string? SelfTest { get; init; } // --launcher-selftest=update|play headless scripted run (no UI)
    public string? Fake { get; init; } // --launcher-fake=<scenario>      drive the UI from a fake update service
    public string? Shot { get; init; } // --launcher-shot=<png>           capture the window and exit
    public bool Showcase { get; init; } // --launcher-showcase             component/state gallery
    public bool NoAutoLaunch { get; init; } // --launcher-no-autolaunch

    public static bool LooksLikeVelopackHook(IReadOnlyList<string> args) =>
        args.Count > 0
        && (
            args[0].StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)
            || args[0].StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase)
        );

    public static LauncherArgs Parse(IReadOnlyList<string> args)
    {
        var game = new List<string>(args.Count);
        var unknown = new List<string>();
        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);

        bool passthrough = false; // true once the first bare `--` was seen
        foreach (var token in args)
        {
            if (passthrough)
            {
                game.Add(token);
                continue;
            }
            if (token == "--")
            {
                passthrough = true;
                game.Add(token);
                continue;
            }
            if (token.StartsWith("-psn_", StringComparison.Ordinal))
                continue;
            if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            {
                game.Add(token);
                continue;
            }

            string body = token[Prefix.Length..];
            int eq = body.IndexOf('=');
            string name = eq < 0 ? body : body[..eq];
            string? value = eq < 0 ? null : body[(eq + 1)..];
            if (IsKnown(name))
                flags[name] = value;
            else
                unknown.Add(token);
        }

        string? Value(string name) => flags.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

        return new LauncherArgs
        {
            Original = [.. args],
            GameArgs = game,
            Unknown = unknown,
            IsVelopackHook = LooksLikeVelopackHook(args),
            Feed = Value("feed"),
            GameOverride = Value("game"),
            DataDir = Value("data"),
            Render = Value("render"),
            Resume = Value("resume"),
            SelfTest = Value("selftest"),
            Fake = Value("fake"),
            Shot = Value("shot"),
            Showcase = flags.ContainsKey("showcase"),
            NoAutoLaunch = flags.ContainsKey("no-autolaunch"),
        };
    }

    // The arguments Velopack should restart us with after applying an update: the original command
    // line, with any previous resume marker replaced by the requested one.
    public IReadOnlyList<string> RestartArgs(bool resumePlay)
    {
        var result = new List<string>(Original.Count + 1);
        bool passthrough = false;
        foreach (var token in Original)
        {
            if (token == "--")
                passthrough = true;
            if (!passthrough && token.StartsWith(Prefix + "resume", StringComparison.Ordinal))
                continue;
            result.Add(token);
        }
        if (resumePlay)
            result.Insert(0, Prefix + "resume=play"); // in front, so it can never land after a bare `--`
        return result;
    }

    private static bool IsKnown(string name) =>
        name
            is "feed"
                or "game"
                or "data"
                or "render"
                or "resume"
                or "selftest"
                or "fake"
                or "shot"
                or "showcase"
                or "no-autolaunch";
}
