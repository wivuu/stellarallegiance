# Hosting the public lobby (`server-share` + `coturn`)

The **public lobby** lets player-run game servers be discovered and joined without
port-forwarding. It is two small services that you host once, centrally:

- **`server-share`** — an HTTP service that does two jobs: a **registry** (game servers announce
  a name + session id and heartbeat; clients fetch the list) and a **WebRTC signaling relay**
  (it forwards the SDP offer/answer between a joining client and the game server). It is
  stateless and tiny.
- **`coturn`** — a standard **STUN/TURN** server. STUN lets a NAT'd game server discover its own
  public address so peers can hole-punch directly; TURN is the relay fallback for the ~10–20% of
  networks (symmetric NATs) where hole-punching fails.

```
            ┌─────────────── your hosted box (e.g. 192.168.1.101) ───────────────┐
 client ──▶ │  server-share :8091  (registry + SDP signaling, JSON only)         │
   │        │  coturn       :3478  (STUN/TURN) + relay UDP range                  │
   │        └────────────────────────────────────────────────────────────────────┘
   │                                   ▲
   └───────────────  WebRTC DataChannel (P2P, or TURN-relayed)  ──────▶ game server
```

> **No game traffic flows through `server-share`.** It only relays the short SDP handshake. Game
> packets go peer-to-peer, or through `coturn` when relaying is required. Size `coturn`
> bandwidth for the relayed case; `server-share` only ever moves a few KB of JSON per join.

---

## Ports & firewall

Open these **inbound** on the lobby box (clients *and* game servers connect to all of them):

| Service | Port | Protocol | Purpose |
|---|---|---|---|
| `server-share` | `8091` (`SHARE_PORT`) | TCP / HTTP | registry + signaling REST API |
| `coturn` STUN/TURN | `3478` (`TURN_PORT`) | **UDP and TCP** | candidate discovery + TURN control |
| `coturn` relay range | `49160–49200` (recommended; see below) | **UDP** | TURN-relayed media (one alloc = one port) |
| `coturn` TURN-over-TLS | `5349` (optional) | TCP / UDP | only if you enable `turns:` |

**Player machines need no inbound ports.** Game servers and clients only make *outbound* UDP
connections (ICE) — that is the whole point of the lobby. Do **not** port-forward `8090` on a
player's router for the WebRTC path.

coturn's default relay range is `49152–65535` (huge). Narrow it and open only that range:
add `--min-port=49160 --max-port=49200` to the coturn command (≈40 concurrent relayed
connections — raise the ceiling for more).

---

## Quick start (Docker Compose)

Both services are defined in the repo-root [`docker-compose.yml`](../docker-compose.yml) alongside
the sim server. To run **just the lobby** on a dedicated box:

```bash
# on the lobby box, in the repo:
cp .env.example .env          # set TURN_USER / TURN_PASS at minimum
docker compose up --build server-share coturn
```

Verify it is up:

```bash
curl http://<lobby-host>:8091/servers      # -> []  (empty list until a server registers)
```

That `[]` means the registry is reachable. Point a game server at it (`SIM_PUBLIC_NAME` set,
`PUBLIC_LOBBY=<lobby-host>:8091`) and the same call returns its entry.

---

## `server-share` configuration

Environment variables (see [`ServerShare.cs`](ServerShare.cs)):

| Var | Default | Purpose |
|---|---|---|
| `SHARE_PORT` | `8091` | HTTP listen port. |
| `STUN_URL` | — | STUN URL handed to clients/servers, e.g. `stun:lobby.example.com:3478`. |
| `TURN_URL` | — | TURN URL for relay fallback, e.g. `turn:lobby.example.com:3478`. |
| `TURN_USER` | — | TURN username (required if `TURN_URL` is set). |
| `TURN_PASS` | — | TURN credential (required if `TURN_URL` is set). |

`server-share` is the **single source of truth for ICE config**: it returns `STUN_URL`/`TURN_URL`
(with credentials) in the register response and server list, so neither clients nor game servers
need to know the TURN details themselves. **Set `STUN_URL`/`TURN_URL` to addresses reachable from
the public internet** (your box's public hostname/IP) — not `localhost`. Leave them empty only for
a LAN-only deployment where host candidates suffice.

It holds everything in memory (no database). Registry entries expire 30 s after the last
heartbeat; signaling tickets expire after 60 s. Run a single instance — there is no shared state
across replicas.

### Running `server-share` standalone (no Compose)

```bash
# from the repo root
dotnet run --project server-share -c Release        # listens on :8091

# or build the image directly (self-contained context):
docker build -t wivuullegiance-share server-share/
docker run -p 8091:8091 \
  -e STUN_URL=stun:lobby.example.com:3478 \
  -e TURN_URL=turn:lobby.example.com:3478 \
  -e TURN_USER=wivuu -e TURN_PASS='<strong-secret>' \
  wivuullegiance-share
```

---

## `coturn` configuration

The Compose `coturn` service runs with `network_mode: host` so TURN can hand out reachable relay
addresses and bind the relay UDP range. The essentials:

- **`--external-ip=<public-ip>`** — **required** for TURN relay to work from outside. Set it to
  the box's public IP. If the box is itself behind a NAT (1:1 mapping), use
  `--external-ip=<public-ip>/<private-ip>`. (It is commented out in `docker-compose.yml` — uncomment
  and set it.)
- **`--listening-port=3478`** — STUN/TURN control port (`TURN_PORT`).
- **`--min-port` / `--max-port`** — the relay UDP range (open it in the firewall; see above).
- **`--lt-cred-mech` + `--user=USER:PASS`** — long-term credential auth. These must match the
  `TURN_USER`/`TURN_PASS` you give `server-share`, or clients will fail TURN authentication.
- **`--realm=wivuu`** — any realm string; just keep it stable.

Example hardened command (edit the `coturn` service in `docker-compose.yml`):

```yaml
    command:
      - -n
      - --no-cli
      - --listening-port=3478
      - --min-port=49160
      - --max-port=49200
      - --realm=wivuu
      - --lt-cred-mech
      - --fingerprint
      - --user=${TURN_USER:-wivuu}:${TURN_PASS:-changeme}
      - --external-ip=YOUR_PUBLIC_IP
      - --no-tcp-relay          # optional: UDP relay only
      - --no-multicast-peers    # optional: reject relaying to multicast
```

For a LAN-only test you can skip `coturn` entirely and leave `STUN_URL`/`TURN_URL` empty —
same-subnet peers connect on host candidates.

---

## Pointing servers and clients at the lobby

Both read **`PUBLIC_LOBBY`** (`host:port`, default `192.168.1.101:8091`). A scheme prefix is
optional — `host:port` becomes `http://host:port`; pass `https://host` to use TLS (see below).

- **Game server** — set `SIM_PUBLIC_NAME` (3–50 chars; gates registration) and
  `PUBLIC_LOBBY=<lobby-host>:8091`. With `scripts/run-server.sh` this is the default (no
  `--local`); the name defaults to the hostname.
- **Client** — set `PUBLIC_LOBBY=<lobby-host>:8091` (or `--lobby host:port`). `scripts/run-client.sh`
  opens the lobby browser by default.

The repo default `192.168.1.101:8091` is a placeholder — change it (env, `.env`, or the code
default in `ConnectionManager`/`LobbyRegistrar`) to your real lobby address before sharing builds.

---

## Production hardening

- **TLS for `server-share`.** The REST API is plain HTTP. For internet hosting, terminate TLS at a
  reverse proxy (Caddy/nginx) in front of `:8091` and set `PUBLIC_LOBBY=https://lobby.example.com`.
  The client and game server both honour an `https://` prefix.
- **The registry is unauthenticated.** Anyone who can reach `:8091` can register a server, list
  servers, or post signaling. That is fine for the intended "open public lobby", but expose only
  `:8091` (and coturn), keep it behind your proxy, and rate-limit at the proxy if abused.
- **Rotate TURN credentials.** The long-term `TURN_USER`/`TURN_PASS` are shared with every client.
  Rotate them periodically; for higher security move to coturn's time-limited (REST/HMAC)
  credentials and have `server-share` mint short-lived ones (not implemented today).
- **Resource use.** `server-share` is negligible (stateless JSON). `coturn` relay bandwidth equals
  the game traffic of every *relayed* session — provision accordingly if many players sit behind
  symmetric NATs.

---

## HTTP API (reference)

Registry:

| Method | Path | Body | Notes |
|---|---|---|---|
| `POST` | `/servers` | `{ name, publicEndpoint? }` | `400` if name not 3–50 chars; returns `{ sessionId, iceServers, … }`. |
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
