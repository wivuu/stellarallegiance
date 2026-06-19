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

For a throwaway local server with bots, run it directly: `scripts/run-server.sh --local --autostart`.

## Public lobby & NAT traversal (optional)

Players can always join a server **directly** by `ip:port` (a plain WebSocket; needs a LAN or a
public/port-forwarded host). To make servers **discoverable**, also deploy **`public-lobby`** — a
tiny registry + WebRTC signaling relay (`docker compose up public-lobby`). Servers that set
`SIM_PUBLIC_NAME` register there; clients browse the list.

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

**1. Public-lobby project**

```bash
railway init -n wivuu-public-lobby                      # create + link the project
railway variables --set RAILWAY_DOCKERFILE_PATH=public-lobby/Dockerfile
railway up -c                                           # build + deploy from the repo root
railway domain                                          # generate its public domain
```

Optional env: `STUN_URL` (WebRTC fallback for *non*-Railway NAT'd servers; the public default is
fine). Note the generated domain, e.g. `wivuu-public-lobby-production.up.railway.app`.

**2. Game-server project** — a *separate* Railway project from the same repo:

```bash
railway init -n wivuu-sim-server
railway variables --set RAILWAY_DOCKERFILE_PATH=server/Dockerfile \
  --set SIM_PUBLIC_NAME="My Server" \
  --set PUBLIC_LOBBY=wivuu-public-lobby-production.up.railway.app            # the domain from step 1
  # --set SIM_SECRET=...                                # optional (see Auth)
railway up -c
railway domain
```

`SIM_PUBLIC_ENDPOINT` auto-derives from Railway's `RAILWAY_PUBLIC_DOMAIN` to
`https://<server-domain>`, so the lobby probes it over HTTPS and advertises `wss://<server-domain>` —
clients one-click-join directly, no extra config. On Railway a server is **always direct** (joined
over `wss://<domain>/game`): there's no UDP edge for WebRTC hole-punching, so the WebRTC/STUN
fallback only applies to home/self-hosted NAT'd servers. Verify with
`curl https://<lobby-domain>/servers` — the entry's `publicEndpoint` should be `wss://<server-domain>`.

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
dotnet publish server/SimServer.csproj -c Release -o /opt/wivuullegiance/sim
```

```ini
# /etc/systemd/system/sim-server.service
[Unit]
Description=wivuullegiance sim server
After=network.target

[Service]
WorkingDirectory=/opt/wivuullegiance/sim
ExecStart=/usr/bin/dotnet /opt/wivuullegiance/sim/SimServer.dll --port 8090
Environment=SIM_SECRET=change-me
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
systemctl daemon-reload && systemctl enable --now sim-server
```
