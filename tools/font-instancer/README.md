# font-instancer — static launcher fonts

Avalonia cannot select a weight out of a variable TTF (`FontWeight` is
ignored against a variable font at load time), so the desktop Game Launcher
needs plain STATIC instances of the game's two variable fonts
(`client/assets/fonts/saira.ttf`, `client/assets/fonts/jetbrains-mono.ttf`;
both SIL OFL 1.1, no Reserved Font Name declared by either). `instance_fonts.py`
pins each font's axes with `fontTools.varLib.instancer.instantiateVariableFont`
and writes five fully-static `.ttf` files into `launcher/App/Assets/Fonts/`.

## The two-axis gotcha

Saira has **two** axes — `wght` 100–900 *and* `wdth` 50–125 — and the `wght`
axis's default is **100 (Thin)**, not 400. Pinning only `wght` and leaving
`wdth` to float does not produce a static font; both axes must be pinned
explicitly (`wdth=100`, normal width) for every Saira instance, or you get a
Thin, still-partially-variable font instead of the intended weight.
JetBrains Mono has a single axis, `wght` 100–800, default 400 — simpler, but
still needs to be pinned to drop `fvar`/`gvar`/`avar`/`HVAR`.

Both gotchas, plus the exact naming/weight-bit scheme used for each output,
are documented in the script's own header docstring — read that before
changing anything.

## Regenerate

```sh
uv run --with fonttools==4.59.0 python3 tools/font-instancer/instance_fonts.py
```

Without `uv`:

```sh
python3 -m venv .venv && .venv/bin/pip install fonttools==4.59.0
.venv/bin/python tools/font-instancer/instance_fonts.py
```

The script is deterministic — it opens fonts with `recalcTimestamp=False` and
copies `head.modified` back from the source, so running it twice produces
byte-identical output files (verified via sha256 when this was authored).

## Output (`launcher/App/Assets/Fonts/`)

- `Saira-Regular.ttf` (wght 400, wdth 100)
- `Saira-SemiBold.ttf` (wght 600, wdth 100)
- `Saira-Bold.ttf` (wght 700, wdth 100)
- `JetBrainsMono-Regular.ttf` (wght 400)
- `JetBrainsMono-Medium.ttf` (wght 500)
- `OFL-Saira.txt`, `OFL-JetBrainsMono.txt` — verbatim copies of the license
  texts, shipped next to the fonts as OFL requires.

Each output is a plain static TrueType font: no `fvar`/`gvar`/`avar`/`HVAR`/
`MVAR`/`STAT`/`DSIG` tables, correct `OS/2.usWeightClass`, RIBBI-consistent
`fsSelection`/`head.macStyle` bold bits, and name-table IDs 1/2/4/6/16/17 set
on the Windows (3,1,0x409) platform so a font stack can select faces by
family + weight (see the script header for the exact naming scheme, including
the SemiBold/Medium non-RIBBI convention).

These files are committed generated assets — regenerate them here rather than
editing a `.ttf` by hand, and re-run after any change to the source variable
fonts in `client/assets/fonts/`.

## License

Both source fonts are SIL OFL 1.1 with no Reserved Font Name, so renaming
weight instances (e.g. `Saira-SemiBold.ttf`) and redistributing them is
permitted. The OFL requires the license text to accompany any distributed
copy, which is why `OFL-Saira.txt` / `OFL-JetBrainsMono.txt` are copied
alongside the fonts rather than only living under `client/assets/fonts/`.
