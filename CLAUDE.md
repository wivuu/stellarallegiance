## Quick Reference

**[GLOSSARY.md](GLOSSARY.md)** — reference for common terminology across the codebase. Organized by domain (simulation, weapons, networking, UI, server, etc.) with key file locations for each concept. **Update the glossary when:**
  - Introducing new gameplay systems or mechanics
  - Adding new server/client architecture patterns
  - Coining new domain-specific terms that recur across files
  
**[DESIGN.md](DESIGN.md)** — UI component library and design-system spec (palette, type scale, theme wiring, components).

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
