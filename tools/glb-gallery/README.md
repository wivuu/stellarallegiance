# glb-gallery

Renders a labeled contact sheet of every `.glb` in a source folder, for picking models out of a
large asset library: Godot headless renders one thumbnail per model (`render.gd` in this folder's
own tiny project), then `compose.py` tiles them into a 10-column labeled grid with Pillow.

```bash
tools/glb-gallery/gallery.ps1 [-SrcDir <dir>] [-OutPng <png>] [-Size <n>] [-Limit <n>]
```

`-SrcDir` defaults to the repo's `pick-assets/`; `-OutPng` defaults to
`tools/glb-gallery/glb-gallery.png`. Needs a Godot 4 .NET binary — resolved through
`scripts/godot-bin.ps1`, so the same `$env:GODOT` / user-secrets / PATH order as everything else —
plus `python3` (or `python`) with Pillow. The sheet and the per-model `thumbs/` are gitignored.
