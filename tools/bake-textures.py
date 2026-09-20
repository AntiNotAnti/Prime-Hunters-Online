#!/usr/bin/env python3
"""Bake a Quake level's textures into the DS texture format, ahead of time.

Why ahead of time: the conversion runs on the machine that plays the game,
and on Android that machine has no JPEG decoder -- the STB natives are left
out of the APK on purpose, being desktop builds. Decoding and quantising here
means the device only has to copy bytes.

Why at all: without this a converted map wears textures borrowed from a room
of Metroid Prime Hunters, which look wrong and, worse, put cartridge data
inside the generated room files. Textures baked from the level's own art make
the result the player's own two things -- free level, free textures -- and
nothing from the cartridge is in the map at all.

The output pairs each BSP texture index with an 8-bit paletted image, which
is the format the packer hands to the hardware:

    "FPTX", u16 version, u16 count
    per entry: u16 bsp texture index, u16 width, u16 height,
               u16 palette length, u16 name length, name (utf-8),
               palette (u16 each, BGR555, red in the low bits),
               one byte per pixel

    tools/bake-textures.py wrackdm17.bsp - pak0.pk3,pak4-textures.pk3 out.tex
"""
import io
import os
import struct
import sys
import zipfile

from PIL import Image

# Only the faces that are drawn need a texture, and only these two face types
# are drawn by the importer: 1 is a polygon, 3 is a triangle soup.
DRAWN = (1, 3)
SURF_SKIP = 0x80 | 0x100 | 0x200 | 0x400  # nodraw, hint, skip, sky in any order


def lump(data, index):
    offset, length = struct.unpack("<ii", data[8 + index * 8:16 + index * 8])
    return data[offset:offset + length]


def read_bsp(path, map_name):
    if path.lower().endswith(".pk3"):
        with zipfile.ZipFile(path) as archive:
            return archive.read(f"maps/{map_name}.bsp")
    with open(path, "rb") as handle:
        return handle.read()


def used_textures(data):
    """Which texture entries the drawn faces actually reference, and their names."""
    textures = lump(data, 1)
    names = []
    for i in range(0, len(textures), 72):
        raw = textures[i:i + 64].split(b"\0")[0].decode("latin-1")
        flags, contents = struct.unpack("<ii", textures[i + 64:i + 72])
        names.append((raw, flags))
    faces = lump(data, 13)
    used = []
    seen = set()
    for i in range(0, len(faces), 104):
        texture, effect, kind = struct.unpack("<iii", faces[i:i + 12])
        if kind not in DRAWN or texture in seen or texture >= len(names):
            continue
        name, flags = names[texture]
        if flags & SURF_SKIP:
            continue
        seen.add(texture)
        used.append((texture, name))
    return sorted(used)


def index_archives(archives):
    """Every file in the archives, keyed by lower-case name.

    A shader name in a .bsp is not the spelling of the file it came from:
    the compiler upper-cases some of them, and a level whose author worked on
    Windows can have "SandTrim.JPG" answering to "textures/dust2/SANDTRIM".
    Matching exactly finds nothing and the map comes out untextured.
    """
    found = {}
    for archive in archives:
        for name in archive.namelist():
            found.setdefault(name.lower(), (archive, name))
    return found


def find_image(files, name):
    for extension in (".tga", ".jpg", ".png"):
        entry = files.get((name + extension).lower())
        if entry is not None:
            archive, actual = entry
            return archive.read(actual)
    return None


def bake(image, size, colors):
    image = image.convert("RGB").resize((size, size), Image.LANCZOS)
    image = image.quantize(colors=colors, method=Image.MEDIANCUT)
    palette = image.getpalette()[:colors * 3]
    entries = []
    for i in range(0, len(palette), 3):
        r, g, b = palette[i:i + 3]
        entries.append((b >> 3) << 10 | (g >> 3) << 5 | (r >> 3))
    return entries, image.tobytes()


def main():
    if len(sys.argv) < 5:
        raise SystemExit(__doc__)
    bsp_path, map_name, paks, out = sys.argv[1:5]
    size = int(sys.argv[5]) if len(sys.argv) > 5 else 64
    colors = 256
    data = read_bsp(bsp_path, map_name)
    files = index_archives([zipfile.ZipFile(p) for p in paks.split(",")])
    entries = []
    missing = []
    for texture, name in used_textures(data):
        raw = find_image(files, name)
        if raw is None:
            missing.append(name)
            continue
        palette, pixels = bake(Image.open(io.BytesIO(raw)), size, colors)
        entries.append((texture, name, size, size, palette, pixels))
    with open(out, "wb") as handle:
        handle.write(b"FPTX" + struct.pack("<HH", 1, len(entries)))
        for texture, name, width, height, palette, pixels in entries:
            encoded = name.encode("utf-8")
            handle.write(struct.pack("<HHHHH", texture, width, height, len(palette), len(encoded)))
            handle.write(encoded)
            handle.write(struct.pack(f"<{len(palette)}H", *palette))
            handle.write(pixels)
    print(f"{len(entries)} textures at {size}x{size} -> {os.path.getsize(out):,} bytes  {out}")
    if missing:
        print(f"no image for {len(missing)}: {', '.join(missing[:6])}"
              + (" ..." if len(missing) > 6 else ""))


if __name__ == "__main__":
    main()
