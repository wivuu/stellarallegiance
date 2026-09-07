# Public lobby: accounts, Verified servers and the ladder — what shipped and how to run it

This is the human guide to the identity / persistence / ranking work delivered on the
`auth-lobby-ranking` branch (design: `.PLAN/LobbyRankingService.md`, language:
`public-lobby/CONTEXT.md`, decisions: `docs/adr/0001-*.md`, `docs/adr/0002-*.md`). Operator
reference lives in `public-lobby/README.md`; day-to-day recipes in the `/public-lobby` skill.

## 1. What was delivered

**For players**
- The Godot client asks you to sign in on first launch. It shows an 8-letter code, opens the
  lobby's approval page in your browser, and signs in once you click Approve. Passkeys always work;
  Google, GitHub and Steam appear when the lobby has them configured. "Continue without account"
  keeps direct-by-address joins only (the public list needs a session).
- The server browser badges every listing **VERIFIED** (the server authenticated with the lobby;
  its operator's name is shown; results count) or **UNVERIFIED** (anonymous joins only, never
  reports results). Joining a Verified server uses a single-use, 60-second join token and puts you
  in the roster under your account name.
- An in-client **ACCOUNT** page: change your display name (unique, 3–24 characters), see your
  career line and linked logins, open the web account page, sign out.
- Web pages on the lobby: `/login`, `/me` (passkeys, linked logins, sign out everywhere),
  `/ladder` (global standings from ranked matches), `/players/<name>` (profile + recent matches),
  `/servers/<id>/history` (a server's matches and its own ladder), `/admin` (admins only).

**For server operators**
- A published server (`SIM_PUBLIC_NAME` set) pairs once: on first boot it prints a device code and
  an approval URL and stays unlisted until you approve it in the browser. The credential is saved
  to `SIM_AUTH_FILE` (default next to `sim-cache/`) and every restart re-lists silently under your
  name. `SIM_HOSTED_BY` is gone.
- While listed as Verified, every joiner must present a join token; the server takes the pilot's
  name and player id from it. Unlisted or private (`-Local`) servers behave exactly as before, so
  `--autofly` and the other harnesses are unchanged.
- Match results are reported to the lobby through a disk spool (`SIM_REPORT_SPOOL`) that survives
  lobby outages and server restarts.

**For lobby admins**
- `LOBBY_ADMINS` names the admins; `/admin` lists every authenticated game server with its operator
  and a **Ranked** toggle. Only ranked servers move the global ladder when `RANKED_RESULTS=flagged`
  (the default); `RANKED_RESULTS=authenticated` ranks every Verified server. A result is refused
  whole if any pilot in it never took a join token for that server.

**Under the hood**
- The lobby is now stateful: ASP.NET Core Identity + EF Core on Postgres, with a co-hosted
  Orleans silo. Each entity grain is the single writer of its rows; listings and WebRTC signaling
  stay in memory. Join tokens are ES256 JWTs verified offline by game servers against the lobby's
  JWKS. Protocol version is 38.

## 2. Deploying the lobby (Railway)

The lobby needs a Postgres database and a handful of environment variables. Everything else is
already in `public-lobby/Dockerfile` and `scripts/deploy-railway-lobby.ps1`.

1. **Deploy or update the service** from the repo root:
   ```sh
   scripts/deploy-railway-lobby.ps1            # project wivuu-public-lobby
   ```
2. **Attach Postgres.** In the Railway project add a *Postgres* database service. Copy its
   `DATABASE_URL` into the lobby service as the variable `ConnectionStrings__postgres-database`
   (the lobby normalizes the `postgres://` URL form itself; the reference syntax `${{Postgres.DATABASE_URL}}`
   works too).
3. **Pre-deploy migrations.** Lobby service → Settings → Deploy → *Pre-deploy command*:
   ```
   dotnet PublicLobby.dll --migrate
   ```
   It creates the database if missing, applies the EF Core and Orleans migrations, exits 0, and is
   safe to run on every deploy.
4. **Required variables** on the lobby service:

   | Variable | Value |
   |---|---|
   | `LOBBY_PUBLIC_URL` | `https://<lobby-domain>` — must equal the `PUBLIC_LOBBY` that servers and clients dial; it is the join-token issuer, the device-code link base and the passkey relying-party domain |
   | `LOBBY_ADMINS` | comma list, e.g. `name:Erik,github:erikoleary` (`google:<sub>`, `steam:<steamid64>` also work) |
   | `RANKED_RESULTS` | `flagged` (default) or `authenticated` |
   | `ALLOW_UNVERIFIED_SERVERS` | `false` (default) or `true` |

   Optional providers (section 4): `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`,
   `AUTH_GITHUB_CLIENT_ID`, `AUTH_GITHUB_CLIENT_SECRET`, `AUTH_STEAM_API_KEY`.
   **Never** set `AUTH_DEV_LOGIN` in production — it is the passwordless test grant.
5. **Verify.**
   ```sh
   curl -s https://<lobby-domain>/health          # public-lobby
   curl -s https://<lobby-domain>/health/orleans  # orleans:ok
   open  https://<lobby-domain>/login             # passkey sign-up works with no providers configured
   ```
   If the deploy log says `connection string 'postgres-database' is not set`, step 2 is missing.
6. **Become admin.** Sign in on `/login` with a login that matches `LOBBY_ADMINS`; the Admin link
   appears in the nav. Toggle Ranked on the servers you trust.

**Single box instead of Railway:** `docker compose up --build` now starts `lobby-db`, runs the
one-shot `lobby-migrate`, then `public-lobby` and `sim-server`; every variable above is in
`docker-compose.yml` with comments (`LOBBY_DB_PASSWORD` for the database).

## 3. Deploying / pairing a game server

1. Run the server with `SIM_PUBLIC_NAME="Your Server"` and `PUBLIC_LOBBY=https://<lobby-domain>`
   (both scripts and the compose file already do this; `-Local` keeps it private).
2. On first boot the log prints:
   ```
   Approve this game server at the public lobby: https://<lobby>/device?user_code=XXXX-XXXX
   ```
   Open the link, sign in, click **Approve**. The server registers within seconds and lists as
   Verified with your display name as operator.
3. The credential lands in `SIM_AUTH_FILE` (`lobby-auth.json` beside `sim-cache/`, mode 0600). Keep
   it on a volume for containers, or you pair again after every rebuild. Delete it to re-pair under
   another operator.
4. Results spool to `SIM_REPORT_SPOOL` (`report-spool/` beside `sim-cache/`); leftovers are re-sent
   on the next boot.

## 4. Creating the OAuth apps (step by step)

All providers are optional; passkeys work without any of them. The lobby registers a provider only
when both of its variables are set, so you can add them one at a time. Callback paths are the
ASP.NET defaults: `/signin-google`, `/signin-github`, `/signin-steam`.

### GitHub

1. Go to <https://github.com/settings/developers> → **OAuth Apps** → **New OAuth App**
   (or Organization settings → Developer settings for an org-owned app).
2. Fill in:
   - **Application name**: `Stellar Allegiance Lobby` (shown to players on the consent screen).
   - **Homepage URL**: `https://<lobby-domain>`.
   - **Authorization callback URL**: `https://<lobby-domain>/signin-github` — exact match, https.
3. Click **Register application**. Copy the **Client ID**.
4. Click **Generate a new client secret**, copy it immediately (it is shown once).
5. Set on the lobby service: `AUTH_GITHUB_CLIENT_ID=<client id>`,
   `AUTH_GITHUB_CLIENT_SECRET=<secret>`. Redeploy.
6. Check: `/login` shows **Continue with GitHub**; signing in lands on `/me` with the login
   linked. The default display name is your GitHub login (uniqueness adds digits if taken). For
   `LOBBY_ADMINS` use `github:<your github login>`.

For local testing register a second app with callback `http://localhost:8091/signin-github`
(GitHub allows one callback per app).

### Google

1. Go to <https://console.cloud.google.com/> and create a project (e.g. `stellar-allegiance-lobby`)
   or pick an existing one.
2. **APIs & Services → OAuth consent screen** (Google calls it *Branding* / *Audience* in the
   newer UI):
   - User type **External**; app name `Stellar Allegiance Lobby`; support email; developer contact.
   - Scopes: only the defaults (`openid`, `email`, `profile`) — the lobby asks for nothing more.
   - While the app is in **Testing**, only listed test users can sign in (add yourself). Click
     **Publish app** when you want everyone in; the basic scopes need no verification review.
3. **APIs & Services → Credentials → Create credentials → OAuth client ID**:
   - Application type **Web application**, name `lobby`.
   - **Authorized JavaScript origins**: `https://<lobby-domain>`.
   - **Authorized redirect URIs**: `https://<lobby-domain>/signin-google` (add
     `http://localhost:8091/signin-google` too for local runs — Google allows several).
4. Click **Create**; copy the **Client ID** and **Client secret** from the dialog (also
   downloadable as JSON).
5. Set `AUTH_GOOGLE_CLIENT_ID` and `AUTH_GOOGLE_CLIENT_SECRET` on the lobby service. Redeploy.
6. Check: **Continue with Google** appears on `/login`. Default display name is your given name.
   For `LOBBY_ADMINS` use `google:<sub>` — the `sub` is the numeric account id in the linked-login
   row on `/me`.

### Steam (Web API key)

1. Sign in at <https://steamcommunity.com/dev/apikey>, enter your lobby domain as the *Domain
   Name*, agree, and copy the key.
2. Set `AUTH_STEAM_API_KEY=<key>`. Steam OpenID needs no app registration; the key only fetches the
   persona name used as the default display name. For `LOBBY_ADMINS` use `steam:<steamid64>`.

### Rotating a secret

Set the new value on the service and redeploy; existing sessions and linked logins are unaffected
(they are keyed by the provider's user id, not the secret).

## 5. Next steps

**Before players use it**
1. Create the OAuth apps above and set the variables (passkey-only is fine to start).
2. Attach Railway Postgres, set the pre-deploy migrate command, `LOBBY_PUBLIC_URL` and
   `LOBBY_ADMINS`, redeploy, and run the checks in section 2.
3. Sign up once in a real browser on `/login` with a passkey — the passkey ceremony was verified at
   the API level and in unit tests but not clicked through in a browser yet.
4. Pair each community game server (section 3) and mark the trusted ones **Ranked** on `/admin`.
5. Merge `auth-lobby-ranking` into `master` (the client, server and lobby move together — protocol
   38 clients cannot join protocol 37 servers, and the old lobby has no accounts).

**Known gaps and follow-ups**
- A win-condition match has not been driven end to end headlessly (it needs a base kill); the
  reset and shutdown result paths and the ingestion rules are covered by the suites and an
  end-to-end run.
- A listing's id changes each time a server re-registers; the client automatically retries a
  rejected join with a fresh token, so a mid-join re-registration costs one retry.
- Slice 2 in the plan: Glicko-2 team rating once real match data exists, Steam session tickets
  when there is an AppID. Slice 3: listings and signaling into grains for a second lobby replica,
  Apple login, loadout persistence.
- No CI runs the suites; `tests/PublicLobbyTest` needs Docker (`dotnet run --project
  tests/PublicLobbyTest`) and `tests/LobbyTest` covers the server side.
