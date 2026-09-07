# Deployment — production shape

The backend is a **single standalone service**: the sim server (`server/`). It runs the 20 Hz
**authoritative match** simulation in process memory *and* hosts the lobby; clients connect
directly and download all content (world, defs, lobby, snapshots) over one binary WebSocket.
There is no database to deploy.

| Service | Role | Port |
|---|---|---|
| **Sim server** (`server/`) | authoritative 20 Hz match + lobby host; binary WebSocket | 8090 |

## Local / single-box (docker compose)

```bash
cp .env.example .env
# optionally: SIM_SECRET=$(openssl rand -hex 32)   (omit for an open server)
docker compose up --build           # serves ws://localhost:8090/game
```

For a throwaway local server with bots: `aspire run` with `SIM_AUTOSTART=1` in `.env` (parameter
`sim-autostart`), or the raw `dotnet run --project server -c Release -- --port 8090 --autostart`.

## Public lobby & NAT traversal (optional)

Players can always join a server **directly** by `ip:port` (a plain WebSocket; needs a LAN or a
public/port-forwarded host). To make servers **discoverable**, also deploy **`public-lobby`** — a
tiny registry + WebRTC signaling relay (`docker compose up public-lobby`). Servers that set
`SIM_PUBLIC_NAME` register there; clients browse the list.

**First-run device-code auth.** A server only lists as **Verified** once it authenticates as a
durable Game Server: on first boot with `SIM_PUBLIC_NAME` set it prints a one-time code and an
approval URL, and stays **unlisted** until an Operator opens the URL and approves it. The resulting
credential (refresh token) is saved to `SIM_AUTH_FILE` (default beside the sim-cache dir — see
`server/Assets/SimAssets.cs`), so every later restart re-lists silently under the same Operator with
no prompt. Delete that file (or point `SIM_AUTH_FILE` elsewhere) to re-pair under a different
Operator. `ALLOW_UNVERIFIED_SERVERS=true` on the lobby accepts unauthenticated listings instead
(no Operator, badged Unverified) — see `public-lobby/README.md`'s "Listings: Verified vs Unverified".

Discovery is **direct-first** and automatic:

- On register, the lobby **probes the server's port** (`GET /health` from its public vantage). If
  reachable, it advertises a direct `host:port` and clients connect **straight to the server over
  WebSocket** — no traffic through the lobby. Forward the game port (default `8090`) for this.
- If not reachable (NAT, no port-forward), clients join over a **WebRTC DataChannel**, hole-punching
  with public **STUN**; only the SDP handshake is relayed. Set `STUN_URL` to override the public
  STUN default or list fallbacks (comma-separated) — nothing to host.

**There is no TURN relay** — the lobby never carries game traffic, so a symmetric-NAT client can't
reach a NAT'd server (it can always join direct servers). Only `public-lobby`'s port (`8091`) needs
to be open; put TLS in front of it and set `PUBLIC_LOBBY=https://lobby.example.com`. Full hosting
guide: **[public-lobby/README.md](../public-lobby/README.md)**.

## Railway (two separate projects)

Deploy the lobby and a game server as **two separate Railway projects from this same repo** — they
talk only over their public HTTPS domains (separate projects don't share private networking), so a
community can run one centralized lobby (or one per community) while members independently host
game servers. Both services honor Railway's injected `PORT`.

Both Dockerfiles build from the **repo root** (`server/Dockerfile` needs it for the `shared/`
project reference; `public-lobby/Dockerfile` matches for uniformity). `railway up` tars the git
root and won't read a subdirectory config, so each service just sets `RAILWAY_DOCKERFILE_PATH` to
point at its Dockerfile — no Root Directory setting needed.

Deploy from the Aspire dashboard's **Deploy to Railway** button on the `lobby` or `server`
resource (prompts for the project, environment, and which variables to push; blank leaves a
variable untouched), or from the CLI:

```bash
aspire do deploy-lobby     # deploy/update the public lobby (project wivuu-public-lobby)
aspire do deploy-server    # deploy/update a game server (project wivuu-game-server)
```

Both are **idempotent** — re-running against the same project UPDATES it instead of creating a
duplicate (so a server never gets advertised twice). Project names come from the AppHost
parameters `railway-lobby-project` (default `wivuu-public-lobby`), `railway-server-project`
(default `wivuu-game-server`) and `railway-environment` (default `production`) — override them in
the dashboard prompt or `.env` for a second, differently-named server.

The **lobby** deploy targets project `wivuu-public-lobby` (the domain baked into the
server/client defaults); set `STUN_URL` to override the public STUN handed to WebRTC clients.

The **game-server** deploy uses the project name as the server's public name (`SIM_PUBLIC_NAME`)
and leaves `PUBLIC_LOBBY` unset so it registers with the default hosted lobby (set `PUBLIC_LOBBY`
to point elsewhere). `SIM_PUBLIC_ENDPOINT` auto-derives from Railway's `RAILWAY_PUBLIC_DOMAIN` to
`https://<server-domain>`, so the lobby probes it over HTTPS and advertises `wss://<server-domain>` —
clients one-click-join directly. On a fresh deploy the Railway edge takes ~1 min to propagate; the
server **self-heals** (re-registers until the probe succeeds), so it settles to DIRECT on its own.

On Railway a server is **always direct** (joined over `wss://<domain>/game`): there's no UDP edge for
WebRTC hole-punching, so the WebRTC/STUN fallback only applies to home/self-hosted NAT'd servers.
Verify with `curl https://<lobby-domain>/servers` — the entry's `publicEndpoint` should be
`wss://<server-domain>` (and listed once).

### Lobby prerequisites (Postgres + env)

The lobby is stateful now (accounts, sessions, matches, ladder). Attach a **Postgres** service and
set on the lobby service: `ConnectionStrings__postgres-database` (the database URL), the pre-deploy
command `dotnet PublicLobby.dll --migrate` (applies EF Core + Orleans migrations, idempotent),
`LOBBY_PUBLIC_URL=https://<lobby-domain>` (must equal the `PUBLIC_LOBBY` servers dial — it is the
join-token issuer and the passkey relying party), `LOBBY_ADMINS` (`name:<display>`, `github:<login>`,
`google:<sub>`, `steam:<id>`), optionally `AUTH_GOOGLE_CLIENT_ID/SECRET`, `AUTH_GITHUB_CLIENT_ID/SECRET`,
`AUTH_STEAM_API_KEY`, `RANKED_RESULTS`, `ALLOW_UNVERIFIED_SERVERS`. Never set `AUTH_DEV_LOGIN` in
production. `docker-compose.yml` wires the same for a single box (`lobby-db` + one-shot
`lobby-migrate`). Health: `/health` (web) and `/health/orleans` (silo). Full reference:
`public-lobby/README.md`.

## Match results → public lobby

A server that holds a **Verified** listing reports every match to the lobby with its own access
token: `POST /matches` when a match starts and `POST /matches/{id}/result` when it ends
(win-condition, or a non-counting `reset`/`shutdown` ending). Reports are spooled to disk first
(`SIM_REPORT_SPOOL`, default `report-spool/` beside `sim-cache/`) and retried with backoff until the
lobby answers, so a lobby outage or a server crash never loses a result — leftovers are re-sent on
the next boot. A result the lobby refuses as implausible (a pilot who never took a join token for
this server) is logged and dropped. Unlisted/private servers only log results.

## TLS

The sim server speaks **plain `ws://` on :8090**. Terminate TLS at the **hosting layer's ingress
/ load balancer** (`wss://your-host/game` → `sim-server:8090`); the server honours
`X-Forwarded-Proto`/`-For`. Hand players the **`wss://`** address. Never expose the plain port to
untrusted networks without a secret (below) and TLS.

## Auth (optional shared secret)

Auth is a single optional **shared-secret password**:

- Set `SIM_SECRET` (or `--secret`) on the server. Every client's `Hello` must then carry the
  same secret; the server **constant-time compares** it (`shared/Hmac.cs`).
- The client reads `SIM_SECRET` from its own environment and sends it. Distribute the secret
  out-of-band. Rotate by changing it on the server and all clients.
- **No secret ⇒ open server** (anyone may join) — fine for LAN/dev, logged as a warning. With
  TLS + a secret, a sniffer can't join.

## Match lifecycle

One sim-server process hosts a lobby and runs back-to-back matches: it waits in the lobby until
the matchmaker starts a match (default: every connected player ready; `--autostart` skips the
gate), runs until a base falls, shows the result briefly, then returns to the lobby. It also
resets to a clean lobby whenever it empties out.

## Multiple matches

One process = one lobby/match at a time. For concurrent matches, run multiple sim-server
instances on different ports (`SIM_PORT`) behind your ingress and route players to a cohort. A
built-in match-routing layer is future work — the `IMatchmaker`/`IPlayerDirectory` seams in
`server/Backend/` are where that (or a persistent backend) would plug in.

## systemd (non-container) option

```bash
dotnet publish server/SimServer.csproj -c Release -o /opt/stellarallegiance/sim
```

```ini
# /etc/systemd/system/sim-server.service
[Unit]
Description=stellarallegiance sim server
After=network.target

[Service]
WorkingDirectory=/opt/stellarallegiance/sim
ExecStart=/usr/bin/dotnet /opt/stellarallegiance/sim/SimServer.dll --port 8090
Environment=SIM_SECRET=change-me
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
systemctl daemon-reload && systemctl enable --now sim-server
```
