# Randomize world layout per match (Maps [M] plan item)

## Context

.PLAN/README.md line 284: base, asteroid, and aleph positions within sectors should be
randomizeable so each launch (even of the same map) produces a different layout — players must
explore instead of memorizing.

**Key finding: the world is already 100% procedurally placed from a single seed.** `World`'s
constructor (`server/Sim/World.cs:170-307`) draws base positions (`RandomBasePos`, dedicated
`DetRng` at seed ^ 0xB453…), asteroid fields/belts (`SeedAsteroidField/Belt`), and aleph
positions (`RandomOuterPos`) all from `DetRng(seed)`. The only reason every launch is identical
is that the seed is a hardcoded default: `ulong seed = 1234567` (`server/Program.cs:74`),
overridable only by `--seed`.

Everything downstream is already reroll-safe:
- Clients never regenerate from the seed — all statics (bases, rocks, alephs, sector env/dust)
  are streamed per-entity via Welcome / MsgReveal (`server/Net/Protocol.cs:531-649`).
- Minefield wire seeds derive from `(fieldId, World.Seed)` and are carried on the wire
  (`server/Sim/Simulation.Mines.cs:80-82`).
- `MapCatalog` (`server/Content/MapCatalog.cs:6-9`) already documents positions as "randomized
  per match, kept secret" and streams only team/sector-level preview data.
- Every match start builds a fresh `World` via `BuildMatchWorld` (`Simulation.StartMatch`,
  `server/Sim/Simulation.cs:862-888`), and the hub re-Welcomes all clients + resets fog vision.
- Tests construct `World` with their own explicit seeds; the client's `Starscape.cs` 1234567 is
  an unrelated cosmetic star seed.

So the change is small and confined to seed *sourcing*: random by default, pinnable for repro.

## Changes

All in `server/Program.cs` (plus docs):

1. **Random seed by default.** Replace `ulong seed = 1234567` with a securely-random launch seed
   (e.g. `System.Security.Cryptography.RandomNumberGenerator` → 8 bytes → ulong). Track whether
   the operator pinned it: `ulong? pinnedSeed = null`, set by `--seed <n>` (existing flag,
   Program.cs:117) and a new `SIM_SEED` env var (matching the SIM_MAP/SIM_MAPS_DIR convention,
   env read first, flag wins).

2. **Fresh seed per match unless pinned.** In `BuildWorldForMap` (Program.cs:235-242): when no
   pin, roll a new random seed for each `new World(...)` so every match reshuffles — this matches
   MapCatalog's documented "randomized per match" intent and covers restarting on the same map.
   When pinned, keep using the pinned seed everywhere (boot world, match worlds) so `--seed`
   reproduces an exact layout for tests/benchmarks/bug repro.

3. **Log the seed.** Boot seed is already logged (`Log.ServerListening`, Program.cs:340). Add the
   rolled seed to the match-start path (either a log line in `BuildWorldForMap` or extend
   `Log.MatchStarted`) so any live layout can be reproduced later with `--seed`.

4. **MapCatalog stays on any fixed seed** (`MapCatalog.Build`, Program.cs:205) — previews are
   seed-agnostic (team markers + sector layout only); pass the boot seed as today. No change needed.

5. **New test** (in `tests/FogTest` alongside the existing same-seed determinism check at
   FogTest/Program.cs:1391-1404, or a small addition to an existing suite): two `World`s built
   with different seeds produce different base/rock/aleph positions; two with the same seed stay
   identical. No existing test pins literal coordinates, so nothing else changes.

6. **Fix the stale comment** at `server/Sim/World.cs:13-17` — it claims clients "(later)
   re-derive" the map from the seed; they never did and never will (statics are streamed). Update
   it to describe the actual contract: seed → deterministic layout, streamed per-entity, rolled
   fresh per match unless pinned.

7. **Docs**: note the new behavior in `server/README.md` (seed section: random per match,
   `--seed`/`SIM_SEED` to pin) and GLOSSARY.md if it has a seed/world-gen entry.

Deliberately out of scope: fairness constraints on random draws (min base↔aleph distance etc.) —
the distribution is unchanged from today, only unfrozen; tune `seeding:` world.yaml knobs
(`WorldSeedingTuning`, shared/Defs.cs:524) separately if unfair layouts show up in play. Also out
of scope: splitting asteroids and alephs onto independent RNG streams (they currently share
`DetRng(seed)` at World.cs:257/282, so they reroll together — which is exactly what we want here).

## Execution

Delegate the build to an **Opus subagent** (Agent tool, `model: opus`) with this plan file as its
brief — per the project's work-routing convention (plan in Fable, build in Opus). I (Fable) review
the diff and run the verification below afterward.

## Verification

- `dotnet test` / existing suites (tests pin their own seeds — must stay green, incl.
  FlightModelTest which is asserted all-passing as of 2026-06-12).
- Boot the server twice with no `--seed`, diff the logged seed + a few streamed base/rock
  positions (`--server --anonymous` headless client per headless-sim-testing memory) — layouts
  must differ.
- Boot twice with `--seed 42` — layouts must be identical.
- Start two consecutive matches on the same map in one server lifetime — layouts must differ
  (per-match reroll), and clients must rebuild cleanly on the re-Welcome.
- `/verify`-style smoke: run the real client with `--autofly` once to confirm the client renders
  a rerolled world (statics streamed, no client-side seed assumptions).
