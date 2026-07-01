# logo-gen — Stellar Allegiance brand logo lockup

Generates the brand logo used as the engine **boot splash**
(`client/assets/ui/logo.svg` → `client/assets/ui/logo.png`).

The emblem is the Claude Design splash reference
([Splash.dc.html](https://claude.ai/design/p/28bf0d21-5959-4554-8bfc-a1f92113ea28),
animations stripped); the `STELLAR / ALLEGIANCE` wordmark is set in **Michroma**
(`client/assets/fonts/michroma.ttf`) and baked to outline paths so it renders
without a font engine (Godot's SVG importer / ThorVG has none).

## Regenerate

```sh
# 1. build the self-contained transparent SVG (needs fonttools)
python3 -m venv .venv && .venv/bin/pip install fonttools
.venv/bin/python tools/logo-gen/gen_logo.py

# 2. rasterize it to a transparent PNG headlessly (Godot's CPU SVG loader)
godot --headless --path client -s "$PWD/tools/logo-gen/rasterize_logo.gd"

# 3. re-import so res:// picks up the new PNG
godot --headless --import --path client
```

Commit both `logo.svg` (source) and `logo.png` (the boot image — force-added past
`.gitignore`). Tune wordmark size in `gen_logo.py`; tune the PNG resolution via
`SCALE` in `rasterize_logo.gd`.

The boot splash is wired in `client/project.godot`
(`application/boot_splash/*`): `image` → `logo.png`, `bg_color` → the design
`Void` (`#05070F`), so the transparent logo reads as logo-on-dark.
