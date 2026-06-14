# Deployment — production shape

The backend is two cooperating services (see `.PLAN/NATIVE-SIM.md`):

| Service | Role | Port |
|---|---|---|
| **SpacetimeDB** (self-hosted) | durable spine: accounts, lobby/teams, chat, defs, the static world, match results, and minting join tokens | 3000 |
| **Sim server** (`server/`) | the 20 Hz **authoritative match** simulation, in process memory; binary WebSocket snapshots | 8090 |

There is **no in-module simulation any more** — the STDB module owns only meta/lobby/defs and
hands gameplay to the sim server. With no `SimEndpoint` installed there is no gameplay (lobby
only), so a deployment must always run a sim server and point clients at it.

Maincloud cannot host the sim server, so production = **self-hosted STDB + sim server on the
same box/VPC**.

## Local / single-box (docker compose)

```bash
cp .env.example .env
# edit .env: SIM_SECRET=$(openssl rand -hex 32)   (and STDB_TOKEN for match writeback)
docker compose up --build           # boots spacetimedb (:3000) + sim-server (:8090)

# publish the module + seed the DB (uses the same self-hosted node)
scripts/publish-local.sh

# advertise the sim endpoint + install the SAME secret the sim server got from .env
spacetime call stellar-allegiance set_sim_endpoint \
  '"ws://localhost:8090/game"' "\"$(grep ^SIM_SECRET .env | cut -d= -f2)\""
```

`docker compose` requires `SIM_SECRET`, so the sim server always comes up with auth enabled.
(For a throwaway local sim with bots, `scripts/start-native-server.sh` runs it directly.)

## TLS

The sim server speaks **plain `ws://` on :8090**. TLS is terminated by the **hosting layer's
ingress / load balancer** (`wss://your-host/game` → `sim-server:8090`); the server honours
`X-Forwarded-Proto`/`-For`. Advertise the **`wss://`** URL via `set_sim_endpoint` in production.
Without TLS a valid token could be sniffed and replayed within its lifetime, so never expose
the plain port to untrusted networks.

## Auth (join tokens)

- On match start the module mints one `JoinToken` per player: an **HMAC-SHA256** over
  `(identity, team, matchEpoch, expiry)` keyed by the shared secret (`shared/JoinTokens.cs`,
  `shared/Hmac.cs` — pure-managed so the wasm module and the native server agree byte-for-byte).
- The client relays its token (RLS-restricted to its own row) in the `Hello`; the sim server
  recomputes the MAC, **constant-time compares**, checks **expiry** against its clock, and
  **pins the match epoch** from the first valid join (a previous match's token, with a different
  epoch, is refused even inside its expiry window).
- The secret is `SIM_SECRET` (≥32 random bytes) — supplied via env, never committed, never
  logged. It must equal the value passed to `set_sim_endpoint`. Rotate by setting a new secret
  in both places. **No secret ⇒ dev mode (auth disabled)** — the server logs a loud warning.

## Match-result writeback

When a base falls the sim server POSTs `report_match_result` to STDB's HTTP API
(`server/Net/ResultReporter.cs`), authorized by `STDB_TOKEN` (the DB-owner Bearer). Empty token
⇒ writeback skipped (logged); the match still ends locally.

## Multiple matches

One sim-server process runs one perpetual match (it self-resets when it empties). For concurrent
matches, run multiple sim-server instances on different ports (compose: scale/replicate with
distinct `SIM_PORT`s and `set_sim_endpoint` per match cohort; systemd: one unit per port). A
match-routing layer is future work.

## systemd (non-container) option

`deploy/sim-server.service` + `deploy/sim-server.env.example` run the published server directly:

```bash
dotnet publish server/SimServer.csproj -c Release -o /opt/wivuullegiance/sim
install -Dm640 deploy/sim-server.env.example /etc/wivuullegiance/sim.env   # edit, set SIM_SECRET
install -Dm644 deploy/sim-server.service /etc/systemd/system/sim-server.service
systemctl daemon-reload && systemctl enable --now sim-server
```
