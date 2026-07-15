# Fix: F3 map draws misplaced own-ship altitude stem when viewing another sector

## Context

Bug report: the F3 sector map shows a stray offset vector (yellow altitude stem) for ships that are in a different sector than the one being viewed.

Every sector is origin-centered in the same 3D space, so an entity's position is only meaningful on the grid of its *own* sector. All F3 draw paths are therefore gated on `sector == ViewSector` — with one exception: `RebuildStems()` in `client/scripts/SectorOverview.cs` adds the own ship's stem **unconditionally** (line 606–607):

```csharp
if (_world.LocalShip != null)
    _stemPoints.Add(_world.LocalShip.GlobalPosition);
```

When the player views a different sector (minimap click as commander), their own ship's yellow stem + foot cross is drawn at its local-sector coordinates over the *viewed* sector's grid — a misplaced vector for a ship that isn't in that sector. The friendly/enemy/base stem sources are all `Visible`-gated accessors, so only the own-ship stem leaks.

The correct gate already exists in the same file: `TryLocalShip` (line 520–529) returns the own ship's id+position only while `ViewSector == LocalSector`, precisely to avoid this misplacement for the selection/pick paths.

## Change

**File: `client/scripts/SectorOverview.cs`** — in `RebuildStems()` (line ~606), replace the unconditional own-ship add with the existing gate:

```csharp
if (TryLocalShip(out _, out var localPos))
    _stemPoints.Add(localPos);
```

This reuses `TryLocalShip` rather than duplicating the `ViewSector != LocalSector` comparison, so the stem, pick, and bracket paths stay in lockstep by construction.

No other changes: the friendly/enemy/base stem sources, selection brackets, order glyphs, and leader lines were all verified to already be sector-gated.

## Verification

1. `dotnet build` the client project — compile check.
2. Runtime smoke (manual or via the run/verify harness): start a server + client, launch, press F3, then click a *different* sector on the minimap:
   - Before fix: a yellow vertical stem + foot cross floats over the viewed sector's grid at the own ship's local-sector coordinates.
   - After fix: no own-ship stem while viewing a foreign sector; switching back to the local sector (or warping there) shows the stem again.
3. Confirm the stem still draws normally in the local-sector view (default F3 open).
