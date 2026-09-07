# Admin console for the public lobby

## Context

`/admin` today is one table: game servers, their operator, last-listed time, match count, and a
Ranked toggle (`public-lobby/Pages/Admin.cshtml`, 73 lines). That is the entire admin feature set —
there is no way to find a player, look at a match, or stop a bad actor. The word "ban" does not
appear anywhere in `public-lobby/` or `public-lobby-data/`.

A design was drawn for the console this session and is published at
<https://claude.ai/code/artifact/3ef68780-fa26-40c4-af42-0f6361d0c21b> (working files:
`/private/tmp/claude-501/-Users-erik-projects-wivuullegiance/24904b73-3326-4367-812b-e799226aa67b/scratchpad/admin-design/`).
This plan implements it. Two things the design left open were settled by the user:

- **No admin action log.** Who banned whom, when and why lives on the banned row itself and shows in
  the ban banner. Deletions leave no audit trail — that is the accepted cost.
- **No match discounting.** `/admin/matches/{id}` is read-only: roster, teams, duration, outcome. A
  bad result is answered by unranking or banning the server.

Outcome: an admin can search players, read any match, and ban — reversibly, with a reason and a
duration — either a player or a game server; and can delete a player completely, which is the one
irreversible action and is gated behind a typed confirmation on that player's own page.

## Scope of each screen

| Route | Page | What it shows |
|---|---|---|
| `/admin?tab=servers\|players\|matches&q=&filter=` | rewrite `Admin.cshtml` | tabs with live counts, search box, three filter chips per tab, the list, ban/unban + Ranked actions |
| `/admin/servers/{id:guid}` | new `AdminServer.cshtml` | identity + chips, ban banner, current listing (8 fields, "Drop listing"), Ranked standing panel, recent matches, danger zone |
| `/admin/players/{id:guid}` | new `AdminPlayer.cshtml` | identity + chips, ban banner, 6 stat tiles, linked logins + session line, servers operated, recent matches, danger zone (ban + delete) |
| `/admin/matches/{id:guid}` | new `AdminMatch.cshtml` | facts grid (live: elapsed; final: duration + end reason), one section per team with its roster, read-only result panel |

All four carry `[Authorize(Policy = LobbyRoles.Admin)]` and reuse the existing shell — no nav change
beyond the `/admin` link already in `Pages/Shared/_Layout.cshtml:27-30`.

## Interaction model (deviations from the prototype, deliberate)

The app has no page JS beyond htmx and `passkeys.js`, and every mutation is a server-rendered form
POST. Three prototype behaviours therefore change shape, not substance:

1. **Modals become query-param overlays.** `?ban=<guid>` (and `?confirm=delete`) re-renders the same
   page with the overlay `<div>` on top, exactly as designed. Cancel is a link back to the page
   without the param; confirm is a normal antiforgery-protected POST. No JS, and each state is
   directly addressable in tests.
2. **Search submits, it does not filter as you type.** `<form method="get">` with hidden `tab` and
   `filter` inputs; the box keeps its value from the query string.
3. **The typed delete confirmation is checked on the server.** The button is always enabled; a
   mismatch re-renders with `The display name doesn't match.` in `text-danger`. Comparison is
   `StringComparison.Ordinal` (case-sensitive), as designed.

## Data model

One migration per stage, both in `public-lobby-data/` (`dotnet dotnet-ef migrations add <Name>
--project public-lobby-data`). No new tables, so `tests/PublicLobbyTest/SchemaTests.cs`'s
`ExpectedTables` list is untouched.

**Migration `AdminBans`** — five columns on both `players` and `game_servers`:

```csharp
public DateTimeOffset? BannedAt { get; set; }        // null = never banned
public DateTimeOffset? BanExpiresAt { get; set; }    // null while BannedAt is set = permanent
public string? BanReason { get; set; }
public Guid? BannedByPlayerId { get; set; }          // no FK: the banning admin may later be deleted
public string? BannedByDisplayName { get; set; }     // frozen at ban time, same idea as MatchPilot.DisplayNameAtMatch
```

The same migration adds one index: `matches(started_at desc)`. Every existing index on `matches` is
`(game_server_id, started_at)` (`LobbyDbContext.cs:150`), which does not serve the console's global
"newest first" list; without it that page seq-scans and sorts.

A ban is **in force** when `BannedAt is not null && (BanExpiresAt is null || BanExpiresAt > now)` —
expiry is evaluated at read time, so no reminder or sweeper is needed. Lifting a ban clears all five.
Put the predicate in one place, `public-lobby-data/Bans.cs`:
`static bool InForce(DateTimeOffset? bannedAt, DateTimeOffset? expiresAt, DateTimeOffset now)`.

**Migration `PlayerDeletion`** — the three `Restrict` FKs that actually block deleting a player are
`players.id → asp_net_users.id` (`LobbyDbContext.cs:81`), `join_tokens_issued.player_id`
(`:133`) and `match_pilots.player_id` (`:162`):

- `game_servers.operator_player_id` → **nullable**; a deleted operator orphans their servers, and
  the new **Reassign operator** action on the server page is how one gets adopted again.
- Drop the FK `match_pilots.player_id → players.id` (keep the column and its index). The pilot line
  outlives the account so a match still adds up; `display_name_at_match` becomes `Deleted pilot`.
- Drop the FK `join_tokens_issued.player_id → players.id` too, and **keep the rows**. Deleting them
  would strip the plausibility evidence `MatchGrain.Complete` demands for *every* pilot
  (`MatchGrain.cs:125-141`), so deleting a player who is mid-match would make that match's result
  come back 422 and be dropped — costing everyone else in it the whole game. The rows carry only a
  jti, two ids and timestamps.

## Domain vocabulary

`public-lobby/CONTEXT.md` gains one term under a new **Moderation** heading, and the `Pilot` entry
gains the tombstone sentence:

> **Ban**: A reversible bar on a player or a game server, carrying a reason and either an expiry or
> none. A banned player cannot sign in or receive join tokens; a banned game server cannot list,
> receive join tokens, or report results. Its match record is untouched.
> _Avoid_: suspend, block, kick, blacklist

`GLOSSARY.md` gets a pointer line for Ban next to the existing lobby terms.

## Grain and query changes

**`Grains/GrainContracts.cs`** — a serializable `BanRecord(DateTimeOffset At, DateTimeOffset? Until,
string Reason, Guid? ByPlayerId, string ByDisplayName)` with `bool InForce(DateTimeOffset now)`;
`PlayerSnapshot` and `GameServerSnapshot` each gain `BanRecord? Ban`; `GameServerSnapshot.
OperatorPlayerId` and `GameServerAdminRow.OperatorPlayerId`/`OperatorName` become nullable. New view
records: `PlayerAdminRow`, `MatchAdminRow`, `MatchDetailView`, `PlayerAdminView`, `GameServerAdminView`,
`AdminCounts`.

**`Grains/PlayerGrain.cs`** — `Ban(BanRecord ban)`, `Unban()`, and
`Delete(PlayerDeleteMode mode)` where `mode` is `AnonymisePilots | ErasePilots`. Ban/unban follow the
existing `Persist(Action<Player>)` shape. `Delete` runs one EF transaction: rewrite or remove
`match_pilots` for the player → delete `join_tokens_issued` → delete `sessions` → null the
`operator_player_id` of their `game_servers` → delete the `players` row → remove the `asp_net_users`
row (Identity's own FKs cascade). Then `DeactivateOnIdle()`.

> ADR-0002 note: `match_pilots` is MatchGrain's row, but a fan-out over hundreds of match grains to
> rewrite one column would be far worse than a single statement here, and `MatchGrain` caches only
> the `matches` row — never pilots — so no cache goes stale. `GameServerGrain` *does* cache its row,
> so the operator clear goes through `IGameServerGrain.ClearOperator()` per server, not raw SQL.

**`Grains/GameServerGrain.cs`** — `Ban(BanRecord ban)`, `Unban()`, `ClearOperator()`.

**`Grains/SessionGrain.cs`** — unchanged; `Revoke` already exists. Add
`IQueryGrain.ListSessionLineages(SubjectKind kind, Guid subjectId)` (the `(subject_kind, subject_id)`
index is already there, `LobbyDbContext.cs:96`) so a ban can revoke every lineage.

**`Grains/QueryGrain.cs`** — new read methods, all EF LINQ + `AsNoTracking()` like the existing ones;
counts cached under the existing 5 s `ListTtl`, detail views uncached:

- `AdminCounts()` → servers / players / matches / live matches, for the tab badges.
- `SearchPlayers(string? q, string filter, int limit)` — `q` matches display name (`EF.Functions.ILike`
  with `%`, `_` and `\` escaped in the user's input — both projects set `InvariantGlobalization=true`,
  so never lean on culture-aware casing; the column is `citext`),
  an exact player-id GUID, or an `asp_net_user_logins` provider key/display name; `filter` ∈
  `all|banned|admins`; ordered by `LastSeenAt desc`.
- `SearchGameServers(string? q, string filter)` — name or operator name; `filter` ∈ `all|ranked|banned`.
  Replaces `ListGameServers()`, whose `join` on `players` becomes a **left** join (nullable operator).
- `ListMatches(string? q, string filter, int limit)` — map, game-server name or match id; `filter` ∈
  `all|live|uncounted`; `live` is `Status == Active`; ordered by `StartedAt desc`.
- `MatchDetail(Guid matchId)` — match + server name + operator + `match_teams` + `match_pilots`
  (the first read path that touches `match_teams` outside `MatchGrain.Complete`).
- `PlayerAdminView(Guid id)` — snapshot + operated servers + last 10 matches + active session count +
  last join token issued. The **Identity section** (linked logins, passkeys) is not read here:
  `AdminPlayer.cshtml.cs` injects `UserManager<LobbyUser>` and calls `GetLoginsAsync` /
  `GetPasskeysAsync` exactly like `Pages/Me.cshtml.cs:41-42`, since the manager is scoped and a grain
  has only the `IDbContextFactory`. (`SearchPlayers` still reads `db.UserLogins` directly — that is a
  plain DbSet on `LobbyDbContext`, no Identity API involved.)
- `GameServerAdminView(Guid id)` — snapshot + operator + total matches + last 10 matches.

Duration is not stored: it is `EndedAt - StartedAt`, and `now - StartedAt` while `Active`.

### What the ledger can and cannot show

`MatchGrain.Start` (`Grains/MatchGrain.cs:63-91`) writes **only** the `matches` row — `match_teams`
and `match_pilots` are written at `Complete`. So a **live match has no roster and no scores in the
database**, and the prototype's live state has to be sourced differently:

- **Live roster** comes from the in-memory listing: `IServerRegistry.Get(match.ListingId)?.Roster`
  (`LobbyRosterEntry(Name, Team, Ready, Flying, PlayerId?)`, `Contracts.cs:46`). Names and teams
  only — there are no kills/deaths/points for a match in progress, so the live team sections show a
  roster and no tallies, and "Pilots 14 / 32" comes from the listing's `Players`/`MaxPlayers`. If the
  listing has already gone (the match is awaiting the 10-minute abandon reminder), show `—` rather
  than inventing rows.
- **Pilot count on the matches list** is `match_pilots.Count()` for a finished match and the live
  listing's `Players` for an active one.
- **The "Left" column** is `ConnectedAtEnd ? "to the end" : "left early"`. The prototype's
  `at 31:08` cannot be rendered: no per-pilot leave time is recorded anywhere, and adding one is a
  wire + ingestion change well outside this work.
- **End reason** renders the real enum — `win-condition` / `reset` / `shutdown`
  (`public-lobby-data/Enums.cs:38`), not the prototype's placeholder `home-base-destroyed`.
- **The server page's "Current listing" grid** is `ServerEntry` (`Contracts.cs:52`): State, Pilots
  (`Players`/`MaxPlayers`), Listing id (`SessionId`), Address (`PublicEndpoint`), Password
  (`Protected`), Registered, Last seen, Protocol. There is no `Map` and no transport field on a
  listing — Map and Started come from that server's currently `Active` match if it has one, and the
  prototype's "Transport: WebRTC + WS" cell is dropped (every listing supports both).

## Enforcement — where a ban bites

The sim server burns its stored credential on `invalid_grant` and re-enters the device flow
(`server/Net/LobbyAuthSession.cs:96`), and sets `_needsReauth` on a **401** from `POST /servers`
(`server/Net/LobbyRegistrar.cs:254-259`). **A banned game server must therefore never be refused with
401 or `invalid_grant`** — that would spin it in an approval loop instead of stopping it. Use 403.

| Subject | Seam | Behaviour |
|---|---|---|
| Player | `Auth/LobbyBearerAuthentication.cs:59-78` | after `tokens.Resolve`, if the subject is a **player** whose ban is in force → `AuthenticateResult.Fail` (401). The Godot client refreshes once, then signs out. Makes the ban immediate despite the 60 s `AccessTokenCache`. |
| Player | `Auth/AuthEndpoints.cs:194-202` (`Respond`, player branch) | `access_denied` + "player is banned", beside the existing `player no longer exists` check. Not `invalid_grant`. |
| Player | `Pages/LoginCallback.cshtml.cs:39`, `Hosting/WebAuth.cs:297-329` (passkey assert), `Auth/AuthEndpoints.cs:153-182` (`/login/dev`) | do not issue the cookie; redirect to `/login?banned=<until>` which renders the reason in the existing `border-danger/40 bg-danger/10` callout. |
| Player | `Auth/JoinEndpoints.cs:14-43` | 403 "player is banned" before minting. |
| Player | ban action itself | revoke every session lineage via the new query + `ISessionGrain.Revoke`. |
| Server | `PublicLobby.cs:139-151` (`POST /servers`) | **403** if the game server's ban is in force, **or** its operator's is (the operator snapshot is already loaded at `:145`), **or** it has no operator. |
| Server | `Api/MatchEndpoints.cs:17,56` | **403** for both `POST /matches` and `.../result`. |
| Server | `Auth/JoinEndpoints.cs:27` | a listing whose game server is banned is treated as not joinable (404, same as unverified). |
| Server | ban action itself | `IServerRegistry.Remove(sessionId)` for its live listing, so it leaves the browser at once rather than after the 30 s TTL. |

Matches already in flight complete normally — a banned pilot took their join token before the ban, so
`MatchGrain.Complete`'s plausibility check still passes and the result is not refused for everyone
else. This narrows the prototype's "…or be recorded as a pilot"; that is the right trade.

Three details the prototype implies but does not spell out:

- **Guard rails.** Ban and delete refuse when the target is the acting admin — re-render with
  `You cannot ban yourself.` / `You cannot delete your own account here.` An admin may act on another
  admin; that is deliberate.
- **The sign-in notice** reuses the `error` query parameter `Pages/Login.cshtml` already renders
  (`LoginCallback.cshtml.cs:40` sets it today) rather than inventing a second one; the message carries
  the reason and the expiry.
- **"Drop listing"** on the server page is a POST handler that calls `IServerRegistry.Remove(sessionId)`
  on the injected singleton. `DELETE /servers/{sessionId}` (`PublicLobby.cs:260`) needs the
  registrant's secret, so it cannot be reused. Being an in-memory, process-local registry
  (`ServerRegistry.cs:52`), this — like the listing drop on ban — only works on the replica handling
  the request; the deploy runs a single replica by design.

**Not in scope:** the parts sheet shows the ban notice in the Godot client's server browser too. That
needs a structured refusal on `GET /servers` plus client UI — a wire and client change. Here a banned
player's client sees a plain 401 and signs itself out, and reads the reason on the web sign-in page.

## Staging

Six commits, each leaving the tree buildable (this repo auto-commits and pushes mid-session).

> Ordering note (taken during implementation): the ban schema and grains land **before** the pages,
> so each `.cshtml` is written once with its ban affordances rather than built and then rewritten.
> Same commits, same end state, less churn.

1. **Ban schema + grains.** `AdminBans` migration, `Bans.cs`, `BanRecord`, snapshot changes,
   `Ban`/`Unban` on both grains, `ListSessionLineages`, lineage revoke.
2. **Console + detail pages.** QueryGrain read methods + DTOs; rewrite `Pages/Admin.cshtml(.cs)` with
   tabs, search and filters; the three detail pages; ban/unban buttons, the `?ban=` overlay (reason,
   four durations, server-only "Drop its Ranked flag as well"), ban banners with "Lift ban".
3. **Ban enforcement.** Every row of the table above, including the sign-in notice.
4. *(folded into 2)*
5. **Deletion.** `PlayerDeletion` migration; nullable-operator ripples through `GameServerAdminRow`,
   `ServerHistoryView`, `ListingIdentity`, `QueryGrain`, `PublicLobby.cs:144`; `PlayerGrain.Delete`;
   the danger zone, `?confirm=delete` overlay with the anonymise/erase choice, and the post-delete
   confirmation screen.
6. **Docs + tests.** `CONTEXT.md` Ban term, `GLOSSARY.md` pointer, `public-lobby/README.md` route
   table, `.PLAN/LobbyRankingService.md` §8 progress entry, and the `AdminTests` additions below.

Format only touched files: `dotnet csharpier format <files>` (1.2.6 pinned).

## Files

- Rewrite: `public-lobby/Pages/Admin.cshtml`, `Admin.cshtml.cs`
- New pages: `public-lobby/Pages/AdminServer.cshtml(.cs)`, `AdminPlayer.cshtml(.cs)`,
  `AdminMatch.cshtml(.cs)` — routes `/admin/servers/{id:guid}`, `/admin/players/{id:guid}`,
  `/admin/matches/{id:guid}`
- Grains: `public-lobby/Grains/GrainContracts.cs`, `QueryGrain.cs`, `PlayerGrain.cs`,
  `GameServerGrain.cs`
- Enforcement: `public-lobby/Auth/LobbyBearerAuthentication.cs`, `Auth/AuthEndpoints.cs`,
  `Auth/JoinEndpoints.cs`, `Api/MatchEndpoints.cs`, `PublicLobby.cs`,
  `Pages/LoginCallback.cshtml.cs`, `Pages/Login.cshtml(.cs)`, `Hosting/WebAuth.cs`
- Data: `public-lobby-data/Entities/Player.cs`, `GameServer.cs`, `LobbyDbContext.cs`, new `Bans.cs`,
  two migrations
- Tests: `tests/PublicLobbyTest/AdminTests.cs` (extend), `Program.cs` untouched (same section)
- Docs: `public-lobby/CONTEXT.md`, `public-lobby/README.md`, `GLOSSARY.md`,
  `.PLAN/LobbyRankingService.md`

## Reuse, not reinvention

- Tokens and classes come from `public-lobby/Styles/app.css`'s `@theme` and the patterns already in
  `Pages/`: table skeleton `Ladder.cshtml:19-51`, stat tiles `Players.cshtml:20-28`, chips
  `Players.cshtml:14` and `Index.cshtml:85`, callouts `Device.cshtml:15-29`, danger button
  `Me.cshtml:76`, inline POST form `Admin.cshtml:52-64`, state colours `Index.cshtml.cs:41-48`.
  The prototype was drawn from these, so its hex values map 1:1 onto the tokens — use the token, not
  the hex. `.bracket-frame` (`app.css:48-71`) wraps the delete overlay.
- Page-model shape: primary-constructor DI, `IQueryGrain` via `grains.GetGrain<IQueryGrain>(0)` for
  reads and the owning grain for writes, `NotFound()` on a null view, `RedirectToPage()` after POST.
- Ban/unban/delete POSTs follow `OnPostRankedAsync` (`Admin.cshtml.cs:31-38`) exactly.

## Verification

```sh
docker run -d --name lobby-pg -e POSTGRES_PASSWORD=pg -p 55432:5432 postgres:17-alpine
env "ConnectionStrings__postgres-database=Host=localhost;Port=55432;Username=postgres;Password=pg;Database=lobby" \
  dotnet run --project public-lobby -- --migrate          # twice: must be idempotent
dotnet dotnet-ef migrations has-pending-model-changes --project public-lobby-data   # must be clean
dotnet run --project tests/PublicLobbyTest                # needs Docker; whole suite must stay green
```

Then a live pass with `AUTH_DEV_LOGIN=true LOBBY_ORLEANS_CLUSTERING=localhost PORT=8091
LOBBY_PUBLIC_URL=http://localhost:8091 dotnet run --project public-lobby` (a `dotnet build` first, so
Tailwind regenerates `wwwroot/app.css` for the new markup), signing in with
`curl -c jar "$L/login/dev?displayName=Root"` under `LOBBY_ADMINS=name:Root`, and a sim server paired
by device code so there is a real listing and a real match to look at:

1. All three tabs render, counts are right, search and each filter chip narrow the list.
2. A match opens from the list and shows both rosters; a live match shows elapsed time, a finished
   one shows duration and end reason.
3. Ban a game server with a reason and 7 days → its listing leaves `GET /servers` immediately, its
   next `POST /servers` is 403 (and its log shows no re-auth loop), `POST /matches/.../result` is 403,
   the row and its page show the banner. Lift it → it lists again only after re-registering.
4. Ban a player → their `GET /servers` is 401, `POST /servers/{id}/join` is 403, `/login/dev` refuses
   with the notice, and a server they operate stops listing.
5. Delete a player who has matches and operates a server, once per mode → the ladder loses them, the
   match keeps (or loses) their pilot line, the server shows no operator and refuses to list, and the
   display name can be taken again.

New `AdminTests` cases, in the existing `Suite` section and idiom (`ExtractAntiforgery` +
`FormUrlEncodedContent`, HTML string assertions):

- non-admin gets 403 on each of the three new routes, and on every new POST handler;
- `?tab=players&q=` finds a seeded player, `&filter=banned` empties until one is banned;
- the ban POST persists through the grain and the row renders `Banned`; unban clears it;
- a banned server's `POST /servers` is 403 and its result POST is 403;
- a banned player's `POST /servers/{listingId}/join` is 403;
- an expired ban (`BanExpiresAt` in the past, set through the grain) is not in force;
- delete in both modes: the player is gone from `/ladder`, `GET /players/{name}` is 404, the pilot row
  is either `Deleted pilot` or absent, and the operated server survives with no operator;
- the typed confirmation is case-sensitive: `TUULIKKI` re-renders with the error and deletes nothing.

Note for the report: there is no CI running these suites; they are run by hand on a box with Docker.

---

## Corrections from the design review

A Plan agent stress-tested this plan against the code after it was written. Its valid findings, and
what changed:

**Blockers, all now folded in above or into the code:**

1. **Deleting `sessions` rows behind `SessionGrain`'s back leaves working tokens.** `ValidateAccess`
   (`SessionGrain.cs:80-95`) answers purely from the grain's in-memory `_rows`, so a raw `DELETE`
   revokes nothing until that activation happens to die. `Delete` uses the same
   `ListSessionLineages` → `ISessionGrain.Revoke` path a ban does, *then* deletes the rows.
2. **Deleting `join_tokens_issued` breaks other pilots' in-flight matches** — see the migration
   section above. The rows are kept; their FK is dropped instead.
3. **`/device` is a cookie-authed ban bypass.** `Pages/Device.cshtml.cs:38-61` is `[Authorize]`
   (cookie), and `DeviceCodeGrain.Approve` (`:143-145`) mints a **brand-new** `game_servers` row. A
   banned player holding a live cookie could pair a fresh server. Approve/Deny now check the ban.
4. **`LadderByServer`'s inner join silently corrupts totals and ranks** once a pilot line outlives its
   player: `QueryGrain.cs:147` joins `players` *after* `Skip/Take`, so an anonymised pilot vanishes
   from the page while still counting toward `total`, and every rank below it shifts. It becomes a
   left join falling back to `DisplayNameAtMatch`.
5. **Orphaning an operator was a one-way door.** `GameServerGrain.Create` refuses an existing id
   (`:42-43`) and every device approval mints a fresh one (`DeviceCodeGrain.cs:143-144`), so an
   orphaned server could never be adopted. Hence the new **Reassign operator** action (a display
   name, resolved through `FindPlayerIdByDisplayName`) on `/admin/servers/{id}`.

**Also acted on:**

- **Cookies outlive a ban** by up to Identity's 30-minute security-stamp revalidation. Banning now
  bumps the stamp (`UserManager.UpdateSecurityStampAsync`) and the two cookie pages that can do
  damage — `/device` approve/deny and `/me` rename — check the ban directly.
- **`GET /servers/events`** (`PublicLobby.cs:277-320`) authenticates once and then streams forever;
  the keepalive tick re-checks the ban and closes the stream.
- **A banned game server re-POSTs `/servers` every 5 s forever** (`LobbyRegistrar.cs:176-196` only
  backs off 5 s and only re-auths on 401). `LobbyRegistrar` gains a longer backoff on 403 and logs
  the refusal body, so a ban quiets the server down instead of leaving a permanent 12 req/min loop.
- **`QueryGrain` is `[StatelessWorker(1)]` and non-reentrant**, so one admin search would block every
  visitor's `/ladder`. Its read methods are marked `[ReadOnly]` (the repo's own stated rule; they are
  all `AsNoTracking` and never write).
- **`provider_display_name` is the scheme name** (`"GitHub"`, `"Google"`) for every user of that
  provider — `AccountService.cs:82-85` takes it from `GetExternalLoginInfoAsync`. Player search
  matches `provider_key` only, and the placeholder says "linked login id".
- **The counts are uncached**, like the lists: a badge that lagged a ban by 5 s while the row beneath
  it already read `Banned` would read as a bug.
- Two deleted pilots in one match both rendered `Deleted pilot`; the tombstone carries a short id.

**Accepted as correct, not changed:**

- **403 on `POST /matches/{id}/result` drops the result permanently** (`LobbyMatchReporter.cs:253-256`
  treats 403 as terminal). That is the ban: results a banned server produces are exactly the ones not
  to trust. "Its match record is untouched" refers to matches already reported, which is true.
- **A game server ban is re-mintable** — the operator can delete `SIM_AUTH_FILE` and re-pair for a new
  id. The **operator** ban is the durable lever; the docs say so.
- **`filter=live` counts zombie `Active` matches** whose abandon reminder was lost to a silo restart.
  Pre-existing, out of scope.
- The Godot client needs two consecutive 401s to sign out (`ServerLobbyOverlay.cs:555-566`) and the
  second never arrives, so a banned player's client goes quiet rather than signing out. Cosmetic; the
  web sign-in page is where the reason is read.
