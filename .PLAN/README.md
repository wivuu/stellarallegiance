# .PLAN — Stellar Allegiance

The live roadmap: what is shipped (one line each, with a pointer to the real docs), what is open
(by stage), and the deep backlog. Finished work is condensed here, not narrated — the code,
[`GLOSSARY.md`](../GLOSSARY.md) and the per-feature docs are the authority for how things work.

**What else lives in `.PLAN/`**

| Path | What it is |
| --- | --- |
| [`LobbyRankingService.md`](LobbyRankingService.md) | Public-lobby identity + ranking design; §1.6 slices, §8 what was built and what bit. |
| [`ship_movement/`](ship_movement/README.md) | Allegiance flight-model reference (constants, integration loop, rotation math). **Live reference, not archived.** |
| [`tech-tree-README.md`](tech-tree-README.md) + `tech-tree-*.yaml` | Flattened content dumps for handing to an LLM/human as context. |
| [`TechPathsFromMemory.md`](TechPathsFromMemory.md) | Original brief for the Iron Coalition tech port — executed (see `archive/tech-paths/`). |
| [`code-review-sweep-2026-07-18.md`](code-review-sweep-2026-07-18.md) | One-off review sweep log. Historical. |
| [`archive/`](archive/README.md) | Completed handoff notes (prototype build order, base-building, tech-paths A-D). History only. |

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

- ✅ **Phase 1 + Stages 0-2 — data-driven content, YAML pipeline, strategy spine** (2026-06-28).
  Hulls/weapons/bases/tech/costs and the mechanics knobs are per-server YAML under
  `server/Content/core/` (`world.yaml` holds the sim tuning, including the once-deferred
  `launch-speed` / `dock-radius-frac` / `pod-eject-*`), streamed to clients over `MsgDefs`. Authoring
  guide: the `tech-tree-content` and `hulls-weapons` skills; JSON schemas in `schemas/`.
- ✅ **[L] Ship salvage & pickups + cargo hold** (2026-09-12, PR #82, `Wire.ProtocolVersion` 40 at the
  time). Per-item drop rolls on death, server-authoritative items that drift/bounce/expire per
  sector, touch-pickup by either team into an empty compatible mount or
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
  lobby:8091 → server:8090 with the Godot `client` and the Game Launcher `launcher` (UI review only) as
  explicit-start; root `.env` keys are parameters; `aspire do deploy-lobby|deploy-server` replaces the
  deploy pwsh scripts. See `apphost/` and the `aspire` skill. `scripts/run-server.ps1` / `run-client.ps1`
  target the *hosted* lobby.
- ✅ **[S] Spatial audio, first pass** — `SfxManager`, collision thuds (asteroids and bases, client-side
  interception against the shared convex hulls), per-bus volume sliders persisted via `UserPrefs`.
- ✅ **[M] Settings VIDEO tab** (2026-09-27, issue #96). Window mode (windowed / borderless / exclusive
  fullscreen), vsync, max FPS, 3D render scale (FSR 1.0 below 100%) and UI scale (root
  `ContentScaleFactor`, AUTO against the 1440p design reference, capped to a 1920×1080 logical canvas),
  persisted in `settings.cfg [display]` and restored at boot on player launches only (harness runs keep
  the project window; `SA_DISPLAY_PREFS=1` forces). Detail: [`GLOSSARY.md` → *Display Prefs*](../GLOSSARY.md).
- ✅ **[L] Distribution — installer, auto-update, release pipeline** (2026-09-19 → 2026-09-21, PRs #85,
  #89, #90, #92, #93; shipped through **v0.0.15**). A themed Avalonia launcher (`launcher/`) is the
  Velopack main executable on macOS, Windows and Linux — installer, delta updates from GitHub Releases,
  in-game **UPDATE NOW** handoff (exit code 85), crash notice — and a game server updates itself the
  same way, but only while it has no players and only once the public lobby tells it a release exists.
  Decisions: [`docs/adr/0004`](../docs/adr/0004-game-launcher-fronts-velopack.md) ·
  [`docs/adr/0005`](../docs/adr/0005-game-servers-self-update-via-velopack.md) · process:
  [`docs/RELEASING.md`](../docs/RELEASING.md) · deploy knobs: [`docs/DEPLOY.md`](../docs/DEPLOY.md) ·
  [`launcher/README.md`](../launcher/README.md) · terms:
  [`GLOSSARY.md` → *Distribution & Updates*](../GLOSSARY.md). What is actually proven, and by what:
  - **All three desktop OSes, every time the launcher changes** — `Package dry-run (launcher e2e)`
    packs three launcher versions around a stub game, installs the first the way a player would (on
    Windows the real `Setup.exe --silent` running the `--veloapp-install` hook), then drives two real
    updates, one of them triggered by the game's own exit code. Green on Linux, macOS and Windows.
    `scripts/launcher-e2e.ps1`, `tests/LauncherTest`.
  - **Every release** publishes five Velopack channels (`win osx linux server-linux-x64`,
    `server-linux-arm64`), a Setup + portable per desktop OS, `StellarAllegiance.AppImage`, and a
    two-platform server image — gated on the server update e2e (`server-update-dryrun.yml`, a required
    job since PR #90) and on `--verify-assets` against the staged, re-signed game.
  - **Server auto-update** — `tests/ServerUpdateTest` (259 checks, incl. a 400-timeline invariant fuzz
    and the parked-`Hello` drain race against the real `ClientHub`), the `tests/PublicLobbyTest`
    release section (73 checks against the real host), a Docker e2e that defers while a `simbot` is
    connected then relaunches *inside* the same container, the `docs/DEPLOY.md` systemd AppImage unit
    (needs `fuse3`, and `SuccessExitStatus=85`), and rehearsal tags `v0.0.14-ci.1…3` before the real
    one. Older server images need one manual `docker compose pull && docker compose up -d`.
- ✅ **[M] NativeAOT game server** (2026-09-19, PR #86) — the sim server publishes ahead-of-time:
  source-generated JSON, static YamlDotNet contexts, TrimmerRoots for SIPSorcery's SCTP cookie.
  `PublishAot` applies to ordinary builds too, so a dev run mimics the shipped binary (and the IDE
  cannot attach Hot Reload). [`GLOSSARY.md` → *NativeAOT Server*](../GLOSSARY.md).
- ✅ **[M] Collision sidecars + a loud missing model** (2026-09-20, PR #92) — exported clients ship
  `.glb.simmodel` sidecars (~270 KiB for all 31 models) instead of re-deriving hulls from raw GLBs that
  were never in the package; a model that cannot be built is now a FAULT rather than a phantom sphere.
  `--headless --verify-assets` is the release gate, and `SA_COLLISION_SIDECARS=only` flies the shipped
  path from source. [`GLOSSARY.md` → *Collision Sidecar*](../GLOSSARY.md).
- ✅ **[L] Source-generated netcode** (2026-09-12, PRs #76-#81) — one shared frame definition per
  message generates the codec for both peers, plus `LowRateStream` and the sim→hub `StepEvents` seam.
  A new wire field is now: edit the shared type, **one** version bump per PR, re-pin the goldens.
  Decision: [`docs/adr/0003`](../docs/adr/0003-wire-format-source-generator.md); generator in
  `tools/wire-gen`; suite `tests/WireTest`.
- ✅ **[M] Remote-ship render timeline** (2026-09-13, PR #83) — other ships stopped jerking: a match
  clock render timeline in place of raw tick counts, finer position/rotation on the wire, and the
  offline `tests/InterpTest` harness. [`GLOSSARY.md` → *MotionInterpolator*](../GLOSSARY.md).
- ✅ **[S] Toolchain floor** (2026-09-20, PR #88) — Central Package Management: every NuGet version
  lives in `Directory.Packages.props` (with transitive pinning to lift vulnerable indirect packages),
  and `global.json` pins the .NET SDK to 10.0.401. Both must be `COPY`d into a Dockerfile's restore
  layer (`server/Dockerfile:26-27`, `public-lobby/Dockerfile:13-14`) or every package fails NU1010.
- ✅ **[S] Clean exits** (2026-09-20, PR #93) — quitting to the desktop no longer segfaults: a quit was
  being read as a drop, which auto-reconnected and ran a pool-thread deferred call into an engine that
  was already gone.

---

## Roadmap — open work

### Distribution — what is left

The launcher, the game server's self-update and the release pipeline are shipped (above), and CI
re-proves the mechanics on all three desktop OSes whenever they change. What remains is human eyes and
signing.

- ☐ **By hand on a real machine** — macOS Dock/focus while the launcher is resident, Windows
  SmartScreen + taskbar behaviour, a Linux desktop AppImage. CI installs and updates headlessly on all
  three; nobody has watched a real desktop do it.
- ☐ **Signing** — Developer ID + notarization, Azure Trusted Signing. Both are already wired and
  switch on with secrets ([`docs/RELEASING.md`](../docs/RELEASING.md)).
- ☐ **REPAIR INSTALL** in the launcher — today it links to the releases page.
- ☐ **`:latest` can outrun the release** — `server-image` (and so GHCR `:latest`) depends on
  `package-server` + `server-e2e` but **not** on the three client `package` jobs, so a client
  packaging failure leaves `:latest` pointing at a version that has no published GitHub release.
  One `needs:` line, or push `:latest` only from `publish`.
- ☐ **The post-publish lobby redeploy is de facto optional** — verified live after v0.0.15: the
  deployed lobby reports `baked: 0.0.14` against `confirmed: 0.0.15`. `aspire do deploy-lobby` was
  never run and the `ReleaseWatcher` poll covered for it. Either wire the version into the release
  workflow or demote the step in [`docs/RELEASING.md`](../docs/RELEASING.md) so the documented
  process matches what actually happens.
- ☐ **An empty server browser should explain itself** — the lobby filters listings by protocol and
  the client refuses a skewed server, so a player who dismisses the update nudge sees an empty list
  with no stated cause. The lobby already knows both numbers; this is a cheap client-side fix and it
  blunts the sharpest edge of every future protocol bump.

### Stage 3 — Combat feel & depth

Richer dogfighting on shipped systems. New content is priced + gated by construction and authored in
YAML, so it lands in the existing seams without rework.

- ◐ **Turrets** — allow players to mount turret endpoints while a ship that supports turret
  hardpoints is in-base ('load up' the turrets).
  - ✅ **Slice 1 — hangar crews + ride-along** (2026-09-13, PR #84, protocol 41): authored
    `kind: turret` stations (bomber ×2, Devastator ×4), captain-side per-station gun assignment,
    CREWED SHIPS seat claims from the hangar (docked captains only), per-team crew stream, gunners
    ride along after launch (camera follows the captain, gunner HUD strip). Mechanics + file map:
    [`GLOSSARY.md` → *Crew / Turret Station*](../GLOSSARY.md); suites `tests/CrewTest`,
    `tests/CrewStoreTest`.
  - ✅ **Slice 2 — aim + fire** (2026-09-13, PR #84, protocol 42): the gunner aims freely
    inside a 105° cone around the station's zenith (mouse gimbal, gun cam at the hardpoint with an
    inside-the-turret zoom that hides the ridden hull,
    centre reticle that warns at the arc edge), fires on LMB/`fire_primary`; the server clamps the
    held aim, fires the station's gun on its own cadence and credits hits to the GUNNER; every
    client in range sees the aim (procedural barrel) and the bolts via `MsgTurrets`. Shared rule:
    `shared/TurretAim.cs`. Follow-up (same day): Tab targeting + lead indicator for gunners, gun cam
    = pure free look (`TurretLook`, arc fence only), gunners eject into pods on the captain's death,
    a clean dock keeps the crew seated (joinable again while docked), and a captain who LEAVES (or
    whose reconnect grace expires) hands the launched hull to the lowest-slot gunner rather than
    taking it with them (`Simulation.TryPromoteGunner`). Aim made CLIENT-AUTHORITATIVE
    2026-09-13 (the traversing gun was "very laggy and difficult to control"): `MsgTurretInput`
    carries the ACTUAL aim, the server only arc-clamps it, and the per-station `slew-deg` is now just
    the client's cap on how fast the look/gun may turn (default 69°/s, Devastator 45; no wind-up).
  - ☐ **Slice 3**: mid-flight boarding (today boarding is DOCKED-ONLY — `Simulation.Crew.cs:15`),
    gunner reconnect grace, salvage for turret guns, gunner K/D on the scoreboard readout. *(Slew
    feel is no longer open: the per-station knob shipped and the values have since been retuned —
    `world.yaml` `turret.default-slew-deg` 180, Devastator stations 110.)*
  - ☐ If pigs are active, and a player is docked, the bomber pig should not launch until all
    players either undock or at least one player joins as a gunner (take control of a turret).
    *Note: `Simulation.Pig.cs` has **no** suite and every existing suite sets `PigsEnabled=false`
    for determinism — this is the first work to land on that surface.*
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
  - Should server-force a decloak the instant a ship ripcords (Allegiance drops the cloak on
    ripcord, `cloakIGC.cpp`) — not wired: the equipment PR (2026-09-27) shipped the cloak toggle
    itself but not this interaction, since Ripcord doesn't exist yet either.
  - *Ripcord is warp-with-a-countdown-and-an-interrupt, and `Simulation.Warp.cs` has no dedicated
    suite — a warp-transition suite is the natural precursor.*
- ☐ **[M] Per-ship part restrictions** — let YAML flexibly restrict which guns and missile racks fit
  which ships, e.g. scouts can't carry bomber missiles and the nanite is scout-only. Today the
  weapon's CATEGORY is the only gate (`HardpointDef.MountAccepts`, `shared/Defs.cs:97`: any gun fits
  any gun mount, any rack any missile mount), so a Scout can mount the SRM Anti-Base rack (mass 4
  inside its 5 free payload, no tech) and fly the match-ending ordnance on the fastest hull, and any
  gun-mounted hull can carry the nanite.
  - Suggested shape: named **mount families**, Allegiance's part masks with names instead of bits.
    In IGC a gun fits a hardpoint when `weapon.partMask & hardpoint.partMask` is non-zero. Here each
    part lists `families:`, a hull lists what its mounts `accept:` (a hull-wide default plus
    per-hardpoint overrides), and a part fits when the two intersect. Unauthored = today's open
    behaviour, so hulls opt in one at a time; stock values come from the IGC (`weapons.yaml` already
    records each gun's mask).
  - One rule for every consumer: the hangar (`LoadoutState.Compatible`), `ResolveLoadout`, salvage
    pickup (a gun needs a *compatible* empty mount), turret stations (`ResolveTurretWeapon`), and
    ContentValidator (refuse a tier successor that drops a family its predecessor had, or an
    authored default that doesn't fit its own mount). Families ride `MsgDefs` (one protocol bump).
  - ✅ **Equipment slots already got their own, narrower restriction** (2026-09-27, branch
    equipment-energy-ammo — see *Shields & afterburners as equipment* above): a hull's
    `allowed-parts` (keys `shield`/`afterburner`/`cloak`) lists exactly which parts each slot takes,
    with a listed part's successor chain implicitly allowed. That is the same per-hull `pmEquipment`
    replacement this item used to flag as unused (`Hull.cs`) — it is used now, but only for the
    three equipment slots. Guns and missile racks are UNCHANGED: still gated by mount CATEGORY only,
    still this item's open scope.
  - *Replaces the open mount model (`launchers.yaml` note D3, reworded 2026-09-27); LoadoutTest
    scenario 2 (a Quickfire rack on the Scout) pins today's behaviour and flips to a reject.*
- ✅ **[L] Shields & afterburners as equipment** (2026-09-27, branch equipment-energy-ammo). Shield
  and afterburner are per-ship equipment parts picked in the hangar, not fixed hull stats: a hull's
  `allowed-parts` declares which slots it has (a listed part's successor tiers are implicitly
  allowed) and `preferred-parts` sets each slot's buildable default. 12 new research lines — Sm/Med/
  Lrg Shield 2 and 3, Booster 2 and 3, Lt Booster 1 and 2, Crs Booster, Hvy Booster — upgrade the
  default live via the shipped weapon-tier seam (`shared/EquipmentTier.cs`, `obsoleted-by-techs` +
  `successor-part-id`). Stock numbers are Allegiance-faithful (PCore014, Iron Coalition): shields are
  smaller buffers with slow continuous regen (Sm Shield 1 = 51 at 0.69/s, replacing the old flat
  60-at-8/s-after-3s); the Lt Interceptor has NO shield slot at all (IGC `pmEquipment` 0); boosters
  are much stronger and now finite (Enh Fighter boosted top speed ~140 → ~247 u/s), with no in-flight
  fuel regen (`ab-fuel-recharge: 0` everywhere — Allegiance never refuels in flight). Resolved
  model/runtime mismatches: afterburner `max-thrust` = IGC maxThrust ÷ 30, the same scale as hull
  `thrust`; `fuel-consumption` = fuel/second at full burn; the shield's `recharge-delay` is a runtime
  extension (0 = Allegiance's continuous regen). Mechanics + file map:
  [`GLOSSARY.md` → *Equipment (per-ship slots)*](../GLOSSARY.md), *Shield*.
  - Not done: no hangar part-mesh preview yet (`ui/LoadoutPreview.cs` covers hulls/guns, not
    equipment); PIGs/miners/constructors keep spawning with `Shield = 0` (unchanged — MiningTest
    §29 depends on it); equipment doesn't drop as salvage (see *Salvage follow-ups* below).
- ☐ **In-flight loadout management** — equip and dequip slots, manage inventory, etc. while flying.
  - Some equipped items should have a signature modifier (e.g. shields add to the ship's signature)
    and can be dequipped.
  - Some equipped items can have special in-flight effects when activated (cloak).
  - ✅ Ship 'energy' concept; cloak uses energy (2026-09-27, branch equipment-energy-ammo — shipped
    as part of *Ammo & energy* below: `max-energy`/`energy-recharge-rate` per hull, the cloak's own
    drain/ramp). Still open here: EQUIPPING/DEQUIPPING a slot mid-flight — a pick is resolved once at
    spawn; the cloak's in-flight TOGGLE shipped, but swapping which shield/afterburner/cloak is
    mounted still means a re-dock.
  - *Half-plumbed already: `ShipSim.SigBias` exists as the live per-ship equipment/loadout/ability
    seam and already feeds fog/vision (now re-seeded live on every equip/re-equip), so the
    signature-modifier half has a home.*
- ✅ **[L] Ammo & energy** (2026-09-27, branch equipment-energy-ammo). The resource pressure behind
  Allegiance's sortie → dock → rearm rhythm: PW guns (Gat/Mini-Gun/AutoCan) spend ammo from a shared
  hull magazine (`max-ammo`), the ER Nanite and an engaged cloak drain a recharging energy pool
  (`max-energy`/`energy-recharge-rate`, Iron's ×1.2 now live), and ammo packs ride the cargo hold the
  way fuel pods do (cargo-id 6, auto-load when the magazine runs dry). HUD ENRG/AMMO arcs and a
  WeaponsPanel ammo row report NO ENRG / NO AMMO / LOADING; PIGs whose guns run dry fly home and dock
  to rearm (`Simulation.Pig.cs`'s `TryRearm`/`PigKindRearm`) — the dock IS the rearm, since the
  relaunched hull is fresh. Client prediction is EXACT: `shared/ShipResources.cs` is one shared rule
  stepped identically by the server, the owner's `ResourceMirror`, and a remote's replay, with energy
  on the wire as raw f32 so the fire gate never flips at a fractional value. The ER Nanite now costs
  60 energy a shot at our cadence, against the Scout's 1440-point pool (Iron ×1.2) recharging 60/s:
  about 24 s of flat-out healing, then half rate. Mechanics + file map:
  [`GLOSSARY.md` → *Resource Rule (`ShipResources`)*](../GLOSSARY.md), *Energy Pool*,
  *Ammo Pool & Ammo Pack*, *PIG Rearm*.
  - Not done: the ER Nanite's BASE-repair half (healing a station, not just a ship) stays under
    *Station shields + repair* below; EW-tagged guns (EW Sniper / EW Utl Cannon) are still unported
    — see *Stealth fighters & bombers* next.
- ☐ **[L] Stealth fighters & bombers** — Iron's Stealth Fighter → Adv Stl Fighter and the Stealth
  Bomber (`wc_icsf`, converted in `pick-assets/wc_icsf.glb`), flying the EW Sniper / EW Utl Cannon
  guns and the higher cloak tiers.
  - ✅ **The cloak mechanic itself shipped** (2026-09-27, branch equipment-energy-ammo, with *Ammo &
    energy* above): toggle (`toggle_cloak`), ramp, energy drain, signature × (1 − cloaking) applied
    after the fog clamp, and a shimmer for every viewer — see
    [`GLOSSARY.md` → *Cloak*](../GLOSSARY.md). Carrier: **Sig Cloak 1 on the Scout**, a DELIBERATE
    STAND-IN — verified against PCore014, no ported Iron combat hull can cloak (Iron Scout id 410
    and Lt Interceptor id 415 both have `pmEquipment[Cloak] = 0`; only the unported Stealth Fighter
    can), so the Scout holds the slot until a real stealth hull lands.
  - Still open: the Stealth Fighter / Adv Stl Fighter / Stealth Bomber hulls themselves, the EW
    Sniper / EW Utl Cannon guns, Sig Cloak 2/3 and Hvy Cloak 1/2 (all gated on the Tactical base,
    which isn't ported — the Silicon and Uranium special rocks it could build on are seeded but
    unused), and deciding the Tactical gate itself. Audio (an SFX cue, and any Allegiance voice
    line, for cloak engage/disengage) is also not wired yet — tracked with the existing voice-line
    decision under *Spatial audio polish* (#105) below.
  - *`ShipSim.SigBias` is no longer a seam reserved for later — `Simulation.Equipment.cs`'s
    `ApplyEquipment` now re-seeds it live from the hull + every equipped part's signature every time
    a ship (re)equips. Vision multiplies each target's captured signature
    (`Simulation.Vision.cs`), so fog decides who sees a cloaked ship without new vision code; the
    equipment PR's `tests/FogTest` additions cover the eyeball tier and the cloaked-after-clamp case.*
- ☐ **[M] Damage types vs armor classes** — Allegiance's damage model: every bolt, missile and mine
  carries a damage type, every hull, shield and station a defense type, and a damage-constant table
  (up to 20 × 20) scales each hit (`GetDamageConstant`, `Allegiance/src/Igc/igc.h:2751`). The table
  is how Allegiance tunes who can hurt what: anti-base ordnance vs stations, light guns vs heavy
  hulls, how much a nanite bolt repairs.
  - Import the table from `PCore014.igc`'s constants record, and each item's damage/defense type
    with its part data; one lookup in `ApplyDamage` (`Simulation.cs:157`) and `ApplyBaseDamage`
    (`Simulation.Firing.cs:339`).
  - Precursor to capital ships, carriers and *Base capture*'s Troop Transports balancing sanely.
  - *The fields exist on the factions model (`DamageType`: `Projectile.cs:28`,
    `Expendables/Missile.cs:46`, `Expendables/Mine.cs:16`; `DefenseType`: `Hull.cs:71`,
    `Parts/Shield.cs:13`, `Station.cs:45/48`), but no YAML sets them and the sim reads none. Base
    damage is a `can-damage-base` bool (`shared/Defs.cs:236`) and the shield interaction a
    per-weapon `shield-damage-multiplier`; both would fall out of the table.*
- ✅ **[XS] Name the focused target** (2026-09-26, issue #97, PR #99) — shipped as the richer panel
  rather than the one-label fix: the **Target Pane**, docked right of the minimap, renders the
  Tab-focused target's real model beside a per-kind readout — a ship's pilot, class, hull/shield bars,
  range/speed/closing rate and lock progress; a base's owner, hull and dock clearance or siege lock; an
  asteroid's resource, ore and miners. Detail: [`GLOSSARY.md` → *Target Pane*](../GLOSSARY.md). Not
  done, and only if still wanted: the F3 map, where the pane hides and the focused contact is still the
  one ship without a type caption (`if (f3 && !focused)`, `TargetMarkers.cs:1050` and `:1061`).
- ☐ **[S] Different gun effects** — per-weapon bolt looks (today `projectiles.yaml` only sets
  `bolt-radius` / `bolt-length`).
  - i.e. minigun green, different looking bolt
  - ER nanite shoots glowing blue, thin, toruses
  - *`BoltRenderer` already selects material per weapon (heal vs normal, plus a heal-specific impact
    spark), so a `bolt-color` field rides the existing `tech-tree-content` path rather than needing a
    rendering rework.*
- ☐ **Salvage follow-ups** — hangar fold of salvaged guns into `LoadoutState` (keep-on-dock; today
  the hangar re-equips from `LoadoutState` and salvage is lost on dock); partial stack pickup;
  magazine cap + auto-reload from stowed same-rack stacks; PIG pickup; shootable items; salvage
  economy (sell stowed rounds at dock); a `missile-pickup-match: rack|line` knob; promote the
  stow-only authored-id derivation (now the `AuthoredIds` local function in `server/Net/Frames.cs:637`,
  not `Protocol.BuildShipLoadouts`) to a `Simulation` seam; equipment (shields/afterburners/cloaks)
  as salvage — a destroyed ship's equipped parts don't drop today, only its guns/racks/cargo do.

### Stage 4 — Strategy depth (Allegiance core)

The economic + RTS loop. Largely sequential; each item builds on the shipped money + gating and the
YAML pipeline.

- ☐ **[M] Station shields + repair** — stations get a regenerating shield over their hull, plus
  hull regeneration and nanite repair, as in Allegiance (per-station `armorRegeneration` /
  `shieldRegeneration`, `Allegiance/src/Igc/igc.h:2654-2655`; a nanite bolt heals a station through
  negative damage, `Allegiance/src/Igc/stationIGC.cpp:147`). The precursor to *Base capture*, whose
  gate is a downed shield.
  - Per-station `max-shield` / `shield-regen` / `hull-regen` in `stations.yaml`; Iron's
    `max-shield-station` ×1.15 goes live.
  - The Target Pane's base readout and the base markers gain a SHLD bar beside HULL.
  - **Nanite on bases** — a friendly ER Nanite bolt repairs a station's hull (Allegiance exempts
    repair from its station friendly-fire guard, `Allegiance/src/Igc/stationIGC.cpp:128`). It
    needs no station shield and no wire change (base health already streams), so it can ship
    first. Today bolts consider bases only for `can-damage-base` weapons and only ENEMY ones
    (`Simulation.Firing.cs:128-133`), so a healing bolt needs a same-team base branch plus a base
    heal, clamped to max hull, beside `ApplyHeal` (`Simulation.cs:190`).
  - *Today `max-shield-station` is resolved but INERT (`iron-coalition.yaml:21`). Base hull only
    changes on damage, match reset and upgrade rescale (`Simulation.Firing.cs:343`, `World.cs:1300`,
    `Simulation.Research.cs:460`). `ApplyBaseDamage` (`Simulation.Firing.cs:339`) needs the
    shield-first split `ApplyDamage` already does for ships, and the base row on the wire needs a
    shield value (one protocol bump).*
- ☐ **[L] Base capture (Troop Transports)** — Allegiance's comeback route: a hull with the `board`
  capability that docks at an ENEMY station whose shield is below the core's downed-shield constant
  takes that station for its team (`Allegiance/src/Igc/shipIGC.cpp:849`; the ownership flip is
  `FedSrv/fsship.cpp:577`). Iron's boarders are the Troop Transport → Hvy Troop Transport
  (`utl27b`, converted in `pick-assets/utl27b.glb`).
  - Needs *Station shields + repair*: with no station shield, nothing is ever "downed".
  - Decide: whether the garrison is capturable (in IGC it is), whether taking a team's last
    garrison ends the match (the win check `TeamHasAliveWinBase`, `Simulation.Firing.cs:320`, keys
    on team + health, so it would), and the Troop Transport's research gate (IGC: Expansion, which
    isn't ported).
  - A captures column on the scoreboard and a notice to both teams.
  - *Every forward base already authors `capture` (`stations.yaml:76`, `:97`, `:121`, `:162`; the
    garrison authors only `start`/`restart`), but nothing reads it. Enemy bases are fully solid
    today (the dock-face test only skips your OWN base, `shared/Collision/Collide.cs`), so a boarder
    needs an enemy-door path. `BaseSite` is an immutable record (`World.cs:89`): replace it in place
    so the append-only base indices hold, and move research-by-base, queued constructors, launch
    sites and both teams' fog reveal with it.*
- ☐ **[M] Stat-boost research** — developments that carry team-wide stat multipliers instead of
  (or as well as) tech unlocks: Allegiance's "GA:" research. Iron has 36 across Expansion, Tactical
  and Supremacy — ship speed, acceleration, agility, hull, shield, sensors, signature and energy;
  PW/EW range and damage; missile tracking and damage; He3 yield and mining speed; station hull and
  shield; ripcord time. The ported Supremacy's set (Ship Hull, Ship Shield, Ship Sensors, Missile
  Damage) is the natural first slice.
  - Each boost needs a consumer: 9 of the 25 `GameAttribute`s have one today (gun and missile
    damage, station armor, signature, mining rate and capacity, plus — as of 2026-09-27, branch
    equipment-energy-ammo — `MaxEnergy`, `MaxShieldShip` and `ShieldRegenerationShip`, consumed by
    the equipment PR's energy pool and per-ship shield). `MaxShieldStation` still resolves but is
    unconsumed (no station-shield model — see *Station shields + repair* below); the other 14 have
    nothing. Allegiance's own "GA: Ship Shield" research (a team-wide shield multiplier) belongs
    here too: the multipliers are wired end-to-end, but no stock development grants them yet.
  - Team attributes now STREAM to clients (`TeamStateRecord.Attributes`, protocol 44 — see
    [`GLOSSARY.md` → *Team Attribute Vector*](../GLOSSARY.md)) generically, not just resolved
    server-side, so any future consumed attribute reaches the client for free over the same wire.
  - Flight boosts (MaxSpeed, Thrust, TurnRate, TurnTorque) change `shared/FlightModel.cs` inputs,
    so the client's prediction must apply the same multiplier or every boosted ship rubber-bands.
  - *Half the pipeline exists: `RecomputeTeamAttributes` (`Simulation.cs:1504`) resolves faction ×
    completed developments into `World.TeamAttr` at match start and on every research completion;
    `DevelopmentDef.Attributes` already streams (`shared/Defs.cs:505`) and the Research tab renders
    it as ±% (`ResearchTab.cs:651`). Gotcha (still open): "completed" is inferred from owned techs
    (`Simulation.cs:1516`, `GrantedTechs.Count > 0`), so a pure stat development that grants no tech
    would never count — a stat-only development MUST also grant a marker tech. Track completed
    developments explicitly, in the sim and on the client.*
- ☐ **[L] Update plan to include multiple teams** — each map only supports a certain number of
  teams, so this is a constraint that must be reflected in the plan. Plan should include a richer
  'game lobby' (as opposed to server lobby) experience; allowing users to select or join teams before
  the match starts. First person on a perspective team (and not on NOAT/not on a team) can configure
  the number of teams (2-6 for now). *Concentrated behind `World.MaxSupportedTeams = 2` (a const with
  a fail-fast validator), but it fans out into the lobby, the per-team fog streams, the PIG team loops
  and the win condition — correctly an [L], not a const bump.*
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

### Team comms

In-game communication beyond free-text chat, which today is all or team only (`ChatMessage.Scope`,
`shared/Net/Messages.cs:131`). Independent of the stages.

- ☐ **[M] Quick-chat macros** — Allegiance's quick-chat: a hotkey opens a menu tree of canned calls
  (cover me, need rescue, defend base, found enemy miner, base under attack …) that sends the line
  plus its radio voice clip and can attach a target — the sender's current target, the enemy
  damaging them most, or the sender themself — so a call doubles as a ping or a light order
  (`Allegiance/src/WinTrek/console.cpp:1534`, `SendQuickChat`).
  - A new client→server frame (macro id + attached target) relayed to the team or everyone; the
    server checks the attached target against the sender's team fog, as `MsgOrder` does (no
    wallhack).
  - Shown as a chat line plus a short-lived HUD / F3 marker on the attached target; a `quick_chat`
    action in `InputBindings.cs`.
  - Voice second: the clips are catalogued (`audio-index.md` → *Player & comms voice-over (radio
    callouts)*, 236 of them), but shipping them waits on #105's voice-line decision and the LICENSE
    item under *Cross-cutting*. Text + marker first.
- ☐ **[S] `@user` direct messages (whispers)** — `@name text` in the chat box reaches one pilot
  (Allegiance's `CHAT_INDIVIDUAL`, `Allegiance/src/Igc/igc.h:2733`), with name completion from the
  roster (`GameNetClient.LobbyPlayers`), a distinct whisper style, and `/r` to reply to the last
  sender.
  - Server: parse the `@` prefix beside the existing `/` intercept (`server/Net/ClientHub.cs:901`),
    resolve the name against the roster (unknown or ambiguous → a system notice), then deliver to
    the target and echo to the sender. `ChatRelayMessage.Scope` gains a direct value (today 0 all /
    1 team / 2 commander directive, `shared/Net/Messages.cs:351`): one protocol bump.
  - Decide whether whispers may cross teams, and whether NOAT pilots can whisper players.
  - The same addressing later serves fireteam channels (deep backlog, *Fireteam support*).

### Engineering safety net

**There is no CI for ordinary work.** The three workflows are a tag-only release pipeline plus two
path-filtered distribution dry-runs; a PR touching `server/Sim/**`, `shared/Net/**`, `client/**` or
`server/Content/**` triggers *nothing* — not a build, not the 31 suites, not csharpier, not the Godot
export. `build-godot-client.yml` was deleted as a stale hand-synced copy (`54069b0`) and nothing
replaced it. The first time such a change is compiled-as-exported, asset-verified and packaged is
when someone pushes a version tag — which is also the moment the fleet begins auto-updating onto it.
Everything is green right now (0 build warnings, 30 PASS / 0 FAIL / 1 Docker-skip in ~160 s), so this
is the cheapest possible moment to lock the baseline in.

- ☐ **[S] A pull-request CI workflow** — `dotnet build wivuullegiance.slnx`, `scripts/run-tests.ps1`
  (PublicLobbyTest stays Docker-gated or gets its own job), `dotnet csharpier check .`, the Godot
  client build/import, and a Linux export run through `--verify-assets`. Every piece already exists
  as a script; nothing has to be designed. It is purely additive, starts green, and makes every other
  item on this page cheaper to attempt.
- ☐ **[XS] `tests/LauncherTest` is not in `wivuullegiance.slnx`** — 2,750 lines, the suite that proves
  the launcher can update itself, and a solution-wide build silently skips it. It only runs because
  `run-tests.ps1` globs directories. Fix this *before* the CI job inherits the same blind spot.
- ☐ **[XS] csharpier drift** — `dotnet csharpier check .` exits 1 on `server/SimServer.csproj` and
  `tests/CommanderTest/Program.cs`. Clear it in its own standalone commit (the rule is: format only
  touched files, never blanket-format inside a feature) so enforcement lands green.
- ☐ **[XS] Three docs assert "there is no CI"** — `scripts/run-tests.ps1:3`, `scripts/README.md:33`
  and `CONTRIBUTING.md:44`. `scripts/README.md` additionally says "some suites are known-red", which
  the 30/30 green run disproves. `CONTRIBUTING.md` is agent-required reading, so it actively
  misinforms.
- ☐ **[M] The two real coverage holes** — `Simulation.Pig.cs` has no suite at all (every existing
  suite sets `PigsEnabled=false` for determinism, and PIGs default OFF via `SIM_PIGS`), and
  `Simulation.Warp.cs` has no dedicated suite. Both are precursors to named roadmap items above (the
  PIG bomber launch gate; Ripcord). Separately, ~48.5k lines of Godot client C# have no automated
  coverage of any kind — only 8 client files are linked into any suite.
- ☐ **Known accepted risk, not a bug** — `server-update-dryrun.yml`'s PR path filter deliberately
  excludes `server/Sim/**`, `server/Net/**` and `shared/**`, yet `release.yml` calls it as a required
  gate. So the most likely way to break a release is ordinary gameplay work, and the feedback arrives
  only after the tag is pushed. Recorded here so it is a choice rather than a surprise.

### Cross-cutting / opportunistic

Not stage-bound — done when convenient or when a stage needs them.

- ✅ **[XS] The client shows its own version** (2026-09-27) — `BuildInfo.Display` ("v0.0.15 ·
  osx-arm64" / "DEV BUILD · …") sits at the foot of the in-game settings dialog's tab rail; the
  launcher's settings overlay repeats its footer line beside CLOSE.
- ☐ **[XS] The repo is PUBLIC with no LICENSE** — `gh repo view` reports `licenseInfo: null` and there
  is no `LICENSE*` file, while the project ships public binaries. Also the gate on any decision about
  redistributing sourced audio assets.
- ☐ **[S] No music, and no music bus** — `client/default_bus_layout.tres` is Master/SFX/Engines/
  Ambient/UI and none of `SfxManager`'s 27 `SfxId`s is musical.
- ☐ **[S] Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar
  mapping; explore baking and in-engine parallax/height maps.
- ◐ **[S] Spatial audio polish** (issue #105) — eleven cues are in, each Allegiance's own sound for
  the event: dock, warp, shield down/up, hull critical, out-of-bounds alarm, target pick, chat, payday,
  action denied, match start. Launch is hooked but has no sound selected (Allegiance's 18 s undock
  swell was dropped). Open, with hook anchors on #105: a launch sound, victory/defeat (no asset), own
  death / pod eject / base and miner losses (Allegiance voiced these — decide whether the client gets
  SAL voice lines), and finer mix tuning. Asset catalogue: [`audio-index.md`](../audio-index.md);
  how a cue is wired: [`GLOSSARY.md` → *Cockpit Cue*](../GLOSSARY.md).
- ☐ **[S] The nebula differs between machines** — each sector's nebula is generated in-shader from
  `fract(sin(...))` float hashing (`client/scripts/Starscape.cs:158`), which is not stable across GPU
  vendors and drivers, so two players in the same sector can see different clouds. The sector
  environment row itself now repaints correctly when it arrives (PR #87); this is the remaining half.
- ☐ Look for opportunities to utilize native `Vector3` and SIMD for performance improvements.
  *The reason the codebase avoided `System.Numerics` — bit-identical math between a wasm SpacetimeDB
  module and the mono client — no longer applies: that module is deleted and both consumers are .NET
  (`FlightModel.cs`'s header still cites it, and two `.PLAN` files that no longer exist). Determinism
  across machines for PIG/prediction agreement still needs its own argument before anything changes.*
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
- ☐ **[S] Explicit host transfer** — the game server's host is implicit: `ClientHub` seeds `_hostId`
  to the first pilot to connect (`ReceiveLoop`, the `MsgHello` case) and, when the host drops, hands
  it to the lowest remaining client id (`HandleConnection`'s disconnect path). A host cannot pass the
  role deliberately and nobody can pick one, so map control just follows join order. Wanted: an
  explicit host-selection/transfer frame plus the lobby UI for it.
  **The arena-rebuild seam this item used to be blocked on is shipped** — `Simulation.BuildMatchWorld`
  (`Simulation.cs:632`) → `StartMatch` (`:1342`) swaps the live `World` from the selected map and
  `OnMatchStart` (`ClientHub.cs:431`) re-`Welcome`s every client. `MsgSetMap` still only advertises
  the pick, but the pick *takes effect at the next match start*, so what is left here is a contained
  frame + UI, not a platform change.
- ☐ **[S] Cleanup follow-ups (from the 2026-09-09 pass)** — `Simulation.Docking.cs` partial for the
  ~800-line `DockApproach` block; ClientHub receive-side/snapshot-AOI extraction (needs an interface
  design, not a pure move); `ApDockCreepFacingDot` / `ApDockAlignTimeout` / `ApDockCreepTimeout` still
  compile-time in `Simulation.cs` (seconds-authored keys if they ever need tuning). ~73 hardcoded
  colours remain client-wide (52 outside `ui/`, mostly 3D VFX which may stay). *The design-token
  second group is effectively done — the `new Color(...)` calls left in those four files wrap a token
  or a team colour with an alpha rather than hardcoding a hue.*
- ☐ **[M] Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
- ☐ **[L] Replay system** — tick log or time-travel query playback.
- ☐ **[M] Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
- ☐ **[M] Mutinies** — a player can stage a mutiny on a team; all other players (except the
  commander) can vote to depose the commander. If the vote passes, the mutineer becomes the new
  commander.
