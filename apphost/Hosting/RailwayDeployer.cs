using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

public enum RailwayTargetKind
{
    Lobby,
    Server,
}

public sealed record RailwayVar(string Key, bool Secret, string Label);

public sealed record RailwayTarget(
    RailwayTargetKind Kind,
    string DisplayName,
    string Dockerfile,
    IReadOnlyList<RailwayVar> Vars
)
{
    // Variables the deploy dialog offers to push (blank = leave the Railway value untouched).
    public static readonly RailwayTarget Lobby = new(
        RailwayTargetKind.Lobby,
        "public lobby",
        "public-lobby/Dockerfile",
        [
            new("LOBBY_PUBLIC_URL", false, "LOBBY_PUBLIC_URL (join-token issuer / passkey RP / OAuth callback base)"),
            new("STUN_URL", false, "STUN_URL"),
            new("LOBBY_ADMINS", false, "LOBBY_ADMINS"),
            new("RANKED_RESULTS", false, "RANKED_RESULTS (flagged | authenticated)"),
            new("ALLOW_UNVERIFIED_SERVERS", false, "ALLOW_UNVERIFIED_SERVERS (true | false)"),
            new("AUTH_GITHUB_CLIENT_ID", false, "AUTH_GITHUB_CLIENT_ID"),
            new("AUTH_GITHUB_CLIENT_SECRET", true, "AUTH_GITHUB_CLIENT_SECRET"),
            new("AUTH_GOOGLE_CLIENT_ID", false, "AUTH_GOOGLE_CLIENT_ID"),
            new("AUTH_GOOGLE_CLIENT_SECRET", true, "AUTH_GOOGLE_CLIENT_SECRET"),
            new("AUTH_STEAM_API_KEY", true, "AUTH_STEAM_API_KEY"),
        ]
    );

    public static readonly RailwayTarget Server = new(
        RailwayTargetKind.Server,
        "game server",
        "server/Dockerfile",
        [
            new("PUBLIC_LOBBY", false, "PUBLIC_LOBBY (empty = compiled-in default lobby)"),
            new("SIM_SECRET", true, "SIM_SECRET"),
            new("SIM_AUTOSTART", false, "SIM_AUTOSTART (1 = perpetual match)"),
            new("SIM_MAX_PLAYERS", false, "SIM_MAX_PLAYERS"),
        ]
    );

    public const string LobbyFirstDeploySteps =
        "One-time, in the Railway dashboard for this project: (1) add a Postgres service and set "
        + "ConnectionStrings__postgres-database=${{Postgres.DATABASE_URL}} on the lobby service; (2) Service -> Settings -> Deploy -> "
        + "Pre-deploy command: dotnet PublicLobby.dll --migrate; (3) App Sleeping: OFF; (4) attach the custom domain that "
        + "LOBBY_PUBLIC_URL names. Committing a config change rebuilds the last upload, so do these before the next deploy.";
}

public sealed record RailwayDeployRequest(
    string Project,
    string Environment,
    IReadOnlyDictionary<string, string> Variables, // KEY -> value to set (non-empty only)
    bool DryRun
);

// Port of scripts/deploy-railway-{lobby,server}.ps1: idempotent by project NAME (re-running updates the
// existing Railway project instead of creating a duplicate listing), Dockerfile selected via
// RAILWAY_DOCKERFILE_PATH because `railway up` tars the git root (honouring .railwayignore). Variables are
// set with --skip-deploys BEFORE `railway up`, since every config commit on Railway rebuilds the last upload.
public static class RailwayDeployer
{
    public static async Task<string> DeployAsync(
        RailwayTarget target,
        RailwayDeployRequest request,
        string repoRoot,
        ILogger log,
        CancellationToken ct
    )
    {
        var project = request.Project.Trim();
        if (project.Length is < 3 or > 50)
            throw new DistributedApplicationException("Railway project name must be 3-50 characters.");
        var env = string.IsNullOrWhiteSpace(request.Environment) ? "production" : request.Environment.Trim();

        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RAILWAY_DOCKERFILE_PATH"] = target.Dockerfile,
        };
        if (target.Kind == RailwayTargetKind.Server)
            vars["SIM_PUBLIC_NAME"] = project; // the project name doubles as the server's public name
        foreach (var (k, v) in request.Variables)
            if (!string.IsNullOrWhiteSpace(v))
                vars[k] = v.Trim();
        var secretKeys = target.Vars.Where(v => v.Secret).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);

        if (request.DryRun)
        {
            log.LogInformation(
                "[dry-run] would deploy the {Target} to Railway project '{Project}' ({Env}) with {Count} variable(s): {Keys}",
                target.DisplayName,
                project,
                env,
                vars.Count,
                string.Join(", ", vars.Keys)
            );
            return $"Dry run: {target.DisplayName} -> '{project}' ({env}); variables {string.Join(", ", vars.Keys)}. Nothing was changed.";
        }

        await Railway(["whoami"], "Railway CLI is not logged in - run `railway login` first.");

        var projectId = await FindProjectIdAsync(project, log, repoRoot, ct);
        var manualSteps = "";
        if (projectId is not null)
        {
            log.LogInformation("Updating existing Railway project '{Project}' ({Id})", project, projectId);
            var scope = new[] { "-p", projectId, "-s", project, "-e", env };
            await SetVariablesAsync(vars, secretKeys, scope);
            await Railway(["up", "-c", .. scope], "railway up failed");
        }
        else
        {
            log.LogInformation("Creating new Railway project '{Project}'", project);
            await Railway(["init", "-n", project], "railway init failed");
            var addArgs = new List<string> { "add", "--service", project };
            foreach (var (k, v) in vars)
                if (!secretKeys.Contains(k))
                    addArgs.AddRange(["--variables", $"{k}={v}"]);
            await Railway(addArgs, "railway add failed");
            await SetVariablesAsync(
                vars.Where(kv => secretKeys.Contains(kv.Key)).ToDictionary(),
                secretKeys,
                ["-s", project, "-e", env]
            );
            await Railway(["domain", "--service", project], "railway domain failed");
            await Railway(["up", "-c", "--service", project], "railway up failed");
            if (target.Kind == RailwayTargetKind.Lobby)
                manualSteps = " " + RailwayTarget.LobbyFirstDeploySteps;
        }

        var verify =
            target.Kind == RailwayTargetKind.Lobby
                ? $"Verify: railway domain -s {project}; curl https://<domain>/health/orleans -> orleans:ok."
                : "It should appear as DIRECT in the lobby within ~1 min: curl https://<lobby-domain>/servers.";
        return $"Deployed the {target.DisplayName} to Railway project '{project}' ({env}). {verify}{manualSteps}";

        async Task SetVariablesAsync(
            IReadOnlyDictionary<string, string> values,
            ISet<string> secrets,
            IReadOnlyList<string> scope
        )
        {
            var plain = values.Where(kv => !secrets.Contains(kv.Key)).Select(kv => $"{kv.Key}={kv.Value}").ToList();
            if (plain.Count > 0)
                await Railway(["variable", "set", .. plain, .. scope, "--skip-deploys"], "railway variable set failed");
            foreach (var (k, v) in values.Where(kv => secrets.Contains(kv.Key)))
                await Railway(
                    ["variable", "set", k, "--stdin", .. scope, "--skip-deploys"],
                    $"railway variable set {k} failed",
                    stdin: v
                );
        }

        async Task Railway(IReadOnlyList<string> args, string failure, string? stdin = null)
        {
            var result = await ProcessRunner.RunAsync(new ProcessSpec("railway", args, repoRoot, StdinText: stdin), log, ct);
            if (result.ExitCode != 0)
                throw new DistributedApplicationException(
                    $"{failure} (exit {result.ExitCode}):{Environment.NewLine}{result.Tail}"
                );
        }
    }

    // `railway list --json` shape varies by CLI version; walk the whole document for {name == project, id}.
    static async Task<string?> FindProjectIdAsync(string project, ILogger log, string repoRoot, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("railway")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--json");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            log.LogWarning(
                "railway list --json exited {Code}; assuming project '{Project}' does not exist yet",
                proc.ExitCode,
                project
            );
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return Find(doc.RootElement);
        }
        catch (JsonException e)
        {
            log.LogWarning("Could not parse railway list output: {Message}", e.Message);
            return null;
        }

        string? Find(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (
                        el.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String
                        && n.GetString() == project
                        && el.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                    )
                        return id.GetString();
                    foreach (var prop in el.EnumerateObject())
                        if (Find(prop.Value) is { } hit)
                            return hit;
                    return null;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        if (Find(item) is { } hit)
                            return hit;
                    return null;
                default:
                    return null;
            }
        }
    }
}
