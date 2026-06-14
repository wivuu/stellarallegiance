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
# source-only changes.
COPY shared/Shared.csproj shared/
COPY server/SimServer.csproj server/
RUN dotnet restore server/SimServer.csproj

# Now the sources, and publish a framework-dependent build (the aspnet runtime image below
# supplies the shared framework).
COPY shared/ shared/
COPY server/ server/
RUN dotnet publish server/SimServer.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Binary game socket. Behind the hosting layer's TLS-terminating ingress (wss:// -> here).
EXPOSE 8090

# Config is via env (see Program.cs):
#   SIM_SECRET     optional shared-secret password (enables auth when set)
#   SIM_AUTOSTART  set to 1 to skip the lobby ready-up (bots / benchmarking)
# Seed/port are flags; override the command to change them.
ENTRYPOINT ["dotnet", "SimServer.dll", "--port", "8090"]
