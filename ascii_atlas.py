# ascii_atlas.py   (pip install pillow numpy)
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw, ImageFont
    import numpy as np
except ImportError as e:
    sys.exit(f"Missing package: {e}\nRun:  pip install pillow numpy")

HERE = Path(__file__).resolve().parent
OUT = HERE / "ascii_atlas.png"          # saved next to this script, not the terminal's folder

CELL = 48
RAMP = r'''$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\|()1{}[]?-_+~<>i!lI;:,"^`'. '''

# Uses the first font that exists. Drop VT323-Regular.ttf next to this script to prefer it.
FONT_CANDIDATES = [
    HERE / "VT323-Regular.ttf",
    "C:/Windows/Fonts/consola.ttf",                          # Windows
    "C:/Windows/Fonts/cour.ttf",
    "/System/Library/Fonts/Menlo.ttc",                       # macOS
    "/System/Library/Fonts/Courier.ttc",
    "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",   # Linux
]

font_path = next((p for p in FONT_CANDIDATES if Path(p).exists()), None)
if font_path is None:
    sys.exit("No font found. Put a monospace .ttf (e.g. VT323-Regular.ttf) next to this script.")
print("Using font:", font_path)

font = ImageFont.truetype(str(font_path), int(CELL * 0.8))

if len(set(RAMP)) != len(RAMP):
    sys.exit("Duplicate characters in RAMP")

def glyph(ch):
    img = Image.new("L", (CELL, CELL), 0)
    ImageDraw.Draw(img).text((CELL // 2, int(CELL * 0.75)), ch, font=font, fill=255, anchor="ms")
    return img

glyphs = {c: glyph(c) for c in RAMP}

# Warn about characters the font can't draw (they come out blank)
for c, g in glyphs.items():
    if c != " " and np.asarray(g).max() == 0:
        print(f"WARNING: font has no glyph for {c!r}")

ordered = sorted(RAMP, key=lambda c: np.asarray(glyphs[c]).mean())   # lightest -> densest
print("Glyph count:", len(ordered))

atlas = Image.new("L", (CELL * len(ordered), CELL), 0)
for i, ch in enumerate(ordered):
    atlas.paste(glyphs[ch], (i * CELL, 0))

atlas.save(OUT)
print("Saved:", OUT)

print('C# ramp:  @"' + "".join(ordered).replace('"', '""') + '"')