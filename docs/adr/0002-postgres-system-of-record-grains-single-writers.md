---
status: accepted
date: 2026-09-06
---
# Postgres is the system of record; Orleans grains are single writers through EF Core

The lobby persists players, game servers, and matches, and the original plan framed Orleans grains as
a distributed cache in front of Postgres with journaling for durability. We decided instead that
Postgres is the only system of record, each entity grain (player, game server, match) is the sole
writer of its own rows through EF Core, and Orleans supplies the actor model, in-memory hot state, and
the single-writer guarantee, not persistence. No Orleans grain-storage provider and no journaling.

## Considered options
- **Orleans Journaling**: rejected. Every published version is prerelease (10.3.1-alpha.1 as of
  2026-08-28) and the only storage provider is Azure Storage; the lobby runs on Railway Postgres.
- **Orleans ADO.NET grain storage + relational read model**: rejected; state blobs plus query rows is a
  dual write, and leaderboards need relational columns anyway.
- **No Orleans, EF only**: rejected in favour of keeping the actor model for future horizontal scale.

## Consequences
- The "cache/DB consistency" problem disappears: nothing writes a grain's rows except that grain.
- Single-key reads go through the entity grain; aggregates and lists go through a per-silo
  stateless-worker query grain over a no-tracking context, or SQL views. Endpoints never write via EF.
- The in-memory listing registry and signaling relay stay outside grains until a second replica is
  actually wanted.
