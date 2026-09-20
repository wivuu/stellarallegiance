# Standalone sim server image. The server is published as a NativeAOT binary (SimServer.csproj sets
# PublishAot): one self-contained executable, so the runtime stage is runtime-deps (libc/OpenSSL/zlib,
# no .NET runtime) and the entrypoint is the binary itself.
#
# IMPORTANT: the build context MUST be the repo root, because SimServer.csproj has a
# <ProjectReference> on ../shared/Shared.csproj (the deterministic FlightModel + content defs
# shared with the Godot client), which needs ../tools/wire-gen at compile time. Build with:
#     docker build -f server/Dockerfile -t stellarallegiance-sim .
# or via the repo-root docker-compose.yml (which sets context: .).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# NativeAOT links with the platform toolchain, which the SDK image does not carry.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer caches across
# source-only changes. SimServer.csproj also ProjectReferences the in-repo Allegiance.Factions
# content library, so its .csproj must be present for restore to resolve. Shared.csproj in turn
# references the wire-format source generator (tools/wire-gen) as an ANALYZER: leave it out and
# MSBuild only warns ("Skipping project ... not found"), no codec is generated, and the server
# then fails to compile with CS0117 on every ShipRecord.Size / XMessage.MsgId.
# Central Package Management: EVERY PackageReference in this repo is versionless, so restore
# cannot resolve a single package without the root Directory.Packages.props. It must be copied
# with the .csproj files, into the restore layer, or restore fails with NU1010 on every package.
COPY global.json .
COPY Directory.Packages.props .
COPY shared/Shared.csproj shared/
COPY tools/wire-gen/WireGen.csproj tools/wire-gen/
COPY server/SimServer.csproj server/
COPY factions/src/Allegiance.Factions/Allegiance.Factions.csproj factions/src/Allegiance.Factions/
# --use-current-runtime: restore (and publish, below) for THIS image's RID - linux-x64 on Railway,
# linux-arm64 on an Apple-silicon docker - which is also what pulls the matching ILCompiler pack.
RUN dotnet restore server/SimServer.csproj --use-current-runtime

# Declared AFTER the restore layer so the (slow) restore above stays cached across different
# VERSION builds; only the publish layer below, which actually consumes it, is invalidated per
# version. Empty by default (a plain `docker build` / docker-compose dev build never sets it):
# SimServer.csproj then stamps its "0.0.0-dev" sentinel, which the server reads as "I do not know
# which release I am" (server/Update/ServerBuildInfo.cs) - a source-built image never compares itself
# to a release, and never pretends to be one.
ARG VERSION

# The sources PLUS the art GLBs the csproj links as Content (the sim's collision hulls +
# docking hardpoints). publish copies them to /app/assets, so the runtime image needs no
# separate asset copy. client/assets is also where the Godot client's canonical copy lives.
COPY shared/ shared/
COPY tools/wire-gen/ tools/wire-gen/
COPY server/ server/
COPY factions/ factions/
COPY client/assets/bases client/assets/bases
COPY client/assets/asteroids client/assets/asteroids
COPY client/assets/ships client/assets/ships
# -p:Version stamps the published binary's own version (GET /version, the AppImage's sq.version
# once scripts/package-server.ps1 packs this /app tree) — passed as a plain MSBuild property, not
# baked into the csproj, so a dev `docker build` with no VERSION still publishes cleanly. NEVER add
# -p:PublishAot here: SimServer.csproj already conditions it (default true), and an MSBuild
# property passed on the command line is a GLOBAL property that overrides every project in the
# graph, including tools/wire-gen's own analyzer build — which then fails to produce the wire
# codec and every ShipRecord.Size / XMessage.MsgId reference stops compiling.
RUN dotnet publish server/SimServer.csproj -c Release -o /app --use-current-runtime --no-restore ${VERSION:+-p:Version=$VERSION} \
    # The separate native debug-symbol file is several times the size of the binary and is not
    # needed at run time (managed stack traces come from metadata inside the binary).
    && rm -f /app/*.dbg
# Bake the convex-hull cache into the image (/app/sim-cache) so a cold container doesn't
# recompute hulls on first start. Self-heals at run time if a GLB ever changes.
RUN /app/SimServer --pregen-assets

# Export-only stage: `docker buildx build --target publish-output --output type=local,dest=<dir>`
# lifts the published tree (binary + assets + the baked sim-cache) straight onto the host, with no
# runtime image wrapped around it. scripts/package-server.ps1 uses this so Velopack always packs a
# binary published INSIDE the SDK image — one publish path for CI and a Mac dev box, and the
# glibc floor comes from the image (mcr.microsoft.com/dotnet/sdk:10.0), not whatever happens to be
# on the runner. `FROM scratch` keeps the export to exactly the copied files (no base-image layers
# to filter out of the output). Must sit BEFORE the `runtime` stage below: a target left unnamed on
# `docker build`/`docker compose build` resolves to the LAST stage, and docker-compose.yml /
# Railway must keep building the runtime image by default.
FROM scratch AS publish-output
COPY --from=build /app /

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
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
ENTRYPOINT ["/app/SimServer"]
