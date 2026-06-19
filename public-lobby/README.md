# Hosting the public lobby (`public-lobby`)

The **public lobby** lets player-run game servers be discovered and joined. The only thing **you**
host is one small HTTP service:

- **`public-lobby`** — an HTTP service that does two jobs: a **registry** (game servers announce a
  name + port and heartbeat; clients fetch the list) and a **WebRTC signaling relay** (it forwards
  the SDP offer/answer between a joining client and a NAT'd game server). It is stateless and tiny,
  and **no game traffic ever flows through it.**

## How discovery works (direct-first)

When a game server registers, `public-lobby` **probes it back** to decide how clients should join:

1. **The probe.** The lobby does `GET http://<server-ip>:<port>/health` from its own (public)
   vantage point. The sim server answers `/health` with a known token.
2. **Reachable → direct.** The lobby advertises the server's `host:port`. Clients connect
   **straight to it over WebSocket** — nothing touches the lobby. This is the common case: most
   player-run servers have a public IP or a forwarded port.
3. **Not reachable → WebRTC.** The server is behind a NAT with no port-forward. Clients join over a
   **WebRTC DataChannel**, hole-punching with public **STUN**; only the short SDP handshake is
   relayed through the lobby.

```
        ┌──────── your hosted box ────────┐
client ─│  public-lobby :8091             │   1. probe GET /health  ─────────────────▶ game server
   │     │  (registry + SDP signaling)     │   2a. reachable -> advertise host:port
   │     └──────────────────────────────────┘
   │
   ├──── direct WebSocket (ws://host:port/game) ────────────────────────────────────▶ public server
   └──── WebRTC DataChannel (P2P, hole-punched via STUN) ───────────────────────────▶ NAT'd server
```

> **There is no TURN relay.** The lobby never carries game traffic, and we don't pay to relay it.
> The trade-off: a client behind a symmetric NAT can't reach a *NAT'd* server (hole-punching
> fails). Such clients can always join **direct** servers, so hosting on a public/forwarded port
> gives the widest reach.

---

## Ports & firewall

Open this **one** inbound port on the lobby box:

| Service | Port | Protocol | Purpose |
|---|---|---|---|
| `public-lobby` | `8091` (`SHARE_PORT`) | TCP / HTTP | registry + signaling REST API |

That's the whole lobby surface — no STUN/TURN ports, because STUN is a public service and there is
no TURN.

**Game-server hosts:** to be **directly joinable**, a server's port (default `8090`) must be
reachable from the internet — i.e. a public IP or a forwarded port. If it isn't, the server still
works via WebRTC/STUN for most clients; it just can't serve symmetric-NAT clients.

**Client machines need no inbound ports.**

---

## Quick start (Docker Compose)

`public-lobby` is defined in the repo-root [`docker-compose.yml`](../docker-compose.yml) alongside
the sim server. To run **just the lobby** on a dedicated box:

```bash
# on the lobby box, in the repo:
cp .env.example .env          # optionally set STUN_URL
docker compose up --build public-lobby
```

Verify it is up:

```bash
curl http://<lobby-host>:8091/servers      # -> []  (empty list until a server registers)
```

That `[]` means the registry is reachable. Point a game server at it (`SIM_PUBLIC_NAME` set,
`PUBLIC_LOBBY=<lobby-host>:8091`); the same call then returns its entry, with a `publicEndpoint`
if the lobby's probe found it directly reachable (else `null` → WebRTC).

---

## `public-lobby` configuration

Environment variables (see [`PublicLobby.cs`](PublicLobby.cs)):

| Var | Default | Purpose |
|---|---|---|
| `SHARE_PORT` | `8091` | HTTP listen port. |
| `STUN_URL` | `stun:stun.cloudflare.com:3478` | Public STUN handed to clients/servers for the WebRTC fallback. Comma/space-separate several for redundancy. |

A public STUN server is fine — there's nothing to host. It holds everything in memory (no
database): registry entries expire 30 s after the last heartbeat; signaling tickets expire after
60 s. Run a single instance — there is no shared state across replicas.

### The reachability probe

`public-lobby` decides a server's mode by `GET`ting `/health` on the address it registered from
(see [`ReachabilityProbe.cs`](ReachabilityProbe.cs)). Notes:

- By default it probes the **source IP** of the registration request (so a server behind a
  TLS-terminating proxy needs `X-Forwarded-For` — `public-lobby` honours it).
- A server may instead **assert an explicit address** via `SIM_PUBLIC_ENDPOINT` (host:port) — the
  address clients should actually use when its source IP isn't reachable (container NAT, reverse
  proxy). The lobby probes that directly and advertises it **only if it answers `/health` with the
  `wivuu-sim` token**, which is what keeps the probe from being usable as an SSRF scanner (it only
  ever "succeeds" against a real sim server; link-local/metadata targets are also refused).
- The sim server must serve `GET /health` returning `wivuu-sim` (it does, by default, on its game
  port).
- **Single-box compose caveat:** if the sim server and lobby run in the same Compose project, the
  source IP is the sim server's *container* IP (only reachable on the docker network). Set
  `SIM_PUBLIC_ENDPOINT=<host-address>:<port>` so the lobby probes/advertises the host's reachable
  address instead. (For a large public deployment, run `public-lobby` on its own box so player
  servers probe from their real public IP and need no override.)

### Running `public-lobby` standalone (no Compose)

```bash
# from the repo root
dotnet run --project public-lobby -c Release        # listens on :8091

# or build the image directly (self-contained context):
docker build -t wivuullegiance-public-lobby public-lobby/
docker run -p 8091:8091 -e STUN_URL=stun:stun.cloudflare.com:3478 wivuullegiance-public-lobby
```

---

## Pointing servers and clients at the lobby

Both read **`PUBLIC_LOBBY`** (`host:port`, default `192.168.1.101:8091`). A scheme prefix is
optional — `host:port` becomes `http://host:port`; pass `https://host` to use TLS (see below).

- **Game server** — set `SIM_PUBLIC_NAME` (3–50 chars; gates registration) and
  `PUBLIC_LOBBY=<lobby-host>:8091`. With `scripts/run-server.sh` this is the default (no
  `--local`); the name defaults to the hostname. Forward the game port (default `8090`) to be
  directly joinable; set `SIM_PUBLIC_PORT` if the forwarded external port differs.
- **Client** — set `PUBLIC_LOBBY=<lobby-host>:8091` (or `--lobby host:port`). `scripts/run-client.sh`
  opens the lobby browser by default; it joins direct servers over WebSocket and NAT'd ones over
  WebRTC automatically.

The repo default `192.168.1.101:8091` is a placeholder — change it (env, `.env`, or the code
default in `ConnectionManager`/`LobbyRegistrar`) to your real lobby address before sharing builds.

---

## Production hardening

- **TLS for `public-lobby`.** The REST API is plain HTTP. For internet hosting, terminate TLS at a
  reverse proxy (Caddy/nginx) in front of `:8091` and set `PUBLIC_LOBBY=https://lobby.example.com`.
  The client and game server both honour an `https://` prefix.
- **The registry is unauthenticated.** Anyone who can reach `:8091` can register a server, list
  servers, or post signaling. That is fine for the intended "open public lobby", but expose only
  `:8091`, keep it behind your proxy, and rate-limit at the proxy if abused.
- **Probe SSRF.** The probe only connects back to the registrant's own source IP, over `http` to
  the fixed `/health` path, with a short timeout, and refuses link-local targets — so it can't be
  steered at arbitrary internal hosts. The residual surface (a caller making the lobby connect to
  its own address) is acceptable for an open lobby.
- **Resource use.** `public-lobby` is negligible (stateless JSON; a few KB per join). Game traffic
  is direct (public servers) or peer-to-peer over STUN (NAT'd servers) — none of it is the lobby's.

---

## HTTP API (reference)

Registry:

| Method | Path | Body | Notes |
|---|---|---|---|
| `POST` | `/servers` | `{ name, port, publicEndpoint? }` | `400` if name not 3–50 chars. Lobby probes `port`; returns `{ sessionId, publicEndpoint, iceServers, … }` (`publicEndpoint` null = WebRTC mode). |
| `POST` | `/servers/{sessionId}/heartbeat` | — | `204`, or `404` if expired. Send every ~10 s. |
| `GET` | `/servers/{sessionId}` | — | one entry, or `404`. |
| `GET` | `/servers` | — | active server list (browser view). |
| `DELETE` | `/servers/{sessionId}` | — | graceful removal on host shutdown. |

Signaling (relays opaque SDP; long-polls so a join settles in ~one round trip):

| Method | Path | Body | Notes |
|---|---|---|---|
| `POST` | `/servers/{sessionId}/connect` | `{ sdpOffer }` | client posts its offer; returns `{ ticket }`. |
| `GET` | `/servers/{sessionId}/pending` | — | game server long-polls for offers. |
| `POST` | `/connect/{ticket}/answer` | `{ sdpAnswer }` | game server posts its answer. |
| `GET` | `/connect/{ticket}/answer` | — | client long-polls; `200` with answer, or `204` if not ready. |
