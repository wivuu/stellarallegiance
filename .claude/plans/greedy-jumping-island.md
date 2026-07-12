# Introduce a `ShipKind` role enum (replace `IsMiner`/`IsPod` bools)

## Context

Right now "what kind of ship this is" is expressed **three different, disconnected ways**, which
is confusing and lets representations drift out of sync:

1. **Sim** — parallel bools on `ShipSim`: `IsPig`, `IsPod`, `IsMiner` (`server/Sim/Simulation.cs:199-206`).
2. **Wire** — flag bits in `Protocol.cs`: `ShipFlagPig=1`, `ShipFlagPod=2`, `ShipFlagMiner=32`.
3. **Client** — the `Ship` row + `RemoteShip` re-mirror those bools, and then
   `TargetMarkers.Kind` (`client/scripts/TargetMarkers.cs:79`) is a *rendering* enum that mixes
   ship roles (Miner/Pod), hull classes (Scout/Fighter/Bomber), and non-ship entities
   (Base/Aleph/Mine/Asteroid/Probe) into one glyph picker via `KindOf()`.

We're about to add a **Constructor** ship type, and bolting on another `IsConstructor` bool would
deepen the mess. Instead we introduce a single authoritative **role** enum, `ShipKind`, that flows
sim → wire → client. `IsPig` stays a separate bool because it is an *orthogonal* axis
(AI-controlled vs human) — a PIG escape pod is both `IsPig` and a pod. `ShipClass` (hull model)
also stays separate — it is the *hull* axis, orthogonal to *role*.

Intended outcome: one stored source of truth for a ship's role; `Constructor` reserved as a value
so the type exists end-to-end without any spawn/brain/build behavior yet (mirrors how `Miner`
shipped before its brain landed).

## Decisions (confirmed with user)

- **Enum absorbs role incl. Pod**: `ShipKind { Combat, Pod, Miner, Constructor }`. Replaces both
  `IsMiner` and `IsPod`. `IsPig` remains an orthogonal AI-control bool.
- **Client glyph enum kept**, but `KindOf()` is rewritten to switch on `ShipKind`; the duplicate
  role bools on `RemoteShip` are removed (derived accessors only, single source = `Kind`).
- **Constructor is reserved** — enum value + wire round-trip only, no gameplay behavior.

## The enum

Add to `shared/` (new `shared/ShipKind.cs`, namespace `StellarAllegiance.Shared`; or alongside the
other enums in `shared/Defs.cs`):

```csharp
namespace StellarAllegiance.Shared;

// A ship's mutually-exclusive ROLE. Orthogonal to ShipClass (hull model) and to IsPig
// (AI-controlled). Serialized into the ShipRecord flags byte; see Protocol.ShipFlag*.
public enum ShipKind : byte
{
    Combat = 0,     // player or PIG combat hull (default — no role bit set on the wire)
    Pod,            // ejected escape pod (form change of a combat ship)
    Miner,          // AI ore harvester (server/Sim/Simulation.Mining.cs)
    Constructor,    // RESERVED — no spawn/brain/build behavior yet
}
```

## Server changes

**`server/Sim/Simulation.cs`**
- Replace fields `public bool IsPod;` and `public bool IsMiner;` on `ShipSim` (lines ~200, 206)
  with `public ShipKind Kind;` (keep `IsPig`, `Ore`, `IsHarvesting`). Update the surrounding
  comment block to describe the role/AI/form axes.
- Update every read: `s.IsMiner` → `s.Kind == ShipKind.Miner`; `s.IsPod` → `s.Kind == ShipKind.Pod`
  (sites at ~757, 776, 801, 805, 909, 1232, 1267-1271, 1357, 1717-1721, 1796, 1806, 1981, 2142,
  2232, 2340, 2342, 2486, 2676, plus the `ShieldDefFor`/`StatsFor(..., s.IsPod)` calls — those take
  a `bool isPod` param, pass `s.Kind == ShipKind.Pod`).
- Update every write: `MakePod` (`~1753`) sets `Kind = ShipKind.Pod` (still `IsPig = dead.IsPig`).
- `IsMinerClass(byte cls)` in `Simulation.Mining.cs:121` stays as-is (it derives *eligibility* from
  `OreCapacity > 0`, a def property — not the per-ship role). The miner **spawn** in
  `Simulation.Mining.cs:669` changes `IsMiner = true` → `Kind = ShipKind.Miner`.

**`server/Sim/Simulation.Pig.cs`** — same read/write substitution (`me.IsPig || me.IsPod`, the pod
rescue chain at ~478-499, 581, 630, 856, 958 comment). `IsPig` is untouched; only `IsPod` reads
become `Kind == ShipKind.Pod`.

**`server/Net/Protocol.cs`**
- Keep `ShipFlagPod=2`, `ShipFlagMiner=32`. Add `public const byte ShipFlagConstructor = 128;`
  (documented RESERVED). Bit layout is unchanged for all currently-emitted entities.
- In `WriteShip` (~137-151) replace the `if (s.IsPod)` / `if (s.IsMiner)` blocks with a switch on
  `s.Kind`: `Pod→ShipFlagPod`, `Miner→ShipFlagMiner`, `Constructor→ShipFlagConstructor`,
  `Combat→none`. Leave the `IsPig`/`IsHarvesting`/threat/autopilot flag logic alone.

**`server/Net/ClientHub.cs:1690`** — `ships[i].IsMiner` → `ships[i].Kind == ShipKind.Miner`.

Because no `Constructor` is ever emitted and Pod/Miner keep their existing bits, the serialized
bytes are **identical** to today → no forced protocol-version bump. (Document the reserved bit; a
bump only becomes necessary once a constructor actually ships.)

## Client changes

**`client/scripts/NetTypes.cs`** (`Ship` DTO, ~70-73) — replace `public bool IsPod;` and
`public bool IsMiner;` with `public ShipKind Kind;`. Keep `IsPig`, `IsMining`. Add derived
read-only accessors so the ~20 pod-aware call sites stay untouched and can never disagree with
`Kind`:
```csharp
public bool IsPod   => Kind == ShipKind.Pod;
public bool IsMiner => Kind == ShipKind.Miner;
```
(`ShipClass` enum stays exactly as-is — it is the hull axis, not the role axis.)

**`client/scripts/GameNetClient.cs`** (~1664-1669) — replace `IsPod = (flags & 2) != 0` and
`IsMiner = (flags & 32) != 0` with a single decode:
```csharp
Kind = (flags & 128) != 0 ? ShipKind.Constructor
     : (flags & 32)  != 0 ? ShipKind.Miner
     : (flags & 2)   != 0 ? ShipKind.Pod
     : ShipKind.Combat,
```
Downstream `row.IsPod` usage (e.g. `TryGetStats` at ~1712) works via the derived accessor.

**`client/scripts/RemoteShip.cs`** — replace the stored `IsPod`/`IsMiner` auto-properties (37-43,
146-147) with `public ShipKind Kind { get; private set; }` set from `row.Kind` in `Initialize`,
plus derived `IsPod`/`IsMiner` accessors (single source = `Kind`). Keep `IsPig`, `IsMining`. Pod-
aware calls (`TryGetStats(..., IsPod)` at 148/152) keep working.

**`client/scripts/PredictionController.cs`** — the local ship is only ever Combat or Pod; keep its
`IsPod` but back it with `Kind` (add `Kind` set from `row.Kind`, `IsPod => Kind == ShipKind.Pod`),
or minimally leave `IsPod = row.IsPod` reading the DTO's derived accessor. Prefer the former for
consistency.

**`client/scripts/TargetMarkers.cs`** — leave the `Kind` glyph enum (it still must cover
Base/Aleph/Mine/Asteroid/Probe). Rewrite `KindOf(RemoteShip s)` (~1065) to switch on the shared
role instead of the bool ladder:
```csharp
private static Kind KindOf(RemoteShip s) => s.Kind switch
{
    ShipKind.Pod   => Kind.Pod,
    ShipKind.Miner => Kind.Miner,
    _ => s.Class switch { ShipClass.Scout => Kind.Scout, ShipClass.Bomber => Kind.Bomber, _ => Kind.Fighter },
};
```
Update the `focusedShip.IsMiner` / `focusedFriendly.IsMiner` MINER-tag reads (~801, 812) to the
derived accessor (no change needed if the accessor is kept).

**`client/scripts/WorldRenderer.InterpDiag.cs`** (52-53, 87) — `rs.IsMiner`/`best.IsMiner` keep
working via the derived accessor; no change required. Same for `WorldRenderer.cs` `.IsPod` sites.

## Test changes

**`tests/MiningTest/Program.cs:556`** — `new() { Class = 4, IsMiner = true, ... }` →
`new() { Class = 4, Kind = ShipKind.Miner, ... }`.

Check for any mining/determinism canary that hashes `ShipSim` fields; if a canary enumerates the
old bools, point it at `Kind`.

## Out of scope / leave alone

- `factions/.../Model/Enums.cs` `ShipAbility.IsMiner`/`IsBuilder` — a *separate* heritage capability
  bitmask, unrelated to the per-ship role. Not touched.
- `IsPig` (both axes: sim bool + `ShipFlagPig`) — orthogonal AI-control axis, unchanged.
- `IsHarvesting`/`ShipFlagMining` — per-tick activity state, unchanged.
- No `Constructor` spawn, brain, or build mechanics (reserved value only).

## Verification

1. **Build**: `dotnet build` the server + shared + client-facing test projects.
2. **Tests**: run the full dotnet suite (`dotnet test` per the repo's test runner). Confirm
   `MiningTest`, `AutopilotTest`, `ShieldTest`, `FogTest` behave as before — expect only the known
   pre-existing content-drift failures (ShieldTest/ContentTest/FactionsTest baseline), no new ones.
3. **Wire byte-identity**: since Pod/Miner bits and all other flags are unchanged and no Constructor
   is emitted, a captured `WriteShip` output for existing ships must be byte-for-byte identical —
   spot-check via a headless `--server --anonymous` sim tick if a canary doesn't already cover it.
4. **Client smoke** (Godot client not covered by dotnet tests): launch `--autofly` against a local
   server and confirm miners still tag **MINER**, show the miner pentagon glyph + mining beam/roll,
   and escape pods still render the pod mesh/glyph and are excluded from Tab targeting — i.e. the
   role→glyph mapping survived the refactor.
