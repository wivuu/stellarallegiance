# Locked-server password modal

## Context

The Claude Design "Server Lobby" mockup includes a **locked-server password modal** (amber-accented
`sc-if showLockPass` block): when a pilot joins a password-protected server, a modal prompts for the
passphrase, shows an "Incorrect password — access denied" error on a bad attempt, and only then
connects. We are implementing **only that password dialog** from the design — not the rest of the
mockup (server rows/teams/settings already exist or are out of scope).

Today the codebase has the auth *mechanism* but no UX for it:
- A server run with `--secret`/`SIM_SECRET` uses `SharedSecretAuthenticator`; the client sends the
  secret in the Hello frame (`GameNetClient.SendHello`, seeded by `SetJoinSecret`). On a mismatch the
  server calls `CloseAsync("bad secret")` (`server/Net/ClientHub.cs:486`).
- The **direct-connect modal** already has an inline password field (`ServerLobbyOverlay.BuildConnectModal`).
- **Gaps:** (1) the public-lobby browser never advertises which servers are locked and offers no way to
  enter a password for them; (2) a `"bad secret"` rejection is swallowed — the client shows a generic
  "LINK FAILED / Link dropped during authenticate" and the pilot can't tell the password was wrong.

**Goal (per user):** advertise "protected" so locked lobby servers open a dedicated password modal
*before* dialing, **and** surface the rejection when a join is refused for a bad/missing secret
(especially direct-IP joins) so the pilot can re-enter the password.

## Approach

### 1. Advertise a `Protected` flag end-to-end

- **`public-lobby/Contracts.cs`** — add `bool Protected = false` to `RegisterRequest` (after `Roster`)
  and to `ServerEntry` (after `Roster`). `ServerEntry` is a record serialized directly to SSE/GET, so
  the flag auto-flows to clients; `HeartbeatRequest` needs no change (heartbeat's `with { … }` preserves
  `existing.Protected`).
- **`public-lobby/ServerRegistry.cs:69`** — pass `Protected: req.Protected` into the `new ServerEntry(…)`.
- **`server/Net/LobbyRegistrar.cs`** — add a `bool protected` ctor param + `_protected` field; in
  `FromEnv` (line ~78) accept and forward it; include `protected = _protected` in the register POST body
  (`RegisterAndListen`, the anonymous object at lines ~194-205).
- **`server/Program.cs:330`** — `LobbyRegistrar.FromEnv(hub, port, secret.Length > 0)` (the `secret`
  local is already computed at line 76).
- If a lobby-contract JSON schema exists under `schemas/`, add the `protected` property there too.

### 2. Browser: show the lock + gate the join

In **`client/scripts/ServerLobbyOverlay.cs`**:
- Add `bool Protected` to the private `ServerDto` record (line 33) — case-insensitive JSON fill picks it
  up automatically.
- **`ServerRow`** (line 769): add a `_protected` field set in `Configure`; in `_Draw`, when protected,
  draw a small amber padlock glyph (e.g. `"⚿"` in `DesignTokens.Warn`) just after the server name.
- **`RenderDetail`** (line 603): when `sel.Protected`, add a small amber "PROTECTED" `StatusPill`
  (`StatusPill.Kind.Warn`) into the `titleRow` next to the name.
- **`Join(ServerDto s)`** (line 731): if `s.Protected`, open the new modal instead of dialing —
  `ServerPasswordModal.Open(this, _cm, s.Name, pw => { _cm.SetJoinSecret(pw); <dial as today>; })`,
  where `<dial>` is the existing `ConnectTo(s.PublicEndpoint, s.Name)` / `ConnectToLobby(s.SessionId, s.Name)`
  branch. Non-protected servers keep dialing directly. (`CommitName()` still runs first.)
- Keep the direct-connect modal's inline password field as-is (initial attempt for typed addresses).

### 3. New `client/scripts/ui/ServerPasswordModal.cs`

Follow the **`MapPickerModal`** scaffold exactly (static `Active` gate + `Open(...)` → `ModalHost.Ensure`,
`_EnterTree/_ExitTree`, `_Input` Esc→`Close`, `MenuOpen/MenuClose` SFX, scrim + `CenterContainer` +
`BracketPanel`, header with `✕` icon button, footer buttons), but **amber-accented**:
- `BracketPanel { FillOverride = DesignTokens.PanelDeep, Accent = DesignTokens.Warn, CustomMinimumSize = new Vector2(460,0) }`.
- Header: amber padlock glyph + `UiKit.MakeLabel("PROTECTED SERVER", Label, DesignTokens.Warn)` eyebrow
  + server name (`TextStyle.Title`) + `✕` close (`ButtonVariant.Icon`).
- Body copy (`TextStyle.Data`, `Text2`): "This server requires a password to join. Enter the passphrase
  provided by the host." (Drop the mockup's per-team wording — teams are chosen in-game, not at join.)
- `"SERVER PASSWORD"` label + `LineEdit { Secret = true, PlaceholderText = "••••••••" }` (mono font, matches
  the direct-connect password field). `GrabFocus()` on ready; `TextSubmitted += _ => Submit()` (Enter submits).
- Error line (hidden unless error): `"✕ Incorrect password — access denied."` in `DesignTokens.DangerText`;
  when in error state, override the `LineEdit` normal/focus stylebox to a red (`DesignTokens.Danger`) border.
- Footer: `CANCEL` (`ButtonVariant.Secondary`, → `Close`) + `"⚿ UNLOCK & JOIN"` (`ButtonVariant.Primary`,
  → `Submit`).
- **State/behavior:** fields `_cm`, `_serverName`, `Action<string> _onSubmit`, `bool _error`. `Open`
  variants: `Open(context, cm, name, onSubmit, error=false)`. `Submit()`: trim password; if empty →
  set error state inline and return (client-side guard; the real check is server-side). Else `Close()`
  then `_onSubmit(password)`.

### 4. Surface the rejection (bad/missing secret) — the failure path

Route the WebSocket close reason so a `"bad secret"` refusal becomes a distinct, actionable state
(covers direct-IP joins and wrong passwords on protected servers).

- **`client/scripts/GameNetClient.cs`** — in `RunWebSocket` capture the close reason when the server
  closes (`r.CloseStatusDescription`, available on the `Close` receive result, line ~439) and thread it
  through the closed callback: `OnSocketClosed(string reason = "")` (line 609) → `_cm.NotifyDisconnected(reason)`.
  (Only the terminal-failure branch reads it — see below — so live-drop auto-reconnect is unaffected.
  WebRTC `dc.onclose` carries no reason, so WebRTC-only protected servers fall back to the generic
  failure; note this limitation.)
- **`client/scripts/ConnectionManager.cs`** — add `public bool AuthRejected { get; private set; }`,
  reset it in `BeginStages()`; give `NotifyDisconnected` an optional `string reason = ""` param, and in
  its terminal `State = ConnState.Failed` branch (line ~399) set `FailReason = reason` and
  `AuthRejected = reason == "bad secret"`. Leave the `Connected`/`Reconnecting` branches untouched.
- **`client/scripts/ConnectLinkModal.cs`** — in the `failed` branch (lines ~317-323), when
  `_cm.AuthRejected`: show tailored copy ("Incorrect password — access denied. Re-enter the server
  passphrase.") and repurpose the `RETRY` button to open the password modal in error mode:
  `ServerPasswordModal.Open(this, _cm, _cm.ServerDisplayName, pw => { _cm.SetJoinSecret(pw); _cm.RetryLast(); }, error: true)`.
  `RetryLast()` re-dials the same target over the same transport with the new secret. Non-auth failures
  keep today's generic RETRY behavior.

## Critical files

- `public-lobby/Contracts.cs`, `public-lobby/ServerRegistry.cs`
- `server/Net/LobbyRegistrar.cs`, `server/Program.cs`
- `client/scripts/ServerLobbyOverlay.cs` (ServerDto, ServerRow, RenderDetail, Join)
- `client/scripts/ui/ServerPasswordModal.cs` (**new**)
- `client/scripts/GameNetClient.cs`, `client/scripts/ConnectionManager.cs`, `client/scripts/ConnectLinkModal.cs`
- Reuse: `MapPickerModal.cs` (pattern), `ModalHost`, `BracketPanel`/`StatusPill` (`ui/Surfaces.cs`,
  `ui/DataFeedback.cs`), `UiKit`, `DesignTokens.Warn`, `UiFonts.Mono`.

## Verification

1. **Build**: `dotnet build` the server + public-lobby; build the Godot client
   (`godot --headless --import` then compile) to confirm no errors.
2. **Failure path (no lobby needed)** — run a protected server locally:
   `SIM_SECRET=hunter2 dotnet run --project server -- --port 8090 --autostart`.
   Launch the client, open the direct-connect modal, enter `localhost:8090` with a **wrong** password →
   expect the connecting modal to fail with "Incorrect password — access denied" and a RETRY that opens
   the amber `ServerPasswordModal` (error state) → enter `hunter2` → connects.
3. **Pre-emptive path** — run a local public-lobby (`dotnet run --project public-lobby`) and register the
   protected server against it (`PUBLIC_LOBBY=http://localhost:<port> SIM_PUBLIC_NAME="Locked Test"
   SIM_SECRET=hunter2 …`). Point the client at that lobby (`--lobby localhost:<port>`); confirm the row
   shows the amber padlock + detail "PROTECTED" pill, and `◆ CONNECT TO SERVER` opens the password modal
   *before* dialing; correct password connects, wrong password shows the error.
4. **Regression**: an **open** server (no `SIM_SECRET`) shows no padlock and connects with no modal, and a
   normal live-link drop still auto-reconnects (reason path must not disturb the `Connected` branch).
5. Run the server/lobby dotnet test suites (records gained defaulted fields — should stay green).
6. Optional visual check: add a `ServerPasswordModal` entry to `UiShowcase` (or a debug key) to inspect
   normal vs error styling against the design.
