# Tech-tree dumps (debug / context)

Flattened, self-contained YAML snapshots of the faction content, for handing to an LLM or human as
context. Each file pairs the fully-merged `catalog:` (every hull/weapon/station/development/tech, no
cross-file references) with a per-faction `analysis:` that pre-resolves the relationships:

- `starting` — credits, income, base techs/capabilities, initial station, lifepod.
- `reachable-techs` / `reachable-capabilities` — the full forward closure a faction can eventually own.
- `available-at-start` — buildable ids buildable immediately (no research/building first).
- `techs.<id>` — `granted-by` (what unlocks the research), `unlocks` (what it gates), `requires-first`
  (the techs needed before you can research it).
- `buildables.<id>` — `kind`, `price`, `needs-techs`/`needs-capabilities`, `grants-*`,
  `available-at-start`, and `unlocked-by` (the stations/developments to build/research first).

## Files

- `tech-tree-sample.yaml` — the library's rich multi-faction sample (`factions/sample-data/`), with a
  real research tree (developments + techs). Best for understanding the model's full shape.
- `tech-tree-stock.yaml` — the live game bundle (`server/content/factions/`). Currently shallow:
  everything is gated only on the `base` capability (Stage-2 economy; the tech tree proper is Stage 4).

## Regenerate

```sh
dotnet run --project factions/src/Allegiance.Factions.Cli -- dump <manifest.yaml> --output <out.yaml>
# e.g.
dotnet run --project factions/src/Allegiance.Factions.Cli -- dump factions/sample-data/core.manifest.yaml --output .PLAN/tech-tree-sample.yaml
dotnet run --project factions/src/Allegiance.Factions.Cli -- dump server/content/factions/core.manifest.yaml --output .PLAN/tech-tree-stock.yaml
```

The `dump` command lives in `factions/src/Allegiance.Factions.Cli`; the analysis is built by
`Allegiance.Factions.Resolution.TechTreeReport`.
