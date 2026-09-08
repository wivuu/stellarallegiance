using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

// Two front doors onto RailwayDeployer (argument names avoid the CLI's own --project/--environment options):
//   * dashboard: "Deploy to Railway" on the lobby / server resource - a dialog that asks for project,
//     environment and which variables to push (also `aspire resource lobby deploy-railway --project x ...`);
//   * CLI: `aspire do deploy-lobby` / `aspire do deploy-server` pipeline steps reading the same parameters.
//     They are deliberately NOT wired into `aspire deploy`, so a bare deploy never pushes production.
public static class RailwayExtensions
{
    public static IResourceBuilder<T> WithRailwayDeploy<T>(
        this IResourceBuilder<T> builder,
        RailwayTarget target,
        RepoPaths repo,
        AppParameters p
    )
        where T : IResource
    {
        var projectParam = target.Kind == RailwayTargetKind.Lobby ? p.RailwayLobbyProject : p.RailwayServerProject;
        var inputs = new List<InteractionInput>
        {
            new()
            {
                Name = "railway-project",
                Label = "Railway project / service name",
                InputType = InputType.Text,
                Required = true,
                Value = Current(projectParam),
            },
            new()
            {
                Name = "railway-environment",
                Label = "Environment",
                InputType = InputType.Text,
                Required = true,
                Value = Current(p.RailwayEnvironment),
            },
        };
        foreach (var v in target.Vars)
            inputs.Add(
                new InteractionInput
                {
                    Name = v.Key.ToLowerInvariant().Replace('_', '-'),
                    Label = v.Label,
                    InputType = v.Secret ? InputType.SecretText : InputType.Text,
                    Placeholder = "blank = leave the Railway value untouched",
                    Value = v.Secret ? null : Current(DeployDefault(p, v.Key)),
                    MaxLength = 2000,
                }
            );
        inputs.Add(
            new InteractionInput
            {
                Name = "dry-run",
                Label = "Dry run (print the plan only)",
                InputType = InputType.Boolean,
                Value = "false",
            }
        );

        return builder.WithCommand(
            name: "deploy-railway",
            displayName: "Deploy to Railway…",
            executeCommand: async ctx =>
            {
                var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var v in target.Vars)
                    if (ctx.Arguments.GetString(v.Key.ToLowerInvariant().Replace('_', '-')) is { Length: > 0 } value)
                        vars[v.Key] = value;
                var request = new RailwayDeployRequest(
                    ctx.Arguments.GetString("railway-project") ?? "",
                    ctx.Arguments.GetString("railway-environment") ?? "production",
                    vars,
                    ctx.Arguments.ContainsName("dry-run") && ctx.Arguments.GetBoolean("dry-run")
                );
                try
                {
                    var message = await RailwayDeployer.DeployAsync(
                        target,
                        request,
                        repo.Root,
                        ctx.Logger,
                        ctx.CancellationToken
                    );
                    return CommandResults.Success(message);
                }
                catch (DistributedApplicationException e)
                {
                    return CommandResults.Failure(e.Message);
                }
            },
            commandOptions: new CommandOptions
            {
                Description =
                    $"Upload the repo and (re)deploy the {target.DisplayName} on Railway. Re-deploying the same project name updates it in place.",
                ConfirmationMessage =
                    $"Upload this checkout and deploy the {target.DisplayName} to Railway? Existing projects are updated in place.",
                IconName = "CloudArrowUp",
                UpdateState = _ => ResourceCommandState.Enabled,
                Progress = new CommandProgressOptions
                {
                    Title = "Deploying to Railway",
                    Message = "railway up in progress - see the console log",
                },
                Arguments = inputs,
            }
        );
    }

    public static void AddRailwayPipelineSteps(this IDistributedApplicationBuilder builder, RepoPaths repo, AppParameters p)
    {
        builder.Pipeline.AddStep("deploy-lobby", ctx => RunStepAsync(ctx, RailwayTarget.Lobby, repo, p));
        builder.Pipeline.AddStep("deploy-server", ctx => RunStepAsync(ctx, RailwayTarget.Server, repo, p));
    }

    // CLI path (`aspire do deploy-lobby`): values from parameters (env / .env / user secrets); when the CLI is
    // interactive we confirm project + environment first. Pushes the same non-secret defaults the old
    // scripts did; secrets stay a dashboard / `railway variable set --stdin` affair.
    static async Task RunStepAsync(PipelineStepContext ctx, RailwayTarget target, RepoPaths repo, AppParameters p)
    {
        var ct = ctx.CancellationToken;
        var projectParam = target.Kind == RailwayTargetKind.Lobby ? p.RailwayLobbyProject : p.RailwayServerProject;
        var project = await projectParam.Resource.GetValueAsync(ct) ?? "";
        var env = await p.RailwayEnvironment.Resource.GetValueAsync(ct) ?? "production";

        var interaction = ctx.Services.GetService<IInteractionService>();
        if (interaction is { IsAvailable: true })
        {
            var answers = await interaction.PromptInputsAsync(
                $"Deploy {target.DisplayName} to Railway",
                "Re-deploying an existing project name updates it in place.",
                [
                    new InteractionInput
                    {
                        Name = "railway-project",
                        Label = "Railway project / service name",
                        InputType = InputType.Text,
                        Required = true,
                        Value = project,
                    },
                    new InteractionInput
                    {
                        Name = "railway-environment",
                        Label = "Environment",
                        InputType = InputType.Text,
                        Required = true,
                        Value = env,
                    },
                ],
                cancellationToken: ct
            );
            if (answers.Canceled)
                throw new DistributedApplicationException("Deployment cancelled.");
            project = answers.Data.GetString("railway-project") ?? project;
            env = answers.Data.GetString("railway-environment") ?? env;
        }

        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in target.Vars.Where(v => !v.Secret))
            if (DeployDefault(p, v.Key) is { } param && await param.Resource.GetValueAsync(ct) is { Length: > 0 } value)
                vars[v.Key] = value;

        var message = await RailwayDeployer.DeployAsync(
            target,
            new RailwayDeployRequest(project, env, vars, DryRun: false),
            repo.Root,
            ctx.Logger,
            ct
        );
        ctx.Logger.LogInformation("{Message}", message);
    }

    // Which parameter pre-fills / supplies each pushed variable. AUTH_DEV_LOGIN and the local-only defaults
    // are intentionally absent: they never leave the dev box.
    static IResourceBuilder<ParameterResource>? DeployDefault(AppParameters p, string key) =>
        key switch
        {
            "LOBBY_PUBLIC_URL" => p.LobbyPublicUrl,
            "STUN_URL" => p.StunUrl,
            "LOBBY_ADMINS" => p.LobbyAdmins,
            "RANKED_RESULTS" => p.RankedResults,
            "AUTH_GITHUB_CLIENT_ID" => p.AuthGithubClientId,
            "AUTH_GITHUB_CLIENT_SECRET" => p.AuthGithubClientSecret,
            "AUTH_GOOGLE_CLIENT_ID" => p.AuthGoogleClientId,
            "AUTH_GOOGLE_CLIENT_SECRET" => p.AuthGoogleClientSecret,
            "AUTH_STEAM_API_KEY" => p.AuthSteamApiKey,
            "PUBLIC_LOBBY" => p.PublicLobby,
            "SIM_SECRET" => p.SimSecret,
            "SIM_AUTOSTART" => p.SimAutostart,
            _ => null, // ALLOW_UNVERIFIED_SERVERS, SIM_MAX_PLAYERS: dialog-only, no local default is pushed
        };

    // Dialog pre-fill: parameter values are config-backed constants, safe to read at model-build time.
    static string? Current(IResourceBuilder<ParameterResource>? param)
    {
        if (param is null)
            return null;
        try
        {
            // Model-build time, config-backed constants: blocking here is safe and keeps the dialog static.
            var v = param.Resource.GetValueAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch (MissingParameterValueException)
        {
            return null;
        }
    }
}
