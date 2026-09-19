#!/usr/bin/env python3
"""Produce STATIC font instances for the Avalonia game launcher
(launcher/App/Assets/Fonts/) from the game's two variable fonts
(client/assets/fonts/saira.ttf, client/assets/fonts/jetbrains-mono.ttf).

WHY: Avalonia's FontWeight selector does not work against a variable TTF (it
has no way to ask the font to interpolate a `wght` instance at load time), so
the launcher needs one fully-static .ttf per weight it uses. This script pins
each variable font's axes to a fixed location with
`fontTools.varLib.instancer.instantiateVariableFont` and writes out a plain,
non-variable TrueType font per weight.

GOTCHAS (verified with fontTools against the source files before writing this):
  - Saira has TWO axes: `wght` 100-900 AND `wdth` 50-125. The `wght` axis
    DEFAULT IS 100 (Thin) -- if you only pin wght and let wdth float to its
    default, you still get a variable font; if you forget wdth entirely you
    get whatever fontTools does with an unpinned axis. Both axes must be
    pinned explicitly (wdth=100, the normal width) for every Saira instance.
  - JetBrains Mono has one axis, `wght` 100-800, default 400 (i.e. its
    unpinned default already happens to be Regular, unlike Saira's).
  - Neither source font carries any Mac (platformID=1) name records for IDs
    1/2/4/6/16/17 -- only Windows (platformID=3, platEncID=1, langID=0x409)
    has them. Per the "where those records exist" rule this script only
    writes/updates the Windows platform records for those IDs (it does not
    invent new Mac-platform entries).
  - JetBrains Mono's source name table has NO ID 16/17 (typographic
    family/subfamily) at all -- only Saira's does. This script sets 16/17 on
    every output of both families regardless of what the source shipped.

NAMING SCHEME (set on every output, Windows platform 3/1/0x409):
  RIBBI weights (Regular, Bold):     ID1=family,             ID2=weight
  Non-RIBBI weights (SemiBold, Medium): ID1="<family> <weight>", ID2="Regular"
  Always:                            ID16=family, ID17=weight
  ID4 (full name) = ID1, plus " "+ID2 when ID2 != "Regular"
  ID6 (PostScript name, no spaces) = "<family-no-spaces>-<weight>"
  ID3 (unique identifier) keeps the source's "<version>;<vendorID>;" prefix
  with the trailing PostScript name swapped to the new ID6.
`OS/2.usWeightClass` is set to the true numeric weight; the `fsSelection`
REGULAR (0x40) / BOLD (0x20) bits and `head.macStyle` BOLD bit (0x01) are set
per the OpenType RIBBI convention (REGULAR only for the 400 instance, BOLD
only for the 700 instance, neither for SemiBold/Medium).

DETERMINISM: fonts are opened with `recalcTimestamp=False` and `head.modified`
is explicitly copied back from the source font after instancing, so
`head.modified`/`head.created` never drift to "now". Running this script
twice must produce byte-identical output files (verified by the caller via
sha256; this script does not need network/wall-clock state to do so).

Run:
  uv run --with fonttools==4.59.0 python3 tools/font-instancer/instance_fonts.py

Or, without uv:
  python3 -m venv .venv && .venv/bin/pip install fonttools==4.59.0
  .venv/bin/python tools/font-instancer/instance_fonts.py
"""

import hashlib
import os
import shutil
import sys

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
FONTS_SRC = os.path.join(REPO, "client/assets/fonts")
OUT_DIR = os.path.join(REPO, "launcher/App/Assets/Fonts")

SAIRA_SRC = os.path.join(FONTS_SRC, "saira.ttf")
JBM_SRC = os.path.join(FONTS_SRC, "jetbrains-mono.ttf")

LICENSES = ["OFL-Saira.txt", "OFL-JetBrainsMono.txt"]

# Variable tables that must not survive in a fully-static instance, plus STAT
# and DSIG which the instancer sometimes leaves behind but which no longer
# apply once the font is pinned (DSIG in particular is invalidated by any
# edit, so it must not ship a now-bogus signature).
DROP_TABLES = ("fvar", "gvar", "avar", "cvar", "HVAR", "MVAR", "STAT", "DSIG")

OS2_REGULAR_BIT = 0x0040
OS2_BOLD_BIT = 0x0020
HEAD_BOLD_BIT = 0x0001

RIBBI_SUBFAMILIES = {"Regular", "Bold", "Italic", "Bold Italic"}

# (src, out filename, family, subfamily/weight name, wght, wdth-or-None, usWeightClass)
INSTANCES = [
    (SAIRA_SRC, "Saira-Regular.ttf", "Saira", "Regular", 400, 100, 400),
    (SAIRA_SRC, "Saira-SemiBold.ttf", "Saira", "SemiBold", 600, 100, 600),
    (SAIRA_SRC, "Saira-Bold.ttf", "Saira", "Bold", 700, 100, 700),
    (JBM_SRC, "JetBrainsMono-Regular.ttf", "JetBrains Mono", "Regular", 400, None, 400),
    (JBM_SRC, "JetBrainsMono-Medium.ttf", "JetBrains Mono", "Medium", 500, None, 500),
]

# UI symbol code points the launcher may want to draw from these fonts; when a
# font's cmap is missing one, the launcher falls back to a vector icon.
SYMBOLS = [
    (0x25C6, "BLACK DIAMOND ◆"),
    (0x25CF, "BLACK CIRCLE ●"),
    (0x2713, "CHECK MARK ✓"),
    (0x25B8, "BLACK RIGHT-POINTING SMALL TRIANGLE ▸"),
    (0x2715, "MULTIPLICATION X ✕"),
    (0x25CB, "WHITE CIRCLE ○"),
    (0x26A0, "WARNING SIGN ⚠"),
    (0x2699, "GEAR ⚙"),
    (0x2197, "NORTH EAST ARROW ↗"),
    (0x25B6, "BLACK RIGHT-POINTING TRIANGLE ▶"),
    (0x00B7, "MIDDLE DOT ·"),
    (0x2026, "HORIZONTAL ELLIPSIS …"),
    (0x2014, "EM DASH —"),
    (0x2192, "RIGHTWARDS ARROW →"),
]

WIN_PLAT = dict(platformID=3, platEncID=1, langID=0x409)


def set_win_name(name_table, name_id, value):
    """Set (or add) a Windows (3,1,0x409) name record. Source fonts carry no
    Mac-platform records for these IDs, so per the task's "where those
    records exist" rule we only touch Windows here."""
    name_table.setName(value, name_id, WIN_PLAT["platformID"], WIN_PLAT["platEncID"], WIN_PLAT["langID"])


def build_instance(src_path, out_name, family, subfamily, wght, wdth, weight_class):
    font = TTFont(src_path, recalcTimestamp=False)
    src_modified = font["head"].modified

    axis_limits = {"wght": float(wght)}
    if wdth is not None:
        axis_limits["wdth"] = float(wdth)

    instancer.instantiateVariableFont(font, axis_limits, inplace=True, updateFontNames=False)

    for tag in DROP_TABLES:
        if tag in font:
            del font[tag]

    is_ribbi = subfamily in RIBBI_SUBFAMILIES
    is_bold = subfamily == "Bold"

    id1 = family if is_ribbi else f"{family} {subfamily}"
    id2 = subfamily if is_ribbi else "Regular"
    id16 = family
    id17 = subfamily
    id4 = id1 if id2 == "Regular" else f"{id1} {id2}"
    ps_name = f"{family.replace(' ', '')}-{subfamily}"

    name = font["name"]
    old_id3 = name.getDebugName(3) or ""
    parts = old_id3.split(";")
    new_id3 = ";".join(parts[:-1] + [ps_name]) if len(parts) >= 2 else ps_name

    set_win_name(name, 1, id1)
    set_win_name(name, 2, id2)
    set_win_name(name, 3, new_id3)
    set_win_name(name, 4, id4)
    set_win_name(name, 6, ps_name)
    set_win_name(name, 16, id16)
    set_win_name(name, 17, id17)

    os2 = font["OS/2"]
    os2.usWeightClass = weight_class
    if is_bold:
        os2.fsSelection = (os2.fsSelection | OS2_BOLD_BIT) & ~OS2_REGULAR_BIT
    else:
        os2.fsSelection = os2.fsSelection & ~OS2_BOLD_BIT
        if subfamily == "Regular":
            os2.fsSelection |= OS2_REGULAR_BIT
        else:
            os2.fsSelection = os2.fsSelection & ~OS2_REGULAR_BIT

    head = font["head"]
    if is_bold:
        head.macStyle |= HEAD_BOLD_BIT
    else:
        head.macStyle &= ~HEAD_BOLD_BIT
    head.modified = src_modified  # determinism: never let this drift to "now"

    out_path = os.path.join(OUT_DIR, out_name)
    font.save(out_path)
    return out_path


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        h.update(f.read())
    return h.hexdigest()[:12]


def verify(path):
    """Re-open the written file fresh and check every hard requirement.
    Returns (ok, row) where row is the printable verification line."""
    font = TTFont(path)
    ok = True
    problems = []

    has_fvar = "fvar" in font
    if has_fvar:
        ok = False
        problems.append("fvar still present")
    for tag in DROP_TABLES:
        if tag in font:
            ok = False
            problems.append(f"{tag} still present")

    id16 = font["name"].getDebugName(16)
    id17 = font["name"].getDebugName(17)
    if not id16 or not id17:
        ok = False
        problems.append("missing ID16/17")

    weight_class = font["OS/2"].usWeightClass
    num_glyphs = font["maxp"].numGlyphs
    size = os.path.getsize(path)
    digest = sha256(path)

    row = (
        f"{os.path.basename(path):<28} {id16 or '?':<15} {id17 or '?':<9} "
        f"wght={weight_class:<4} fvar={has_fvar!s:<5} glyphs={num_glyphs:<5} "
        f"size={size:<7} sha256={digest}"
    )
    if problems:
        row += "  FAIL: " + "; ".join(problems)
    return ok, row


def print_symbol_coverage():
    print("\nSource cmap coverage of UI symbol code points (informational --")
    print("launcher draws any MISSING cell as a vector icon instead):")
    header = f"{'codepoint':<28}" + "".join(f"{n:<16}" for n in ("Saira", "JetBrains Mono"))
    print(header)
    cmaps = {label: TTFont(path).getBestCmap() for label, path in (("Saira", SAIRA_SRC), ("JetBrains Mono", JBM_SRC))}
    for cp, desc in SYMBOLS:
        row = f"U+{cp:04X} {desc:<21}"
        for label in ("Saira", "JetBrains Mono"):
            present = cp in cmaps[label]
            row += f"{'present' if present else 'MISSING':<16}"
        print(row)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    print(f"Writing static instances to {OUT_DIR}\n")
    written = []
    for src, out_name, family, subfamily, wght, wdth, weight_class in INSTANCES:
        path = build_instance(src, out_name, family, subfamily, wght, wdth, weight_class)
        written.append(path)

    for lic in LICENSES:
        shutil.copyfile(os.path.join(FONTS_SRC, lic), os.path.join(OUT_DIR, lic))
        print(f"copied {lic} -> {OUT_DIR}")

    print("\nVerification (fresh re-open of each written file):")
    header = f"{'file':<28} {'ID16':<15} {'ID17':<9} {'weight':<9} {'fvar':<9} {'glyphs':<11} {'size':<11} sha256"
    print(header)
    all_ok = True
    for path in written:
        ok, row = verify(path)
        print(row)
        all_ok = all_ok and ok

    print_symbol_coverage()

    if not all_ok:
        print("\nFAIL: one or more static instances did not pass verification.", file=sys.stderr)
        sys.exit(1)

    print("\nOK: all static instances verified.")


if __name__ == "__main__":
    main()
