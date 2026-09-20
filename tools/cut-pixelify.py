#!/usr/bin/env python3
"""Cut the three static weights the launcher ships out of Pixelify Sans.

The upstream release is one wght-variable file (400-700). Avalonia's font
manager does not set a variation axis, so a Typeface asking that file for 600
gets the 400 default with a *synthesised* bold laid over it -- an extra half
pixel on every stem, which on a pixel face is the difference between the deck
theme's buttons and something that merely resembles them.

So the instances are cut here instead, once, and the three static files are
what is committed. Regular (400) sets the server rows, SemiBold (600) every
label, Bold (700) the wordmark -- which is exactly what the reference layout's
`font-weight` declarations say.

    pip install fonttools
    python3 tools/cut-pixelify.py PixelifySans[wght].ttf

The argument is the upstream variable file, from
https://github.com/google/fonts/tree/main/ofl/pixelifysans (SIL OFL 1.1; the
licence travels with the fonts in Assets/Fonts/PixelifySans-OFL.txt).
"""
import os
import sys

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "src", "MphRead", "Assets", "Fonts")
WEIGHTS = ((400, "Regular"), (600, "SemiBold"), (700, "Bold"))


def cut(source: str) -> None:
    for weight, style in WEIGHTS:
        font = instancer.instantiateVariableFont(
            TTFont(source), {"wght": weight}, inplace=True, updateFontNames=True)
        # One family name across the three, so a Typeface picks by weight
        # rather than by three unrelated families that happen to look alike.
        names = font["name"]
        for record in list(names.names):
            if record.nameID == 1:
                record.string = "Pixelify Sans"
            elif record.nameID == 2:
                record.string = style
            elif record.nameID == 4:
                record.string = f"Pixelify Sans {style}"
            elif record.nameID in (16, 17):
                names.removeNames(nameID=record.nameID)
        font["OS/2"].usWeightClass = weight
        path = os.path.normpath(os.path.join(OUT, f"PixelifySans-{style}.ttf"))
        font.save(path)
        print(f"{path}  wght={weight}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    cut(sys.argv[1])
