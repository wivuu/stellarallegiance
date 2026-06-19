# Public lobby + WebRTC signaling box. Self-contained (no ProjectReference), so the build
# context is just this directory:
#     docker build -t wivuullegiance-public-lobby public-lobby/
# or via the repo-root docker-compose.yml (which sets context: ./public-lobby).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project file first so the restore layer caches across source-only changes.
COPY PublicLobby.csproj .
RUN dotnet restore PublicLobby.csproj

COPY . .
RUN dotnet publish PublicLobby.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# HTTP lobby/signaling port (see SHARE_PORT in PublicLobby.cs).
EXPOSE 8091

ENTRYPOINT ["dotnet", "PublicLobby.dll"]
