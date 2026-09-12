# .PLAN — Stellar Allegiance

The live roadmap: what is shipped (one line each, with a pointer to the real docs), what is open
(by stage), and the deep backlog. Finished work is condensed here, not narrated — the code,
[`GLOSSARY.md`](../GLOSSARY.md) and the per-feature docs are the authority for how things work.

**What else lives in `.PLAN/`**

| Path | What it is |
| --- | --- |
| [`LobbyRankingService.md`](LobbyRankingService.md) | Public-lobby identity + ranking design; §1.6 slices, §8 what was built and what bit. |
| [`ship_movement/`](ship_movement/README.md) | Allegiance flight-model reference (constants, integration loop, rotation math). **Live reference, not archived.** |
| [`sfx-gaps.md`](sfx-gaps.md) | Audit of gameplay events that still play no sound (feeds the spatial-audio item below). |
| [`tech-tree-README.md`](tech-tree-README.md) + `tech-tree-*.yaml` | Flattened content dumps for handing to an LLM/human as context. |
| [`TechPathsFromMemory.md`](TechPathsFromMemory.md) | Original brief for the Iron Coalition tech port — executed (see `archive/tech-paths/`). |
| [`code-review-sweep-2026-07-18.md`](code-review-sweep-2026-07-18.md) | One-off review sweep log. Historical. |
| [`archive/`](archive/README.md) | Completed handoff notes (prototype build order, base-building, tech-paths A–D). History only. |

---

## Content philosophy (the through-line)

**All content and mechanics tuning is server-authored data, downloaded by the client — never
baked into the client.** A game server defines what exists (ships, weapons, bases, factions, tech,
costs, mechanics knobs); clients receive it and render/predict from it. Today the def set streams
server→client over `MsgDefs` and the client keeps **no** compile-time tuning fallback. The remaining
gap is binary assets: closing it (see *Runtime asset streaming* in the deep backlog) lets an operator
**override content per server, and eventually add an entire faction, with no client patch**.

---

## Shipped

Condensed outcomes. Each line names where the detail now lives.

- ✅ **Phase 1 + Stages 0–2 — data-driven content, YAML pipeline, strategy spine** (2026-06-28).
  Hulls/weapons/bases/tech/costs and the mechanics knobs are per-server YAML under
  `server/Content/core/` (`world.yaml` holds the sim tuning, including the once-deferred
  `launch-speed` / `dock-radius-frac` / `pod-eject-*`), streamed to clients over `MsgDefs`. Authoring
  guide: the `tech-tree-content` and `hulls-weapons` skills; JSON schemas in `schemas/`.
- ✅ **[L] Ship salvage & pickups + cargo hold** (2026-09-12, `Wire.ProtocolVersion` 40, branch
  `salvage` — **not merged to `master`**). Per-item drop rolls on death, server-authoritative items
  that drift/bounce/expire per sector, touch-pickup by either team into an empty compatible mount or
  the hull's `cargo-capacity` hold. Mechanics + file map: [`GLOSSARY.md` → *Salvage*](../GLOSSARY.md);
  tuning `salvage:` in `world.yaml`; suite `tests/SalvageTest`; harness `--salvage-test`.
- ✅ **Stage 5 slice 1 — accounts, Verified servers, ladder** (merged PR #73 `9077060`, deployed at
  <https://stellarlobby.wivuu.com>). Passkey + Google/GitHub/Steam sign-in with an in-client device
  code; game servers pair by device code and list as **Verified** (single-use ES256 join tokens on
  `Hello`); match results spool to the lobby and feed durable W/L/K/D/EJ/points, `/ladder`,
  `/players/<name>`, `/me`; `/admin` moderation console. Design + progress:
  [`LobbyRankingService.md`](LobbyRankingService.md) · operator reference:
  [`public-lobby/README.md`](../public-lobby/README.md) · what shipped and how to run/deploy:
  [`docs/LOBBY-ACCOUNTS-AND-RANKING.md`](../docs/LOBBY-ACCOUNTS-AND-RANKING.md) · language:
  `public-lobby/CONTEXT.md` · decisions: `docs/adr/0001`, `0002` · recipes: the `/public-lobby` skill.
- ✅ **[S] Local dev orchestration (Aspire)** — `aspire run` boots postgres → `lobby-migrate` →
  lobby:8091 → server:8090 with the Godot `client` as explicit-start; root `.env` keys are parameters;
  `aspire do deploy-lobby|deploy-server` replaces the deploy pwsh scripts. See `apphost/` and the
  `aspire` skill. `scripts/run-server.ps1` / `run-client.ps1` target the *hosted* lobby.
- ✅ **[S] Spatial audio, first pass** — `SfxManager`, collision thuds (asteroids and bases, client-side
  interception against the shared convex hulls), per-bus volume sliders persisted via `UserPrefs`.

---

## Roadmap — open work

### Stage 3 — Combat feel & depth

Richer dogfighting on shipped systems. New content is priced + gated by construction and authored in
YAML, so it lands in the existing seams without rework.

- ☐ **Turrets** — allow players to mount turret endpoints while a ship that supports turret
  hardpoints is in-base ('load up' the turrets).
  - Once launched, the player will 'ride along' with the pilot, able to control a gun from the
    turret's hardpoint, aiming it and firing it.
- ☐ **Ripcord** — allows specific types of ships (with the ability) to jump to a specific location
  in a sector after a brief, configurable, delay.
  - When the player picks the sector to teleport to, the ship will pick a ripcordable device (either
    a probe or a teleport base) in that sector.
  - The player yields control of the ship, and is exposed/vulnerable to damage for a length of time
    based on yaml config + a configurable target multiplier (i.e. probes take longer to ripcord to).
  - While preparing to ripcord, display a visual countdown and very slowly list/spin in a circle
    until the countdown reaches zero, then transport instantly.
  - If the player interrupts the autopilot-ripcord (thrust, fire, steer etc), control is yielded
    back and the countdown cancels.
  - Show a visual flash as the user leaves and enters the sector via teleport/ripcord.
- ☐ **In-flight loadout management** — equip and dequip slots, manage inventory, etc. while flying.
  - Some equipped items should have a signature modifier (e.g. shields add to the ship's signature)
    and can be dequipped.
  - Some equipped items can have special in-flight effects when activated (cloak).
  - Ship 'energy' concept; cloak uses energy.
- ☐ **[M] Mini-preview of target** — display information about the currently selected target in a
  compact HUD element (health, type, distance) so players can assess threats without opening the
  full F3 map. Show it in the F3 map as well.
- ☐ **Different gun effects** — per-weapon bolt looks (today `projectiles.yaml` only sets
  `bolt-radius` / `bolt-length`).
  - i.e. minigun green, different looking bolt
  - ER nanite shoots glowing blue, thin, toruses
- ☐ **Salvage follow-ups** — hangar fold of salvaged guns into `LoadoutState` (keep-on-dock; today
  the hangar re-equips from `LoadoutState` and salvage is lost on dock); partial stack pickup;
  magazine cap + auto-reload from stowed same-rack stacks; PIG pickup; shootable items; salvage
  economy (sell stowed rounds at dock); a `missile-pickup-match: rack|line` knob; promote the
  stow-only authored-id derivation in `Protocol.BuildShipLoadouts` to a `Simulation` seam.

### Stage 4 — Strategy depth (Allegiance core)

The economic + RTS loop. Largely sequential; each item builds on the shipped money + gating and the
YAML pipeline.

- ☐ **[L] Update plan to include multiple teams** — each map only supports a certain number of
  teams, so this is a constraint that must be reflected in the plan. Plan should include a richer
  'game lobby' (as opposed to server lobby) experience; allowing users to select or join teams before
  the match starts. First person on a perspective team (and not on NOAT/not on a team) can configure
  the number of teams (2-6 for now).
- ☐ **[L] Factions** — distinct factions with unique ship classes, tech trees, and visual styles for
  asymmetric play (a faction dimension on YAML defs). *Faction rules ride the YAML pipeline; faction
  assets ride runtime asset streaming (deep backlog).*

### Stage 5 — Social & persistence (independent track)

Slice 1 is shipped (see above). Open, in plan-§1.6 order:

- ☐ **Before players use it** (user-owned): Google OAuth app + Steam Web API key; one real browser
  passkey click-through; pair + Ranked-flag the dedicated server.
- ☐ **[S] Assign lobby admin from the UI**, storing roles in the DB (existing ASP.NET Identity).
  Today the `admin` role is granted only at sign-in from the `LOBBY_ADMINS` env list
  (`AccountService.ApplyAdminPolicyAsync`); there is no in-app grant/revoke.
- ☐ **[L] Slice 2** (plan §1.6) — Glicko-2 team rating once real match data exists (the ladder is
  cumulative points, not ELO/Glicko); Steam session tickets when there is an AppID; matchmaking.
- ☐ **[L] Slice 3** (plan §1.6) — listings + WebRTC signaling into grains so the lobby can run more
  than one replica (both are per-process today; a 2-replica scale test passed only on IPv6
  advertising and was scaled back to 1); Apple login; loadout persistence (needs a per-server content
  fingerprint — separate design).

### Cross-cutting / opportunistic

Not stage-bound — done when convenient or when a stage needs them.

- ☐ **[S] Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar
  mapping; explore baking and in-engine parallax/height maps.
- ☐ **[S] Spatial audio polish** — finer mix tuning / more event coverage; the missing-sound audit is
  [`sfx-gaps.md`](sfx-gaps.md), the asset catalogue is [`audio-index.md`](../audio-index.md).
- ☐ Look for opportunities to utilize native `Vector3` and SIMD for performance improvements.
- ☐ Work to establish a full control map; manual.

---

## Deep backlog

- ☐ **[XL] Runtime asset streaming (client-patchless content)** — the client downloads meshes/textures/
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
- ☐ **[M] Explicit host transfer + runtime arena rebuild** — the game server's host is implicit:
  `ClientHub` seeds `_hostId` to the first pilot to connect (`ReceiveLoop`, the `MsgHello` case)
  and, when the host drops, hands it to the lowest remaining client id (`HandleConnection`'s
  disconnect path). A host cannot pass the role deliberately and nobody can pick one, so map
  control just follows join order. Wanted: an explicit host-selection/transfer frame plus the lobby
  UI for it. Blocking the same feature: `MsgSetMap` only advertises the pick as the "next" map
  (`_selectedMap`), because the live `World` is built once at boot — making a mid-lobby map change
  take effect needs an arena-rebuild seam (regen the `World`, re-`Welcome` every client).
- ☐ **[S] Cleanup follow-ups (from the 2026-09-09 pass)** — design-token migration second group
  (`TechDetailPanel`, `RosterCells`, `CommandSidebar`, `DataFeedback`; ~130 raw literals remain
  client-wide, mostly 3D VFX which may stay); `Simulation.Docking.cs` partial for the ~800-line
  `DockApproach` block; ClientHub receive-side/snapshot-AOI extraction (needs an interface design,
  not a pure move); `ApDockCreepFacingDot` / `ApDockAlignTimeout` / `ApDockCreepTimeout` still
  compile-time in `Simulation.cs` (seconds-authored keys if they ever need tuning).
- ☐ **[S] simbot speaks proto 7** — `tools/simbot` hand-builds its frames instead of referencing
  `shared/Net/Wire.cs`, and its header still says "Protocol v7". Port it to `Wire` so a protocol
  bump can't silently rot the load harness, or delete it.
- ☐ **[M] Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
- ☐ **[L] Replay system** — tick log or time-travel query playback.
- ☐ **[M] Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
- ☐ **[M] Mutinies** — a player can stage a mutiny on a team; all other players (except the
  commander) can vote to depose the commander. If the vote passes, the mutineer becomes the new
  commander.
