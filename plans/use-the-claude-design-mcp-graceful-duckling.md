# Game Lobby update — team names, sector map picker, host, roster restructure

## Context

The in-game **Game Lobby** (`client/scripts/Lobby.cs`) is the screen a pilot sees after
joining a server and whenever they aren't flying. It is a full C#, code-built overlay that
was ported from the Claude Design "Game Lobby" mock, and deliberately renders placeholders
for anything the wire doesn't carry (team names, per-player K/D/EJ/PTS, match info).

The design has been updated (Claude Design project `Stellar Allegiance UI design` →
`Game Lobby.dc.html`) with four changes. We are implementing them at a **"networked where
cheap"** scope (confirmed with the user):

- **Team name editing** → genuinely networked: broadcast through the server so all clients see it.
- **Sector map + picker** → real map list + selected map streamed from the server's Maps
  subsystem; host applies `SET MAP` over the wire (advertises the *next* map — no live world rebuild).
- **Host** → inferred authoritatively on the **server** (first pilot = host; transfers to the
  next pilot on leave; a comment notes we may later allow explicit host selection), streamed to clients.
- **Layout + roster** → pure client UI: 3-column body with a new right-hand sector pane; roster
  drops the Ship/Status columns. Per-player **K/D/EJ/PTS stay placeholders** (`—`) at this scope.

The goal: the four design features work end-to-end in multiplayer for the pieces that are cheap
and meaningful (names, host, map selection), while keeping the file's honest-placeholder style
for data the protocol still doesn't carry.

## Key facts that shape the approach (verified)

- **Protocol** is a single registry of `byte` id consts in `server/Net/Protocol.cs`
  (client→server `MsgHello=1`…`MsgBye=8`; server→client `MsgWelcome=1`…`MsgProbeGone=19`).
  The **client mirrors ids as raw byte literals** — send = `_tx.Writer.TryWrite([5, team])`,
  receive = `switch(r.ReadByte())` with numeric `case` arms in `GameNetClient.cs`. No shared enum.
- **Version gate**: `shared/Net/Wire.cs` `ProtocolVersion` (currently **24**) is the one source;
  `ApplyWelcome` refuses to play on mismatch. Any frame-layout change **must** bump it to 25.
  Client and server ship together (hard break, fails closed — acceptable here).
- **Lobby roster** (`server/Net/Lobby.cs`) holds only per-player recs. Session-global state
  (team names, selected map, host) belongs on **`ClientHub`**, which owns `_lobby` and does
  `BroadcastLobby()` on every change. Client ids are monotonic (`Interlocked.Increment`), so
  **host = lowest client id still present** is a clean "first pilot, transfers on leave" rule.
- **Maps subsystem is thin**: `MapDef` (`server/Content/MapLoader.cs`) has only `Name` +
  `Sectors[{Id,Radius?,Name?}]`. No mode/size/garrison-count/node-ownership is authored; garrison
  (base) positions are seed-generated at `World` construction (exactly 2 bases, no neutral).
  `Program.cs` currently **discards** the `LoadAvailable` dictionary. Only **one** map ships
  (`content/maps/brimstone-gambit.yaml`) → the picker is 1-up today, extensible via `--maps-dir`.
- **Reuse spine for thumbnails**: `client/scripts/ui/SectorMapPreview.cs`
  (`MapModel`/`SectorModel`/`BaseMark`, `SetMap`) already renders sector circles + team base
  diamonds; `ServerLobbyOverlay.ToMapModel` (client) and `LobbyStatus.BuildMap(World)` (server,
  `server/Net/LobbyStatus.cs`) already project into/around this shape.
- **Modal scaffold to copy**: `client/scripts/ui/SettingsDialog.cs` (+ `ModalHost.Ensure`,
  Layer 200): `static Active`, `static Open(ctx)`, scrim (`DesignTokens.Scrim`, non-dismissing) →
  `CenterContainer` → `BracketPanel{FillOverride=PanelDeep}` → header (✕) + content + footer.
- **UI toolkit** (reuse, don't hardcode): `DesignTokens` (`Faction0/1`, `TeamAccent`, `Ok/Warn/Data`,
  `TextHi/Text2/TextDim`, `PanelDeep`, `Scrim`), `UiKit.MakeLabel/MakeButton/MakeSegmented`,
  `ChamferButton`/`ButtonVariant`, `Surfaces.BracketPanel/DiamondDivider`,
  `DataFeedback.SegmentedBar/StatReadout/StatusPill`. Custom-draw pulls fonts from `UiFonts`;
  hairlines keep `CornerRadius=0`, `AntiAliasing=false`.

## Plan

### 1. Wire protocol (`server/Net/Protocol.cs`, `shared/Net/Wire.cs`)

Bump `Wire.ProtocolVersion` **24 → 25** (comment: "lobby team names, host, map list + selected map").

**New client→server ids** (after `MsgBye=8`):
```
MsgSetTeamName = 9;  // u8 team, u16 len, utf8 name  — rename a team you are on
MsgSetMap      = 10; // u16 len, utf8 mapName         — host picks the next map
```

**Extend `MsgLobbyState` (id 8)** — append *after* the per-player entries (prefix stays byte-stable,
per the append-last discipline noted at `Protocol.cs:632`):
```
+ str team0Name, str team1Name
+ i32 hostClientId        // -1 when the server is empty
+ str selectedMapName
```
`BuildLobbyState(...)` gains those four params.

**New server→client `MsgMapList = 20`** — the static catalog, sent **once after Defs** (not in
Welcome, which is fog-gated and re-sent on team change):
```
u8 mapCount, mapCount × {
  str name, str mode, str sizeLabel, str sectorLabel, u8 garrisonCount,
  u8 sectorCount, sectorCount × { u32 id, f32 radius, str name,
                                  u8 baseCount, baseCount × { u8 team, f32 x, f32 z } } // team 0/1; 0xFF reserved for neutral
}
```
Add `Protocol.BuildMapList(...)` next to `BuildLobbyState`, reusing `WriteString`.

### 2. Server (`server/Net/ClientHub.cs`, `server/Program.cs`, `server/Content/MapLoader.cs`)

- **Session state on `ClientHub`** (near `_lobby`): `string[] _teamNames = {"IRON COIL","ASH SYNDICATE"}`
  (design defaults, replacing the client's BLUE/RED), `int _hostId = -1`, `string _selectedMap`,
  `IReadOnlyList<MapCatalogEntry> _mapCatalog`. `BroadcastLobby()` passes these into `BuildLobbyState`.
- **Host inference**: on `MsgHello` after `_lobby.Add`, `if (_hostId < 0) _hostId = client.Id;`. On
  disconnect (the `finally` after `_lobby.Remove`), if the leaver was host transfer to the earliest
  remaining: `_hostId = _lobby.Snapshot().Select(e => e.Id).DefaultIfEmpty(-1).Min();`. Add the
  `// TODO: later allow explicit host selection/transfer` comment. `BroadcastLobby()` already fires there.
- **Command handlers** (new `case` arms by `MsgSetTeam`):
  - `MsgSetTeamName`: validate real team `&&` `_lobby.TeamOf(client.Id) == team`; `name = name.Trim().ToUpperInvariant()`,
    cap 18, non-empty → `_teamNames[team] = name; BroadcastLobby();`.
  - `MsgSetMap`: `client.Id == _hostId` only (host enforced server-side); if the name is in `_mapCatalog`,
    `_selectedMap = want; BroadcastLobby();`. Comment: cheap path — advertises "next" map, does **not**
    rebuild the live `World`.
- **Map catalog at boot** (`Program.cs:~144`): stop discarding `MapLoader.LoadAvailable`. For each
  `MapDef`, build a throwaway `World` (same ctor used at `Program.cs:157`, map applied to a copy of
  `content.World`) and project it (helper modeled on `LobbyStatus.BuildMap`) to get sector radii + base
  `{team,x,z}`. Derive `garrisonCount = world.Bases.Count`, `sizeLabel` from max sector radius,
  `sectorLabel` from the home sector name, `mode` from a new optional `MapDef.Mode` (default `"CONQUEST"`).
  Add optional `Mode` (`string?`) to `MapDef`. Pass the catalog + initial selected name into `ClientHub`.
  New `MapCatalogEntry` record beside `LobbyStatus` (or `server/Content/MapCatalog.cs`). `ClientHub`
  sends `MsgMapList` once after Defs in the `MsgHello` handler.

### 3. Client net layer (`client/scripts/GameNetClient.cs`, `client/scripts/NetTypes.cs`)

- `NetTypes.cs`: `public sealed record MapInfo(string Name, string Mode, string SizeLabel,
  string SectorLabel, int GarrisonCount, SectorMapPreview.MapModel Layout);` (Layout prebuilt at decode
  so the UI just calls `SetMap`).
- `GameNetClient.cs`: new props `Team0Name`, `Team1Name`, `SelectedMap` (with BLUE/RED/map fallbacks),
  `int HostId`, `bool IsHost => HostId == LocalClientId`, `IReadOnlyList<MapInfo> Maps`; event
  `MapListChanged`. Send methods `SetTeamName(byte,string)` / `SetMap(string)` mirroring the `SendChat`
  length-prefix pattern. `ApplyLobbyState`: after the entry loop, read the 4 appended fields into the
  new props before `LobbyChanged?.Invoke()`. New `ApplyMapList` + `case 20:`, converting sectors/bases
  into `MapInfo.Layout` exactly like `ServerLobbyOverlay.ToMapModel`.

### 4. Client `Lobby.cs`

- **State-driven names**: replace static `TeamName(int)` (`:847`) with an instance method reading
  `_net.Team0Name/Team1Name` (fallbacks BLUE/RED/NOAT). Every call site (status bar, roster header,
  tabs, win text) already funnels through it → tabs, score bar, header update for free.
- **Inline rename** in the roster header (`:261`/`:597`): when `CanEditTeam(_selectedTeam)`
  (`(t==0||t==1) && MyTeamNow()==t`) show a `✎` `ButtonVariant.Icon`. While editing
  (`_editingTeam == _selectedTeam`) render a `LineEdit{MaxLength=18}` seeded with the name + `✓`/`✕`.
  `✓`/Enter → `_net.SetTeamName(team, text.Trim().ToUpper()[..≤18])`; `✕`/Esc → cancel. Handle Enter/Esc
  in `_Input` (`:421`) guarded by the edit field's focus, and add `_editingTeam < 0` to the `_Process`
  focus-grab guard (`:498`). Server rebroadcast makes the confirmed name appear everywhere for all clients.
- **3-column body** (`BuildBody`, `:230`): `228 | 1fr | 320`. Keep left team tabs + center roster; add a
  vertical hairline + a `320px` **sector pane** column.
- **Sector pane** (new `BuildSectorPane()`/`UpdateSectorPane()`): (1) `SectorMapPreview` thumbnail (click →
  `MapPickerModal.Open`), fed `CurrentMap().Layout` (looked up in `_net.Maps` by `_net.SelectedMap`,
  falling back to the live world layout); host affordance label HOST→"CHANGE", non-host→"LOCKED/VIEW".
  (2) Sector Intel 2-col stat grid (MODE/SECTOR/GARRISONS/MAP SIZE) via `StatReadout`/local `StatCol`.
  (3) Garrison Control: proportional bar + rows `"{Team0Name} n/total"`, `"NEUTRAL/UNCLAIMED n/total"`,
  `"{Team1Name} n/total"` (counts from layout bases; neutral is 0 today — comment it). Refresh from
  `RebuildBody` and on `MapListChanged`.
- **Status bar** (`:190`,`:503`): title = `CurrentMap().Name` (keep ENDED win text); subtitle field =
  `$"{mode} · {sectorLabel} · {garrisons} GARRISONS"`.
- **Roster restructure** (`ColumnHeader :728`, `RosterRow :684`): drop SHIP + STATUS → 6 columns
  `[tick] CALLSIGN K D EJ PTS` (PTS right-aligned). Remove `StatusOf` (`:835`). K/D/EJ/PTS stay `—`
  placeholders (keep the existing placeholder comment). Re-tune `Cell` ratios.

### 5. New file: `client/scripts/ui/MapPickerModal.cs`

Copy the `SettingsDialog` scaffold (`static Active`/`Open(ctx, net)`, `ModalHost.Ensure`, scrim +
centered `BracketPanel{FillOverride=PanelDeep}`, header "SELECT SECTOR MAP" + ✕, footer). Body = 2-up
`GridContainer` of cards, one per `net.Maps`: `SectorMapPreview.SetMap(map.Layout)` thumbnail + name +
`mode · size · N garrisons`; selecting stages `_pendingMap` (highlight); the card matching
`net.SelectedMap` shows an **"IN PLAY"** badge. Footer: `CANCEL` + (host) `SET MAP` → `net.SetMap(...)`
then close, or (non-host) `CLOSE` + a "not the host — locked" notice, gated on `net.IsHost`. Add
`MapPickerModal.Active` to `Lobby._Process` focus-grab and Esc guards (mirroring `SettingsDialog.Active`).

## Reuse (paths)

- Thumbnails: `client/scripts/ui/SectorMapPreview.cs`; conversion pattern `ServerLobbyOverlay.cs` `ToMapModel`.
- Server world→layout projection to copy for the catalog: `server/Net/LobbyStatus.cs` `BuildMap`.
- Modal: `client/scripts/ui/SettingsDialog.cs` + `client/scripts/ui/ModalHost.cs`.
- Grids/bars/pills: `client/scripts/ui/DataFeedback.cs`; buttons/labels: `UiKit.cs`; panels: `Surfaces.cs`;
  tokens `DesignTokens.cs`; fonts `UiFonts.cs`. Wire string helper: `Protocol.WriteString`.

## Risks

- **Protocol 24→25 is a hard break** — fails closed on mismatch; ship client + server together.
- **SET MAP advertises the next map only** — actually regenerating the arena needs a `World`-rebuild +
  re-Welcome seam that doesn't exist (World built once in `Program.cs`). Land wire/UI/storage now; leave
  an explicit server TODO.
- **Thin map data** — no neutral garrisons; garrison count fixed at 2; mode/size/sectorLabel are
  derived or newly authored, not currently in YAML. Support `team==0xFF` neutral for forward-compat but
  don't fabricate it. Flag to design that the neutral row reads 0 today.
- **Catalog builds a `World` per map at boot** — cheap now (1 map), O(maps) later.
- **Team-name defaults** switch to COIL/ASH (design) from BLUE/RED (current code).

## Verification (no dotnet tests cover the Godot client — smoke with two clients)

1. `dotnet build server/SimServer.csproj` and the solution (shared/Protocol).
2. `godot --headless --path client --import`, then a headless client build so C# compiles against the new
   `GameNetClient`/`Lobby` members.
3. Run `SimServer` (default map "Brimstone Gambit"); optionally add a second yaml via `--maps-dir` for a
   real 2-up picker.
4. **Two clients** (the meaningful integration test for a protocol bump): verify (a) first joiner shows
   **HOST** + enabled SET MAP, second shows **LOCKED/VIEW**; (b) host picks a map → both clients' pane +
   subtitle + thumbnail update ("applies to next match"); (c) join a team + rename → new name on **both**
   clients across tabs, score bar, roster header, garrison labels; (d) no ✎ when not on a team; (e) host
   leaves → host transfers to the remaining client; (f) roster is the 6-column table (SHIP/STATUS gone,
   K/D/EJ/PTS as `—`). Watch for `protocol vN ≠` errors (stale binary).
5. `--host`/`--autofly` smoke to confirm lobby→active→lobby still broadcasts the extended `MsgLobbyState`
   without desync.

## Critical files

- `shared/Net/Wire.cs` — version bump 24→25.
- `server/Net/Protocol.cs` — new ids, extended `BuildLobbyState`, new `BuildMapList`.
- `server/Net/ClientHub.cs` — team names / host / selected map, host inference, command handlers, catalog send.
- `server/Program.cs` + `server/Content/MapLoader.cs` — retain map dictionary; build per-map catalog
  (derived mode/size/garrison/node layout); optional `MapDef.Mode`.
- `client/scripts/GameNetClient.cs` + `client/scripts/NetTypes.cs` — send methods, `ApplyLobbyState`/
  `ApplyMapList` decode, host/name/map props, `MapInfo`.
- `client/scripts/Lobby.cs` — 3-col body, sector pane, inline team-name editor, roster restructure,
  state-driven `TeamName`.
- `client/scripts/ui/MapPickerModal.cs` — **new**, reuses `SectorMapPreview` + `SettingsDialog` scaffold.
