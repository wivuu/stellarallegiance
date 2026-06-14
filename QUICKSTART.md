# Quickstart

Get a match running locally in five steps.

### 1. Install prerequisites
- **.NET 8 SDK** — verify with `dotnet --version` (≥ 8).
- **Godot 4.6.3, Mono/.NET build** — the `godot-mono` binary on your PATH.

### 2. Clone and restore
```bash
git clone <repo-url> wivuullegiance
cd wivuullegiance
dotnet build shared/Shared.csproj      # sanity-check the toolchain
```

### 3. Start the server (terminal 1)
```bash
scripts/run-server.sh
```
It rebuilds, then listens on `ws://localhost:8090/game`. You should see
`[SimServer] ws://localhost:8090/game … 20 Hz`.

### 4. Launch the client (terminal 2)
```bash
scripts/run-client.sh
```
The first screen prompts for a **server address**. Enter `localhost:8090` and Connect.
(Or skip it: `scripts/run-client.sh --host localhost:8090`.)

### 5. Play
Pick **BLUE** or **RED**, click **Ready**, and the match starts once everyone in the lobby is
ready. Fly with `W/S` throttle, mouse aim, `Shift` afterburner, click/`Space` to fire. AI drones
fill out the opposition.

---

### Solo / unattended
Skip the ready-up gate and start a perpetual match immediately:
```bash
scripts/run-server.sh --autostart
```

### Two machines
Run the server on one box, then on the other launch the client and enter that box's
`hostname-or-ip:8090` on the address screen (or pass `--host`). For untrusted networks, start
the server with `--secret <password>` and set `SIM_SECRET=<password>` in the client's
environment.

### Load test
```bash
scripts/run-server.sh --autostart
dotnet run --project tools/simbot/SimBot.csproj -c Release -- --bots 100 --seconds 30
```

### Trouble?
- **"Server offline" on the client** — confirm the server terminal shows the `20 Hz` line and
  the address/port match. Retry returns you to the address screen.
- **`godot-mono: command not found`** — install the Mono/.NET build of Godot 4.6.3 and put it on
  your PATH (or run the client from the Godot editor by opening `client/`).
- **Protocol mismatch warning** — the client and server were built from different revisions;
  rebuild both (`run-server.sh` and `run-client.sh` rebuild on launch).
