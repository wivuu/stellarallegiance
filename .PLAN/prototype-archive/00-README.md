# Stellar Allegiance — Two-Ship Prototype

**A handoff package for an autonomous coding agent.**

This repository of documents specifies a minimal vertical slice of an Allegiance-style
team space combat game, built on **Godot 4 (C#)** as the client and **SpacetimeDB 2.0**
as the authoritative server/database. The goal of this prototype is *not* the full game.
It is the smallest build that proves the core architecture works: an authoritative
server holding world state, multiple Godot clients flying ships with local prediction,
and a clean reducer/subscription loop between them.

---

## What this prototype IS

- Two ship classes (a **Scout** and a **Fighter**) that fly in a linear-drag flight model.
- A single 3D sector containing **asteroids** the ships fly around.
- Two teams, each with one **base** that acts as a spawn/dock point.
- Authoritative server state in SpacetimeDB: ship transforms, health, team membership.
- Client-side prediction for the local player's flight, reconciled against the server.
- A trivial win/loss stub (destroy the enemy base) to prove end-to-end state flow.

## What this prototype IS NOT (explicitly deferred)

- No commander / RTS map view (deferred to milestone after prototype).
- No multiple sectors or alephs yet (single sector only).
- No mining economy, constructors, or tech paths.
- No matchmaking, accounts, or persistence beyond a live match.
- No art pass — primitive meshes and placeholder materials only.

These deferrals are deliberate. Do not implement them. If a task seems to require one,
stop and flag it in `99-OPEN-QUESTIONS.md` rather than expanding scope.

---

## Document map (read in this order)

| # | File | Purpose |
|---|------|---------|
| 00 | `00-README.md` | This file. Orientation and scope. |
| 01 | `01-ARCHITECTURE.md` | How Godot and SpacetimeDB fit together; the authority model. |
| 02 | `02-ENVIRONMENT-SETUP.md` | Exact toolchain, versions, install commands, project layout. |
| 03 | `03-DATA-MODEL.md` | SpacetimeDB tables (the schema). |
| 04 | `04-REDUCERS.md` | SpacetimeDB reducers (the server logic API). |
| 05 | `05-CLIENT-GODOT.md` | Godot scene tree, scripts, prediction & reconciliation. |
| 06 | `06-FLIGHT-MODEL.md` | The linear-drag flight math, shared between client and server. |
| 07 | `07-NETSYNC-PROTOCOL.md` | Tick rates, what syncs when, reconciliation rules. |
| 08 | `08-BUILD-ORDER.md` | The ordered task list the agent should execute. |
| 09 | `09-ACCEPTANCE-TESTS.md` | How to know each milestone is done. |
| 99 | `99-OPEN-QUESTIONS.md` | Where to record decisions that need a human. |

---

## How the agent should work

1. Read every document `00` through `09` before writing code.
2. Execute tasks **in the order given in `08-BUILD-ORDER.md`**. Each task lists its
   acceptance test in `09-ACCEPTANCE-TESTS.md`. Do not start a task until the previous
   task's acceptance test passes.
3. When a fact about SpacetimeDB or Godot is unclear, prefer the **official docs**
   (links in `02-ENVIRONMENT-SETUP.md`) over guessing. APIs in this space change
   between releases.
4. When blocked or when a decision exceeds this spec, append to `99-OPEN-QUESTIONS.md`
   and continue with the most reasonable assumption, clearly logged.

---

## One-paragraph mental model

SpacetimeDB is the single source of truth. The client never trusts itself for anything
another player can see. The local player *predicts* their own ship's motion each frame so
flight feels responsive, then corrects toward the authoritative position when the server's
update arrives. Everything else — other ships, bases, health, who is on which team — is
just rows in the database that the client subscribes to and renders. If you keep that
split clean, the prototype is small. If you blur it, it will fight you.
