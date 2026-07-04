# Contributing

## Project layout

| Path | What it is |
|------|------------|
| `client/` | Godot 4.6.3 (C#/.NET 8) client — rendering, input, client-side prediction. Scripts live in `client/scripts/`. |
| `server/` | .NET 8 console — the authoritative 20 Hz sim (`Sim/`), the networking/lobby layer (`Net/`), and pluggable backend seams (`Backend/`). |
| `shared/` | Deterministic `FlightModel` + content `Defs` (ship/weapon/base/world). **Referenced** by both client and server so physics + content stay bit-identical — edit it once, here. |
| `tools/simbot/` | Bot swarm for load testing the server. |
| `tools/asteroid-gen/` | Generates the asteroid mesh/normal-map catalog. |
| `tests/` | `FlightModelTest` (determinism + golden), `CryptoTest` (shared-secret HMAC). |

## Architecture in one paragraph

The **server is the sole authority**: it integrates the fixed-dt simulation, validates inputs,
owns health/collision/death/win state, and hosts the lobby. The **client predicts** locally and
reconciles against the server's authoritative snapshots. The client **downloads all content from
the server** over one WebSocket (Protocol v7, `server/Net/Protocol.cs`): world statics in
`MsgWelcome`, the runtime defs in `MsgDefs`, the lobby roster in `MsgLobbyState`, and live state
in snapshots. The client keeps **no compile-time tuning fallback** — `client/scripts/DefRegistry.cs`
guards until the server's defs arrive, so prediction never runs on stale numbers.

When you change the wire format, bump `Protocol.Version` (server) and the matching
`ProtocolVersion` constant in `client/scripts/GameNetClient.cs` together; the handshake refuses a
mismatch rather than misreading frames.

## Building & running

```bash
dotnet build shared/Shared.csproj
dotnet build server/SimServer.csproj -c Release
dotnet build client/stellarallegiance.csproj
scripts/run-server.sh        # server (rebuilds + runs)
scripts/run-client.sh        # client (rebuilds + launches Godot)
```

See [QUICKSTART.md](QUICKSTART.md) for the full local loop.

## Tests

```bash
dotnet run --project tests/FlightModelTest/FlightModelTest.csproj -c Release   # must print ALL TESTS PASSED
dotnet run --project tests/CryptoTest/CryptoTest.csproj -c Release             # must print all checks passed
```

`FlightModelTest` is the determinism guard — any failure is a real regression in the shared
flight math, which would desync client prediction from server authority. Run it after any change
to `shared/FlightModel.cs`.

## Formatting

Code is formatted with [CSharpier](https://csharpier.com) (pinned in `dotnet-tools.json`):

```bash
dotnet tool restore
dotnet csharpier format .
```