using SimServer.Update;

// AutoUpdateOptions.Resolve: "put behind a flag, on by default for docker deployments, warn by default
// for other deployments" - plus the conventions the rest of Program.cs follows (flag beats env, an empty
// env value means unset).
static class OptionsTests
{
    static AutoUpdateOptions Resolve(
        Dictionary<string, string?> env,
        bool container,
        bool service,
        out List<string> warnings,
        params string[] args
    )
    {
        string? Get(string key) => env.GetValueOrDefault(key);
        return AutoUpdateOptions.Resolve(args, Get, container, service, out warnings);
    }

    public static void Run()
    {
        T.Section("options: defaults");
        var docker = Resolve([], container: true, service: false, out var w1);
        T.Eq(AutoUpdateMode.On, docker.Mode, "in a container: ON by default");
        T.Eq(
            UpdateRestart.Exit,
            docker.Restart,
            "in a container: restart = exit (a supervisor relaunches; nothing survives PID 1)"
        );
        var bare = Resolve([], container: false, service: false, out _);
        T.Eq(AutoUpdateMode.Warn, bare.Mode, "anywhere else: WARN by default");
        T.Eq(UpdateRestart.Relaunch, bare.Restart, "started by hand: restart = relaunch");
        T.Eq(
            UpdateRestart.Exit,
            Resolve([], false, service: true, out _).Restart,
            "under systemd: restart = exit (a relaunch would die with the unit's cgroup)"
        );
        T.Eq(
            TimeSpan.FromSeconds(60),
            bare.IdleWindow,
            "idle window defaults to 60 s (above the client's 20 s auto-reconnect)"
        );
        T.Eq(TimeSpan.FromHours(6), bare.SafetyNetInterval, "safety net defaults to 6 h");
        T.Check(
            !bare.Prerelease && bare.FeedOverride is null && bare.SimulatedVersion is null,
            "no prerelease / feed / simulate by default"
        );
        T.Eq(0, w1.Count, "defaults produce no warnings");
        T.Eq(85, AutoUpdateOptions.RestartExitCode, "restart exit code = 85, the same 'U' the client hands the launcher");

        T.Section("options: flag, env, empties, garbage");
        T.Eq(
            AutoUpdateMode.Off,
            Resolve(new() { ["SIM_AUTO_UPDATE"] = "off" }, true, false, out _).Mode,
            "SIM_AUTO_UPDATE=off wins over the container default"
        );
        T.Eq(
            AutoUpdateMode.On,
            Resolve(new() { ["SIM_AUTO_UPDATE"] = " ON " }, false, false, out _).Mode,
            "case and whitespace are forgiven"
        );
        T.Eq(
            AutoUpdateMode.Warn,
            Resolve(new() { ["SIM_AUTO_UPDATE"] = "on" }, true, false, out _, "--auto-update", "warn").Mode,
            "the --auto-update flag beats the env"
        );
        T.Eq(
            AutoUpdateMode.On,
            Resolve(new() { ["SIM_AUTO_UPDATE"] = "" }, true, false, out var wEmpty).Mode,
            "EMPTY env = unset (compose passes ${VAR:-}): the container default stands"
        );
        T.Eq(0, wEmpty.Count, "…and an empty value is not worth a warning");
        var bad = Resolve(new() { ["SIM_AUTO_UPDATE"] = "sometimes" }, false, false, out var wBad);
        T.Eq(AutoUpdateMode.Warn, bad.Mode, "a bad mode keeps the default…");
        T.Check(wBad.Count == 1 && wBad[0].Contains("sometimes"), "…and says so");
        T.Eq(
            AutoUpdateMode.Warn,
            Resolve(new() { ["SIM_AUTO_UPDATE"] = "7" }, false, false, out var wNum).Mode,
            "a number is not a mode"
        );
        T.Eq(1, wNum.Count, "…warned");

        T.Section("options: knobs");
        var knobs = Resolve(
            new()
            {
                ["SIM_UPDATE_IDLE_SECONDS"] = "120",
                ["SIM_UPDATE_INTERVAL_SECONDS"] = "3600",
                ["SIM_UPDATE_PRERELEASE"] = "1",
                ["SIM_UPDATE_FEED"] = " /feed ",
                ["SIM_UPDATE_RESTART"] = "relaunch",
            },
            container: true,
            service: false,
            out var wKnobs
        );
        T.Eq(TimeSpan.FromSeconds(120), knobs.IdleWindow, "idle seconds");
        T.Eq(TimeSpan.FromSeconds(3600), knobs.SafetyNetInterval, "interval seconds");
        T.Check(knobs.Prerelease, "prerelease");
        T.Eq("/feed", knobs.FeedOverride, "feed override, trimmed");
        T.Eq(UpdateRestart.Relaunch, knobs.Restart, "explicit restart beats the container default");
        T.Eq(0, wKnobs.Count, "valid knobs: no warnings");

        var clamped = Resolve(
            new() { ["SIM_UPDATE_IDLE_SECONDS"] = "1", ["SIM_UPDATE_INTERVAL_SECONDS"] = "5" },
            false,
            false,
            out var wClamp
        );
        T.Eq(TimeSpan.FromSeconds(10), clamped.IdleWindow, "idle window is clamped to 10 s");
        T.Eq(TimeSpan.FromSeconds(30), clamped.SafetyNetInterval, "safety net is clamped to 30 s");
        T.Eq(2, wClamp.Count, "both clamps are reported");
        var off = Resolve(
            new() { ["SIM_UPDATE_INTERVAL_SECONDS"] = "0", ["SIM_UPDATE_IDLE_SECONDS"] = "0" },
            false,
            false,
            out _
        );
        T.Check(!off.SafetyNetEnabled, "interval 0 switches the safety net OFF");
        T.Eq(TimeSpan.FromSeconds(10), off.IdleWindow, "…but an idle window of 0 is clamped, never 'apply at once'");
        var junk = Resolve(
            new() { ["SIM_UPDATE_IDLE_SECONDS"] = "soon", ["SIM_UPDATE_RESTART"] = "maybe" },
            false,
            false,
            out var wJunk
        );
        T.Eq(TimeSpan.FromSeconds(60), junk.IdleWindow, "garbage seconds keep the default");
        T.Eq(UpdateRestart.Relaunch, junk.Restart, "garbage restart keeps the default");
        T.Eq(2, wJunk.Count, "both reported");

        var simulated = Resolve(
            new() { ["SIM_UPDATE_SIMULATE"] = "9.9.9", ["SIM_AUTO_UPDATE"] = "off" },
            false,
            false,
            out _
        );
        T.Eq("9.9.9", simulated.SimulatedVersion, "SIM_UPDATE_SIMULATE is carried");
        T.Eq(AutoUpdateMode.On, simulated.Mode, "…and implies ON (warn/off would never reach the notice or the drain)");

        T.Section("options: what counts as a container / a service manager");
        bool Container(Dictionary<string, string?> env, params string[] files)
        {
            string? Get(string key) => env.GetValueOrDefault(key);
            bool Exists(string path) => files.Contains(path);
            return AutoUpdateOptions.DetectContainer(Get, Exists);
        }
        T.Check(
            Container(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }),
            "DOTNET_RUNNING_IN_CONTAINER (Microsoft's base images - ours)"
        );
        T.Check(Container([], "/.dockerenv"), "/.dockerenv (any other base image)");
        T.Check(Container([], "/run/.containerenv"), "/run/.containerenv (podman)");
        T.Check(Container(new() { ["KUBERNETES_SERVICE_HOST"] = "10.0.0.1" }), "KUBERNETES_SERVICE_HOST");
        T.Check(!Container(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "false" }), "an explicit false is not a container");
        T.Check(!Container([]), "a bare host is not a container");
        string? NoEnv(string _) => null;
        string? Systemd(string key) => key == "INVOCATION_ID" ? "abc" : null;
        T.Check(AutoUpdateOptions.DetectServiceManager(Systemd), "INVOCATION_ID = started by systemd");
        T.Check(!AutoUpdateOptions.DetectServiceManager(NoEnv), "no INVOCATION_ID = not a service");

        T.Section("build info: which release is this server?");
        T.Eq("0.0.14", ServerBuildInfo.Resolve("0.0.14", "0.0.0-dev", null), "a packaged install knows from its manifest");
        T.Eq("0.0.14", ServerBuildInfo.Resolve(null, "0.0.14", "0.0.13"), "else the stamped assembly version");
        T.Eq(
            "0.0.13",
            ServerBuildInfo.Resolve(null, "0.0.0-dev", "v0.0.13"),
            "else SIM_BUILD_VERSION (run-server.ps1: git describe)"
        );
        T.Eq<string?>(null, ServerBuildInfo.Resolve(null, "0.0.0-dev", null), "the dev sentinel is NOT a version");
        T.Eq(
            "1.0.0",
            ServerBuildInfo.Resolve("1.0.0", "0.0.0-dev", null),
            "a packaged 1.0.0 IS 1.0.0 - the manifest is taken at its word (found by the Docker e2e: it used to read as 'unknown', so nothing was ever newer)"
        );
        T.Eq(
            "1.0.0",
            ServerBuildInfo.Resolve(null, "1.0.0", null),
            "…and so is an assembly stamped 1.0.0: unstamped builds carry the csproj sentinel, never the SDK default"
        );
        T.Eq<string?>(null, ServerBuildInfo.Resolve("junk", null, "nope"), "garbage everywhere = unknown");
    }
}
