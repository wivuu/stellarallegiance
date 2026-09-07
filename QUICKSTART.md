# Quickstart

Get a match running locally in five steps.

### 1. Install prerequisites
- **.NET 10 SDK** — verify with `dotnet --version` (≥ 10).
- **Aspire CLI** — `dotnet tool install -g Aspire.Cli` or `curl -sSL https://aspire.dev/install.sh | bash`.
- **Docker** — runs the local Postgres container for the public lobby.
- **Godot 4.7, Mono/.NET build** — auto-detected from the `GODOT` env var, the
  `godot.executablePath` user secret, PATH, or standard install locations. If none of those
  resolve, the Aspire dashboard prompts you for the path when you start the `client` resource.

### 2. Clone and restore
```bash
git clone <repo-url> stellarallegiance
cd stellarallegiance
dotnet build shared/Shared.csproj      # sanity-check the toolchain
```

### 3. Start the stack
```bash
aspire run
```
This starts a Postgres container, applies the lobby's EF Core migrations, then brings up the
public lobby (`http://localhost:8091`) and the sim server (`ws://localhost:8090/game`), and opens
the Aspire dashboard. Local defaults (`.env`, see `.env.example`) turn on the lobby's dev login and
register the server under your machine's hostname — it stays **unlisted** (direct-connect only)
until you approve its device code (see *Public lobby* below).

### 4. Launch the client
In the dashboard, click **Start** on the `client` resource — it builds the client C# fresh, then
launches Godot on the local lobby's server browser (parameter `client-mode`, default `lobby`;
`direct` skips the browser and dials `localhost:8090`, which only works while the server is
unapproved or private). Equivalently: `aspire resource client start`.

### 5. Play
Pick **BLUE** or **RED**, click **Ready**, and the match starts once everyone in the lobby is
ready. Fly with `W/S` throttle, mouse aim, `Shift` afterburner, click/`Space` to fire. AI drones
fill out the opposition.

---

### Public lobby

The local stack publishes the server to the local lobby (`http://localhost:8091`). A server only
**lists** once its device code is approved: the `server` console log prints `Code: XXXX-XXXX` on
first start — approve it with the **Approve device code** button on the `lobby` resource, or

```bash
aspire resource lobby approve-device-code --user-code XXXX-XXXX
```

The credential is saved under `apphost/.local/server/`, so later runs re-list silently.

**Approved = Verified**, and a Verified server refuses direct `host:port` joins (`join token
required`): clients must join through the lobby browser — launch one in **Lobby** mode (dashboard
**Launch client…** → Lobby, or `aspire resource client launch --mode lobby`), sign in (the client
shows a device code and opens the lobby's approval page; with dev login on, pick any display name),
and click the listing. Prefer plain direct connects while iterating on gameplay? Don't approve the
code (or delete `apphost/.local/server/lobby-auth.json` to un-pair).

To browse the hosted lobby (`https://stellarlobby.wivuu.com`) instead, launch a client with
`--godot-args "--lobby https://stellarlobby.wivuu.com"` in Lobby mode.

### Solo / unattended
Skip the ready-up gate and start a perpetual match immediately — set `SIM_AUTOSTART=1` in `.env`
(parameter `sim-autostart`) before `aspire run`, or pass it as an env override in the dashboard.

### Two machines
Run the stack (`aspire run`) on one box, then on the other launch a client and enter that box's
`hostname-or-ip:8090` on the address screen. For untrusted networks, set `SIM_SECRET` (parameter
`sim-secret`) and give the client the same value via `SIM_SECRET` in its environment.

### Load test
```bash
aspire start                        # background: same stack as `aspire run`, no dashboard attached
aspire wait server                  # block until the sim server resource is ready
dotnet run --project tools/simbot/SimBot.csproj -c Release -- --bots 100 --seconds 30
aspire stop
```

### Trouble?
- **"Server offline" on the client** — confirm the `server` resource in the dashboard is Running
  and the address/port match. Retry returns you to the address screen.
- **Godot not found / dashboard prompts for a path** — install the Mono/.NET build of Godot 4.7
  and either put it on PATH, set the `GODOT` env var, or enter the path when the dashboard asks
  (it saves to the `godot.executablePath` user secret).
- **Protocol mismatch warning** — the client and server were built from different revisions;
  restart the `server` resource and relaunch the client from the dashboard to rebuild both.
