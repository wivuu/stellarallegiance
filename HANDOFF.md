# Agent Handoff — wivuullegiance

## Current state

T0 and **T1 are complete and passing**. The repo has:
- A SpacetimeDB C# module with the **full game schema** (7 tables + 2 enums + scheduled SimTick) published to a local server, seeded with 1 Match, 2 Bases, 30 Asteroids
- The 20 Hz `SimTick` scheduled reducer is live (stub body — increments `Match.Tick` only; full sim is T4)
- A Godot 4.6.3 C# client that connects and logs its identity
- Regenerated `client/module_bindings/`; `dotnet build` succeeds (one harmless generated-code warning)

**Next task: T2 — client subscriptions + rendering** (see `.PLAN/08-BUILD-ORDER.md`). `ConnectionManager.cs` still only connects; it needs to subscribe to the tables and surface rows.

---

## Environment

| Thing | Detail |
|-------|--------|
| SpacetimeDB server | Docker on **host port 3001** (port 3000 is taken) |
| Database name | `stellar-allegiance` |
| Godot | `~/.local/bin/godot` → Godot 4.6.3 mono |
| .NET SDK | 10.0 (host) |

> **Identity persistence (important — read before publishing).** Each `docker run` of
> the CLI gets a *fresh* identity unless you mount a persistent config dir. If you publish
> with a different identity than the one that created the DB, you get `403 ... reset database`
> / "not authorized". The repo keeps a persistent CLI config in **`.stdb-config/`** (gitignored,
> holds a token). **Always mount it** (`-v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime`)
> on every publish/sql/generate-needing-auth call. Then iterative republish "just works"
> ("Updated database"). **After a server restart**, the server rotates its signing key, so the
> saved token goes stale (`401 Invalid token`) — refresh it once with the `login` command below.

**Start the server** (do this first; data does NOT persist across container restarts):
```bash
docker run -d --rm -p 3001:3000 --name stdb clockworklabs/spacetime start
```

**Refresh the CLI token** (run once after every fresh server start):
```bash
docker run --rm -v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime --network host \
  clockworklabs/spacetime login --server-issued-login http://localhost:3001
```

**Publish module** (run from repo root after any change to `module/`):
```bash
docker run --rm \
  -v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime \
  -v "$(pwd)/module":/workspace -w /workspace --network host \
  clockworklabs/spacetime publish stellar-allegiance \
    --server http://localhost:3001 --yes
```
> Breaking schema change on an existing DB? Add `--delete-data` (a **boolean flag** — NOT
> `--delete-data always`). Only the owning identity can reset; with `.stdb-config` mounted that's you.

**Regenerate bindings** (run from repo root after schema changes; no server/auth needed):
```bash
docker run --rm -v "$(pwd)":/workspace -w /workspace \
  clockworklabs/spacetime generate --lang csharp \
    --out-dir client/module_bindings \
    --module-path module/spacetimedb \
    --yes
```

**Build client:**
```bash
cd client && dotnet build
```

---

## T1 (DONE): schema + seed data

`module/spacetimedb/Lib.cs` now holds the full schema, seed, and lifecycle reducers:
- **7 tables** (`Player`, `Ship`, `ShipInput`, `Base`, `Asteroid`, `Projectile`, `Match`),
  all `Public = true`, plus the scheduled `SimTickTimer` table.
- **2 enums**: `ShipClass { Scout, Fighter }`, `MatchPhase { Lobby, Active, Ended }`
  (no explicit values — SpacetimeDB forbids them; order defines them).
- **Reducers**: `Init` (seeds 1 Match singleton, 2 Bases at ±500 on X, 30 Asteroids via
  deterministic `ctx.Rng`, and schedules SimTick @ 20 Hz), `ClientConnected`
  (create/reactivate Player + team-balance assignment + Lobby→Active when both teams ready),
  `ClientDisconnected` (Online=false, destroy ship). `SimTick` is scheduled and **stubbed** —
  it only increments `Match.Tick` (full integration is T4).

Verified: `SELECT * FROM Match` = 1 row (Phase=Lobby; `Tick` is a moving number because the
scheduler is live), 2 Bases at distinct positions, 30 Asteroids, `dotnet build` clean.

### Re-verify any time (sql check)
```bash
docker run --rm -v "$(pwd)/.stdb-config":/home/spacetime/.config/spacetime --network host \
  clockworklabs/spacetime sql stellar-allegiance \
    "SELECT id,tick,phase,winner FROM Match" \
    --server http://localhost:3001
```

## Next: T2 — client subscriptions + render

Per `.PLAN/05`/`07`, `ConnectionManager.cs` should `.SubscribeToAllTables()` (or explicit SQL
subs) after connect and wire `OnInsert`/`OnUpdate`/`OnDelete` for the tables the client renders.
The generated `DbConnection` in `SpacetimeDB.Types` now exposes all the new tables/reducers.

---

## Key facts to avoid repeating past mistakes

- The SDK builder uses **`WithDatabaseName()`** not `WithModuleName()`.
- `OnConnectError` callback signature is `Action<Exception>`, not `Action<ErrorContext>`.
- **SpacetimeDB enums forbid explicit values.** `enum ShipClass : byte { Scout = 0 }` fails
  with `BSATN0002`. Declaration order defines the value — write `{ Scout, Fighter }`.
- **`--delete-data` is a boolean flag**, not `--delete-data always` (that errors with
  "unexpected argument 'always'"). The `always`/`migrate` style values belong on `--yes`.
- Mount **`.stdb-config/`** on every authenticated CLI call so the identity stays stable
  (see the Environment section). A bare `docker run` gets a new identity → `403`/`401`.
- Don't mount a *named* docker volume at the CLI config path — it's root-owned and the
  container user can't use it (the publish hangs silently). Use the host-path bind mount.
- `spacetime init` puts the C# project in `module/spacetimedb/` (not `module/` directly). The `module/spacetime.json` ties it together. **Do not move these files.**
- The module's `CLAUDE.md` (generated by `spacetime init`) has accurate 2.x syntax — treat it as the ground truth for attribute names and DB operation patterns.
- The `.PLAN/` files are the design spec; they were written before the SDK was pinned and some syntax details differ. Prefer `module/CLAUDE.md` for C# specifics, `.PLAN/` for game design intent.

---

## File map

```
.stdb-config/             ← persistent CLI identity/token (gitignored); mount on every auth'd call
module/
  spacetime.json          ← points to spacetimedb/ subdirectory
  spacetimedb/
    Lib.cs                ← full T1 schema + reducers (SimTick body is a T4 stub)
    StdbModule.csproj     ← do not touch
    global.json           ← pins net8.0, do not touch
  CLAUDE.md               ← 2.x API reference, read this

client/
  scripts/ConnectionManager.cs   ← T2: add subscriptions after connect
  module_bindings/               ← GENERATED (now has all 7 tables), do not edit
  wivuullegiance.csproj
  scenes/Main.tscn

.PLAN/                    ← design docs; read for intent, not for copy-paste syntax
ACCEPTANCE01.md           ← T0 manual test checklist (already passing)
```
