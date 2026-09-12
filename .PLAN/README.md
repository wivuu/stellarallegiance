# .PLAN — Stellar Allegiance

---

## QUICKNOTES:
- Look for opportunities to utilize native vector3 and SIMD for performance improvements
- Use lobby favicon as client app icon (instead of godot icon)
- Allow assigning lobby admin via UI, store roles in db (existing aspnet identity)
---

## Content philosophy (the through-line)

**All content and mechanics tuning is server-authored data, downloaded by the client — never
baked into the client.** A game server defines what exists (ships, weapons, bases, factions, tech,
costs, mechanics knobs); clients receive it and render/predict from it. This is already half-true:
the def set streams server→client over `MsgDefs` and the client keeps **no** compile-time tuning
fallback. The roadmap closes the rest of the gap so an operator can **override content per server,
and eventually add an entire faction, with no client patch**:

---

### Phase 1 — Configurability & maintainability refactor — ✅ DONE

Tuning and content are data, not code, so new ships, weapons, and bases are config.

- ☐ N/A

### Stage 0 — Data-driven cleanup — ✅ DONE (2026-06-27)

Finishes what Phase 1 started: removes the *remaining* hardcoding and lays the weapon seam, so
everything downstream is authored as data + a behavior module rather than rebuilt later.

- ☐ **[S]** *Deferred:* lift sim-only tuning constants (`LaunchSpeed`, `DockRadiusFrac`, pod-eject params)
  into the tuning config *when they need runtime tuning* (folds into Stage 1's YAML).

### Stage 1 — Content pipeline (YAML authoring + per-server override) — ✅ DONE (2026-06-28)

Make all content editable data the server loads, not C# — the substrate for every later def
(weapons, costs, factions, tech, mechanics knobs). Reuses the existing def→`MsgDefs`→client path
(no client change); adds only a server-side loader (`YamlDotNet`).

- ☐ N/A

### Stage 2 — Thin strategy spine — ✅ DONE (2026-06-28)

Cheap foundations that unblock economy, buying, and gating. Costs/unlocks are authored in the
Stage-1 YAML. Build minimally; enrich in Stage 4.

- ☐ N/A

### Stage 3 — Combat feel & depth

Richer dogfighting on shipped systems. Content authored after Stage 2 is **priced + gated by
construction** (and YAML-defined per Stage 1) — missiles land into the Stage-0 seam and the
Stage-2 economy, no rework.

- ☐ Turrets: Allow players to mount turret endpoints while a ship that supports turret hardpoints is in-base ('load up' the turrets)
  - Once launched, the player will 'ride along' with the pilot, able to control a gun from the turret's hardpoint, aiming it and firing it
- ☐ Ripcord: Allows specific types of ships (with the ability) to jump to a specific location in a sector after a brief, configurable, delay
  - When player picks the sector to teleport to, the ship will pick a ripcordable device (either a probe or a teleport base) in that sector
  - The player yields control of the ship, and is exposed/vulnerable to damage for a length of time that is based on yaml config + configurable target multiplier (i.e. probes take longer to ripcord to, potentially)
  - As the ship is preparing to ripcord, it should display a visual countdown, and very slowly list/spin in a circle until the countdown reaches zero, then the ship is instantly transported.
  - If the player interrupts the autopilot-ripcord (thrust, fire, steer etc), control is yielded back and the countdown cancels
  - Show a visual flash as the user leaves and enters the sector via teleport/ripcord
- ☐ **[M]** **Mini-preview of target** — Display information about the currently selected target in a compact HUD element, including health, type, and distance. This allows players to quickly assess threats and opportunities without opening the full F3 map. We should also show it in the F3 map.
- ☐ Add different gun effects
  - i.e. minigun green, different looking bolt
  - ER nanite shoots glowing blue, thin, toruses
- ✅ **[L]** **Ship salvage & pickups** (2026-09-12) — the Allegiance treasure loop, protocol 39. A dying
  combat hull rolls `salvage.drop-chance` **per item** — each mounted gun, the missile magazine (dropped as
  ONE bare-missile item whose count is the remaining rounds, never the rack), each stowed stack and each
  cargo kind — and PIG wrecks drop too (`drop-from-drones`). Survivors are server-authoritative items
  (`server/Sim/Simulation.Salvage.cs`): they fly out on a random vector off the wreck's velocity, drag to
  rest, bounce off asteroids / bases / constructor build shells / ships that can't carry them, stay in
  their sector, never dock, and expire on a per-sector cap + `lifetime-seconds`. Any player combat hull on
  **either team** collects by touch (no tech gate): a gun needs an empty type-compatible mount plus payload,
  loose rounds join the magazine of the SAME rack or else **stow** as inert cargo (HOLD row, re-drops on
  death), cargo packs mirror the dispenser seeding. Streams per anchor sector (`MsgSalvage=30`, reconciled
  by omission) with a reliable `MsgSalvageGone=31` carrying the collector so the pickup FX/banner has an
  authority; fog is plain point visibility. Items render as the real IGC part meshes (`client/assets/parts/`),
  with a crate HUD glyph + labels, a `SALVAGED …` banner, the `pickup_part` cue, and a `--salvage-test`
  harness. Salvage is **not kept across a dock** (the hangar re-equips from `LoadoutState`). Tuning:
  `salvage:` in `world.yaml`; suite: `tests/SalvageTest`.
  - Follow-ups: hangar fold of salvaged guns into `LoadoutState` (keep-on-dock); partial stack pickup;
    magazine cap + auto-reload from stowed same-rack stacks; PIG pickup; shootable items; salvage economy
    (sell stowed rounds at dock); a `missile-pickup-match: rack|line` knob; promote the stow-only
    authored-id derivation in `Protocol.BuildShipLoadouts` to a `Simulation` seam.

### Stage 4 — Strategy depth (Allegiance core)

The economic + RTS loop. Largely sequential; each item builds on Stage 2's money + gating and the
Stage-1 YAML pipeline.

- ☐ **[L]** **Update plan to include multiple teams**; each map only supports a certain number of teams, so this is a constraint that must be reflected in the plan. Plan should include a richer 'game lobby' (as opposed to server lobby) experience; allowing users to select or join teams before the match starts. First person on a perspective team (and not on NOAT/not on a team) can configure the number of teams (2-6 for now).
- ☐ **[L]** **Factions** — distinct factions with unique ship classes, tech trees, and visual styles for
  asymmetric play (a faction dimension on YAML defs). *Faction rules ride Stage 1; faction assets
  ride asset streaming above.*

### Stage 5 — Social & persistence (independent track) — ◐ accounts + ranking DONE (2026-09-07)

Orthogonal to the strategy loop, which runs on ephemeral per-match state. **Slice 1 is merged to
`master`** (PR #73, `9077060`) **and deployed at <https://stellarlobby.wivuu.com>.** The lobby is no longer a
stateless directory: it is the identity issuer and the system of record — ASP.NET Core Identity +
EF Core on Postgres with a co-hosted **Orleans** silo, where each entity grain is the single writer
of its rows and endpoints never write through EF directly.

> Design, work packages and the progress log: [`LobbyRankingService.md`](LobbyRankingService.md)
> (§1.6 slices, §8 what was built and what bit) · operator reference:
> [`public-lobby/README.md`](../public-lobby/README.md) · what shipped and how to run/deploy it:
> [`docs/LOBBY-ACCOUNTS-AND-RANKING.md`](../docs/LOBBY-ACCOUNTS-AND-RANKING.md) · language:
> `public-lobby/CONTEXT.md` · decisions: ADR-0001/0002 in `docs/adr/` · day-to-day recipes: the
> `/public-lobby` skill.

- ✅ **[L]** **Client authentication** — passkeys (WebAuthn) plus Google/GitHub/Steam external
  logins; the Godot client signs in through a device code it shows at launch and the browser approval
  page (`AuthSession`, `SignInDialog`, in-client `AccountDialog`), or skips it for direct-by-address
  joins. `ALLOW_PASSKEY_SIGNUP` can force every account to start from an external provider.
- ✅ **[M]** **Game-server authentication** — a published server prints a device code + approval
  URL on first boot, stays unlisted until approved, saves its credential to `SIM_AUTH_FILE`, and
  re-lists silently on every restart under its operator's name (`SIM_HOSTED_BY` is gone). Listings are
  badged **Verified** / **Unverified**; joining a Verified server needs a single-use 60 s **join token**
  (ES256 JWT, verified offline by the server against the lobby's JWKS) carried on `Hello` — protocol 38.
- ✅ **[M]** **Scores, kills/deaths & ranks** — servers report results through a disk spool that
  survives lobby outages and restarts; `MatchGrain` ingests them with a plausibility rule (a result is
  refused whole if any pilot never took a join token for that server), and `PlayerGrain` keeps the
  durable W/L/K/D/EJ + points counters. Ranked is gated by `RANKED_RESULTS` (`flagged` default) plus a
  per-server Ranked toggle. Web surfaces: `/ladder`, `/players/<name>`, `/servers/<id>/history`, `/me`.
- ◐ **[XL]** **Matchmaking, accounts & persistence** — accounts, persistence and match history are done
  (Postgres + Orleans, migrations run via `--migrate`, DataProtection keys persisted so cookies and
  passkey state survive redeploys). **Not done:** matchmaking, and a real rating — the ladder is
  cumulative points, not ELO/Glicko.

Also shipped alongside: a public web shell on the lobby (`/`, `/login`, `/device`, `/me`, `/ladder`,
`/players`, htmx live server strip over an anonymous `/servers/live` SSE stream, release downloads,
responsive layout) and an `/admin` moderation console (searchable servers/players/matches tabs +
detail pages, bans, player and game-server deletion, operator reassignment). Suites:
`tests/PublicLobbyTest` (Testcontainers Postgres, needs Docker) and an expanded `tests/LobbyTest`;
no CI runs either.

**Open before players use it** (user-owned): Google OAuth app + Steam Web API key; one real browser
passkey click-through; pair + Ranked-flag the dedicated server.

- ☐ **[L]** **Slice 2** (plan §1.6) — Glicko-2 team rating once real match data exists; Steam
  session tickets when there is an AppID.
- ☐ **[L]** **Slice 3** (plan §1.6) — listings + WebRTC signaling into grains so the lobby can run
  more than one replica (both are per-process today; a 2-replica scale test passed only on IPv6
  advertising and was scaled back to 1), Apple login, loadout persistence (needs a per-server
  content fingerprint — separate design).

### Cross-cutting / opportunistic

Not stage-bound — done when convenient or when a stage needs them.

- ☐ **[S]** **Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar mapping;
  explore baking and in-engine parallax/height maps.
- ✅ **[S]** **Local dev orchestration (Aspire)** — `aspire run` boots postgres → `lobby-migrate` →
  lobby:8091 → server:8090 with the Godot `client` as explicit-start; every key in the root `.env` is a
  parameter. Dashboard/CLI commands cover `client launch`, `lobby approve-device-code` and
  `deploy-railway` (`aspire do deploy-lobby|deploy-server`), replacing the deploy pwsh scripts;
  `scripts/run-server.ps1` / `run-client.ps1` now target the *hosted* lobby.
- ◐ **[S]** **Spatial audio polish** — `SfxManager` exists; ✅ collision thuds (asteroids AND bases,
  client-side interception in `WorldRenderer.CheckCollisions` against the shared convex hulls, with
  the own-base dock-disc carve-out) and ✅ a volume settings UI (per-bus sliders in the Lobby
  overlay, persisted via `UserPrefs`) shipped. Remaining: finer mix tuning / more event coverage.
  - Use audio-index.md for reference

## Deep backlog

- ☐ **[XL]** **Runtime asset streaming (client-patchless content)** — the client downloads meshes/textures/
  audio it lacks from the game server into a temp cache, so a server can define an entire faction
  (or new ship/weapon) that clients render **without installing a patch**. Defs already stream
  (`MsgDefs`); this extends the same model to binary assets (transfer + cache + load-from-temp +
  validation/eviction). A substantial sub-project — the enabler for fully server-authored factions.
  - **On-join loading gate.** Asset transfer is an explicit **blocking phase behind a loading
    screen**, completed *before* the 20 Hz state stream starts — so bulk bytes never compete with
    realtime gameplay on the single reliable-ordered channel (WS or WebRTC alike), and no second
    data channel / CDN is required. A bad or missing asset fails at the loading screen with a clear
    error (the client has no compile-time fallback), never mid-match.
  - **Content-hash manifest + resumable cache.** Server is authoritative over a hashed manifest
    (`assetId → {sha256, size, optional httpUrl}`) streamed over the existing def path; the temp
    cache is keyed by content hash so a rejoining client re-pulls only what changed. The optional
    per-asset `httpUrl` lets a high-scale operator offload fanout to a bucket/CDN without making one
    a requirement.
  - **Client ships a seed cache (not a baked-in fallback).** The client bundles the stock-faction
    assets at install, pre-populating the hash-keyed cache so a vanilla first-join downloads ~nothing.
    This is *not* the forbidden "baked-in" pattern: bundled assets are only ever used when the
    server manifest names their exact `sha256` — a different/updated server asset has a different
    hash and streams normally. So the server stays authoritative over content (binary-asset analog
    of the no-baked-tuning rule: defs are authority data with no fallback; assets are content-
    addressed blobs validated against server-named hashes, safe to pre-ship).

- ☐ **[S]** **Cleanup follow-ups (from the 2026-09-09 pass)** — design-token migration second group
  (`TechDetailPanel`, `RosterCells`, `CommandSidebar`, `DataFeedback`; ~130 raw literals remain client-wide,
  mostly 3D VFX which may stay); `Simulation.Docking.cs` partial for the ~800-line `DockApproach` block;
  ClientHub receive-side/snapshot-AOI extraction (needs an interface design, not a pure move);
  `ApDockCreepFacingDot`/`ApDockAlignTimeout`/`ApDockCreepTimeout` still compile-time (seconds-authored keys
  if they ever need tuning).

- ☐ **[M]** **Explicit host transfer + runtime arena rebuild** — the game server's host is implicit:
  `ClientHub` seeds `_hostId` to the first pilot to connect (`ReceiveLoop`, the `MsgHello` case)
  and, when the host drops, hands it to the lowest remaining client id (`HandleConnection`'s
  disconnect path). A host cannot pass the role deliberately and nobody can pick one, so map
  control just follows join order. Wanted: an explicit host-selection/transfer frame plus the lobby
  UI for it. Blocking the same feature: `MsgSetMap` (`ReceiveLoop`) only advertises the pick as the
  "next" map, because the live `World` is built once at boot — making a mid-lobby map change take
  effect needs an arena-rebuild seam (regen the `World`, re-`Welcome` every client). Lifted from
  four `// TODO`s in `server/Net/ClientHub.cs`.
- ☐ **[S]** **simbot speaks proto 7** — `tools/simbot` hand-builds its frames instead of
  referencing `shared/Net/Wire.cs`, and its header still says "Protocol v7". Port it to `Wire` so a
  protocol bump can't silently rot the load harness, or delete it.
- ☐ **[M]** **Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
- ☐ **[L]** **Replay system** — tick log or time-travel query playback.
- ☐ **[M]** **Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
- ☐ **[M]** **Mutinees** — A player can stage a mutiny on a team, all other players (except commander) can vote to depose the commander; if the vote passes, the mutineer becomes the new commander.