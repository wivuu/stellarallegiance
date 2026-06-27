# .PLAN — Stellar Allegiance

> **⚠ Architecture note:** SpacetimeDB has been removed. The server is now a standalone
> authoritative 20 Hz sim that *also* hosts the lobby, and the client downloads everything
> (world, content defs, live state) over the wire. Roadmap items below that mention an STDB
> backend or `STDB_*` env vars are historical; the prediction-lead override is still *named*
> `STDB_LEAD` in code but the lead is adaptive now (see Stage 5 / hosting). For the current shape
> see the repo **README.md**.

## Prototype (COMPLETE)

The two-ship prototype (T0-T10) and post-prototype polish are **finished**.
Archives:

- `prototype-archive/00-09, 99` — original prototype plan documents
- `prototype-archive/10-POST-PROTOTYPE-DONE.md` — completed polish & PIGs
- `docs/PROTOTYPE-ARCHITECTURE.md` — consolidated architecture reference

---

## QUICKNOTES:
- Code cleanup and refactor
- ✅ Password-protect game servers — done; set `--secret` / `SIM_SECRET` to gate joins
  (constant-time compare in `server/Backend/Backends.cs`).
- ✅ `/pigs on|off` in-game text commands — done (`server/Net/ClientHub.cs`).
- ✅ HUD health bars are screen-space — player HP text (`client/scripts/Hud.cs`), base health
  overlay (`client/scripts/TargetMarkers.cs`, replaced the old world-space quad).
---

## Content philosophy (the through-line)

**All content and mechanics tuning is server-authored data, downloaded by the client — never
baked into the client.** A game server defines what exists (ships, weapons, bases, factions, tech,
costs, mechanics knobs); clients receive it and render/predict from it. This is already half-true:
the def set streams server→client over `MsgDefs` and the client keeps **no** compile-time tuning
fallback. The roadmap closes the rest of the gap so an operator can **override content per server,
and eventually add an entire faction, with no client patch**:

1. **Single-source defs in code** — ✅ done (Stage 0).
2. **YAML authoring + per-server override** — defs come from editable YAML the server loads at
   startup, not C# (Stage 1).
3. **Runtime asset streaming** — the client downloads *binary* assets (meshes/textures/audio) it
   lacks from the game server, so server-defined factions need no client install (Stage 4).

Corollary for sequencing: **the content pipeline lands before we author much new content**, so
missiles, tech, and factions are written as YAML data from the start, never as C# to be migrated.

---

## Roadmap (re-sequenced by dependency — 2026-06-27)

> **Why this order.** Each feature lands *after* the foundation that should own it; each
> foundation is built **minimally first, enriched later**; and all content is authored as
> **server-side data + behavior modules**, never hardcoded and rebuilt. The worries that drove the
> sequencing, and how the code resolves them:
>
> 1. **Missiles before a tech tree → hardcoding?** The real risk was the *bolt-only weapon
>    behavior* + the *sim's duplicate stat tables*, **not** the tree — fixed in Stage 0. The tree
>    only gates *availability* (Stage 4).
> 2. **Tech tree before a commander → who researches?** Research needs an *authority + money + a
>    gating hook*, not necessarily a commander. Bootstrap simple (Stage 2); commander in Stage 4.
>    **No accounts required** — the strategy layer is independent of the persistence track.
> 3. **Buy things without a rich UI?** The in-match **spawn menu is the buy-menu seam**; the "rich
>    UI" is the commander map (Stage 4).
> 4. **Author content without recompiling / patching clients?** Content is YAML on the server
>    (Stage 1), streamed to clients; new *assets* stream too (Stage 4). See *Content philosophy*.
>
> **Dependency spine:** single-source defs → **YAML content pipeline (per-server override)** →
> per-team state + money + unlock gating + buy menu → combat content → tech + commander + mining
> (+ **asset streaming** for client-patchless factions) → accounts/persistence *(independent)*.

### Phase 1 — Configurability & maintainability refactor — ✅ DONE

Tuning and content are data, not code, so new ships, weapons, and bases are config.

- ✅ **Data-driven ship classes & loadouts** — weapon/tuning constants lifted out of `Lib.cs`
  into runtime-configurable class + loadout defs (`DefRegistry`); weapon/ship/base logic split
  into focused modules.
- ✅ **Ship meshes & hardpoints** — `ShipModelLoader` reads GLBs carrying `HP_` hardpoint nodes.
  The `tools/ship-gen` pipeline builds modular GLBs from YAML.
- ✅ **Base meshes & hardpoints** — `BaseModelLoader` reads base models with docking, lighting,
  and exit hardpoints.
- ✅ **(bonus) Shared collision** — the convex-hull collision core (`ConvexHull`, GLB parser,
  `SimModel`, sphere-vs-hull response + dock-disc carve-out) lives in `shared/Collision/`; the
  server reads GLBs from disk and the **client builds the same hulls from its `res://` GLB bytes**,
  so the client *predicts* collision response identically (no penetrate-then-snap) and collision
  audio is hull-accurate. Damage stays server-authoritative.

### Stage 0 — Data-driven cleanup — ✅ DONE (2026-06-27)

Finishes what Phase 1 started: removes the *remaining* hardcoding and lays the weapon seam, so
everything downstream is authored as data + a behavior module rather than rebuilt later.

- ✅ **Single-source stat tables** — the sim no longer keeps private `Weapons[]` / `MaxHull()` /
  `PodMaxHull` duplicates of the authored defs. It resolves a ship's gun by its Weapon
  hardpoint's `WeaponId` (carried on `Muzzle`) and its spawn hull from the class def, via
  `WeaponDefs`/`ShipDefs`/`HullFor`/`PrimaryWeapon` built straight from `GameContent`
  (`server/Sim/Simulation.cs`, `Simulation.Pig.cs`). One source of truth; no drift.
- ✅ **Weapon behavior-type seam** — `WeaponKind : byte { Bolt }` (append-only) + `WeaponDef.Kind`
  (`shared/Defs.cs`); `TryFire` dispatches on kind (one branch today). Per-`WeaponId` muzzles let a
  hull mix weapons later. **Server-only — not on the wire** until a kind needs distinct client
  rendering (Stage 3), so no protocol bump.
- ✅ **Guard test** — `FlightModelTest` asserts every weapon-hardpoint `WeaponId` has a def and
  every non-pod class has a positive hull.
- ☐ *Deferred:* lift sim-only tuning constants (`LaunchSpeed`, `DockRadiusFrac`, pod-eject params)
  into the tuning config *when they need runtime tuning* (folds into Stage 1's YAML).

### Stage 1 — Content pipeline (YAML authoring + per-server override)

Make all content editable data the server loads, not C# — the substrate for every later def
(weapons, costs, factions, tech, mechanics knobs). Reuses the existing def→`MsgDefs`→client path
(no client change); adds only a server-side loader (`YamlDotNet`).

- ☐ **YAML def authoring** — the server reads ship/weapon/base/world (and future faction/tech)
  defs from YAML at startup, builds the same def objects `GameContent` does today, and ships them
  over `MsgDefs`. Keep `GameContent` as built-in **defaults**; YAML **overrides and extends** them
  (server still boots with no YAML).
- ☐ **Per-server override** — a config path / env var picks the YAML set a server loads (mirror the
  custom-maps scheme: built-in default, a specific file, or pick-from-dir). An operator retunes
  mechanics or adds content per server with **no recompile** and **no client patch** — for content
  reusing existing assets; brand-new visual assets need asset streaming (Stage 4).
- ☐ **Schema + validation** — fail fast on a malformed/partial YAML def at load (the client has no
  fallback), with a clear error; extend the Stage-0 content guard to the loaded set.

### Stage 2 — Thin strategy spine

Cheap foundations that unblock economy, buying, and gating. Costs/unlocks are authored in the
Stage-1 YAML. Build minimally; enrich in Stage 4.

- ☐ **Per-team shared state** — a per-team container (today only base health, in `server/Sim/World.cs`)
  to home credits, unlocked content, and score.
- ☐ **Team credits + flat paycheck** — a per-team balance that accrues over time. The simplest
  "money"; the real mining economy replaces the income source in Stage 4.
- ☐ **Per-team unlock-set (def gating hook)** — defs filtered by what a team has unlocked
  (repurpose the unused `ShipClassDef.FactionId`, or add an `UnlockId`). The tech tree's
  *enforcement* mechanism, in place before the tree UI exists.
- ☐ **Buy menu** — extend the existing in-match spawn menu (`client/scripts/Hud.cs`): show cost +
  team balance, gray out unaffordable/locked options. Reuse `Lobby.MakeButton()`.
- ☐ **Authority: bootstrap-simple** — any-player-spends or auto; **no commander yet**.

### Stage 3 — Combat feel & depth

Richer dogfighting on shipped systems. Content authored after Stage 2 is **priced + gated by
construction** (and YAML-defined per Stage 1) — missiles land into the Stage-0 seam and the
Stage-2 economy, no rework.

- ✅ **Escape pods** — ships eject a pod on death; the pod must die or be rescued by a teammate
  before the player respawns.
- ✅ **Booster / smoke-trail FX** — booster smoke trail reacting to thrust.
- ☐ **Missiles** — a `WeaponKind` behavior module (launchers, lock-on, chaff/flare), defined in
  YAML with cost + unlock. *Where the Stage-0 seam pays off and the wire protocol bump lands.*
- ☐ **Mines & fields** — deployable mines/minefields as a `WeaponKind` behavior module.
- ☐ **Shields & damage systems** — regenerating shields over the raw-health model; damage-type
  interactions.
- ☐ **Boost recharge & ship-class feel** — boost limit + recharge; some classes recharge, some
  don't. (FX done; the recharge *mechanic* is not.)
- ☐ **Ship salvage & pickups** — destroyed ships drop ammo / guns / missiles / mines (and credits)
  to fly over and collect; ties into the Stage-2 economy.
- ☐ **Fog of war** — asteroids and enemy bases stay hidden until scouted by a teammate.
  (Independent — slot anytime.)
- ◐ **In-match HUD polish** — velocity indicator, radar/targeting, base health bar, minimap
  shipped. Still want player-facing **health/shield bars** and **in-match team scores** as proper
  HUD elements (needs Stage-2 per-team/player state; see QUICKNOTES). Durable per-player
  scores/ranks are Stage 5.

### Stage 4 — Strategy depth (Allegiance core)

The economic + RTS loop. Largely sequential; each item builds on Stage 2's money + gating and the
Stage-1 YAML pipeline.

- ☐ **Commander** — the richer decision authority for tech/build: the lobby-leader / first player
  to join a team (or promoted). **No accounts required.**
- ☐ **Tech paths** — team investment tree unlocking ship upgrades, new classes, and base defenses;
  the **tree is YAML data** (Stage 1). The UI + research-over-time; credits and per-team gating
  already exist from Stage 2.
- ☐ **Commander / RTS map view** — the "rich UI": a 2D strategic overlay across all sectors;
  commander issues waypoints and investment orders. Reuse minimap/sector data.
- ☐ **Mining + economy** — resource asteroids, miners, ore flow, build queues — *upgrades* the
  Stage-2 flat paycheck into the real Allegiance economy.
- ☐ **Base building + constructors** — deployable structures for resource processing; ships land,
  repair, and rearm at bases.
- ☐ **Runtime asset streaming (client-patchless content)** — the client downloads meshes/textures/
  audio it lacks from the game server into a temp cache, so a server can define an entire faction
  (or new ship/weapon) that clients render **without installing a patch**. Defs already stream
  (`MsgDefs`); this extends the same model to binary assets (transfer + cache + load-from-temp +
  validation/eviction). A substantial sub-project — the enabler for fully server-authored factions.
- ☐ **Factions** — distinct factions with unique ship classes, tech trees, and visual styles for
  asymmetric play (a faction dimension on YAML defs). *Faction rules ride Stage 1; faction assets
  ride asset streaming above.*

### Stage 5 — Social & persistence (independent track)

Orthogonal to the strategy loop, which runs on ephemeral per-match state. Do when persistence is
wanted. **The discovery + hosting core is done; the social/persistence layer is not.**

- ✅ **Public lobby & discovery** — `public-lobby/` registry + WebRTC signaling relay; direct-first
  reachability probing, WebRTC/STUN fallback (no TURN), Railway deploy.
- ✅ **Server lifecycle** — empty-server idle reset + match recycling; protocol versioning;
  client-update release checks that ban out-of-date servers/clients.
- ✅ **Adaptive prediction lead** — lead derived from measured RTT + jitter (`UpdateAdaptiveLead`);
  `STDB_LEAD` (legacy name) remains as a manual override.
- ☐ **Matchmaking, accounts & persistence** — player identities/auth, ELO, match history. Lobby
  owns the persistent storage; deployed as part of the lobby project. Use **Orleans** so the lobby
  is horizontally scalable and manages state.
- ☐ **Client authentication** — clients prove identity to the lobby (per-session secrets/tokens).
  Choose a provider that supports **passkeys**; lobby issues a session secret per client, validated
  by game servers (JWT?).
- ☐ **Game-server authentication** — game servers prove identity to the lobby; on start, show a
  link in the terminal to authenticate the session. Same userbase as clients.
- ☐ **Scores, kills/deaths & ranks** — *durable* per-player post-match stats, an overall point
  system, and player ranks. (In-match scoreboards are Stage 3.)
- ☐ **Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
- ☐ **Custom maps** — server-configurable aleph layout instead of a hardcoded asteroid field;
  store as YAML in a known location (same per-server-override mechanism as Stage 1). Each file is a
  map; env vars pick the map (random, specific, pick-from-files). *(Could be pulled forward to feed
  Stage-4 resource-asteroid maps.)*

### Cross-cutting / opportunistic

Not stage-bound — done when convenient or when a stage needs them.

- ✅ **CI / automated testing** — tag-triggered Release workflow (client zips + GHCR server image);
  `FlightModelTest` (determinism/golden + content guard) and `CryptoTest` in `tests/`.
- ☐ **Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar mapping;
  explore baking and in-engine parallax/height maps.
- ◐ **Spatial audio polish** — `SfxManager` exists; ✅ collision thuds (asteroids AND bases,
  client-side interception in `WorldRenderer.CheckCollisions` against the shared convex hulls, with
  the own-base dock-disc carve-out) and ✅ a volume settings UI (per-bus sliders in the Lobby
  overlay, persisted via `UserPrefs`) shipped. Remaining: finer mix tuning / more event coverage.

## Deep backlog

- ☐ **Replay system** — tick log or time-travel query playback.
- ☐ **.NET 10 upgrade** — upgrade from .NET 8 to 10 for perf.
- ☐ **Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
