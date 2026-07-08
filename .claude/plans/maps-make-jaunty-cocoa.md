# Maps: functional map selector + lobby/map-picker UI polish

## Context

Four map-related asks, one substantive and three cosmetic:

1. **Right-side lobby menu 20% smaller** — the lobby's "sector pane" column is a touch too wide.
2. **Make the map selector functional** — today the host's map pick only *advertises* a "next map"; the live server never actually switches arenas. Per the user, the arena should **not even be built until the match launches** — map selection stays a pre-launch lobby choice, and the world is built from whatever map is selected at launch time.
3. **Map-picker modal taller** — reveal ~50% of the next row of map cards so it reads as scrollable.
4. **Map-picker modal slightly less wide** — there's trailing whitespace after the 2nd card per row.

The map selector is already wired end-to-end for *advertising* (`MsgSetMap` → host-gated `_selectedMap` → `BroadcastLobby`). What's missing is committing that selection to the live world. The server builds `World` once at boot (`Program.cs:208`) and holds it in a `readonly` field; the arena the players play should instead be built from the selected map at the Lobby→Active transition, which already runs on the sim thread inside `StartMatch()`.

---

## Part A — Functional map switch (build the selected arena at launch)

**Approach:** Keep `MsgSetMap` exactly as-is (cheap advertise-only). Rebuild the `World` from the currently-selected map inside `Simulation.StartMatch()` (sim thread, `Simulation.cs:575-576` → `845`), then re-send `Welcome` to every connected client so they load the new arena. This makes the picked map the one that actually gets played, with no mid-lobby cross-thread world swap.

### 1. Expose a WorldConfig clone + retain the maps for runtime builds
`server/Content/MapCatalog.cs` — the private `static WorldConfig Clone(WorldConfig)` (line 111) already does the per-map isolated clone `MapCatalog.Build` relies on. Make it reusable (make it `public`/`internal static`, or lift it to a `WorldConfig.Clone()` instance method that `MapCatalog` then calls — keep the "keep in sync when WorldConfig gains fields" comment in one place).

`server/Program.cs` (around lines 184-210):
- Keep the `maps` dictionary alive past boot (it's currently a local discarded after `MapLoader.LoadAvailable`).
- Capture a **pristine** config clone *before* the boot `MapLoader.ApplyTo(selectedMapDef, content.World)` at line 193 (ApplyTo mutates sectors/scale/radius, so later rebuilds must clone from the pristine base — same reason `MapCatalog.Build` clones per map).
- Build a rebuild closure:
  ```csharp
  Func<string, World?> buildWorld = name =>
  {
      if (!maps.TryGetValue(name, out var def)) return null;
      var cfg = MapCatalog.Clone(pristineWorldCfg);
      MapLoader.ApplyTo(def, cfg);
      return new World(seed, cfg, content.Bases[0].MaxHealth, content.Start);
  };
  ```

### 2. Simulation: swap the world at match start
`server/Sim/Simulation.cs`:
- Change `public readonly World World;` (line 296) to a settable field (`public World World { get; private set; }` or drop `readonly`). All ~104 existing `World.` reads keep working (reference-type field; reassignment is atomic).
- Add two hooks next to the existing lobby hooks (lines 400-401):
  ```csharp
  public Func<World?>? BuildMatchWorld; // returns a fresh World for the selected map (null = keep current)
  public Action? OnMatchStart;          // hub re-Welcomes all clients + invalidates its rock cache
  ```
- In `StartMatch()` (line 845), **before** `Array.Fill(World.BaseHealth, …)` / `SeedEconomy` / `ResolveTeamUnlocks`:
  ```csharp
  var next = BuildMatchWorld?.Invoke();
  if (next != null) World = next; // swap to the selected map's fresh arena
  ```
  A fresh `World` already brings full `BaseHealth`, its own rock grid (`World.RockGrid`), sectors, bases, alephs — so the existing `Array.Fill` / `SeedEconomy(Content.Start)` / `ResetVision()` calls that follow all operate on the new world and reset per-match state correctly. `_shipGrid` is empty in lobby and rebuilt every step (`RebuildShipGrid`, line 594). Content-derived lookups (`WeaponDefs`, `ShipDefs`, dispensers) are unaffected — they come from `Content`, not `World`.
- At the **end** of `StartMatch()`, after `Phase = PhaseActive`: `OnMatchStart?.Invoke();`

### 3. ClientHub: expose selected map + re-Welcome on match start
`server/Net/ClientHub.cs`:
- Add `public string SelectedMap => _selectedMap;` (field at line 204).
- Add a match-start hook mirroring the existing `OnReturnToLobby`:
  ```csharp
  public void OnMatchStart()
  {
      _rockIndexById = null;                 // world changed → rebuild the O(1) rock-id index lazily
      foreach (var c in <connected clients>)  // same enumeration BroadcastLobby uses
          SendWelcome(c);
  }
  ```
  `SendWelcome` (line 337) is already documented safe on the sim thread and the client "fully rebuilds its world on any Welcome after the first" (`ApplyWelcome.Reset`) — this is the same reconnect/world-rebuild path (see memory `reconnect-ship-grace`). Ordering is guaranteed: `OnMatchStart` runs inside `sim.Step()`, so the Welcome frames are queued to each client's Outbound *before* `hub.AfterStep()` streams the first Active snapshot (`Program.cs:281→284`).

### 4. Wire it up in Program.cs
Next to the existing hooks (lines 214-215):
```csharp
sim.BuildMatchWorld = () => buildWorld(hub.SelectedMap);
sim.OnMatchStart  = hub.OnMatchStart;
```

**Net effect:** In the lobby, picking a map stays a cheap advertise (`_selectedMap` + `BroadcastLobby`, unchanged). The full arena is built only when the match launches, from the selected map, and every client is snapped to it. Between matches (`ReturnToLobby` keeps the old world) the host can pick a different map and the next `StartMatch` rebuilds from it. The boot-time world becomes just a placeholder that's never actually played.

---

## Part B — UI tweaks

### B1. Right-side lobby menu 20% smaller
`client/scripts/Lobby.cs:326` — `new Vector2(320, 0)` → `new Vector2(256, 0)` (320 × 0.8). This is the only fixed width on the sector-pane column; its contents use `ExpandFill` and reflow.

### B2. Map-picker modal — less wide (kill trailing whitespace)
`client/scripts/ui/MapPickerModal.cs:76` — panel `CustomMinimumSize = new Vector2(760, 0)` → `~700` (nudge to taste; the 2-column `GridContainer` cards `ExpandFill`, so a narrower panel tightens the two cards against the trailing edge).

### B3. Map-picker modal — taller (reveal ~50% of next row)
`client/scripts/ui/MapPickerModal.cs:89` — scroll `CustomMinimumSize = new Vector2(0, 360)` → `~470`. A card is ≈132 (thumb, line 189) + ~53 footer ≈ 185; row pitch ≈ 185 + 14 (`v_separation`) ≈ 199. `360` shows ~1.8 rows; `~455-475` cuts the third row roughly in half. Tune visually.

(Grid stays `Columns = 2` with `14`/`14` separations — no change.)

---

## Files to modify
- `server/Content/MapCatalog.cs` — expose `Clone` (or add `WorldConfig.Clone()`).
- `server/Program.cs` — retain `maps` + pristine cfg, add `buildWorld` closure, wire `BuildMatchWorld` / `OnMatchStart`.
- `server/Sim/Simulation.cs` — settable `World`, `BuildMatchWorld` + `OnMatchStart` hooks, swap in `StartMatch`.
- `server/Net/ClientHub.cs` — `SelectedMap` getter, `OnMatchStart()` re-Welcome + rock-cache invalidation.
- `client/scripts/Lobby.cs` — sector-pane width 320→256.
- `client/scripts/ui/MapPickerModal.cs` — panel width 760→~700, scroll height 360→~470.

## Verification
1. **Build server:** `dotnet build server/` (watch for the NuGet-lock gotcha — see `server-glb-collision` memory).
2. **End-to-end map switch:** launch the server, connect two clients to the lobby. As host, open the map picker, pick a **non-default** map (not "Brimstone Gambit"), close, ready up / launch. Confirm:
   - server logs `[Sim] match started`;
   - both clients enter an arena whose sector layout/count matches the *picked* map (not the boot default) — e.g. pick a map with a distinctly different sector count/silhouette and eyeball the in-game F3 sector map;
   - the non-host client also sees the correct arena (re-Welcome reached everyone).
   Then return to lobby (empty the server or finish the match), pick a *different* map, launch again, and confirm the arena changes again.
3. **UI polish:** open the map picker in the live lobby (or `--ui-showcase` if the modal is registered there). Confirm the modal is narrower with no trailing whitespace after the 2nd card, and the next row of cards is ~half-visible below the fold. Confirm the lobby's right-hand sector pane is visibly narrower (256 vs 320) and its contents still lay out cleanly.
4. **Regression:** `dotnet test` for the server sim suites (the map swap reuses the existing `StartMatch`/`ReturnToLobby` reset paths; the Godot client isn't covered by dotnet tests, so smoke the launch flow with `--autofly` per the `missiles-chaff-seams` memory note).
