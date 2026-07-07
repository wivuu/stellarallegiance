# Dynamic ship signature — composable modifiers

## Context

A ship's **radar signature** (`RadarSignature`) is what scales every viewer's fog-of-war
detection range against that ship (sphere / eyeball / cone tiers in
`server/Sim/Simulation.Vision.cs`). Today it is a **constant per hull class** with exactly **one**
dynamic modifier hard-coded inline: a "just fired → louder" boost
(`Simulation.Vision.cs:497-504`).

We want signature to be a **composable value** driven by several contributors — equipment /
loadout, whether a shield is fitted, firing, afterburner/boost, and being inside a dust cloud —
so stealth becomes a real tradeoff (run cold + shields-down + hide in dust = quiet; boost + fire +
shielded = loud). The one inline fire-boost is the seam to generalize.

Two decisions from the user shape this:
- **Equipment/abilities:** also project a **per-hull authored signature bias** from content
  (`Hull.Signature` + the default loadout's `Part.Signature` sum) so content can tune per-hull
  signature now, without building a full runtime slot/fitting system. A per-ship bias field is the
  seam a future loadout/ability system (e.g. cloak) writes to.
- **Shields:** having an **equipped** shield (`ShieldCapacity > 0`), regardless of current pool,
  raises signature. Static per class, but expressed as a pipeline term for legibility/tuning.

Signature is **server-only / fog-only** — never streamed to the client
(`Protocol.cs` writes it for defs but vision is computed server-side). **No protocol bump and no
client change is required.** The `ShipLoadout` hangar "SIGNATURE" bar is a cosmetic proxy from mass
and is explicitly out of scope.

The user also flagged `Simulation.Vision.cs` as spaghetti with unused vars: fold in a **measured**
refactor — extract the signature math into a pure, unit-testable unit and drop dead locals — but
**keep the fog worker/threading structure untouched**.

## Design — the signature pipeline

Effective per-tick signature, computed on the sim thread at capture time (the worker only ever reads
value-copied snapshots, so this must stay at capture):

```
effSig = clamp(
    (RadarSignature + SigBias)      // base + equipment/loadout/ability bias (additive)
    × fireMult                      // recent gun/missile fire (existing boost, decaying window)
    × boostMult                     // afterburner: 1 + (BoostSigMult-1)·AbPower   (ramp 0..1)
    × shieldMult                    // equipped shield present → ShieldSigMult, else 1
    × dustMult,                     // inside dust: 1 + (DustSigMult-1)·coverage    (<1 = quieter)
    (RadarSignature+SigBias)·MinMult, (RadarSignature+SigBias)·MaxMult )   // safety rails
```

**Neutral-by-default invariant:** every new knob defaults to `1.0` (bias `0`), so with nothing
authored the result is byte-identical to today's fire-boost-only behavior. Only authoring a knob (or
`Hull.Signature`/preferred parts) changes detection.

## Files & changes

### 1. `shared/Defs.cs`
- `ShipClassDef`: add `public float SignatureBias;` (additive, radar-signature units, default 0),
  next to `RadarSignature` (~line 111).
- `WorldDef`: add `BoostSignatureMult`, `ShieldSignatureMult`, `DustSignatureMult` (default `1f`),
  and `SignatureMinMult`/`SignatureMaxMult` clamp rails (defaults e.g. `0.1f`/`8f`), beside the
  existing `FireSignatureBoost`/`FireSignatureWindow` (~lines 379-380). Keep the fire knobs as-is.

### 2. `server/Sim/SignatureModel.cs` (new — the unit-testable core)
Pure static, **no Simulation/World references** so it tests standalone:
```csharp
readonly record struct SignatureKnobs(float FireBoost, float FireWindowTicks,
    float BoostMult, float ShieldMult, float DustMult, float MinMult, float MaxMult);
readonly record struct SignatureInputs(float BaseSig, float Bias, uint Tick,
    uint LastFireTick, uint LastMissileTick, float AbPower, bool HasShield, float DustCoverage);
static float Compute(in SignatureInputs i, in SignatureKnobs k);   // the pipeline above
```
Move the existing fire-boost formula (`Vision.cs:501-503`) here verbatim as `fireMult`.

### 3. `server/Sim/Simulation.Vision.cs`
- Cache the new knobs in `InitVision` (~line 288) alongside `_fireSigBoost`, as one
  `SignatureKnobs _sigKnobs` (built from `Content.World`).
- Replace the inline block at **497-504** with:
  `float sig = SignatureModel.Compute(new SignatureInputs(def.RadarSignature, s.SigBias, tick,
     s.LastFireTick, s.LastMissileTick, s.State.AbPower,
     ShieldCapacityFor(s) > 0f, DustCoverageAt(s.SectorId, s.State.Pos)), _sigKnobs);`
- Add small helper `float DustCoverageAt(uint sector, Vec3 pos)` — max `Density` over
  `_dustClouds[sector]` whose sphere contains `pos`, else 0 (short-circuit on `!_hasDust`). Reuses
  the already-cached `_dustClouds`. **Note:** this stacks with the existing `DustVisionMult`
  sightline attenuation (viewer→target); intentional (hiding in a cloud), flag when tuning.
- Refactor pass (measured): remove compiler-flagged dead locals/fields (build with warnings, chase
  `CS0219`/`CS0168`/`IDE0059`), tidy the signature-adjacent comments. **Do not** restructure the
  worker/threading, `ClassifyTarget` tier math, or the apply step.

### 4. `server/Sim/Simulation.cs`
- `ShipSim`: add `public float SigBias;` near `Shield` (~line 178) — per-ship equipment/ability
  signature bias; the seam a future loadout/cloak system mutates live.
- At spawn (where `s.Shield = ...` is set, ~line 938): `s.SigBias = ShieldDefFor(s).SignatureBias;`
  (seed from the class's projected default-loadout bias).

### 5. `server/Content/FactionsContentProjection.cs` (`ProjectShip`, ~line 120)
- Project `SignatureBias = (float)h.Signature + preferred-parts signature sum`, resolving
  `h.PreferredParts` against the Core parts catalog (`Core.AllParts()` / Weapons∪Shields∪Cloaks∪
  Afterburners∪Launchers∪AmmoPacks) and summing each `Part.Signature`. Needs the `Core` handle in
  scope — thread it through like other cross-referencing projections in this file. Stock core hulls
  author neither today ⇒ bias 0 ⇒ no behavior change; factions that author loadouts get it for free.

### 6. `server/Content/core/world.yaml`
- Under the existing `fire-signature-*` comment block (~line 29), document + author the new knobs:
  `boost-signature-mult`, `shield-signature-mult`, `dust-signature-mult`, and the clamp rails.
  Recommend modest live stock values (e.g. boost `1.4`, shield `1.15`, dust `0.5`) so the feature is
  active, with a comment that these shift fog detection ranges and are safe to sweep server-side.
  (Leaving them at `1.0` keeps current behavior — call this out.)
- Optionally document a per-hull `signature:` (→ `Hull.Signature`) key in `hulls.yaml` header as the
  authored per-hull bias hook (no stock values needed).

## Reuse notes
- Fire-boost formula, `_dustClouds` cache, `ShieldCapacityFor(s)`, `ShieldDefFor(s)` all already
  exist — reuse, don't duplicate.
- Knob-load idiom (`Content.World.X > 0 ? X : default`) mirrors existing `InitVision` lines 287-292.
- `WorldDef` knobs are server-only (already the pattern for `FireSignature*`) — nothing to serialize.

## Verification
1. **Unit test `SignatureModel.Compute`** (new cases in `tests/FogTest/Program.cs`, or a tiny
   `SignatureModelTest`): base only == base; fire within window > base and decays to base at window
   end; `AbPower=1` applies `BoostMult`; `HasShield` applies `ShieldMult`; `DustCoverage=1` applies
   `DustMult` (<base); clamp rails bound extreme stacking; all-neutral knobs + bias 0 == base
   (byte-identical guard).
2. **`tests/FogTest` behavior** (boots real sim, `VisionSynchronous`, ships parked in empty sector
   999 — see its `BootSim`/`Park`): assert detection range scales as expected — a boosting ship is
   picked up farther than a coasting one; a shield-equipped hull farther than an identical no-shield
   hull; a ship placed inside a seeded dust cloud detected at shorter range. Read thresholds from the
   loaded `ContentSet` (the file's existing idiom) so retuning YAML never breaks assertions.
3. **Regression:** run the full server test suite (`FogTest`, `ShieldTest`, `MissileTest`,
   `ContentTest`, `FactionsTest`, `FlightModelTest`). With stock knobs left at `1.0`, FogTest's
   existing assertions must stay green (neutral invariant).
4. **Smoke:** `--autofly` headless run (per the protocol-smoke habit) to confirm no sim crash — even
   though there's no protocol bump, this exercises the new capture-time path under real traffic.
