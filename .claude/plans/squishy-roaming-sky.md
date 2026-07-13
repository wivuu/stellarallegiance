# Enable Enter-to-chat on the hangar screen

## Context

Once connected to a game server, the player expects to press **Enter** to talk in
chat from **any** screen. Today it works while flying, on the F3 sector map, and in
the Game Lobby (which has its own always-focused comms box) — but **not on the
hangar / ship-loadout screen** (`ShipLoadout`). This is the last coverage gap.

### Root cause

There are two chat UIs:
- The floating **`Chat`** overlay (`client/scripts/Chat.cs`) — the in-flight / F3
  comms, Enter-to-open.
- The **Lobby** comms panel (`client/scripts/Lobby.cs`) — an always-focused
  `LineEdit`, used on the not-flying lobby screen.

`Chat` deliberately steps aside whenever it is not flying, via `LobbyOwnsScreen`
(`Chat.cs:135-136`):

```csharp
private bool LobbyOwnsScreen => _cm.State == ConnState.Connected && _world.LocalShip == null
    && !SectorOverview.Active;
```

When `LobbyOwnsScreen` is true, `Chat._UnhandledInput` returns early (no Enter) and
`Chat._Process` hides the overlay — on the assumption the Lobby's comms panel is
available. **That assumption breaks on the hangar screen:** with no ship
(`LocalShip == null`) the hangar covers the Lobby (Lobby hides itself when the spawn
hangar is committed, or its comms box loses focus while `ShipLoadout.Active`), and
the hangar itself binds no Enter and hosts no chat (`ShipLoadout.cs:557-558`
intentionally leaves Enter free). So no live handler owns Enter → nothing happens.

Note this is the exact same situation the code already solves for the pre-launch F3
map: the Lobby hides to uncover the overview camera, and `Chat` takes over comms
because `SectorOverview.Active` is excluded from `LobbyOwnsScreen`. We extend that
same pattern to the hangar.

## Approach

Let the floating `Chat` overlay take over comms whenever the hangar is up — mirroring
the existing F3 handoff — and ensure it renders above the full-screen hangar.

### 1. Relax the `Chat` handoff gate — `client/scripts/Chat.cs`

Exclude `ShipLoadout.Active` from `LobbyOwnsScreen` (line 135-136):

```csharp
private bool LobbyOwnsScreen => _cm.State == ConnectionManager.ConnState.Connected && _world.LocalShip == null
    && !SectorOverview.Active && !ShipLoadout.Active;
```

`ShipLoadout.Active` (`ShipLoadout.cs:33`, set in `_EnterTree`/`_ExitTree`, lines
102-116) is true exactly while the hangar node is in the tree. With this, on the
hangar screen `LobbyOwnsScreen` becomes false, so:
- `Chat._Process` (line 277) no longer hides the overlay → it renders and stays lit
  (the `_world.LocalShip == null` branch at line 295 already keeps it non-faded).
- `Chat._UnhandledInput` (line 140) no longer early-returns → **Enter opens the input
  box**, Tab toggles team/all, Esc cancels, Enter sends — identical to in-flight.

Update the explanatory comments at `Chat.cs:129-134` and `276` to note the hangar is
now covered too (same reasoning as the F3 case already documented there).

No input-plumbing changes are needed: the hangar's key handler
(`ShipLoadout._UnhandledKeyInput`) only consumes Esc + 1-9 and never Enter, so the
Enter event already reaches `Chat._UnhandledInput` untouched. `ShipLoadout`'s
`MouseFilter.Stop` only blocks mouse, not keyboard. While the chat box is focused,
its `LineEdit` swallows number keys, so typing "1" won't select a hull (same as
flight).

### 2. Draw `Chat` above the hangar — `client/scripts/Hud.cs`

The hangar (`_hangar`) is added to the Hud `CanvasLayer` *after* `Chat`
(`Hud.OpenHangar`, line 241-246), so as a later sibling it currently draws on top and
would visually cover the chat log/input. Raise `Chat` back to the front when the
hangar opens.

- Store the chat node so the Hud can reference it. In `_Ready` (line 155-157) the
  `chat` is a local; assign it to a new field `private Chat? _chat;` (mirrors the
  existing `_hangar`/`_showcase` fields at lines 33-36).
- In `OpenHangar` (line 241-246), after `AddChild(_hangar)`, call
  `_chat?.MoveToFront();` so `Chat` becomes the last Hud child (drawn above the
  hangar). This is a one-shot on open — cheap, no per-frame reordering.

`Chat` has `MouseFilter.Ignore` (`Chat.cs:56`) so raising it does not block the
hangar's buttons; only the transient input row (a `LineEdit`, visible while typing)
sits over the lower-center strip, matching in-flight behavior. The Escape menu and
Settings live on a separate `CanvasLayer` (ModalHost, layer 200), so they still draw
above `Chat` — opening a modal from the hangar is unaffected.

### Scope / non-goals

- **Lobby screen** keeps its own always-focused comms box (Enter-to-send already
  works there); unchanged.
- **Modal dialogs** (Escape menu, Settings, Map picker) intentionally do not float
  chat — they are dismiss-first modals. Only the hangar gap is closed here.

## Files to modify

- `client/scripts/Chat.cs` — extend `LobbyOwnsScreen` with `&& !ShipLoadout.Active`;
  refresh the two nearby comments.
- `client/scripts/Hud.cs` — add `_chat` field; assign in `_Ready`;
  `_chat?.MoveToFront()` after adding the hangar in `OpenHangar`.

## Verification

Build the client and drive it headlessly (per the `verify` skill / `run-client.sh`):

1. **Mandatory spawn hangar** — connect, pick a team, press LAUNCH so the match-start
   spawn hangar opens (`OpenedForSpawn`, Lobby hidden). Press **Enter**: the chat
   input row must appear centered, focused, above the hangar. Type a message + Enter →
   it relays (appears in the top-center log). Tab toggles [ALL]/[TEAM]; Esc cancels
   without launching. Confirm number keys still select hulls only when the chat box is
   **closed**.
2. **F4 browse hangar** — from the pre-match lobby, press F4 to open the hangar over
   the lobby. Enter opens chat the same way; closing the hangar (F4) returns comms to
   the Lobby's own panel (floating `Chat` hides again).
3. **Regression** — in flight and on the F3 map, Enter-to-chat still works and the
   chat still hides on the plain Lobby screen (Lobby comms panel owns it).
4. Optionally capture a screenshot with the chat input open over the hangar
   (`--ui-shot=`) as evidence.

No automated dotnet suite covers the Godot client input path, so verification is via
the running client (protocol version is unchanged — no wire bump).
