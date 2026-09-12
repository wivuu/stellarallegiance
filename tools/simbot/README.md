# simbot

Headless bot swarm for load-testing the sim server. Each bot connects over WebSocket and does
Hello -> ready-up -> Spawn (alternating Scout/Fighter), then sends 20 Hz input frames **every**
tick — deliberately worst-case ingest, since real clients only send on change.

```bash
dotnet run --project tools/simbot -- --bots 50 --url ws://localhost:8090/game --seconds 60 [--orbit]
```

`--orbit` keeps the swarm packed into one sector (tiny thrust + steady turn) for sustained
worst-case AOI overlap; without it the bots thrust flat out and disperse, so load decays as the
fight spreads. Run the server with `--autostart` so the match goes live as the bots ready up.
Output is a console report covering both directions of the pipe: received snapshot bytes/rate and
the freshest server tick seen.

Frames come from the shared wire definitions (`shared/Net/Messages.cs`, generated codecs), so a
wire change is a rebuild here, never a hand edit.
