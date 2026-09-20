using System.Globalization;

namespace SimServer.Update;

// What the server does when a newer release of the game exists (docs/adr/0005).
//   Off   never looks, ignores the lobby's Release Adverts.
//   Warn  tells the OPERATOR (a log warning) and changes nothing.
//   On    restarts onto the new build - but only once the server has been EMPTY of players for the
//         idle window. Needs a packaged (Velopack) install; anywhere else it degrades to Warn.
public enum AutoUpdateMode
{
    Off,
    Warn,
    On,
}

// How the new build starts once the package has been swapped in.
//   Exit      exit with code 85 and let whatever supervises this process start it again: the release
//             image's entrypoint, systemd (RestartForceExitStatus=85), a shell loop. The right choice
//             wherever a supervisor exists - and the only one that works in a container, where the
//             server is PID 1 and nothing it spawns can outlive it.
//   Relaunch  ask Velopack's updater to start the new build after this process has gone. For a
//             server somebody started by hand in a terminal.
public enum UpdateRestart
{
    Exit,
    Relaunch,
}

public sealed record AutoUpdateOptions(
    AutoUpdateMode Mode,
    TimeSpan IdleWindow,
    TimeSpan SafetyNetInterval,
    bool Prerelease,
    string? FeedOverride,
    UpdateRestart Restart,
    string? SimulatedVersion
)
{
    // The exit code that means "I swapped my package in, start me again". Same value the game client
    // uses towards the Game Launcher (LauncherContract.UpdateExitCode, 85 = 'U'): one meaning repo-wide.
    public const int RestartExitCode = StellarAllegiance.Shared.LauncherContract.UpdateExitCode;

    // Above the client's 20 s auto-reconnect (ConnectionManager.ReconnectMax): a pilot whose link
    // dropped must be able to get back in before the server decides it is empty.
    public static readonly TimeSpan DefaultIdleWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinIdleWindow = TimeSpan.FromSeconds(10);

    // Not a poller - the public lobby tells listed servers about releases. This only covers a server
    // the lobby cannot reach (unlisted, or a lobby that predates Release Adverts).
    public static readonly TimeSpan DefaultSafetyNet = TimeSpan.FromHours(6);
    public static readonly TimeSpan MinSafetyNet = TimeSpan.FromSeconds(30);

    public bool SafetyNetEnabled => SafetyNetInterval > TimeSpan.Zero;

    // Pure: everything it needs comes in, every complaint goes out as a warning for the caller to log
    // (the logger does not exist yet when options are resolved). A `--auto-update` flag beats
    // SIM_AUTO_UPDATE, like every other flag/env pair in Program.cs; an EMPTY env value means unset
    // (compose passes `${VAR:-}`).
    public static AutoUpdateOptions Resolve(
        string[] args,
        Func<string, string?> env,
        bool inContainer,
        bool underServiceManager,
        out List<string> warnings
    )
    {
        warnings = [];
        var problems = warnings;
        string? Env(string key) => (env(key) ?? "").Trim() is { Length: > 0 } value ? value : null;

        // DEV ONLY: pretend a release exists, so the notice / drain / restart path can be exercised from
        // `dotnet run` without packaging anything. Implies On - the default Warn would never reach them.
        string? simulate = Env("SIM_UPDATE_SIMULATE");

        var mode = inContainer ? AutoUpdateMode.On : AutoUpdateMode.Warn;
        string? requested = Env("SIM_AUTO_UPDATE");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--auto-update")
                requested = args[i + 1].Trim();
        if (requested is not null)
        {
            if (Enum.TryParse<AutoUpdateMode>(requested, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                mode = parsed;
            else
                problems.Add(
                    $"auto-update mode '{requested}' is not off|warn|on - using {mode.ToString().ToLowerInvariant()}"
                );
        }
        if (simulate is not null)
            mode = AutoUpdateMode.On;

        var restart = inContainer || underServiceManager ? UpdateRestart.Exit : UpdateRestart.Relaunch;
        if (Env("SIM_UPDATE_RESTART") is { } restartRaw)
        {
            if (
                Enum.TryParse<UpdateRestart>(restartRaw, ignoreCase: true, out var parsedRestart)
                && Enum.IsDefined(parsedRestart)
            )
                restart = parsedRestart;
            else
                problems.Add(
                    $"SIM_UPDATE_RESTART '{restartRaw}' is not exit|relaunch - using {restart.ToString().ToLowerInvariant()}"
                );
        }

        TimeSpan Seconds(string key, TimeSpan fallback, TimeSpan min, bool zeroDisables)
        {
            if (Env(key) is not { } raw)
                return fallback;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            {
                problems.Add($"{key} '{raw}' is not a number of seconds - using {fallback.TotalSeconds:0}");
                return fallback;
            }
            if (seconds == 0 && zeroDisables)
                return TimeSpan.Zero;
            var value = TimeSpan.FromSeconds(seconds);
            if (value < min)
            {
                problems.Add($"{key} {seconds:0.##} is below the minimum - using {min.TotalSeconds:0}");
                return min;
            }
            return value;
        }

        return new AutoUpdateOptions(
            Mode: mode,
            IdleWindow: Seconds("SIM_UPDATE_IDLE_SECONDS", DefaultIdleWindow, MinIdleWindow, zeroDisables: false),
            SafetyNetInterval: Seconds("SIM_UPDATE_INTERVAL_SECONDS", DefaultSafetyNet, MinSafetyNet, zeroDisables: true),
            Prerelease: Env("SIM_UPDATE_PRERELEASE") is "1" or "true" or "TRUE" or "True",
            FeedOverride: Env("SIM_UPDATE_FEED"),
            Restart: restart,
            SimulatedVersion: simulate
        );
    }

    // "Docker deployment" for the default. DOTNET_RUNNING_IN_CONTAINER is set by Microsoft's base
    // images (ours); the file markers cover other bases and podman, the last one Kubernetes.
    public static bool DetectContainer(Func<string, string?> env, Func<string, bool> fileExists) =>
        (env("DOTNET_RUNNING_IN_CONTAINER") ?? "") is "true" or "1" or "TRUE" or "True"
        || fileExists("/.dockerenv")
        || fileExists("/run/.containerenv")
        || !string.IsNullOrEmpty(env("KUBERNETES_SERVICE_HOST"));

    // systemd sets INVOCATION_ID for every service it starts. Under it a Velopack relaunch would be
    // killed with the unit's control group the moment this process exits - so exit and let it restart us.
    public static bool DetectServiceManager(Func<string, string?> env) => !string.IsNullOrEmpty(env("INVOCATION_ID"));
}
