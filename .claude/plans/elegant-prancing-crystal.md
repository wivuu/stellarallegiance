# Aspire AppHost for local dev + Railway deploy

## Context

Getting a match running locally today takes two terminals, PowerShell 7, hand-set env vars, a
Docker compose stack if you want the lobby with Postgres, and two Railway deploy scripts whose
one-time dashboard steps live only in script comments. The "Add aspire" commit (e2bed81) brought
in Microsoft's Aspire skills and the `aspire agent mcp` server but no AppHost.

Goal: one `aspire run` boots Postgres → lobby migrations → public lobby → sim server, with a
dashboard that can launch Godot clients, deploy lobby/server to Railway, and prompt for any
config value that is missing. The old run/deploy scripts are deleted because the Aspire CLI
covers every flow they served (`aspire run/start`, `aspire resource <res> <cmd> --<arg>`, `aspire do <step>`).

Decisions confirmed with the user:
1. Client = tracked `client` executable resource (explicit start) **plus** a `launch` custom
   command that prompts (mode / build config / extra Godot args / movie path) and spawns extra instances.
2. Delete `scripts/run-server.ps1`, `scripts/run-client.ps1`, `scripts/deploy-railway-lobby.ps1`,
   `scripts/deploy-railway-server.ps1`. Keep `scripts/export-clients.ps1` (CI) and therefore
   `scripts/godot-bin.ps1` (dot-sourced by export-clients, `tools/godot-import.ps1`,
   `tools/glb-gallery/gallery.ps1`). The AppHost gets a C# resolver that mirrors it.
3. "Prompt for config vars" = local run (AppHost parameters, dashboard "Unresolved parameters"
   dialog + save-to-user-secrets, root `.env` honored) **and** deploy (the Railway command prompts
   for project / environment / variables).
4. Local lobby defaults: `ALLOW_UNVERIFIED_SERVERS=true`, `AUTH_DEV_LOGIN=true` (parameters).

Toolchain present: Aspire CLI 13.5.3, .NET SDK 10.0.400, Railway CLI 5.49.3, Docker, pwsh 7.
Godot 4.7 mono is in `/Applications` (not on PATH). `dotnet new aspire-apphost` template is installed.

## Resource graph (`apphost/AppHost.cs`)

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
DotEnv.ApplyToParameters(builder, Path.Combine(repoRoot, ".env"));   // KEY=VALUE → Parameters:<kebab>

// parameters (see table) …

var pg = builder.AddPostgres("postgres").WithDataVolume("stellarlobby-postgres");
var db = pg.AddDatabase("postgres-database");          // → ConnectionStrings__postgres-database (exact key the lobby reads)

var migrate = builder.AddProject<Projects.PublicLobby>("lobby-migrate")
    .WithArgs("--migrate").WithReference(db).WaitFor(db);

var lobby = builder.AddProject<Projects.PublicLobby>("lobby")
    .WithHttpEndpoint(port: 8091, targetPort: 8091, name: "http", env: "PORT", isProxied: false)
    .WithHttpHealthCheck("/health/orleans")
    .WithReference(db).WaitForCompletion(migrate)
    .WithEnvironment("LOBBY_PUBLIC_URL", lobby http endpoint)   // self endpoint ref; fallback literal http://localhost:8091
    .WithEnvironment("AUTH_DEV_LOGIN", authDevLogin) … LOBBY_ADMINS, STUN_URL, RANKED_RESULTS,
       ALLOW_UNVERIFIED_SERVERS, LOBBY_ORLEANS_CLUSTERING, AUTH_GITHUB_*, AUTH_GOOGLE_*, AUTH_STEAM_API_KEY
    .WithRailwayDeploy(RailwayTarget.Lobby);

var server = builder.AddProject<Projects.SimServer>("server")
    .WithHttpEndpoint(port: 8090, targetPort: 8090, name: "http", env: "PORT", isProxied: false)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("PUBLIC_LOBBY", lobby.GetEndpoint("http"))
    .WithEnvironment("SIM_PUBLIC_NAME", simPublicName) … SIM_AUTOSTART, SIM_SECRET, SIM_MAP,
       SIM_CACHE_DIR = apphost/.local/server/sim-cache, SIM_AUTH_FILE = apphost/.local/server/lobby-auth.json
    .WaitFor(lobby)
    .WithRailwayDeploy(RailwayTarget.Server);

var client = builder.AddGodotClient("client", repoRoot, lobby, server, godotPath, clientMode, clientArgs, simSecret, pilotName);
// = AddExecutable(name, <godot exe>, repoRoot, "--path", "client") .WithExplicitStart() .WaitFor(server)
//   .WithEnvironment(PUBLIC_LOBBY, SIM_SECRET, PILOT_NAME)
//   .WithArgs(async ctx => mode/extra args read from parameters at start time)
//   .OnBeforeResourceStarted(build client C# — blocks start until done)
//   .WithCommand("launch", …)

builder.Pipeline.AddStep("deploy-lobby",  ctx => RailwayDeployer.RunAsync(RailwayTarget.Lobby,  …));
builder.Pipeline.AddStep("deploy-server", ctx => RailwayDeployer.RunAsync(RailwayTarget.Server, …));
// deliberately NOT requiredBy Deploy: `aspire do deploy-lobby` is explicit; a bare `aspire deploy` must not push both.
builder.Build().Run();
```

Why proxyless fixed ports: with no `launchSettings.json` Aspire adds no endpoints on its own
(project-resources doc), so we own them. The lobby binds `0.0.0.0:{PORT}` via `UseUrls` and the
server via `ListenAnyIP(PORT)`; a proxy would make the server advertise a random internal port to
the lobby probe and the client. Fixed 8090/8091 keep every default (`localhost:8090`, `PUBLIC_LOBBY`)
and every doc line true.

## Parameters

Convention: root `.env` `KEY=VALUE` → `Parameters:<kebab(KEY)>` via a small `DotEnv` loader
(`AddInMemoryCollection`, added last so it wins over user secrets; skipped for any key that has a
real `Parameters__<name>` env var). Parameters declare defaults with
`AddParameter(name, new ConstantParameterDefault(default), persist: false, secret)` so config wins
when present and nothing nags; a parameter with no default prompts in the dashboard (save to user
secrets). `.env` stays the one file you edit; `.env.example` documents it.

| Parameter | Secret | Default | Feeds |
|---|---|---|---|
| `godot-path` | no | `AddParameter(name, () => GodotLocator.Resolve(config) ?? throw new MissingParameterValueException(...))` → prompts only when auto-detect fails; restart after saving (executable command is fixed at model build) | client command |
| `client-mode` | no | `direct` (`direct` = `--host localhost:8090`, `lobby` = server browser, `autofly` = `--host … --autofly`) | client args |
| `client-args` | no | empty | client args (extra Godot flags, `--` passthrough allowed) |
| `pilot-name` | no | empty | client `PILOT_NAME` |
| `sim-public-name` | no | machine hostname (≤50) | server `SIM_PUBLIC_NAME` |
| `sim-autostart` | no | `.env` else `0` | server `SIM_AUTOSTART` |
| `sim-secret` | yes | `.env` else empty (open) | server + client `SIM_SECRET` |
| `sim-map` | no | empty (server default) | server `SIM_MAP` |
| `lobby-admins` | no | `.env` else empty | lobby + deploy |
| `stun-url` | no | `.env` else empty | lobby + deploy |
| `ranked-results` | no | `flagged` | lobby + deploy |
| `allow-unverified-servers` | no | `true` | lobby + deploy |
| `auth-dev-login` | no | `true` | lobby only (never pushed) |
| `lobby-orleans-clustering` | no | `adonet` (mirrors prod; `localhost` = no membership table) | lobby |
| `auth-github-client-id`, `auth-google-client-id` | no | `.env` else empty | lobby + deploy |
| `auth-github-client-secret`, `auth-google-client-secret`, `auth-steam-api-key` | yes | `.env` else empty | lobby + deploy |
| `lobby-public-url` | no | `.env` else `https://stellarlobby.wivuu.com` | **deploy only** (local lobby uses its own endpoint) |
| `public-lobby` | no | `.env` else empty (= compiled default) | **server deploy only** (local server always dials the local lobby) |
| `railway-lobby-project` / `railway-server-project` | no | `wivuu-public-lobby` / `wivuu-game-server` (server name doubles as `SIM_PUBLIC_NAME` on Railway) | deploy |
| `railway-environment` | no | `production` | deploy |
| postgres password | yes | Aspire-generated, persisted to AppHost user secrets (needs `UserSecretsId`) | postgres |

## Custom commands

**`client` → `launch`** — dashboard dialog, or CLI
`aspire resource client launch --mode autofly --config Debug --godot-args "--strafe-test -- --ui-shot=/tmp/x.png --ui-shot-delay=14"`.
- Arguments (`CommandOptions.Arguments`, `InteractionInput`): `mode` (Choice direct|lobby|autofly, default = `client-mode`),
  `config` (Choice Debug|Release), `godot-args` (Text), `write-movie` (Text, optional → `--write-movie <abs> --fixed-fps 30`,
  honors `MOVIE_FPS` / `MOVIE_RESOLUTION` like the old script), `pilot-name` (Text, optional).
- Runs `ClientBuilder.BuildAsync(config)` (Release mirrors `client/.godot/mono/temp/bin/Release/*.dll` → `bin/Debug/`,
  the Godot `--path` load-path quirk), then spawns Godot as a detached `Process` with the same env as the tracked
  resource; stdout/stderr piped into the `client` resource log via `ResourceLoggerService`; pids tracked and killed on
  `IHostApplicationLifetime.ApplicationStopping` (no orphans). `UpdateState` = always enabled; `IsHighlighted`.
- Returns `CommandResults.Success("Godot pid N …")` / `Failure(build output tail)`.

**`lobby` → `deploy-railway`** and **`server` → `deploy-railway`** (`ConfirmationMessage` set; outward-facing and billable).
- Arguments: `project`, `environment` (defaults from parameters), then the variables to push, pre-filled from
  parameters — lobby: `LOBBY_PUBLIC_URL`, `STUN_URL`, `LOBBY_ADMINS`, `RANKED_RESULTS`, `ALLOW_UNVERIFIED_SERVERS`,
  `AUTH_GITHUB_CLIENT_ID`, `AUTH_GITHUB_CLIENT_SECRET` (SecretText), `AUTH_GOOGLE_*`, `AUTH_STEAM_API_KEY` (SecretText);
  server: `PUBLIC_LOBBY`, `SIM_SECRET` (SecretText), `SIM_AUTOSTART`, `SIM_MAX_PLAYERS`.
  **Blank = leave the Railway value untouched**; non-blank = `railway variable set` (secrets via `--stdin`).
- `RailwayDeployer` (port of the deleted scripts): preflight `railway whoami`; `railway list --json` → project id by
  name; existing → `railway variable set … -p <id> -s <name> -e <env> --skip-deploys` then
  `railway up -c -p <id> -s <name> -e <env>`; new → `railway init -n`, `railway add --service … --variables …`,
  `railway domain --service …`, `railway up -c --service …`. Always sets `RAILWAY_DOCKERFILE_PATH`
  (`public-lobby/Dockerfile` | `server/Dockerfile`); server also `SIM_PUBLIC_NAME=<project>`. Working dir = repo root
  (`railway up` tars the git root honoring `.railwayignore`). Output streamed line-by-line to the resource logger.
- On a NEW lobby project the result message lists the manual one-time steps (attach Postgres →
  `ConnectionStrings__postgres-database=${{Postgres.DATABASE_URL}}`, pre-deploy `dotnet PublicLobby.dll --migrate`,
  App Sleeping OFF, custom domain = `LOBBY_PUBLIC_URL`).

**Pipeline steps** `deploy-lobby`, `deploy-server` — `aspire do deploy-lobby` from the CLI; same `RailwayDeployer`;
values from parameters (`Parameters__railway_lobby_project=…` etc.); a missing project name is asked via
`IInteractionService.PromptInputsAsync` (the only prompt allowed in CLI pipelines). Not hooked into `aspire deploy`.

## Old script → new command

| Old | New |
|---|---|
| `scripts/run-server.ps1 -Local` | `aspire run` (whole stack; server stays private when `sim-public-name` is empty) |
| `scripts/run-server.ps1 --autostart` / `--secret X` | parameters `sim-autostart` / `sim-secret` (`.env`, `Parameters__sim_autostart=1`, or dashboard) |
| `scripts/run-client.ps1 -Local` | dashboard **Start** on `client`, or `aspire resource client start` |
| `scripts/run-client.ps1 -GodotArgs @('--autofly','--','--ui-shot=…')` | `aspire resource client launch --mode autofly --godot-args "-- --ui-shot=…"` |
| `scripts/run-client.ps1 -Release` | `aspire resource client launch --config Release` |
| `scripts/run-client.ps1 -WriteMovie out.avi` | `aspire resource client launch --write-movie out.avi` |
| `scripts/deploy-railway-lobby.ps1 [name]` | dashboard **Deploy to Railway** on `lobby`, or `aspire do deploy-lobby` |
| `scripts/deploy-railway-server.ps1 [name]` | dashboard **Deploy to Railway** on `server`, or `aspire do deploy-server` |
| VS Code "Run server" / "Run client" | "Aspire: run" (`aspire run`) / "Aspire: launch client" (`aspire resource client launch`) |
| perf/benchmark server (Release) | unchanged raw form already in the verify skill: `dotnet run --project server -c Release -- --port 8090 --autostart` (Aspire builds Debug) |

## Work packages

### WP1 — AppHost project + solution wiring
- `dotnet new aspire-apphost -o apphost -n AppHost` → `apphost/AppHost.csproj` (Aspire.AppHost.Sdk + Aspire.Hosting.AppHost 13.5.3,
  add `Aspire.Hosting.PostgreSQL` 13.5.3 via `aspire add postgresql`), `UserSecretsId`, `ProjectReference` to
  `../public-lobby/PublicLobby.csproj` and `../server/SimServer.csproj` (**not** the Godot client csproj — it is a
  Godot.NET.Sdk project launched by the Godot binary; the client is an executable resource).
- `apphost/appsettings.json` with non-secret `Parameters:` defaults that are not code constants (none required initially).
- `aspire.config.json` at repo root → `apphost/AppHost.csproj`; slnx folder `/apphost/`.
- `.gitignore`: `apphost/.local/` (sim-cache + lobby-auth.json). `.railwayignore` + `.dockerignore`: `apphost`, `aspire.config.json`, `.aspire`.

### WP2 — Helpers (`apphost/*.cs`, one class per file)
- `DotEnv.cs` — parse `.env` (`#` comments, `KEY=VALUE`, optional quotes) → `Parameters:<kebab>`; empty value = set-to-empty.
- `Parameters.cs` — `ParamWithDefault(name, default, secret)` wrapper over `ConstantParameterDefault` (+ `WithDescription`).
- `GodotLocator.cs` — order: `GODOT` env → `Parameters:godot-path` config → `~/.microsoft/usersecrets/stellarallegiance/secrets.json`
  `godot.executablePath` (same store the VS Code task writes; `%APPDATA%\Microsoft\UserSecrets` on Windows) → PATH
  (`godot-mono`, `godot4`, `godot`) → standard locations (same globs as `scripts/godot-bin.ps1`).
- `ClientBuilder.cs` — `dotnet build client/stellarallegiance.csproj -c <cfg>` + Release dll mirror; output → resource logger.
- `GodotClient.cs` — `AddGodotClient` extension: `AddExecutable` + `WithExplicitStart` + `WithArgs` callback + `OnBeforeResourceStarted` build + `launch` command + spawned-process tracking.
- `RailwayDeployer.cs` + `RailwayDeploy.cs` (`WithRailwayDeploy(target)` extension + pipeline-step registration).

### WP3 — Lobby telemetry
- Add to `public-lobby/PublicLobby.csproj`: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`,
  `OpenTelemetry.Instrumentation.AspNetCore`, `.Http`, `.Runtime`; new `public-lobby/Hosting/Telemetry.cs` with
  `AddLobbyTelemetry()` (logs + metrics + traces, OTLP exporter only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set — Aspire
  injects it; prod unaffected) called from `PublicLobby.cs` next to `AddLobbyPersistence()`. Keep the lobby's own
  `/health*` routes (do NOT add a ServiceDefaults `MapDefaultEndpoints`, it would collide on `/health`).
  No Dockerfile change (packages only). SimServer: stdout capture is enough for now.

### WP4 — Script cleanup + docs/tasks/skills sweep
- Delete the four scripts; rewrite `scripts/README.md` (export + godot-bin only); `.vscode/tasks.json`: replace "Run server"/"Run client"
  with "Aspire: run" and "Aspire: launch client"; keep the Godot import/set-path/export tasks.
- Update to the new commands: `README.md`, `QUICKSTART.md`, `CONTRIBUTING.md`, `docs/DEPLOY.md`, `docs/LOBBY-ACCOUNTS-AND-RANKING.md`,
  `client/README.md`, `server/README.md`, `public-lobby/README.md`, `.env.example` (compose **and** AppHost read it),
  `.claude/skills/verify/SKILL.md` (launch flow + the pwsh `-GodotArgs` gotcha becomes the `--godot-args` string),
  `.claude/skills/public-lobby/SKILL.md` (run + deploy sections), `.claude/skills/hardpoints/SKILL.md`,
  `GLOSSARY.md` (AppHost / parameters / `.local` entry). Leave `.PLAN/` and `.claude/plans/` history alone.
- Delegation: WP4 doc sweep and WP3 package wiring are mechanical → Sonnet subagents; WP1/WP2 stay here.

## Verified mechanics (aspire docs 13.5.3)
- `AddParameter(name, Func<string> valueGetter, publishValueAsDefault, secret)` + `MissingParameterValueException` → getter-driven prompt (godot-path).
- `AddParameter(name, ParameterDefault, persist, secret)` with `ConstantParameterDefault` → config-first defaults (no nag).
- `WithHttpEndpoint(port, targetPort, name, env, isProxied)`; proxyless requires a target port; no launchSettings → no implicit endpoints.
- `WithHttpHealthCheck(path)` on project resources; `WaitFor` honors it. `WaitForCompletion(resource, exitCode=0)`.
- `OnBeforeResourceStarted(Func<T, BeforeResourceStartedEvent, CancellationToken, Task>)` blocks the start until the callback finishes.
- `WithExplicitStart()` (13.5): `WithArgs`/`WithEnvironment` callbacks run at manual start, so parameter values are read then.
- `WithCommand(name, displayName, execute, CommandOptions{Arguments, ConfirmationMessage, IconName, IsHighlighted, UpdateState})`;
  arguments double as `aspire resource <res> <cmd> --<name>` options; `ExecuteCommandContext.Arguments.GetString(...)`.
- `builder.Pipeline.AddStep(name, action, dependsOn, requiredBy)` → `aspire do <name>`; `--list-steps` previews.
- `IInteractionService.IsAvailable` false from the CLI; only `PromptInputs*` allowed in pipelines.

Open points to confirm on first run (each has a fallback):
1. Two `AddProject<Projects.PublicLobby>` resources (`lobby-migrate`, `lobby`) — fallback: `AddExecutable("lobby-migrate", "dotnet", …, "<lobby build output>/PublicLobby.dll", "--migrate")`.
2. Self endpoint reference for `LOBBY_PUBLIC_URL` — fallback: literal `http://localhost:8091`.
3. `aspire do deploy-lobby` with no compute environment — confirm with `aspire do deploy-lobby --list-steps`; the dashboard command works regardless.

## Verification
1. `dotnet build wivuullegiance.slnx`; `dotnet run --project tests/PublicLobbyTest` stays green (Docker).
2. `aspire run` (human) / `aspire start --non-interactive` (agent) → `aspire wait lobby`, `aspire wait server`;
   `curl localhost:8091/health/orleans` → `orleans:ok`; `curl localhost:8090/health` → `wivuu-sim`;
   `curl localhost:8091/servers` lists the local server (unverified allowed); dashboard shows lobby traces/logs.
3. Dashboard: no parameter nag with a filled `.env`; with `GODOT` unset and Godot moved off the standard path the
   `godot-path` prompt appears and a saved value is honored on the next run. **Start** on `client` builds then opens
   Godot connected to `localhost:8090`.
4. `aspire resource client launch --mode autofly --godot-args "-- --ui-shot=<scratch>/live.png --ui-shot-delay=14"` → PNG written, Godot exits, no orphan after `aspire stop`.
5. `aspire do deploy-lobby --list-steps`; dashboard **Deploy to Railway** against a throwaway project name first
   (creates, then re-run updates instead of duplicating); real `wivuu-public-lobby` / `wivuu-game-server` only on request.
6. Grep confirms no remaining references to the deleted scripts outside `.PLAN/` and `.claude/plans/`.
