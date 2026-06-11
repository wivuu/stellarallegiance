# Allegiance Ship Movement — Self-Contained Reference

Everything needed to **replicate Allegiance's flight feel** in an homage game, extracted from the
engine so you don't need the original repository. All relevant constants, enums, struct layouts, the
exact integration loop, and the rotation math are embedded here as code/pseudocode.

## Files

| File | What's in it |
|---|---|
| `README.md` | This overview + the "feel" summary. |
| `01_flight_model.md` | The per-tick physics integration loop — pseudocode + the verbatim engine excerpt. |
| `02_rotation_math.md` | Orientation matrix Yaw/Pitch/Roll/Renormalize/TimesInverse — verbatim + pseudocode. |
| `03_constants_and_enums.md` | Button masks, axis enum, `ControlData`, float-constant IDs, hull & afterburner struct layouts. |
| `04_data_schema.md` | Per-ship stat schema: CSV column order + DB→engine mapping + derivation formulas. |
| `05_reference_implementation.py` | A standalone, runnable Python port of the entire model. No dependencies. |
| `06_extracted_hull_stats.md` | **Real numbers** for 74 ship types, extracted from the game core, grouped by role. |
| `extract_hulls.py` | The extractor: parses ship stats out of a binary `.igc` static core. |
| `hull_stats.csv` | Full per-record dump (379 rows, all civilizations) — feed straight into the model. |
| `source_excerpts/` | Verbatim C++ snippets (the only engine code that matters), so nothing links back to the repo. |

## Where the numbers come from

The engine code defines *how* ships move; the *per-ship numbers* live in a binary static "core". This
folder already contains both: `06_extracted_hull_stats.md` + `hull_stats.csv` hold real values pulled
from `artwork/tester.igc` by `extract_hulls.py`. To extract from a different core:

```
python3 extract_hulls.py path/to/core.igc --csv out.csv
```

The original human-authored source for that binary is a `TypesOfShips.csv` (column map in
`04_data_schema.md`); the binary `.igc` format and offsets are documented at the top of `extract_hulls.py`.

---

## TL;DR — the five things that make it *feel* like Allegiance

1. **Newtonian flight + artificial drag.** Ships are points with velocity. Thrust adds velocity; an
   exponential drag term pulls it back toward a per-ship terminal speed. `maxSpeed` is the *equilibrium*
   of thrust vs. drag, not a hard cap — you can briefly exceed it (afterburner) and bleed off smoothly.
2. **Turning is rate- *and* acceleration-limited.** The stick commands a target angular *velocity*
   (`MaxTurnRate`); the actual rate slews toward it at a max angular *acceleration* (`TurnTorque`).
   This gives rotational inertia — ships don't snap, and they keep rotating briefly after you release.
3. **Speed-dependent agility.** Angular acceleration ramps from 50% at rest to 100% at max speed
   (`TorqueMultiplier`). Ships feel locked-up stationary, crisp while flying. (Max rate is constant.)
4. **Strafe and reverse are deliberately weaker** than forward thrust (`SideMultiplier`,
   `BackMultiplier`, both < 1). Forward is always the strongest axis.
5. **Mass cancels out of acceleration and turning.** It's stored per hull but only affects collisions/
   momentum. Heaviness is expressed by the designer picking lower accel/turn numbers, *not* the mass
   term — see the algebra in `04_data_schema.md`.

The whole model is ~8 steps per tick; the runnable version is `05_reference_implementation.py`.
