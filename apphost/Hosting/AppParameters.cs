namespace StellarAllegiance.AppHost.Hosting;

// Every knob the AppHost exposes, in one place. Names are the kebab-case form of the env var each one
// feeds (so `.env` maps onto them 1:1 - see DotEnv). Defaults are the LOCAL-dev posture.
public sealed class AppParameters
{
    public required IResourceBuilder<ParameterResource> GodotPath { get; init; }
    public required IResourceBuilder<ParameterResource> ClientMode { get; init; }
    public required IResourceBuilder<ParameterResource> ClientArgs { get; init; }
    public required IResourceBuilder<ParameterResource> ClientBuildConfig { get; init; }
    public required IResourceBuilder<ParameterResource> PilotName { get; init; }

    public required IResourceBuilder<ParameterResource> SimPublicName { get; init; }
    public required IResourceBuilder<ParameterResource> SimAutostart { get; init; }
    public required IResourceBuilder<ParameterResource> SimSecret { get; init; }
    public required IResourceBuilder<ParameterResource> SimMap { get; init; }

    public required IResourceBuilder<ParameterResource> LobbyOrleansClustering { get; init; }
    public required IResourceBuilder<ParameterResource> AllowUnverifiedServers { get; init; }
    public required IResourceBuilder<ParameterResource> AuthDevLogin { get; init; }
    public required IResourceBuilder<ParameterResource> LobbyAdmins { get; init; }
    public required IResourceBuilder<ParameterResource> StunUrl { get; init; }
    public required IResourceBuilder<ParameterResource> RankedResults { get; init; }
    public required IResourceBuilder<ParameterResource> AuthGithubClientId { get; init; }
    public required IResourceBuilder<ParameterResource> AuthGithubClientSecret { get; init; }
    public required IResourceBuilder<ParameterResource> AuthGoogleClientId { get; init; }
    public required IResourceBuilder<ParameterResource> AuthGoogleClientSecret { get; init; }
    public required IResourceBuilder<ParameterResource> AuthSteamApiKey { get; init; }

    // Deploy-only: the LOCAL lobby/server always dial each other's Aspire endpoints.
    public required IResourceBuilder<ParameterResource> LobbyPublicUrl { get; init; }
    public required IResourceBuilder<ParameterResource> PublicLobby { get; init; }
    public required IResourceBuilder<ParameterResource> RailwayLobbyProject { get; init; }
    public required IResourceBuilder<ParameterResource> RailwayServerProject { get; init; }
    public required IResourceBuilder<ParameterResource> RailwayEnvironment { get; init; }

    public static AppParameters Declare(IDistributedApplicationBuilder b)
    {
        return new AppParameters
        {
            GodotPath = b.AddParameter(
                    "godot-path",
                    () =>
                        GodotLocator.Resolve(b.Configuration)
                        ?? throw new MissingParameterValueException(GodotLocator.Guidance)
                )
                .WithDescription(
                    "Godot 4 .NET (mono) EXECUTABLE (not the macOS .app folder). Auto-detected from GODOT, the "
                        + "`godot.executablePath` user secret, PATH and standard install dirs; enter it here only when "
                        + "detection fails, then restart the AppHost."
                ),
            ClientMode = Param(
                b,
                "client-mode",
                "lobby",
                "How the tracked `client` resource connects: lobby (server browser on the local lobby, default), direct (localhost:8090, only while the server is unapproved/private), autofly (self-driving harness)."
            ),
            ClientArgs = Param(
                b,
                "client-args",
                "",
                "Extra Godot args appended verbatim to the tracked `client` resource (may include `--` before UI-harness flags)."
            ),
            ClientBuildConfig = Param(
                b,
                "client-build-config",
                "Debug",
                "Client C# build configuration built before each Start (Release dlls are mirrored into the Debug load path Godot uses)."
            ),
            PilotName = Param(
                b,
                "pilot-name",
                "",
                "Default pilot name for launched clients (PILOT_NAME); empty = whatever the client remembers."
            ),

            SimPublicName = Param(
                b,
                "sim-public-name",
                DefaultServerName(),
                "3-50 char name the server lists under on the LOCAL lobby; empty = private (no registration). First start needs one device-code approval: `aspire resource lobby approve-device-code --user-code XXXX-XXXX`."
            ),
            SimAutostart = Param(
                b,
                "sim-autostart",
                "",
                "1 = skip the lobby ready-up and run a perpetual match (bots / benchmarks)."
            ),
            SimSecret = Param(
                b,
                "sim-secret",
                "",
                "Shared-secret password required from every client; empty = open server.",
                secret: true
            ),
            SimMap = Param(b, "sim-map", "", "Map name to load (SIM_MAP); empty = the server default."),

            LobbyOrleansClustering = Param(
                b,
                "lobby-orleans-clustering",
                "adonet",
                "adonet = cluster through Postgres like production; localhost = single dev silo with in-memory reminders."
            ),
            AllowUnverifiedServers = Param(
                b,
                "allow-unverified-servers",
                "true",
                "Accept listings without a Game Server credential (badged Unverified). Local default true; never pushed on deploy."
            ),
            AuthDevLogin = Param(
                b,
                "auth-dev-login",
                "true",
                "Enable /login/dev and grant_type=dev on the LOCAL lobby (what makes device-code approval one click). Never pushed on deploy."
            ),
            LobbyAdmins = Param(
                b,
                "lobby-admins",
                "",
                "Comma list of admin logins: github:<login>, google:<sub>, steam:<id>, name:<display>."
            ),
            StunUrl = Param(
                b,
                "stun-url",
                "",
                "STUN url(s) handed to clients/servers for the WebRTC fallback; empty = lobby default (Cloudflare)."
            ),
            RankedResults = Param(
                b,
                "ranked-results",
                "flagged",
                "flagged (only admin-flagged servers count) or authenticated (every Verified server counts)."
            ),
            AuthGithubClientId = Param(
                b,
                "auth-github-client-id",
                "",
                "Optional GitHub OAuth app id (callbacks only work on the deployed domain)."
            ),
            AuthGithubClientSecret = Param(
                b,
                "auth-github-client-secret",
                "",
                "Optional GitHub OAuth app secret.",
                secret: true
            ),
            AuthGoogleClientId = Param(b, "auth-google-client-id", "", "Optional Google OAuth client id."),
            AuthGoogleClientSecret = Param(
                b,
                "auth-google-client-secret",
                "",
                "Optional Google OAuth client secret.",
                secret: true
            ),
            AuthSteamApiKey = Param(b, "auth-steam-api-key", "", "Optional Steam Web API key.", secret: true),

            LobbyPublicUrl = Param(
                b,
                "lobby-public-url",
                "https://stellarlobby.wivuu.com",
                "DEPLOY ONLY: pushed as LOBBY_PUBLIC_URL (join-token issuer, passkey RP, OAuth callback base); must equal the domain servers dial."
            ),
            PublicLobby = Param(
                b,
                "public-lobby",
                "",
                "DEPLOY ONLY: pushed as PUBLIC_LOBBY to a Railway game server; empty = the compiled-in default lobby."
            ),
            RailwayLobbyProject = Param(
                b,
                "railway-lobby-project",
                "wivuu-public-lobby",
                "Railway project/service name for the public lobby (re-deploying the same name updates it)."
            ),
            RailwayServerProject = Param(
                b,
                "railway-server-project",
                "wivuu-game-server",
                "Railway project/service name for a game server; doubles as its SIM_PUBLIC_NAME."
            ),
            RailwayEnvironment = Param(b, "railway-environment", "production", "Railway environment to deploy into."),
        };
    }

    static IResourceBuilder<ParameterResource> Param(
        IDistributedApplicationBuilder b,
        string name,
        string @default,
        string description,
        bool secret = false
    ) => b.AddParameter(name, new ConstantParameterDefault(@default), secret: secret).WithDescription(description);

    static string DefaultServerName()
    {
        var host = Environment.MachineName.Trim();
        if (host.Length > 50)
            host = host[..50];
        return host.Length >= 3 ? host : "Local Dev Server";
    }
}
