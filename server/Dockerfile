# Standalone native sim server image.
#
# IMPORTANT: the build context MUST be the repo root, because SimServer.csproj has a
# <ProjectReference> on ../shared/Shared.csproj (the deterministic FlightModel + content defs
# shared with the Godot client). Build with:
#     docker build -f server/Dockerfile -t wivuullegiance-sim .
# or via the repo-root docker-compose.yml (which sets context: .).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer caches across
# source-only changes. SimServer.csproj also ProjectReferences the in-repo Allegiance.Factions
# content library, so its .csproj must be present for restore to resolve.
COPY shared/Shared.csproj shared/
COPY server/SimServer.csproj server/
COPY factions/src/Allegiance.Factions/Allegiance.Factions.csproj factions/src/Allegiance.Factions/
RUN dotnet restore server/SimServer.csproj

# The sources PLUS the art GLBs the csproj links as Content (the sim's collision hulls +
# docking hardpoints). publish copies them to /app/assets, so the runtime image needs no
# separate asset copy. client/assets is also where the Godot client's canonical copy lives.
COPY shared/ shared/
COPY server/ server/
COPY factions/ factions/
COPY client/assets/bases client/assets/bases
COPY client/assets/asteroids client/assets/asteroids
COPY client/assets/ships client/assets/ships
RUN dotnet publish server/SimServer.csproj -c Release -o /app /p:UseAppHost=false
# Bake the convex-hull cache into the image (/app/sim-cache) so a cold container doesn't
# recompute hulls on first start. Self-heals at run time if a GLB ever changes.
RUN dotnet /app/SimServer.dll --pregen-assets

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
# Carries the published binary, /app/assets (GLBs), and /app/sim-cache (warm hull cache).
COPY --from=build /app ./

# Binary game socket. Behind the hosting layer's TLS-terminating ingress (wss:// -> here).
EXPOSE 8090

# Config is via env (see Program.cs):
#   SIM_SECRET     optional shared-secret password (enables auth when set)
#   SIM_AUTOSTART  set to 1 to skip the lobby ready-up (bots / benchmarking)
#   PORT/SIM_PORT  listen port (PORT wins; default 8090). A PaaS (Railway) injects PORT.
# Seed is a flag; append --seed/--port to override the command.
ENTRYPOINT ["dotnet", "SimServer.dll"]
