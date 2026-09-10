# cursor-gen

Draws the Stellar Allegiance mouse cursors procedurally with Pillow — 8x supersampled and
downscaled for clean anti-aliased edges, Void-dark fill with the base cyan chrome accent. Colors
mirror `client/scripts/ui/DesignTokens.cs` (`Void` #05070F, `TeamAccentBase` #37E0FF); the cursor
always uses the BASE accent, never a faction tint, because like all chrome it must read the same
on every team.

```bash
python3 tools/cursor-gen/gen_cursor.py    # requires Pillow
```

Writes two 32x32 PNGs into `client/assets/ui/`: `cursor.png` (angular pointer, hotspot at the tip
`(2, 2)`) and `cursor_ibeam.png` (text-field I-beam, hotspot `(16, 16)`). Both are committed, so
re-run this only when the cursor art itself changes.
