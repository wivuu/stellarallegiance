# Contributing

## Project layout

| Path | What it is |
|------|------------|
| `client/` | Godot 4.7 (C#/.NET 10) client — rendering, input, client-side prediction. Scripts live in `client/scripts/`. |
| `server/` | .NET 10 console — the authoritative 20 Hz sim (`Sim/`), the networking/lobby layer (`Net/`), and pluggable backend seams (`Backend/`). |
| `shared/` | Deterministic `FlightModel` + content `Defs` (ship/weapon/base/world). **Referenced** by both client and server so physics + content stay bit-identical — edit it once, here. |
| `tools/simbot/` | Bot swarm for load testing the server. |
| `tools/asteroid-gen/` | Generates the asteroid mesh/normal-map catalog. |
| `tests/` | 24 suites — `FlightModelTest` (determinism + golden), `CryptoTest` (shared-secret HMAC), plus `ShieldTest`, `FogTest`, `MissileTest`, `MineTest`, `MiningTest`, `CommanderTest`, `ConstructorTest`, `FuelPodTest`, `LoadoutTest`, and more (one `.csproj` per suite). |

## Architecture in one paragraph

The **server is the sole authority**: it integrates the fixed-dt simulation, validates inputs,
owns health/collision/death/win state, and hosts the lobby. The **client predicts** locally and
reconciles against the server's authoritative snapshots. The client **downloads all content from
the server** over one WebSocket (wire protocol single-sourced in `shared/Net/Wire.cs`, aliased by `server/Net/Protocol.cs`): world statics in
`MsgWelcome`, the runtime defs in `MsgDefs`, the lobby roster in `MsgLobbyState`, and live state
in snapshots. The client keeps **no compile-time tuning fallback** — `client/scripts/DefRegistry.cs`
guards until the server's defs arrive, so prediction never runs on stale numbers.

When you change the wire format, bump `ProtocolVersion` in `shared/Net/Wire.cs` — the single
source; the server's `Protocol.Version` aliases it.

## Building & running

```bash
dotnet build shared/Shared.csproj
dotnet build server/SimServer.csproj -c Release
dotnet build client/stellarallegiance.csproj
aspire run             # whole local stack (Postgres, lobby, sim server) + dashboard
```

Start a Godot client from the dashboard (**Start** on the `client` resource), or
`aspire resource client start` from a terminal. To run against the hosted public lobby instead,
`scripts/run-server.ps1` / `scripts/run-client.ps1` (see `scripts/README.md`). See [QUICKSTART.md](QUICKSTART.md) for the full
local loop, including the Aspire CLI/Docker prerequisites.

## Tests

Everything at once, with a pass/fail summary table (local only — this repo has **no CI**):

```bash
scripts/run-tests.ps1                     # every suite except PublicLobbyTest
scripts/run-tests.ps1 -Filter Collision   # just the suites whose name matches
scripts/run-tests.ps1 -IncludeDocker      # also PublicLobbyTest (needs Docker)
```

Some suites are known-red at the time of writing, so read the table rather than the exit code
alone. The two load-bearing smoke tests, individually:

```bash
dotnet run --project tests/FlightModelTest/FlightModelTest.csproj -c Release   # must print ALL TESTS PASSED
dotnet run --project tests/CryptoTest/CryptoTest.csproj -c Release             # must print all checks passed
```

`tests/` holds 24 suites in total (one `.csproj` each — `ShieldTest`, `FogTest`, `MissileTest`, `MineTest`, `MiningTest`,
`CommanderTest`, `ConstructorTest`, `FuelPodTest`, `LoadoutTest`, and more); run the suite(s)
covering whatever you touched with the same `dotnet run --project tests/<Suite>/<Suite>.csproj`
pattern.

`FlightModelTest` is the determinism guard — any failure is a real regression in the shared
flight math, which would desync client prediction from server authority. Run it after any change
to `shared/FlightModel.cs`.

## Formatting

Code is formatted with [CSharpier](https://csharpier.com) (pinned at 1.2.6 in
`dotnet-tools.json`; it formats `.cs` **and** `.csproj`). **Format only the files you touched** —
pass them as paths:

```bash
dotnet tool restore
dotnet csharpier format server/Net/ClientHub.cs shared/Net/Wire.cs   # the paths you changed
dotnet csharpier check .                                             # must print nothing dirty
```

The tree was blanket-formatted once, on 2026-09-09, so `dotnet csharpier check .` is clean at HEAD
and stays that way as long as everyone formats what they touch. Do **not** repeat a blanket
`dotnet csharpier format .` inside feature work: it buries the real change under hundreds of
unrelated reformats and makes the diff unreviewable. EF Core migrations (`**/Migrations/*.cs`) are
excluded in `.csharpierignore`.