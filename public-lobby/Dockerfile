# Public lobby + WebRTC signaling box + identity issuer. References public-lobby-data/ (EF model)
# and shared/ (wire contracts), so it is built from the REPO ROOT context — which also deploys
# uniformly with the sim server and with `railway up` (which tars the git root). Build with:
#     docker build -f public-lobby/Dockerfile -t stellarallegiance-public-lobby .
# or via the repo-root docker-compose.yml (context: ., dockerfile: public-lobby/Dockerfile). On
# Railway, set the service var RAILWAY_DOCKERFILE_PATH=public-lobby/Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files first so the restore layer caches across source-only changes.
COPY public-lobby/PublicLobby.csproj public-lobby/
COPY public-lobby-data/PublicLobby.Data.csproj public-lobby-data/
COPY shared/Shared.csproj shared/
RUN dotnet restore public-lobby/PublicLobby.csproj

COPY public-lobby/ public-lobby/
COPY public-lobby-data/ public-lobby-data/
COPY shared/ shared/
RUN dotnet publish public-lobby/PublicLobby.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# HTTP lobby/signaling port (see SHARE_PORT/PORT in PublicLobby.cs). A PaaS (Railway) injects PORT.
EXPOSE 8091

ENTRYPOINT ["dotnet", "PublicLobby.dll"]
