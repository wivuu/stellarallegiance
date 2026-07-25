# .PLAN — Stellar Allegiance

---

## QUICKNOTES:
- **[M]** Code cleanup and refactor
- Proceed to dropped salvage below
- Look for opportunities to utilize native vector3 and SIMD for performance improvements
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

### Stage 4 — Strategy depth (Allegiance core)

The economic + RTS loop. Largely sequential; each item builds on Stage 2's money + gating and the
Stage-1 YAML pipeline.

- ☐ **[L]** Update plan to include multiple teams; each map only supports a certain number of teams, so this is a constraint that must be reflected in the plan. Plan should include a richer 'game lobby' (as opposed to server lobby) experience; allowing users to select or join teams before the match starts. First person on a perspective team (and not on NOAT/not on a team) can configure the number of teams (2-6 for now).
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
- ☐ **[L]** **Factions** — distinct factions with unique ship classes, tech trees, and visual styles for
  asymmetric play (a faction dimension on YAML defs). *Faction rules ride Stage 1; faction assets
  ride asset streaming above.*

### Stage 5 — Social & persistence (independent track)

Orthogonal to the strategy loop, which runs on ephemeral per-match state. Do when persistence is
wanted. **The discovery + hosting core is done; the social/persistence layer is not.**

- ☐ **[M]** **Scores, kills/deaths & ranks** — *durable* per-player post-match stats, an overall point
  system, and player ranks. (In-match scoreboards are Stage 3.)
- ☐ **[XL]** **Matchmaking, accounts & persistence** — player identities/auth, ELO, match history. Lobby
  owns the persistent storage; deployed as part of the lobby project. Use **Orleans** so the lobby
  is horizontally scalable and manages state. (BIG)
- ☐ **[L]** **Client authentication** — clients prove identity to the lobby (per-session secrets/tokens).
  Choose a provider that supports **passkeys**; lobby issues a session secret per client, validated
  by game servers (JWT?).
- ☐ **[M]** **Game-server authentication** — game servers prove identity to the lobby; on start, show a
  link in the terminal to authenticate the session. Same userbase as clients.

### Cross-cutting / opportunistic

Not stage-bound — done when convenient or when a stage needs them.

- ☐ **[S]** **Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar mapping;
  explore baking and in-engine parallax/height maps.
- ◐ **[S]** **Spatial audio polish** — `SfxManager` exists; ✅ collision thuds (asteroids AND bases,
  client-side interception in `WorldRenderer.CheckCollisions` against the shared convex hulls, with
  the own-base dock-disc carve-out) and ✅ a volume settings UI (per-bus sliders in the Lobby
  overlay, persisted via `UserPrefs`) shipped. Remaining: finer mix tuning / more event coverage.
  - Use audio-index.md for reference

## Deep backlog

- ☐ **[M]** **Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
- ☐ **[L]** **Replay system** — tick log or time-travel query playback.
- ☐ **[M]** **Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
- ☐ **[M]** **Mutinees** — A player can stage a mutiny on a team, all other players (except commander) can vote to depose the commander; if the vote passes, the mutineer becomes the new commander.
- ☐ **[L]** **Ship salvage & pickups** — destroyed ships drop expendables (ammo / booster fuel / guns / missiles / mines)
  to fly over and collect; ties into the Stage-2 economy.
  - When a ship is destroyed, there should be a chance that it drops whatever expendable or weapon that was equipped/not consumed, flying out in a random direction until it comes to rest.
  - Meshes for various dropped items should match GLB visual representation, or if none are available, pick an asset from the pick-assets folder. Ask me for each missing asset.
  - The dropped item should be able to be picked up by a ship flying over it, if the ship has the capacity to carry it.
  - If the ship does not have capacity, the item can bounce off harmlessly.
  - If the item is in-motion, it should collision detect with asteroids and bases