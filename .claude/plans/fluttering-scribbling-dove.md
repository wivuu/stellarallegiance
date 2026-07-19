# Per-base research trees (scope the RESEARCH tab by selected base)

## Context

The RESEARCH docked-screen tab currently renders **the entire development catalog** regardless of
which base is selected in the CommandSidebar. Selecting the Garrison still shows the greyed-out
Supremacy-tier items (PW Gat, MRM Seeker, etc.) even before a Supremacy Center exists. The player
expects each base to be its own research facility with **its own tree, rooted at that base** — the
Garrison shows the Garrison's research, the Supremacy shows the advanced weapons/missiles, and so on.

Research is base-agnostic today: nothing on a `TechDef`/`DevelopmentDef` (or in YAML/wire) says
"researched at base type X". The *only* base-type binding that exists is the derived from-type guard
for single-scope station-upgrade devs (`TriggeredUpgrades` / `UpgradeFromType`). However, each dev's
intended home base is **already implied by its gate** — the tech/capability a base type grants:

| Gate on the dev | Granted by | Home base family |
|---|---|---|
| `base` capability (and no base-tech) | every base (primordial) | **Garrison** (default) |
| `supremacy-1` / `supremacy-adv` tech | Supremacy Center / its upgrade | **Supremacy** |
| `garrison-str` tech | Garrison upgrade | **Garrison** |
| `shipyard-1` / `shipyard-dry` tech | Shipyard / its upgrade | **Shipyard** |
| single-scope upgrade dev | derived from-type | that base's family |

So we can **derive** each dev's home base family from data the client and server already have —
no authored field, no protocol bump. Confirmed decisions:

- **Approach:** derive from existing gating (no new authored `research-at` field, no wire-version bump).
- **Enforcement:** UI filtering **plus** server-side validation of which research may start at which base.
- **Starter research** (Mini-Gun, Mine, Counter/chaff, Nanite, Bomber — gated only on `base`): homes to
  the **Garrison family** only (user clarified "starbase" = the Garrison's upgraded tier, same family).

## The home-base-family rule (shared, mirrored client + server)

A **base family** = a root base type plus every tier reachable through its `SuccessorBaseTypeId` chain:

- Garrison (0) → Garrison Str (4) — **root 0**
- Outpost (1) → Outpost Hvy (7) — **root 1**
- Supremacy Center (2) → Adv Supremacy (5) — **root 2**
- Shipyard (3) → Shipyard Dry (6) — **root 3**

`FamilyRoot(baseTypeId)` = walk the successor chain backward to the base that is nobody's successor.

`HomeFamilyRoot(dev)`:
1. **Single-scope upgrade dev** → `FamilyRoot(UpgradeFromType(dev))` (reuse the existing derivation).
2. **Otherwise** → scan `dev.RequiredTechIdx` against a precomputed **tech→family** map; if any required
   tech maps to a family, that is the home (current content is unambiguous — a dev's base-origin
   requirements are all one family). If none map (only the `base` capability), default to **root 0
   (Garrison)**.

The **tech→family** map is built once from streamed defs:
- each base type's `StationCatalogDef.GrantedTechIdx` → `FamilyRoot(that base type)` (`supremacy-1`→2, `shipyard-1`→3)
- each single-scope upgrade dev's `GrantedTechIdx` → `FamilyRoot(UpgradeFromType(dev))` (`garrison-str`→0, `supremacy-adv`→2, `shipyard-dry`→3, `outpost-hvy`→1)

### Resulting per-base trees

- **Garrison / Garrison Str (root 0):** dev-bomber, dev-minigun-2/3, dev-nanite-2/3, dev-mine-2/3, dev-chaff-2/3, dev-upgrade-garrison
- **Supremacy / Adv Supremacy (root 2):** dev-gat-2/3, dev-autocan-2/3, dev-seeker-2/3, dev-quickfire-2/3, dev-dumbfire-2/3, dev-anti-base-2/3, dev-probe-2/3, dev-upgrade-supremacy
- **Shipyard / Shipyard Dry (root 3):** dev-upgrade-heavy-class
- **Outpost / Outpost Hvy (root 1):** dev-upgrade-outpost

## Part 1 — Client: filter the RESEARCH tab by selected base

File: `client/scripts/ui/ResearchTab.cs`

1. **Derivation helpers** (new, near the existing `UpgradeFromType` at ~line 653). Compute lazily on
   first `_Process` after `_defs` is populated and cache in fields (`_familyRoots`,
   `_techFamily`, plus a `_derivedReady` flag — the streamed catalog is stable, so build once):
   - `Dictionary<byte,byte> BuildFamilyRoots()` from `_defs.AllStationCatalog()` successor chains.
   - `Dictionary<ushort,byte> BuildTechFamily()` from station `GrantedTechIdx` + upgrade-dev
     `GrantedTechIdx` (via existing `UpgradeFromType`).
   - `byte HomeFamilyRoot(DevelopmentDef dev)` implementing the rule above.

2. **Filter in `RebuildClusters`** (line 338). Do **not** build a filtered list — the loop index `i`
   (line 347/356) is used as the **global wire dev-index** everywhere downstream (`StatusOf`,
   `Send`, `Authorize`). Instead `continue`-skip devs not in the selected family, exactly like the
   BuildTab hidden-not-greyed precedent (`RebuildGrid` skipping `!IsAvailable`):
   ```csharp
   byte fam = FamilyRootOf(_baseType);   // FamilyRoot of the selected base's type
   for (ushort i = 0; i < devs.Count; i++)
   {
       if (HomeFamilyRoot(devs[i]) != fam) continue;   // scope to this base's family
       ...
   }
   ```
   This keeps `i` global, keeps within-family parent inference correct, and simply omits other
   families' nodes.

3. **Rebuild on base change.** `SetBase` (lines 79–91) currently only refreshes chrome when the base
   is already built. When the **family root changes**, force a structural rebuild the same way the
   collapse chevrons do — call `_gate.Invalidate()` — so `_Process` re-runs `RebuildClusters` with the
   new filter next tick. (Guard on family-root change, not raw id, to avoid needless rebuilds when
   switching between two bases of the same family.)

4. **Reset a stale selection.** After a filtered rebuild, if the detail panel's selected dev
   (`_selectedDev`) is no longer in the visible family, clear it / fall back to the first visible node
   (mirror BuildTab "clears a selection that fell out"), so the right-hand `TechDetailPanel` never
   describes a hidden dev.

5. No change needed to `TechDetailPanel` — the "AT" cell already shows the selected base title, which
   is now always the correct home base for every visible dev. The single-scope-upgrade footer gate
   (`▲ AUTHORIZE AT …`, lines 727–742) still applies and is now mostly redundant (upgrade devs only
   appear under their from-type family) but stays as-is for the already-upgraded-tier case.

## Part 2 — Server: validate research-at-base

File: `server/Sim/Simulation.Research.cs`

Mirror the same derivation over the sim's `Content` model (which already exposes everything —
`Content.Bases` with `SuccessorBaseTypeId`/`BaseTypeId`, `StationCatalogFor(type).GrantedTechIdx`,
`Content.Developments[i].GrantedTechIdx`/`RequiredTechIdx`/`UpgradeScope`, and the existing
`TriggeredUpgrades` / `BaseDefForType`).

1. Add cached `FamilyRoot(byte type)` + `HomeBaseFamilyRoot(DevelopmentDef dev)` helpers (build the
   family-root and tech→family maps once; content is immutable after load).

2. In `ApplyResearchOp`, `ResearchOpStart`, extend gating **after** the existing single-scope-upgrade
   guard (lines 128–142) and **before** the credits check (line 143). Keep the existing exact-from-type
   guard for single-scope upgrades (it is *stricter* than a family match — an already-upgraded tier is
   no longer a valid from-type). For everything that is **not** a single-scope upgrade, add:
   ```csharp
   else
   {
       byte hostType = World.Bases[baseIdx].BaseTypeId;
       byte want = HomeBaseFamilyRoot(dev);
       if (FamilyRoot(hostType) != want)
       {
           string wantName = BaseDefForType(want)?.Name ?? "the correct base";
           ResearchNoticesThisStep.Add((cid, $"{dev.Name} must be researched at a {wantName}."));
           return;
       }
   }
   ```
   This makes "guns/missiles at the Supremacy, starter lines at the Garrison" a real server rule, not
   just a UI convention, while leaving the team-wide uniqueness, availability, slot, and credit checks
   untouched.

## Tests

- **New/extended server test** (add to `StrategyTest` or a focused research test): with a Supremacy
  built + `supremacy-1` owned, assert `ResearchOpStart` for `dev-gat-2` is **rejected at the Garrison**
  and **allowed at the Supremacy**; and a Garrison-family dev (`dev-minigun-2`) is **rejected at the
  Supremacy**. Assert the notice text names the correct base.
- **Audit existing suites that drive research ops** — `StrategyTest` (drives devs by index),
  `ConstructorTest` (asserts `UnlockedClasses` after research), and any `ResearchTest`. The new server
  rule will reject a supremacy-gated dev started at base index 0; update those harnesses to start each
  dev at a **family-matching** base (or add a Supremacy site and target it). This is the main
  regression risk — verify before/after.
- Consider extracting the client `HomeFamilyRoot`/`FamilyRoot` into a pure static so a headless client
  test (like the `TeamStateStore` tests) can assert the four expected family partitions; otherwise
  cover it via the manual client smoke below.

## Docs / memory

- Update **GLOSSARY.md** "Tech Paths / Research" (~line 534) to note research is now scoped per base
  family (home-base derivation + server validation).
- Update the **tech-paths-research** memory with the new research-at-base rule and the derivation.

## Verification

1. `dotnet build` the server + `dotnet test` all suites; confirm the new research test passes and no
   suite regresses beyond the known pre-existing failures (ContentTest garrison-vision, FogTest
   sector-leak, AutopilotTest ×3, CollisionTest ×4, CommanderTest time-seed flake).
2. Client smoke (use the `verify` skill / `--autofly` + docked screen, commander on a team with a
   Supremacy built): open RESEARCH, select the **Garrison** → only Garrison-family nodes show (no
   greyed Supremacy items); select the **Supremacy** → only the advanced weapon/missile lines +
   `dev-upgrade-supremacy` show; select the **Outpost**/**Shipyard** → only their upgrade dev. Capture
   screenshots of at least the Garrison and Supremacy views.
3. Confirm the server rule end-to-end: as commander, attempt to authorize a Supremacy-tier dev while
   the Garrison is selected — the client should not offer it, and a hand-crafted MsgResearch at the
   wrong base returns the "must be researched at a …" notice.

## Notes / out of scope

- **No protocol/wire-version bump** — all inputs (`StationCatalogDef.GrantedTechIdx`,
  `SuccessorBaseTypeId`, dev `GrantedTechIdx`/`RequiredTechIdx`/`UpgradeScope`) are already streamed.
- The client and server derivations are **mirrors** (same precedent as `UpgradeFromType` ↔
  `TriggeredUpgrades`); keep them in sync — a divergence would show a dev in the UI that the server
  rejects, or vice-versa.
- Multi-family requirements (a dev gated by two different base families) don't exist in current
  content; the rule picks the mapped family and defaults `base`-only devs to the Garrison. If future
  content needs a dev whose home differs from its gate, that's when the explicit authored field
  (deferred here) would be warranted.
