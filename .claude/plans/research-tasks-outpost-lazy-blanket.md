# Research / Tech-tree tasks

## Context

The tech-tree already has most of its engine: per-instance station upgrades
(`upgrade-scope: single`), tech-gating via `RequiredTechIdx`, `IsHealing` weapons, and a
streamed def catalog. But several player-facing gaps remain in the **Research tab** and the
**Hangar/loadout** UI, plus a reported bug that the Enhanced Fighter can't be launched after a
Supremacy Center is built. This plan closes the 7 gaps. Most are UI + wire-surfacing that reuse
existing patterns; two are new content (a light Outpost tier, weapon-tier obsolescence); one is a
bug hunt.

Key facts established during exploration:
- Wire version is a single constant: `Wire.ProtocolVersion = 34` (`shared/Net/Wire.cs:82`,
  aliased `Protocol.Version`). Any change to the defs stream requires bumping it → **35**. (The
  "proto 36–42" numbers in code comments are an internal *content-schema* count, not this wire
  version.)
- `WriteTechList`/`ReadTechList` (u8 count + n×u16) already exist on both peers — adding a
  tech-list field to a def mirrors the weapon/dev/station pattern exactly.
- Station upgrades run entirely through `MsgResearch` (no separate "upgrade base" message). The
  physical swap is per-instance (`ApplyStationUpgrades` single-scope), the tech grant is team-wide
  — this already satisfies "not centralized."
- Research tab is the single research screen (`ResearchTab.cs`); there is no separate "supremacy
  center" UI. Base *type* is read in `CommandSidebar.Refresh` then dropped before reaching the tab.
- New developments must be **appended at the end** of `developments.yaml` (StrategyTest drives
  research by dev index; existing indices must stay stable — the file already documents this).

---

## Shared wire changes (do first — several tasks depend on these)

1. **Bump `Wire.ProtocolVersion` 34 → 35** (`shared/Net/Wire.cs:82`).

2. **Stream hull tech-gates** (unblocks Task 3 + Task 6). `ShipClassDef` has no
   `RequiredTechIdx` today; hull gating is consumed server-side only.
   - Add `public ushort[] RequiredTechIdx = System.Array.Empty<ushort>();` to `ShipClassDef`
     (`shared/Defs.cs:66`).
   - Project it in `FactionsContentProjection.cs` where the hull → `ShipClassDef` is built, using
     the existing helper: `RequiredTechIdx = TechIdxArray(hull.RequiredTechs, techIdx)` (mirror
     the dev/weapon calls at `FactionsContentProjection.cs:126/280`).
   - Write it at the **tail of the ship block** in `Protocol.BuildDefs` via
     `WriteTechList(w, sc.RequiredTechIdx)`; read it at the tail of the ship block in
     `GameNetClient.ApplyDefs` via `RequiredTechIdx = ReadTechList(r)`.

3. **Stream weapon obsolescence + successor** (unblocks Task 2). `WeaponDef` has neither today.
   - Add to `WeaponDef` (`shared/Defs.cs`): `public ushort[] ObsoletedByTechIdx = ...Empty()` and
     `public uint SucceededByWeaponId = uint.MaxValue;` (`uint.MaxValue` = `NoWeapon` = none).
   - Author in `weapons.yaml`: on each non-top tier, `obsoleted-by-techs: [<next-tier-tech>]` and
     `succeeded-by-weapon: <next-tier-weapon-id>`. E.g. `gat-gun-1`: `obsoleted-by-techs: [gat-2]`,
     `succeeded-by-weapon: 1`; `gat-gun-2`: `[gat-3]` / `2`. Same for mini-gun (9→10→11),
     autocan (12→13→14), nanite (15→16→17).
   - Project (`FactionsContentProjection.cs` weapon block), write (`Protocol.cs:1398` area, tail of
     weapon block) and read (`GameNetClient.cs:1707` area, tail of weapon block).

---

## Task 1 — Outpost → Hvy Outpost as a per-instance research upgrade  *(new content)*

Today `outpost` (base-type-id 1) is already "Outpost (Hvy)", constructor-built directly, with no
tier below it. Add a light tier and make the current heavy one its upgrade successor — mirroring
`garrison → garrison-str`. **Content-only**; the upgrade engine (`TriggeredUpgrades` /
`ApplyStationUpgrades` / successor matching) is generic and needs no code changes.

In `server/Content/core/`:
- `techs.yaml`: add tech `outpost-hvy`.
- `stations.yaml`: add a new **light** base `outpost-lt` (next free `base-type-id`, e.g. 7):
  constructor-built on `regolith`, cheaper price + lower `max-armor`/`radius`, `abilities:
  [land, repair, capture]`, `successor-station-id: outpost`. Reuse the `ss90` mesh initially (like
  `garrison-str` reuses the garrison mesh — a distinct lighter mesh is a later art pass).
  Then edit the existing `outpost` (type 1): add `required-techs: [outpost-hvy]` and **remove**
  `build-on-rock-class` (it's now reached by upgrade, not built directly).
- `developments.yaml`: **append at the very end** a `dev-upgrade-outpost` in `group: UPGRADES`,
  `upgrade-scope: single`, `required-capabilities: [base]`, `granted-techs: [outpost-hvy]`.

The successor match (`Simulation.Research.TriggeredUpgrades`) resolves `outpost-lt → outpost` from
`dev-upgrade-outpost`'s granted tech vs the heavy outpost's `required-techs`, so authorizing the
dev at a light outpost swaps only that instance. Verify `CoreValidator` still passes (the new base
type + tech + dev must all resolve).

> Note: adding a dev shifts nothing above it (appended last), but adding a **base-type-id** and a
> **hull/weapon**-adjacent tech must keep existing type-ids stable — only *append* the new type-id.

---

## Task 2 — Upgraded weapon tiers vanish from the hangar; saved loadouts auto-upgrade

Uses the `ObsoletedByTechIdx` + `SucceededByWeaponId` wire fields from Shared change #3.

- **Hide obsoleted tiers** in the arsenal: in `ShipLoadout.RefreshArsenal()`
  (`ShipLoadout.cs:630`), before the existing fit/tech filters, `continue` (fully skip — not the
  locked bucket) when `w.ObsoletedByTechIdx.Any(t => _world.TeamOwnsTech(team, t))`. So once the
  team owns `gat-2`, Gat Gun 1 disappears from selection.
- **Auto-migrate loadouts** (client): add a helper that walks the successor chain while the
  obsoleting tech is owned —
  `while (defs.GetWeapon(id) is {} w && w.ObsoletedByTechIdx.Any(owned) && w.SucceededByWeaponId != NoWeapon) id = w.SucceededByWeaponId`.
  Apply it (a) when hydrating saved overrides in `LoadoutState.Load` (`LoadoutState.cs:200`, next to
  the existing def-existence validation) and re-persist the migrated id, and (b) to the hull's
  **default mount** weapon in `RefreshLoadoutViews` so an un-customized mount also shows the current
  tier. "All loadouts reflect the upgrade."
- **Auto-migrate at spawn** (server, authoritative): apply the same successor walk in
  `Simulation.ResolveLoadout` (`Simulation.cs:1269`) to every resolved mount weapon (override *and*
  default) using the team's `OwnedTechs`. In-flight ships keep their old tier (they were resolved at
  their own launch); only the next dock+launch upgrades.
- **Payload guard**: a higher tier may have larger `Mass`. The migration must not overflow
  `PayloadCapacity` — if the successor doesn't fit, keep the current weapon (both client display and
  server resolve). Check against the existing payload validation in `ResolveLoadout`.

---

## Task 3 — Locked hull cards name the required tech (match the weapon-row pattern)

Uses `ShipClassDef.RequiredTechIdx` from Shared change #2. Today the weapon row reads
`REQUIRES <tech>` from `WeaponDef.RequiredTechIdx` (`ShipLoadout.cs:699`); the hull card shows a
generic `⚿ TECH LOCKED` (`ShipLoadout.Hangar.cs:610 ShipCard.SetGate`) because no tech name reached
the client.

- In `RefreshShipCardStates` (`ShipLoadout.Hangar.cs:149`, which has `_defs` + `_world`), when the
  gate is `Locked`, build the tech-name string exactly like the weapon row:
  `string.Join(", ", def.RequiredTechIdx.Select(t => _defs.GetTech(t)?.Name.ToUpperInvariant() ?? $"TECH {t}"))`
  and pass it into `SetGate` so the card shows `⚿ REQUIRES {techs}` (thread the string through the
  `ShipCard.SetGate` signature). Fall back to `TECH LOCKED` only if `RequiredTechIdx` is empty.

---

## Task 4 — Nanite reads as HEAL, not DMG  *(client-only, no wire change)*

`WeaponStatLine` (`ShipLoadout.cs:712`) unconditionally emits `DMG {w.Damage}`. `WeaponDef.IsHealing`
is already streamed and decoded (`GameNetClient.cs:1709`).

- Branch the readout: `w.IsHealing ? $"HEAL {w.Damage:0}" : $"DMG {w.Damage:0}"` (keep the RoF /
  speed tail). This is the single formatting site feeding both the equipped-slot sub-line and the
  arsenal rows, so both fix at once.

---

## Task 5 — Fix: can't launch the Enhanced Fighter after building a Supremacy Center  *(bug)*

Static analysis shows the unlock chain is correct: Supremacy completion →
`GrantStationUnlocks` (grants `supremacy-1`, calls `ResolveTeamUnlocks`, sets
`TeamStateChangedThisStep`) → `BuildTeamState` streams class-id 1 → client `TeamUnlocked` →
`TryReserveSpawn` allows. The constructor-built-supremacy path is **untested endgame** (per memory),
so the break is likely runtime/timing/streaming, not a visible logic error.

- **Repro via the `verify` skill** (headless server + client): buy a constructor, build a Supremacy
  on a carbonaceous rock, then inspect: does `TeamState.OwnedTechs` gain `supremacy-1`? does
  `UnlockedClasses` gain class-id 1? does a fresh `MsgTeamState` reach the client and refresh
  `_teamUnlocks`? does the hull card flip to unlocked and does `MsgSpawn` succeed? Add a focused
  server test seeding a constructor-completed Supremacy and asserting enh-fighter becomes buildable
  (fills the noted test gap).
- **Fix wherever it breaks.** Prime suspects, in order: (a) the completion actually taken isn't
  `CompleteConstruction` / doesn't hit `GrantStationUnlocks`; (b) `MsgTeamState` isn't re-sent or the
  client applies it for the wrong team so `_teamUnlocks[myTeam]` never updates;
  (c) `BuildableResolver` misses enh-fighter (e.g. the `base` capability not owned by the team).
- **Secondary (optional):** a `dev-enh-fighter` research node (append last, `required-techs:
  [supremacy-1]`, mirroring `dev-bomber`) would make the fighter *discoverable as research* — but the
  primary deliverable is the launch fix; Task 6 already surfaces the fighter under the Supremacy in
  the UNLOCKS list.

---

## Task 6 — Research UNLOCKS list names the certified hull

`ResearchTab.BuildUnlocks` (`ResearchTab.cs:547`) matches granted techs against devs / station
catalog / weapons, but a code comment there notes hulls were unnameable because `ShipClassDef`
carried no tech field. Shared change #2 fixes that.

- In `BuildUnlocks(DevelopmentDef dev)`, add a hull section: for each `_defs.BuildableShips()` whose
  `sc.RequiredTechIdx` intersects `dev.GrantedTechIdx`, add `sc.Name`. Remove the stale comment.
  Result: `dev-bomber` → "Bomber", `dev-upgrade-supremacy` → "Adv Fighter",
  `dev-upgrade-heavy-class` → "Devastator".
- Do the same in `BuildTab.BuildUnlocks(StationCatalogDef s)` (`BuildTab.cs:623`) so the
  **Supremacy** station card lists "Enh Fighter" (unlocked by the base itself, not a dev) — this is
  the discoverability half of Task 5.

---

## Task 7 — Upgrade-research gated by the selected base's type in the UI

Today ResearchTab knows only `(baseId, title, sector)` and will authorize "Upgrade Supremacy" with a
Garrison selected, relying on the server's `MsgResearch` rejection
(`Simulation.Research.cs:128-142`). Surface the check client-side.

- **Propagate base type to the tab.** `CommandSidebar` already resolves `typeId` from
  `WorldRenderer.KnownBases()` (`CommandSidebar.cs:120`) then drops it. Carry it: add the type to
  the `BaseSelected` emission / a `SelectedBaseType` field, thread through
  `ShipLoadout.OnBaseSelected` → `ResearchTab.SetBase(id, title, sector, typeId)`.
- **Derive each upgrade dev's from-type client-side** (no extra wire): for a `UpgradeScopeSingle`
  dev, find the successor base type whose `BaseDef.RequiredTechIdx` contains a tech in
  `dev.GrantedTechIdx` (the TO-type), then the FROM-type is the base whose
  `BaseDef.SuccessorBaseTypeId == TO-type`. `DefRegistry.GetBaseDef`/`AllBaseDefs` expose
  `SuccessorBaseTypeId` (`shared/Defs.cs:295`). Cache a `{fromType → dev}` map on defs load.
- **Gate the card/footer**: when the selected base's type ≠ the dev's from-type, render the upgrade
  as not-applicable (disabled footer, hint e.g. `AUTHORIZE AT <from-base-name>`) instead of a live
  AUTHORIZE. Mirrors the server guard, so no more relying on rejection.
  - Alternative (heavier): add a streamed `UpgradeFromBaseTypeId` to `DevelopmentDef` computed
    server-side from `TriggeredUpgrades`. Prefer the client-side derivation — no wire growth.

---

## Verification

- **Build + unit tests:** `dotnet build` the solution; run the dotnet suites (StrategyTest,
  ContentTest, FactionsTest, ShieldTest). Expect the 6 pre-existing content-drift failures noted in
  memory; anything else new is a regression. Confirm `CoreValidator`/`ContentValidator` accept the
  new outpost tier + tech + dev at server boot.
- **Protocol bump smoke:** a defs-stream change + version bump won't be caught by the dotnet suites
  (they don't drive the Godot client). Smoke with `--autofly` and dock to open the hangar/research
  tabs (per the missiles/protocol-bump memory).
- **Runtime (`verify` skill):** drive real server + Godot client headlessly and screenshot:
  1. Task 3/4/6: dock, open Hangar — a locked hull card reads `REQUIRES <tech>`; the ER-Nanite row
     reads `HEAL n`; open Research — a dev's UNLOCKS names the hull.
  2. Task 1/7: select a garrison, confirm only garrison-applicable upgrades are live; select a light
     outpost, authorize `dev-upgrade-outpost`, confirm only that instance swaps to Hvy Outpost.
  3. Task 2: research `gat-2`; dock — Gat Gun 1 is gone from the arsenal and a saved Gat-1 loadout
     now shows Gat Gun 2; launch and confirm the ship carries the upgraded gun; a ship that was
     already in flight still carries Gat-1 until it re-docks.
  4. Task 5: build a Supremacy Center via a constructor, then launch an Enhanced Fighter — succeeds.

## Suggested implementation order

1. Shared wire changes (#1–#3) + version bump.
2. Task 4 (trivial, client-only) and Task 3 / Task 6 (consume hull tech-gates).
3. Task 2 (weapon obsolescence: hangar hide + client/server migration).
4. Task 1 (content: light outpost tier).
5. Task 7 (base-type gating in ResearchTab).
6. Task 5 (repro + fix the enh-fighter launch bug; add the covering test).
