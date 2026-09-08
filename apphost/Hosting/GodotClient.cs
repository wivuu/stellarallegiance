using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

public sealed record GodotClientOptions(
    RepoPaths Repo,
    string? GodotPath,
    EndpointReference LobbyEndpoint,
    EndpointReference ServerEndpoint,
    AppParameters Parameters
);

// The Godot client as an Aspire resource:
//   * `client`   - an explicit-start executable (dashboard Start / `aspire resource client start`): builds the
//                  client C# first (OnBeforeResourceStarted blocks the start), then runs Godot with args from
//                  the client-mode / client-args parameters. Logs stream to the dashboard.
//   * `launch`   - a custom command (dashboard dialog or `aspire resource client launch --mode autofly ...`)
//                  that spawns an ADDITIONAL Godot instance with per-launch options; output goes to the same
//                  console log, processes are killed when the AppHost stops.
public static class GodotClientExtensions
{
    static readonly List<Process> Spawned = [];
    static int _lifetimeHooked;

    public static IResourceBuilder<ExecutableResource> AddGodotClient(
        this IDistributedApplicationBuilder builder,
        string name,
        GodotClientOptions o
    )
    {
        var p = o.Parameters;
        var client = builder
            .AddExecutable(name, o.GodotPath ?? "godot-not-found", o.Repo.Root, "--path", "client")
            .WithExplicitStart()
            .WithEnvironment("PUBLIC_LOBBY", o.LobbyEndpoint)
            .WithEnvironment("SIM_SECRET", p.SimSecret)
            .WithEnvironment("PILOT_NAME", p.PilotName)
            .WithArgs(async ctx =>
            {
                var mode = ClientLauncher.ParseMode(await p.ClientMode.Resource.GetValueAsync(ctx.CancellationToken));
                foreach (var a in ClientLauncher.ModeArgs(mode, HostPort(o.ServerEndpoint)))
                    ctx.Args.Add(a);
                foreach (var a in ClientLauncher.SplitArgs(await p.ClientArgs.Resource.GetValueAsync(ctx.CancellationToken)))
                    ctx.Args.Add(a);
            });

        client.OnBeforeResourceStarted(
            async (resource, evt, ct) =>
            {
                if (o.GodotPath is null)
                    throw new DistributedApplicationException(GodotLocator.Guidance);
                var log = evt.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);
                var config = await p.ClientBuildConfig.Resource.GetValueAsync(ct) ?? "Debug";
                await ClientBuild.BuildAsync(o.Repo, config, log, ct);
            }
        );

        client.WithCommand(
            name: "launch",
            displayName: "Launch client…",
            executeCommand: ctx => LaunchAsync(ctx, o),
            commandOptions: new CommandOptions
            {
                Description = "Build the client C# and start another Godot instance with the chosen mode and extra args.",
                IconName = "Rocket",
                IsHighlighted = true,
                UpdateState = _ => ResourceCommandState.Enabled,
                Progress = new CommandProgressOptions
                {
                    Title = "Launching client",
                    Message = "Building client C# and starting Godot…",
                },
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "mode",
                        Label = "Mode",
                        InputType = InputType.Choice,
                        Required = true,
                        Value = "lobby",
                        Options =
                        [
                            new("lobby", "Lobby - server browser on the local lobby (default)"),
                            new("direct", "Direct - connect to localhost:8090 (unapproved/private server only)"),
                            new("autofly", "Autofly - self-driving harness (--autofly)"),
                        ],
                    },
                    new InteractionInput
                    {
                        Name = "config",
                        Label = "Build configuration",
                        InputType = InputType.Choice,
                        Value = "Debug",
                        Options = [new("Debug", "Debug"), new("Release", "Release (optimized C#)")],
                    },
                    new InteractionInput
                    {
                        Name = "godot-args",
                        Label = "Extra Godot args",
                        InputType = InputType.Text,
                        MaxLength = 2000,
                        Placeholder = "--strafe-test -- --ui-shot=/tmp/live.png --ui-shot-delay=14",
                    },
                    new InteractionInput
                    {
                        Name = "write-movie",
                        Label = "Write movie to (path)",
                        InputType = InputType.Text,
                        MaxLength = 500,
                    },
                    new InteractionInput
                    {
                        Name = "pilot-name",
                        Label = "Pilot name",
                        InputType = InputType.Text,
                        MaxLength = 24,
                    },
                    new InteractionInput
                    {
                        Name = "seed-dev-login",
                        Label = "Seed a dev sign-in for the local lobby first (replaces user://auth.json)",
                        InputType = InputType.Boolean,
                        Value = "false",
                    },
                    new InteractionInput
                    {
                        Name = "skip-build",
                        Label = "Skip client build",
                        InputType = InputType.Boolean,
                        Value = "false",
                    },
                    new InteractionInput
                    {
                        Name = "wait",
                        Label = "Wait for Godot to exit (harness runs)",
                        InputType = InputType.Boolean,
                        Value = "false",
                    },
                ],
            }
        );

        return client;
    }

    static string HostPort(EndpointReference endpoint) => $"{endpoint.Host}:{endpoint.Port}";

    static async Task<ExecuteCommandResult> LaunchAsync(ExecuteCommandContext ctx, GodotClientOptions o)
    {
        var ct = ctx.CancellationToken;
        if (o.GodotPath is null)
            return CommandResults.Failure(GodotLocator.Guidance);
        var p = o.Parameters;
        var mode = ClientLauncher.ParseMode(ctx.Arguments.GetString("mode"));
        var config = ctx.Arguments.GetString("config") ?? "Debug";
        var extra = ClientLauncher.SplitArgs(ctx.Arguments.GetString("godot-args"));
        var skipBuild = ctx.Arguments.ContainsName("skip-build") && ctx.Arguments.GetBoolean("skip-build");
        var wait = ctx.Arguments.ContainsName("wait") && ctx.Arguments.GetBoolean("wait");

        var pilot = ctx.Arguments.GetString("pilot-name") ?? await p.PilotName.Resource.GetValueAsync(ct);
        if (ctx.Arguments.ContainsName("seed-dev-login") && ctx.Arguments.GetBoolean("seed-dev-login"))
            await DevSession.SeedAsync(
                o.LobbyEndpoint.Url,
                string.IsNullOrWhiteSpace(pilot) ? "Dev Pilot" : pilot,
                ctx.Logger,
                ct
            );

        if (!skipBuild)
            await ClientBuild.BuildAsync(o.Repo, config, ctx.Logger, ct);

        var args = new List<string> { "--path", "client" };
        args.AddRange(ClientLauncher.ModeArgs(mode, HostPort(o.ServerEndpoint)));
        args.AddRange(ClientLauncher.MovieArgs(ctx.Arguments.GetString("write-movie"), o.Repo.Root, extra));
        args.AddRange(extra);

        var env = new Dictionary<string, string> { ["PUBLIC_LOBBY"] = o.LobbyEndpoint.Url };
        if (await p.SimSecret.Resource.GetValueAsync(ct) is { Length: > 0 } secret)
            env["SIM_SECRET"] = secret;
        if (!string.IsNullOrWhiteSpace(pilot))
            env["PILOT_NAME"] = pilot.Trim();

        var spec = new ProcessSpec(o.GodotPath, args, o.Repo.Root, env);
        if (wait)
        {
            var result = await ProcessRunner.RunAsync(spec, ctx.Logger, ct);
            return result.ExitCode == 0
                ? CommandResults.Success($"Godot exited 0 ({mode}, {config}).")
                : CommandResults.Failure($"Godot exited {result.ExitCode}:{Environment.NewLine}{result.Tail}");
        }

        HookLifetime(ctx.Services);
        var process = ProcessRunner.StartDetached(spec, ctx.Logger);
        lock (Spawned)
            Spawned.Add(process);
        process.Exited += (_, _) =>
        {
            lock (Spawned)
                Spawned.Remove(process);
            ctx.Logger.LogInformation("Godot (pid {Pid}) exited with {Code}.", process.Id, process.ExitCode);
        };
        return CommandResults.Success($"Launched Godot pid {process.Id} ({mode}, {config}).");
    }

    // Detached clients would outlive `aspire stop` otherwise (the godot wrapper is known to orphan children).
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
