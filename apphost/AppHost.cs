// Stellar Allegiance local-dev AppHost. `aspire run` from the repo root boots, in order:
//   postgres (container, persistent volume) -> lobby-migrate (one-shot `--migrate`) -> lobby (:8091)
//   -> server (:8090, registered on the local lobby) ; `client` (Godot) is explicit-start.
// Config: every KEY in the root .env is a parameter (see Hosting/DotEnv.cs + Hosting/AppParameters.cs);
// unresolved parameters are prompted for in the dashboard. Commands: client `launch`, lobby
// `approve-device-code`, lobby/server `deploy-railway`; CLI `aspire do deploy-lobby|deploy-server`.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StellarAllegiance.AppHost.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var repo = RepoPaths.From(builder.AppHostDirectory);

var fromDotEnv = DotEnv.Apply(builder.Configuration, repo.EnvFile);
var p = AppParameters.Declare(builder);
Directory.CreateDirectory(repo.ServerLocalDir);

// ---- Postgres: the lobby's system of record (identity, sessions, servers, matches, ladder, Orleans tables).
var postgres = builder.AddPostgres("postgres").WithDataVolume("stellarallegiance-postgres");

// The database name is the connection-string key the lobby reads: ConnectionStrings__postgres-database.
var db = postgres.AddDatabase("postgres-database", databaseName: "lobby");

// ---- Public lobby: migrate first (same `--migrate` Railway runs pre-deploy), then serve on a fixed,
// proxyless :8091 so PUBLIC_LOBBY / LOBBY_PUBLIC_URL are the stable http://localhost:8091 everywhere.
var lobbyMigrate = builder
    .AddProject<Projects.PublicLobby>("lobby-migrate", launchProfileName: null)
    .WithArgs("--migrate")
    .WithReference(db)
    .WaitFor(db)
    .WithEnvironment("LOBBY_ORLEANS_CLUSTERING", p.LobbyOrleansClustering);

var lobby = builder
    .AddProject<Projects.PublicLobby>("lobby", launchProfileName: null)
    .WithHttpEndpoint(port: 8091, targetPort: 8091, name: "http", env: "PORT", isProxied: false)
    .WithHttpHealthCheck("/health/orleans")
    .WithReference(db)
    .WaitFor(db)
    .WaitForCompletion(lobbyMigrate)
    .WithEnvironment("LOBBY_ORLEANS_CLUSTERING", p.LobbyOrleansClustering)
    .WithEnvironment("ALLOW_UNVERIFIED_SERVERS", p.AllowUnverifiedServers)
    .WithEnvironment("AUTH_DEV_LOGIN", p.AuthDevLogin)
    .WithEnvironment("LOBBY_ADMINS", p.LobbyAdmins)
    .WithEnvironment("STUN_URL", p.StunUrl)
    .WithEnvironment("RANKED_RESULTS", p.RankedResults)
    .WithEnvironment("AUTH_GITHUB_CLIENT_ID", p.AuthGithubClientId)
    .WithEnvironment("AUTH_GITHUB_CLIENT_SECRET", p.AuthGithubClientSecret)
    .WithEnvironment("AUTH_GOOGLE_CLIENT_ID", p.AuthGoogleClientId)
    .WithEnvironment("AUTH_GOOGLE_CLIENT_SECRET", p.AuthGoogleClientSecret)
    .WithEnvironment("AUTH_STEAM_API_KEY", p.AuthSteamApiKey);
var lobbyHttp = lobby.GetEndpoint("http");
lobby
    .WithEnvironment("LOBBY_PUBLIC_URL", lobbyHttp) // join-token issuer + passkey RP: must equal what servers dial
    .WithApproveDeviceCodeCommand(lobbyHttp)
    .WithRailwayDeploy(RailwayTarget.Lobby, repo, p);

// ---- Sim server: fixed proxyless :8090 (the lobby probes host:port/health and clients dial it directly).
var server = builder
    .AddProject<Projects.SimServer>("server", launchProfileName: null)
    .WithHttpEndpoint(port: 8090, targetPort: 8090, name: "http", env: "PORT", isProxied: false)
    .WithHttpHealthCheck("/health")
    .WaitFor(lobby)
    .WithEnvironment("PUBLIC_LOBBY", lobbyHttp)
    .WithEnvironment("SIM_PUBLIC_NAME", p.SimPublicName)
    .WithEnvironment("SIM_AUTOSTART", p.SimAutostart)
    .WithEnvironment("SIM_SECRET", p.SimSecret)
    .WithEnvironment("SIM_MAP", p.SimMap)
    // Hull cache + device-code credential live outside bin/ so clean/config switches never re-pair.
    .WithEnvironment("SIM_CACHE_DIR", Path.Combine(repo.ServerLocalDir, "sim-cache"))
    .WithEnvironment("SIM_AUTH_FILE", Path.Combine(repo.ServerLocalDir, "lobby-auth.json"))
    .WithRailwayDeploy(RailwayTarget.Server, repo, p);

// ---- Godot client (explicit start) + `launch` command for extra instances.
var godot = GodotLocator.Resolve(builder.Configuration);
builder.AddGodotClient("client", new GodotClientOptions(repo, godot, lobbyHttp, server.GetEndpoint("http"), p));

builder.AddRailwayPipelineSteps(repo, p);

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();
if (fromDotEnv.Count > 0)
    log.LogInformation("Parameters from .env: {Names}", string.Join(", ", fromDotEnv));
if (godot is null)
    log.LogWarning("{Guidance}", GodotLocator.Guidance);
else
    log.LogInformation("Godot: {Path}", godot);
app.Run();
