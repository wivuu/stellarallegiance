## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

## UI / design tasks

For any UI, styling, or design-system work in the Godot client, read [DESIGN.md](DESIGN.md)
first. It is the source of truth for the "Stellar Allegiance" design system — palette/type
tokens (`DesignTokens`), fonts (`UiFonts`), theme wiring, and the reusable components in
`client/scripts/ui/`. Build UI from those tokens and components rather than hardcoding colors,
fonts, or sizes; verify changes against the `UiShowcase` gallery (F9 in-game, or
`--ui-showcase`).

## dotnet restore hangs

If `dotnet restore` hangs/takes forever, stopped (`T` state) `aspire-managed` processes are likely holding a NuGet scratch lock file in `/private/var/folders/.../NuGetScratch/lock/`. Kill them:

```sh
ps aux | grep "[a]spire-managed" | awk '{print $2}' | xargs kill -9
# Then clean up stale locks
rm -f /private/var/folders/*/*/*/NuGetScratch/lock/*
```
