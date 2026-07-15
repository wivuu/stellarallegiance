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
- **[M]** Code cleanup and refactor
- ✅ Base avoidance not working for autopilot (for constructors or own ship) — fixed 2026-07-15:
  `Simulation.Avoidance.cs` (`AvoidRocks`/`AvoidBases`/`AvoidObstacles`); autopilot, miners, and
  constructors now steer around base hulls (dock/keep-station target excluded); PIG combat steering
  untouched (asteroid-only, determinism-guarded).
- Set minimum distance for bases to spawn near asteroids and asteroids to spawn from each other during initial game state generation
- Add a custom cursor
- Add a 'Map' button to top (next to the 'Build' and 'Research' tabs, to the left of 'Hangar') that opens F3
- F3 map view shows offset vector for ships in other sectors
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
   startup, not C# (Stage 1). **The authoring schema is the in-repo `Allegiance.Factions` `Core`
   model** (`factions/`): one data layer — shared buyable catalog + factions + tech/capability
   gating — that the server loads, validates, and *projects* into the runtime def stream, so Stage
   2/4 (unlock gating, tech tree, factions) extend the same model instead of a parallel one.
3. **Runtime asset streaming** — the client downloads *binary* assets (meshes/textures/audio) it
   lacks from the game server to temp storage, so server-defined factions need no client install (Stage 4).

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
- ☐ **[S]** *Deferred:* lift sim-only tuning constants (`LaunchSpeed`, `DockRadiusFrac`, pod-eject params)
  into the tuning config *when they need runtime tuning* (folds into Stage 1's YAML).

### Stage 1 — Content pipeline (YAML authoring + per-server override) — ✅ DONE (2026-06-28)

Make all content editable data the server loads, not C# — the substrate for every later def
(weapons, costs, factions, tech, mechanics knobs). Reuses the existing def→`MsgDefs`→client path
(no client change); adds only a server-side loader (`YamlDotNet`).

- ✅ **YAML is the authoritative content** — there is **no compile-in content**: the server reads
  ship/weapon/base/world defs from YAML at boot (`server/Content/ContentLoader` + `ContentSet`,
  `YamlDotNet`), builds the shared def objects, and ships them over `MsgDefs` (no client change).
  `GameContent`/`FlightModel` keep only stable **id constants + the integrator** — the stat *numbers*
  live solely in the YAML bundle. The flight-stat path is single-sourced from the loaded def on BOTH
  sides (`ShipStats.FromDef`; server authority + client `Mass` re-derive route through it), and base
  health + world-scale seed from the content too, so a YAML-tuned ship/world can't desync.
- ✅ **Default location + per-server override** — the server loads `content/stock.yaml` (shipped next
  to the binary, resolved via `AppContext.BaseDirectory`) by default; `--content PATH` / `CONTENT_PATH`
  overrides the **location** with a different complete bundle (mirrors the `--secret`/`SIM_SECRET`
  pattern). An operator retunes mechanics or adds content per server by editing/copying the YAML —
  **no recompile**, **no client patch** (content reusing existing assets; new visual assets need
  asset streaming, Stage 4).
- ✅ **Schema + validation** — `ContentValidator` (shared) fails fast at boot on a malformed/incomplete
  bundle (dangling weapon-hardpoint refs, non-positive non-pod hull, dup ids, no base def) with a
  clear error and a refuse-to-start (the client has no fallback). `tests/ContentTest` loads the bundle,
  validates it, spot-checks the loader, and asserts deterministic wire defs; `tests/FlightModelTest`
  is now a pure flight-model determinism guard (its golden uses inline stat fixtures).
- ✅ **PIVOT — adopt `Allegiance.Factions` as the canonical content model** *(DONE 2026-06-28)*. The
  bespoke v1 `ContentLoader`/`ContentSet`/`ContentValidator` is gone; the in-repo
  `Allegiance.Factions` library (`factions/`) is now the **source of the configuration mechanism
  itself**. The server boots from the factions bundle and projects it into the unchanged `MsgDefs`
  wire path — in-game ships/weapons/base are byte-identical to pre-pivot and the **client is
  untouched**. Concretely:
  - ✅ **Author content as a factions `Core` bundle** — the single `stock.yaml` is replaced by a
    manifest split under `server/content/core/` (shared catalog fragments + `factions/stock.yaml`)
    with the library's checked-in **JSON schemas** for editor validation/autocomplete. The schemas are
    regenerated from the model via the library CLI.
  - ✅ **Load + validate with the library's own machinery** — `ContentLoader` now calls
    `CoreSerializer.Load` + `CoreValidator.Validate` (referential integrity, refuse-to-start on
    error), then runs the existing shared `ContentValidator` on the projected defs as a second gate.
  - ✅ **Project `Core` → runtime defs** — `server/Content/FactionsContentProjection.cs` maps
    `Hull`/`Weapon`/`Station` buildables (+ world config) onto the existing
    `ShipClassDef`/`WeaponDef`/`BaseDef`/`WorldConfig` records, so the **wire path and determinism
    contract are unchanged**. The runtime-only data the records need — **hardpoint geometry, the 13
    flight f32s, stable byte ids, tick-domain ballistics** — is carried by optional omit-when-default
    fields added to the `Hull`/`Weapon`/`Station` model + a `Core`-level world-config record (the
    library's 16 tests stay green; `sample-data` is unaffected).
  - ✅ **One data layer, no second migration** — the same `Core` now feeds Stage 2's unlock gating
    (`TechSet`/`Capability` forward-closure) below, and is ready for Stage 4's tech tree + factions,
    rather than a parallel def schema that would later need reconciling.

### Stage 2 — Thin strategy spine — ✅ DONE (2026-06-28)

Cheap foundations that unblock economy, buying, and gating. Costs/unlocks are authored in the
Stage-1 YAML. Build minimally; enrich in Stage 4.

- ✅ **Per-team shared state** — a per-team `TeamState` container in `server/Sim/World.cs` (keyed by
  team byte, parallel to base health) homing `Credits`, `OwnedTechs`/`OwnedCapabilities` (seeded from
  the stock faction's `BaseTechs`/`BaseCapabilities`), and `Score`.
- ✅ **Team credits + flat paycheck** — a per-team balance that accrues on a fixed tick cadence
  (`Faction.BonusMoney` at start + `Faction.IncomeMoney` rate), driven from the sim step. The simplest
  "money"; the real mining economy replaces the income source in Stage 4.
- ✅ **Per-team unlock-set (def gating hook)** — spawns are gated by the `Core` model's
  **`TechSet`/`Capability` forward-closure** via `BuildableResolver.GetBuildables(core, ownedTechs,
  ownedCapabilities)` (no ad-hoc `UnlockId`). The tech tree's *enforcement* mechanism, in place before
  the tree UI exists.
- ✅ **Server spawn-cost enforcement** — the `EnqueueJoin → ProcessRespawns → SpawnCombatShip` path
  rejects a spawn whose hull is locked or unaffordable and deducts `Cost` on success
  (server-authoritative); the client's pre-check (`WorldRenderer.CheckSpawnGate`) suppresses doomed
  buys and surfaces the reason without hanging `_spawnPending`.
- ✅ **Wire: per-team state + ship cost** — proto bumped 9→10; `ShipClassDef.Cost` rides `MsgDefs`,
  and a low-rate `MsgTeamState` carries per-team `Credits`/`Score` + the unlocked-class snapshot to
  the client (`WorldRenderer.NetUpdateTeamState`).
- ✅ **Buy menu** — the in-match spawn menu (`client/scripts/Hud.cs`) is now def-driven
  (`DefRegistry.BuildableShips`): one button per hull showing `Spawn <name> — <cost> credits`, grayed
  out when unaffordable or locked, with a running team-credits readout. Reuses `Lobby.MakeButton()`.
- ✅ **Authority: bootstrap-simple** — any-player-spends / auto; **no commander yet** (Stage 4).

### Stage 3 — Combat feel & depth

Richer dogfighting on shipped systems. Content authored after Stage 2 is **priced + gated by
construction** (and YAML-defined per Stage 1) — missiles land into the Stage-0 seam and the
Stage-2 economy, no rework.

- ✅ **Escape pods** — ships eject a pod on death; the pod must die or be rescued by a teammate
  before the player respawns.
- ✅ **Booster / smoke-trail FX** — booster smoke trail reacting to thrust.
- ✅ **Missiles** — guided missiles shipped (2026-07-02, proto 15): `WeaponKind.Missile` launchers
  authored as factions `Launcher`+`Missile` YAML (turn rate/accel/speed/lock/damage/trail all
  data), hung on fighter/bomber hardpoints; server-authoritative lock + `MissileSim` pursuit
  streamed via `MsgMissiles`/`MsgMissileGone`; finite racks; lock/ammo/incoming-warning HUD with
  original Allegiance models+SFX; PIGs fire them; `tests/MissileTest` determinism guard.
  Completed 2026-07-03 (proto 18): **chaff** (`WeaponKind.Chaff` decoy-dispenser YAML; drop with
  `C`; deterministic pure-hash decoy roll vs authored `chaff-resistance` at the `TryChaffAim`
  seam; `MsgChaff` puffs), blast-radius splash (`ApplyBlast` + client `CreateBlast` VFX scaled by
  `blast-radius`), pre-launch "being locked" warning (ship-record threat flags → amber/red HUD
  banner + original lock tone), rearm at base (voluntary dock refunds the ship's `PaidCost`;
  dock→relaunch = free full rearm+repair; death refunds nothing). *Deferred: PIG auto-chaff.*
- ✅ **Mines & fields** — shipped 2026-07-03 (proto 18): `WeaponKind.Mine` mine-dispenser
  authored as factions `Launcher`+`Mine` YAML (trigger/power/blast/cloud radius+count/arm-delay/
  lifespan all data); `B` deploys a seed-based pseudo-random field (`shared/MinefieldLayout`,
  ≤64 mines, `aliveMask`) streamed via `MsgMinefields`/`MsgMineGone` for the anchor sector; mines
  arm after a delay, trigger per-mine on enemy proximity (grid-cube scan), splash via
  `ApplyBlast`, and the field depletes gradually; team-tinted billboard sprites client-side;
  default hold authored per hull (`default-cargo`, payload-costed, hangar steppers live via
  `MsgSpawn` cargo). `tests/MineTest` determinism guard. *Deferred: mine-vs-mine chains,
  shootable mines, PIG mine-laying.*
- ✅ **Shields & damage systems** — shipped (proto 19): a regenerating energy **shield** authored
  per hull/faction in YAML (`shield-capacity`/`shield-recharge`/`shield-delay` on `Hull`;
  Fighter/Bomber carry one, Scout/Pod don't) layered over the raw-health model. One central
  `Simulation.ApplyDamage` seam routes **all** damage (bolts, missiles, blast, mines, collisions,
  boundary) through the shield first: it absorbs while it holds (hull untouched) and overflow spills
  to hull when a hit pops it; recharge resumes after a per-hull quiet delay. The **damage-type
  interaction** is a per-weapon `shield-damage-multiplier` (default 1.0; the bomber cannon authors
  0.5 = half vs shields). Current shield rides the snapshot; the client draws a cyan **SHLD** solid
  arc wrapping the HULL gauge (`SystemRing`, completing the Claude Design "Game HUD" ring), plays a
  distinct `shield_hit.ogg` + a hemisphere `ShieldFlash` when a bolt strikes a raised shield vs a
  bare hull. `tests/ShieldTest` guards absorb/spillover/recharge/multiplier; a `Simulation.ShieldsEnabled`
  toggle lets the missile/mine damage tests isolate raw damage.
- ✅ **Boost recharge & ship-class feel** — boost limit + recharge; some classes recharge, some
  don't. (FX done; the recharge *mechanic* is not.)
- ✅ **Adaptive prediction lead** — lead derived from measured RTT + jitter (`UpdateAdaptiveLead`);
  `STDB_LEAD` (legacy name) remains as a manual override.
- ✅ **[M]** **Alephs block shots**  - from weapons and missiles
  - Alephs should act as physical barriers that prevent projectiles from passing through them, requiring
    players to navigate around or otherwise account for their presence in combat scenarios.
- ✅ **[M]** **Control settings and mappings** — shipped (2026-07-09): flight/combat/scope controls
  migrated onto Godot InputMap actions (`client/scripts/InputBindings.cs` — defaults single-sourced
  in C#, registered at boot in `SfxManager._Ready`, overrides persisted in `UserPrefs` `[bindings]`).
  Rebindable from Settings → Controls (grouped `KeybindRow` list, click-to-capture key/mouse/**gamepad**,
  RESTORE DEFAULTS + revert), with analog joystick support via `Input.GetAxis`. Menu/system keys
  (Esc, F3/F4/F9, base-select/spawn digits, chat) stay hardcoded to protect the modal input-gating.
  *Deferred: named preset schemes (Default/Southpaw), rebinding the menu/system keys.*
- ✅ **[L]** **Ship autopilot and navigation** — shipped 2026-07-10 (protocol v30): players get
  the PIG-style navigation PIGs already had. Pick a target (Tab now cycles ships → bases →
  asteroids in view; F3 map left-click selects an entity or drops a grid-plane **waypoint**,
  right-click engages), press **T** (new rebindable `engage_autopilot`) and the ship flies
  itself. Steering is **server-side**, synthesized at the existing `InputFor()` seam via the
  shared `AutoSteer` (verbatim extraction of the PIG steer/attack bodies, determinism-guarded by
  the PIG suites): approach + brake-to-standoff on waypoints/rocks/enemy bases, keep-station
  (never auto-fires) on enemy ships, fly-to-door auto-dock on a friendly base, single-hop aleph
  transit cross-sector; arrival/dock/death/target-loss or any real manual stick input disengages
  (cruise-control handback). New `MsgSetAutopilot=11` carries the target; `ShipFlagAutopilot=16`
  echoes the engaged state back. Client uses **follow-authority prediction**: while engaged it
  suspends own-ship `Step()` and eases the render onto authoritative snapshots through the
  reconcile spring (chase cam stays smooth, `ReconcileCount` doesn't climb), re-anchoring C¹ on
  entry/exit; it keeps sampling+sending real sticks so the server detects override. AUTOPILOT HUD
  banner + disengage toast (`DesignTokens` cyan chrome); friendly-base focus now draws its TARGET
  bracket (never a lock arc). `tests/AutopilotTest` covers approach/standoff/stop/aleph/avoidance/
  override/friendly-dock/target-loss. *Deferred: multi-hop aleph routing, enemy-ghost tracking
  through fog, reuse for miners/constructors.* 
  - Make it so tab can select other types of targets (e.g., bases, asteroids), prioritize in-view enemy ships, then enemy bases, then cycle through other targets (e.g., asteroids) in view.
  - Make it so targets can be selected with the mouse from the F3 screen.
  - Abstract the PIG autopilot/autosteer behavior so that it can be reused for player ships, allowing them to follow waypoints or targets with similar logic.
  - We will eventually (out of scope) want to be able to attach this autopilot behavior to other entities, such as miners and constructors (that fly to asteroids to make bases)
  - Press 'T' (new mappable control) to engage the autopilot towards the selected target or waypoint.
- ☐ **[L]** **Ship salvage & pickups** — destroyed ships drop expendables (ammo / booster fuel / guns / missiles / mines)
  to fly over and collect; ties into the Stage-2 economy.
  - When a ship is destroyed, there should be a chance that it drops whatever expendable or weapon that was equipped/not consumed, flying out in a random direction until it comes to rest.
  - Meshes for various dropped items should match GLB visual representation, or if none are available, pick an asset from the pick-assets folder. Ask me for each missing asset.
  - The dropped item should be able to be picked up by a ship flying over it, if the ship has the capacity to carry it.
  - If the ship does not have capacity, the item can bounce off harmlessly.
  - If the item is in-motion, it should collision detect with asteroids and bases

### Stage 4 — Strategy depth (Allegiance core)

The economic + RTS loop. Largely sequential; each item builds on Stage 2's money + gating and the
Stage-1 YAML pipeline.

- ✅ **Fog of war** — shipped (proto 23): server-authoritative **per-team shared vision**. Statics
  (asteroids, bases, alephs) stay hidden until a teammate scouts them, then persist as team memory —
  a base destroyed while unseen keeps its last-known health (`LastKnownBaseHealth`) until re-scouted.
  Enemy ships are streamed only while in a teammate's sight; once radar-seen and then lost they
  persist as last-known **ghost** contacts (HUD/F3 glyph only, never a 3D mesh) until re-scouted empty
  or re-spotted. Vision is per-hull YAML (long-range **cone** + omni proximity **sphere**, scouts get
  much bigger values; asteroids occlude the cone via the bolt raycast); **bases** contribute an
  unoccluded vision sphere (garrison watches from tick 0); every target carries a **radar signature**
  multiplier scaling all detection ranges (small scouts seen closer, bombers/bases farther). An outer
  **eyeball tier** (`fog-eyeball-multiplier`, ~1.5× sphere) streams an enemy's mesh in-world while its
  radar/HUD/targeting stay silent — a keen-eyed player can spot a stealth fleet before radar. Deployable
  **recon probes** (`WeaponKind.Probe` launcher + expendable, model `acs64`) add team vision spheres.
  Vision is computed on a **dedicated 2 Hz worker thread** (snapshot-in / apply-at-boundary pipeline,
  `Simulation.Vision.cs`) so the sim tick is untouched; wire frames `MsgReveal`/`MsgContacts` (ghosts +
  radar-id list) / `MsgProbes`/`MsgProbeGone` + `MsgShipGone reason=2` (quiet fade), all per-team,
  reusing Welcome's static encoders so they can't drift. PIGs and missile-lock respect team vision (no
  more wallhack). World-YAML `fog-of-war` (default on) toggles it; **off = bytes/behavior identical to
  pre-fog** (single shared coarse buffer, no worker, full Welcome). `tests/FogTest` covers cone/sphere/
  occlusion/signature/eyeball/ghost-lifecycle/base-vision/probe/determinism. *Deferred: shootable
  probes (combat fields dormant), PIG auto-probe usage, `MsgMissiles` team-filtering (accepted
  incoming-warning leak), vision-cone HUD rendering, eyeball-tier occlusion (unoccluded in v1).*
- ✅ **[M]** **Commander** — shipped (proto 34): explicit per-team commander STATE in the
  connection-layer `Lobby` (seeded to the first pilot to join the side, falls to the next-lowest
  client id on leave/drop, manually reassignable via `/commander <name>` by the sitting commander
  or the host — so it can't be purely derived). Streamed on the `MsgLobbyState` tail
  (`commander0/commander1`); client exposes `CommanderIdOf`/`IsCommander` and renders a gold
  **CMDR badge** in the lobby roster. The commander is the single AI authority: `/mine` and
  `/buyminer` are now commander-gated (`ClientHub.CommanderOrWarn` — closes the mining deferred
  item). **No accounts required.** `tests/LobbyTest` covers seed/fall-through/manual-set.
  *Deferred: lobby "MAKE CMDR" button (chat command only), commander persistence across reconnect
  (rank falls to next senior; re-promote manually).*
- ✅ **[L]** **Commander orders / F3 select-and-command** — shipped (proto 34): left-click on the
  F3 map SELECTS any entity (friendly/enemy ship, base, rock — new `SectorOverview.SelectedId`,
  separate from Tab focus; click-away deselects; gold brackets = commandable friendly ship). With
  a friendly ship selected, right-click sends **`MsgOrder`** naming the clicked target; the server
  infers the verb (enemy ship → attack, enemy base → attack/`AttackPoint`, anything else → go to
  and idle near; right-click the selected ship itself = release to autonomy). **Anyone may issue
  orders; AI obeys only the commander's** (hub-gated) — orders to a HUMAN teammate relay as a
  gold team-chat directive (`MsgChatRelay` scope 2) instead. AI orders live in
  `Simulation.Orders.cs` (`_pigOrders` keyed by ShipId) and are consumed by `TryObeyOrder`, a
  top-priority goal in the `PigDecide` chain (below rescue) emitting only existing plan kinds —
  orders complete-and-revert (target dies / radar contact lost under fog / base destroyed), and a
  holding pig defends its point. Miner subjects map onto mining state (rock order pins the claim +
  authorizes the sector; point = per-miner `/mine`; friendly base = pinned offload).
  `tests/CommanderTest` (9 scenarios). *Deferred: multi-select / order queueing, order markers on
  the F3 map, PIG order-status HUD.*
- ✅ **[M]** **Maps** — shipped (2026-07-10): base/asteroid/aleph positions reshuffle every match
  (fresh random seed per match start, even on the same map), so players explore instead of
  memorizing. Layouts were already fully seed-generated; the seed is now random by default and
  pinnable via `SIM_SEED` / `--seed N` for exact repro (each rolled match seed is logged).
- ✅ **[XL]** **Mining + economy** — DONE (2026-07-11, `mining` branch): rock classes (He3 harvestable,
  volume-proportional shrink streamed via `MsgRockUpdate`), per-team AI miner drones
  (`Simulation.Mining.cs`, purchasable via `/buyminer`, sector orders via `/mine <sector>`, status via
  `/miners`), multi-hop aleph routing (`World.NextGateTo`, players get multi-hop autopilot free),
  offload → team credits alongside the flat paycheck. `tests/MiningTest` + pinned-seed layout golden.
  *Deferred:* build queues, commander-gated authority, refinery uses for uranium/silicon/carbonaceous,
  shrunk-rock vision occlusion / PIG avoidance (stay at spawn radius).
  - Create classes of rock that asteroids should be categorized into (e.g., helium-3, uranium, silicon, and carbonaceous).
  - Each team starts with 1 miner, but can purchase up to 4 at a time (configurable max in world yaml)
  - Miners harvest only from helium-3 asteroids
  - Miners, once launched, will auto-pilot to the nearest helium-3 asteroid that is
    - a. Not already targeted by another miner, unless there are more miners than asteroids
    - b. Not depleted
    - c. Was already 'working on' before filling
  - Once miner is filled, it will return to the nearest base to offload the harvested resources, then after a brief delay, relaunch to harvest again, either the same rock, or if it is depleted, the next eligible helium-3 asteroid according to the rules above.
  - If the nearest base is not in the same sector, the miner will navigate to the base across sectors, potentially taking longer to offload resources before resuming harvesting.
  - A miner will not enter a new sector to mine unless the commander tells him to go to that sector to mine (at least once)
- ✅ **[L]** **Tech paths** — shipped 2026-07-14 (proto 36, `tech-tree` branch; handoffs in
  `.PLAN/tech-paths/PHASE-{A,B,C,D}-HANDOFF.md`). The docked screen is a real three-tab shell
  (**HANGAR / BUILD / RESEARCH**) over a shared **CommandSidebar** (SectorMapPreview command map +
  "Your Bases" with live research banners); the hangar's ship picker is a horizontal card strip and
  the sidebar's launch-base pick is REAL (MsgSpawn carries the base id, server validates+falls back).
  Research is YAML-authored `Development`s (price + `build-time-seconds` + required/granted techs —
  the existing factions Core model; NEW `obsoleted-by-techs` field for future tier replacement +
  `research-slots` per station), streamed as a full catalog in MsgDefs; a **commander** authorizes
  at a base (`MsgResearch`, or `/research <id>` chat) — per-base **slots + one on-deck queue**
  (deduct at start AND at queue-reservation; cancel = 100% refund; dead base = loss); completion
  grants techs/caps and re-resolves unlocks **mid-match** (`Simulation.Research.cs`; per-team
  `MsgResearchState`, startTick+duration encoded). Stock tree gates the **bomber** behind
  `heavy-ordnance` and a new **heavy-cannon** behind `cannon-tier-2` (arsenal shows real
  `⚿ LOCKED · REQUIRES <tech>` rows). The BUILD tab renders the 7 future station types from YAML as
  placeholders (real gating states, purchase disabled until base building). `tests/StrategyTest`
  research suite + ContentTest catalog checks. *Deferred: stat-modifier developments
  (AttributeModifiers), per-site base types for slots, authored obsoleted-by content, hull unlock
  chips (ShipClassDef has no wire tech refs).*
- ◐ **[XL]** **Base building + constructors** — MVP shipped 2026-07-14 (proto 37): the **Outpost** is
  buildable end-to-end; the other station types are one YAML edit away (see below). Bases are now
  **per-type data** like ship hulls — `BaseDef.ModelName` selects the GLB (server collision + client
  mesh, mirroring `ShipClassDef`), `BaseSite.BaseTypeId` rides the wire, and `World.CreateBase`
  appends a base at runtime (growable `BaseHealth`/`ResearchByBase`, index-parallel). A **constructor**
  is an AI drone (`ShipKind.Constructor`, `Simulation.Constructors.cs`) modeled on the miner: bought
  from the docked **Build tab** bound to a station type (commander-gated `MsgBuildConstructor`, charges
  the station price), launched from a **garrison** (win-condition base) only, F3-ordered to a
  compatible rock (reuses the miner order plumbing; stock outpost → **Regolith**), then it navigates,
  aligns, **sinks** into the rock, and a spinning greenish-blue **build sphere** (`BuildSphere.cs`,
  streamed via `MsgConstructorBuilds`) envelops the asteroid over the station's `build-time-seconds`
  before the base appears fully constructed (reveals via the fog log / a fog-off broadcast) and grants
  its capabilities. **Win condition reworked**: a per-type `WinCondition` flag (= the `start` ability,
  garrison-only) — a team loses only when ALL its win-condition bases die, so a destroyed outpost never
  ends the match. `tests/ConstructorTest` covers the full loop + the rock-class gate + win-condition.
  *Deferred:* docking/repair/rearm at outposts (no dock faces on Outpost.glb — sphere/convex collision
  only); the 6 other station types (add `base-type-id` + `model-name` + `build-on-rock-class` to
  `stations.yaml` — no code); cap-revoke when a granting base is destroyed (grants are additive for the
  match); CommandSidebar build banner; per-site research slots; live visual sign-off of the sphere.
  - ✅ Generalize bases so the mesh is YAML config (`model-name`), like ship hulls — not hardcoded.
  - ◐ Multiple base types by YAML (Garrison + Outpost live; Shipyard/Supremacy/Tactical-Lab/Expansion/
    Teleport-Receiver/Refinery are authored placeholders, buildable via YAML). Bases grant caps/tech.
  - ✅ Commander buys constructors from the docked Build tab (miners stay on `/buyminer`).
  - ✅ Constructors are AI drones (`utl11.glb`, not hardcoded), launch from a garrison, buildable rock
    class configurable per base type.
  - ✅ Ordered to a compatible asteroid → navigate → standoff → align (configurable) → sink in.
  - ✅ Spinning greenish/blue translucent multi-layer sphere envelops the asteroid over a configurable
    time; the constructor mesh vanishes (server despawns it at completion, sphere covers it).
  - ✅ Base appears fully constructed on the asteroid; build effect removed; tech paths unlocked.
- ☐ **[L]** Update plan to include multiple teams; each map only supports a certain number of teams, so this is a constraint that must be reflected in the plan. Plan should include a richer 'game lobby' (as opposed to server lobby) experience; allowing users to select or join teams before the match starts. First person on a perspective team (and not on NOAT/not on a team) can configure the number of teams (2-6 for now).
- ☐ **[M]** **Mutinees** — A player can stage a mutiny on a team, all other players (except commander) can vote to depose the commander; if the vote passes, the mutineer becomes the new commander.
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

- ✅ **Public lobby & discovery** — `public-lobby/` registry + WebRTC signaling relay; direct-first
  reachability probing, WebRTC/STUN fallback (no TURN), Railway deploy.
- ✅ **Server lifecycle** — empty-server idle reset + match recycling; protocol versioning;
  client-update release checks that ban out-of-date servers/clients.
- ☐ **[M]** **Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
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

- ✅ **CI / automated testing** — tag-triggered Release workflow (client zips + GHCR server image);
  `FlightModelTest` (determinism/golden + content guard) and `CryptoTest` in `tests/`.
- ☐ **[S]** **Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar mapping;
  explore baking and in-engine parallax/height maps.
- ◐ **[S]** **Spatial audio polish** — `SfxManager` exists; ✅ collision thuds (asteroids AND bases,
  client-side interception in `WorldRenderer.CheckCollisions` against the shared convex hulls, with
  the own-base dock-disc carve-out) and ✅ a volume settings UI (per-bus sliders in the Lobby
  overlay, persisted via `UserPrefs`) shipped. Remaining: finer mix tuning / more event coverage.

## Deep backlog

- ☐ **[L]** **Replay system** — tick log or time-travel query playback.
- ☐ **[M]** **.NET 10 upgrade** — upgrade from .NET 8 to 10 for perf.
- ☐ **[M]** **Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
