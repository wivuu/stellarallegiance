# logo-gen — Stellar Allegiance brand logo

Produces the brand logo used as the engine **boot splash**
(`client/assets/ui/logo.png`) from the source artwork (`source_logo.png`, the
finished Stellar Allegiance emblem + `STELLAR / ALLEGIANCE` wordmark).

`gen_logo.py` keys the artwork's near-black background out to a true-transparent
alpha by **un-premultiplying** it (subtract the flat bg, derive alpha from the
residual luminance, divide it back out). That yields clean soft edges with no dark
fringe on any background — the emblem's own dark interior keys out too, which is
correct: on the dark boot screen the transparent areas show the matching
`bg_color`, reproducing the artwork. The output is trimmed to content and scaled.

## Regenerate

```sh
python3 -m venv .venv && .venv/bin/pip install pillow
.venv/bin/python tools/logo-gen/gen_logo.py
godot --headless --import --path client   # re-import so res:// picks up the new PNG
```

Commit both `source_logo.png` (the master art) and `client/assets/ui/logo.png`
(the boot image — force-added past `.gitignore`). Tune the cutout / size via the
constants at the top of `gen_logo.py` (`TARGET_H`, `FLOOR`, `KNEE`, `PAD`).

The boot splash is wired in `client/project.godot` (`application/boot_splash/*`):
`image` → `logo.png`, `bg_color` → the design `Void` (`#05070F`), so the
transparent logo reads as logo-on-dark like the source art.
