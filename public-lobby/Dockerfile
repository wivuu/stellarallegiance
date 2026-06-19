# Public lobby + WebRTC signaling box. Self-contained (no ProjectReference), but built from the
# REPO ROOT context so it deploys uniformly with the sim server and with `railway up` (which tars
# the git root). Build with:
#     docker build -f public-lobby/Dockerfile -t wivuullegiance-public-lobby .
# or via the repo-root docker-compose.yml (context: ., dockerfile: public-lobby/Dockerfile). On
# Railway, set the service var RAILWAY_DOCKERFILE_PATH=public-lobby/Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project file first so the restore layer caches across source-only changes.
COPY public-lobby/PublicLobby.csproj .
RUN dotnet restore PublicLobby.csproj

COPY public-lobby/ .
RUN dotnet publish PublicLobby.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# HTTP lobby/signaling port (see SHARE_PORT/PORT in PublicLobby.cs). A PaaS (Railway) injects PORT.
EXPOSE 8091

ENTRYPOINT ["dotnet", "PublicLobby.dll"]
