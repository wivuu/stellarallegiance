using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

public sealed record GameLauncherOptions(RepoPaths Repo, EndpointReference LobbyEndpoint, AppParameters Parameters);

// The Game Launcher (Avalonia) as an Aspire resource, for UI REVIEW only - it never needs to start the
// actual game. Shape mirrors GodotClient, with one difference: there is no ProjectReference AND the
// resource runs the BUILT BINARY directly (not `dotnet run`/a wrapper), so it is a clean child process
// and a launcher compile error only fails this one resource (output lands in its console log):
//   * `launcher` - explicit-start executable (dashboard Start / `aspire resource launcher start`): builds
//                  launcher/App/StellarLauncher.csproj first (OnBeforeResourceStarted blocks the start),
//                  then runs repo.LauncherExe against the LOCAL lobby, always in the `main` data dir.
//   * `show`     - custom command (dashboard dialog or `aspire resource launcher show --view showcase`)
//                  that opens one view - the real flow, the component showcase, or any fake state from
//                  FakeViews.cs - each in its own data dir so views can be open side by side; `shot` renders
//                  one off-screen to a PNG and exits instead of leaving a window open.
//
// A window needs an AWAKE display: on a Mac whose display sleeps Avalonia dies at start (CVDisplayLink,
// native error -6661). `shot` renders off-screen and works regardless.
public static class GameLauncherExtensions
{
    static readonly List<Process> Spawned = [];
    static int _lifetimeHooked;

    public static IResourceBuilder<ExecutableResource> AddGameLauncher(
        this IDistributedApplicationBuilder builder,
        string name,
        GameLauncherOptions o
    )
    {
        var p = o.Parameters;
        var mainDataDir = Path.Combine(o.Repo.LauncherLocalDir, "main");
        var launcher = builder
            .AddExecutable(name, o.Repo.LauncherExe, o.Repo.Root)
            .WithExplicitStart()
            .WithEnvironment("PUBLIC_LOBBY", o.LobbyEndpoint)
            .WithArgs(async ctx =>
            {
                ctx.Args.Add($"--launcher-data={mainDataDir}");
                var view = await p.LauncherView.Resource.GetValueAsync(ctx.CancellationToken);
                foreach (var a in GameLauncherArgs.ViewArgs(view))
                    ctx.Args.Add(a);
                foreach (
                    var a in ClientLauncher.SplitArgs(await p.LauncherArgs.Resource.GetValueAsync(ctx.CancellationToken))
                )
                    ctx.Args.Add(a);
            });

        launcher.OnBeforeResourceStarted(
            async (resource, evt, ct) =>
            {
                var log = evt.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);
                await LauncherBuild.BuildAsync(o.Repo, log, ct);
            }
        );

        launcher.WithCommand(
            name: "show",
            displayName: "Show launcher view…",
            executeCommand: ctx => ShowAsync(ctx, o),
            commandOptions: new CommandOptions
            {
                Description =
                    "Build the Game Launcher C# and open one view (the real flow, the component "
                    + "showcase, or any fake state) for UI review - optionally as an off-screen screenshot.",
                IconName = "WindowApps",
                IsHighlighted = true,
                UpdateState = _ => ResourceCommandState.Enabled,
                Progress = new CommandProgressOptions
                {
                    Title = "Opening launcher view",
                    Message = "Building the Game Launcher C# and starting it…",
                },
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "view",
                        Label = "View",
                        InputType = InputType.Choice,
                        Required = true,
                        Value = "flow",
                        // Fake states hard-coded from launcher/App/Views/FakeViews.cs (Names) - update both
                        // when a state is added there.
                        Options =
                        [
                            new("flow", "Flow - the real launcher (from-source: DEV BUILD, no update calls)"),
                            new("showcase", "Showcase - every ported component on one page"),
                            new("checking", "Fake: checking - CHECKING FOR UPDATES"),
                            new("uptodate", "Fake: uptodate - READY FOR LAUNCH"),
                            new("available", "Fake: available - UPDATE AVAILABLE"),
                            new("downloading", "Fake: downloading - DOWNLOADING UPDATE"),
                            new("rebuilding", "Fake: rebuilding - REBUILDING PACKAGE"),
                            new("applying", "Fake: applying - INSTALLING UPDATE"),
                            new("updated", "Fake: updated - UPDATE COMPLETE"),
                            new("checkfailed", "Fake: checkfailed - READY FOR LAUNCH (check offline)"),
                            new("updatefailed", "Fake: updatefailed - UPDATE FAILED"),
                            new("launching", "Fake: launching - LAUNCHING"),
                            new("adopted", "Fake: adopted - GAME RUNNING"),
                            new("crashed", "Fake: crashed - SIGNAL LOST"),
                            new("requested", "Fake: requested - UPDATE REQUESTED (from game)"),
                            new("missing", "Fake: missing - GAME FILES MISSING"),
                            new("dev", "Fake: dev - DEV BUILD"),
                            new("translocated", "Fake: translocated - READY FOR LAUNCH (updates offline)"),
                        ],
                        AllowCustomChoice = true, // lets a state added to FakeViews.cs be typed before this list catches up
                    },
                    new InteractionInput
                    {
                        Name = "launcher-args",
                        Label = "Extra launcher args",
                        InputType = InputType.Text,
                        MaxLength = 2000,
                    },
                    new InteractionInput
                    {
                        Name = "shot",
                        Label = "Screenshot to (PNG path)",
                        InputType = InputType.Text,
                        MaxLength = 500,
                        Placeholder = "shot.png",
                    },
                    new InteractionInput
                    {
                        Name = "skip-build",
                        Label = "Skip launcher build",
                        InputType = InputType.Boolean,
                        Value = "false",
                    },
                ],
            }
        );

        return launcher;
    }

    static async Task<ExecuteCommandResult> ShowAsync(ExecuteCommandContext ctx, GameLauncherOptions o)
    {
        var ct = ctx.CancellationToken;
        var view = GameLauncherArgs.Normalize(ctx.Arguments.GetString("view"));
        var extra = ClientLauncher.SplitArgs(ctx.Arguments.GetString("launcher-args"));
        var shotArg = ctx.Arguments.GetString("shot");
        var skipBuild = ctx.Arguments.ContainsName("skip-build") && ctx.Arguments.GetBoolean("skip-build");

        if (!skipBuild)
            await LauncherBuild.BuildAsync(o.Repo, ctx.Logger, ct);

        // Every view/shot gets its OWN data dir (own single-instance lock): opening the same view twice
        // refocuses the existing window instead of stacking copies, different views can sit side by side,
        // and a shot never collides with an open window's lock.
        var args = new List<string>();
        string? shotPath = null;
        if (!string.IsNullOrWhiteSpace(shotArg))
        {
            shotPath = Path.IsPathRooted(shotArg) ? shotArg : Path.GetFullPath(Path.Combine(o.Repo.Root, shotArg));
            args.Add($"--launcher-data={Path.Combine(o.Repo.LauncherLocalDir, "shot")}");
            args.Add($"--launcher-shot={shotPath}");
        }
        else
        {
            args.Add($"--launcher-data={Path.Combine(o.Repo.LauncherLocalDir, view)}");
        }
        args.AddRange(GameLauncherArgs.ViewArgs(view));
        args.AddRange(extra);

        var env = new Dictionary<string, string> { ["PUBLIC_LOBBY"] = o.LobbyEndpoint.Url };
        var spec = new ProcessSpec(o.Repo.LauncherExe, args, o.Repo.Root, env);

        if (shotPath is not null)
        {
            // Off-screen render mode: the launcher exits on its own once the PNG is written.
            var result = await ProcessRunner.RunAsync(spec, ctx.Logger, ct);
            return result.ExitCode == 0
                ? CommandResults.Success($"Saved screenshot to {shotPath}.")
                : CommandResults.Failure($"Launcher exited {result.ExitCode}:{Environment.NewLine}{result.Tail}");
        }

        HookLifetime(ctx.Services);
        var process = ProcessRunner.StartDetached(spec, ctx.Logger);
        lock (Spawned)
            Spawned.Add(process);
        process.Exited += (_, _) =>
        {
            lock (Spawned)
                Spawned.Remove(process);
            ctx.Logger.LogInformation("Launcher (pid {Pid}) exited with {Code}.", process.Id, process.ExitCode);
        };
        return CommandResults.Success($"Opened launcher view '{view}' pid {process.Id}.");
    }

    // Detached launcher windows would outlive `aspire stop` otherwise. Duplicated from GodotClient rather
    // than shared, to keep each resource's lifecycle hook independent and this diff small.
    static void HookLifetime(IServiceProvider services)
    {
        if (Interlocked.Exchange(ref _lifetimeHooked, 1) == 1)
            return;
        services
            .GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() =>
            {
                Process[] all;
                lock (Spawned)
                    all = Spawned.ToArray();
                foreach (var proc in all)
                    ProcessRunner.TryKill(proc);
            });
    }
}

// Pure view -> launcher-args mapping, shared by the tracked resource and the `show` command so the two
// can never drift.
public static class GameLauncherArgs
{
    // The view doubles as a data-dir name and may be TYPED (AllowCustomChoice), so reduce it to a plain
    // lowercase token - never a path.
    public static string Normalize(string? view)
    {
        var key = new string(
            (view ?? "").Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()
        );
        return key.Length == 0 ? "flow" : key;
    }

    public static IReadOnlyList<string> ViewArgs(string? view) =>
        Normalize(view) switch
        {
            "flow" => [], // real flow: from-source build shows "DEV BUILD · not installed", no update calls
            "showcase" => ["--launcher-showcase"],
            var fake => [$"--launcher-fake={fake}"],
        };
}

// Mirrors ClientBuild: the `launcher` resource runs a BUILT BINARY (not `dotnet run`), so every start/show
// builds launcher/App/StellarLauncher.csproj first.
public static class LauncherBuild
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task BuildAsync(RepoPaths repo, ILogger log, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var result = await ProcessRunner.RunAsync(
                new ProcessSpec("dotnet", ["build", repo.LauncherProject, "-c", "Debug", "--nologo"], repo.Root),
                log,
                ct
            );
            if (result.ExitCode != 0)
                throw new DistributedApplicationException(
                    $"Game Launcher build failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Tail}"
                );
        }
        finally
        {
            Gate.Release();
        }
    }
}
