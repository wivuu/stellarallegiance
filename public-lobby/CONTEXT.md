# Public Lobby — Identity & Ranking

The hosted service that lists game servers, brokers joins, and keeps each player's identity and
match record across sessions. Vocabulary here is deliberately distinct from the in-match
vocabulary on the game server (see the repo GLOSSARY.md).

## Language

**Player**:
A persistent account in the public lobby, recognised across sessions through external logins.
_Avoid_: user, account, pilot (when meaning the durable identity)

**Pilot**:
A player's presence in one match on one game server.
_Avoid_: player (when meaning the in-match presence), client, connection

**Match**:
One game on one game server, from its start to its end.
_Avoid_: game, session, round

**Listing**:
A game server's live registration in the public lobby; it exists only while the server stays in
contact.
_Avoid_: session, entry, registration

**Public Lobby**:
This service. The bare word "lobby" names the per-server pre-match roster and must not be used
for the service.
_Avoid_: lobby, hub, matchmaker

## Identity

**Display Name**:
The unique, player-chosen name shown everywhere the player appears; a match remembers the name in
force when it was played.
_Avoid_: callsign, handle, username, nickname

**External Login**:
One identity at an outside provider (or a passkey) that proves a player is who they say.
_Avoid_: social login, connection, account

**Device Code**:
The short code a client or game server shows so its operator can approve it from a browser.
_Avoid_: pairing code, auth code, PIN

**Join Token**:
The short-lived, single-use proof a player carries from the public lobby to one specific game
server. (This replaces the retired HMAC join token of the same name.)
_Avoid_: ticket, session token, auth token

**Anonymous Join**:
Joining a game server with a typed callsign and no join token; the only way onto an unverified or
unlisted server, and never recorded against a player.
_Avoid_: guest play, offline play, unauthenticated join

**Admin**:
A player granted authority over public-lobby policy, such as marking game servers as ranked.
_Avoid_: operator, moderator, superuser

## Servers

**Game Server**:
A durable, operator-owned server identity that persists across restarts; it may or may not hold a
Listing at any moment.
_Avoid_: host, sim server, server session

**Operator**:
The player who owns a game server and answers for what it reports.
_Avoid_: host, hosted-by, admin

**Ranked**:
The admin-granted standing that lets a game server's match results move the global ladder.
_Avoid_: official, trusted, verified

**Verified**:
The standing of a listing whose game server has authenticated with the public lobby; an
unverified listing has no operator and can neither receive join tokens nor deliver results.
_Avoid_: authenticated, official, trusted

**Trust Level**:
The public-lobby policy that says whose results move the global ladder: ranked game servers only,
or every verified one.
_Avoid_: mode, security level, ranked mode

## Matches

**Result**:
A game server's final account of one match: outcome, per-team tallies, and per-pilot tallies.
_Avoid_: report, score sheet, ledger

**Abandoned**:
The final state of a match whose game server disappeared before delivering a result.
_Avoid_: cancelled, void, timed out

## Standing

**Ladder**:
Cumulative standings built from what pilots did in counted matches: points, wins, kills, deaths,
ejects.
_Avoid_: leaderboard, scoreboard, high scores

**Rating**:
A player's estimated skill, updated from team wins and losses in ranked matches; the basis of
rank.
_Avoid_: ELO, MMR, score
